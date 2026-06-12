using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RtdDolarNative.Dom;
using RtdDolarNative.Flow;
using RtdDolarNative.Logging;
using RtdDolarNative.MarketData;

namespace RtdDolarNative.Heatmap
{
    public sealed class HeatmapProcessor : IDisposable
    {
        private readonly object _lock = new object();
        private readonly decimal _tickSize;
        private readonly MarketHeatmapSqliteStore _store;
        private readonly Dictionary<string, Dictionary<decimal, HeatmapCell>> _bookByAsset = new Dictionary<string, Dictionary<decimal, HeatmapCell>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<TradePrint>> _tradesByAsset = new Dictionary<string, List<TradePrint>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, decimal?> _currentPriceByAsset = new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase);
        private long _version;

        public HeatmapProcessor(decimal tickSize, string databasePath, Logger log)
        {
            _tickSize = tickSize <= 0m ? 0.5m : tickSize;
            _store = new MarketHeatmapSqliteStore(databasePath, log);
        }

        public long Version
        {
            get { return Interlocked.Read(ref _version); }
        }

        public string StorageStatus
        {
            get { return _store.Status; }
        }

        public string DatabasePath
        {
            get { return _store.DatabasePath; }
        }

        public void Start()
        {
            _store.Start();
        }

        public void PostSnapshot(MarketSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Asset))
            {
                return;
            }

            List<HeatmapBookLevel> levels = BuildBookLevels(snapshot);

            lock (_lock)
            {
                _currentPriceByAsset[snapshot.Asset] = snapshot.Ultimo;
                _bookByAsset[snapshot.Asset] = ApplyBookChanges(snapshot.Asset, AggregateBook(levels));
            }

            if (levels.Count > 0)
            {
                _store.EnqueueBookLevels(snapshot.Asset, snapshot.LocalTimestamp, levels);
            }

            Interlocked.Increment(ref _version);
        }

        public void PostTrade(TradePrint trade)
        {
            if (trade == null || string.IsNullOrWhiteSpace(trade.Asset) || trade.Price <= 0m)
            {
                return;
            }

            lock (_lock)
            {
                List<TradePrint> trades;

                if (!_tradesByAsset.TryGetValue(trade.Asset, out trades))
                {
                    trades = new List<TradePrint>();
                    _tradesByAsset[trade.Asset] = trades;
                }

                trades.Insert(0, trade);

                DateTimeOffset cutoff = DateTimeOffset.Now.AddMinutes(-30);
                trades.RemoveAll(x => x.LocalTimestamp < cutoff);

                if (trades.Count > 5000)
                {
                    trades.RemoveRange(5000, trades.Count - 5000);
                }
            }

            _store.EnqueueTrade(trade);
            Interlocked.Increment(ref _version);
        }

        public HeatmapSnapshot GetSnapshot(string asset, decimal? currentPrice, int maxRows)
        {
            HeatmapSnapshot snapshot = new HeatmapSnapshot();
            snapshot.Asset = asset;
            snapshot.LocalTimestamp = DateTimeOffset.Now;
            snapshot.CurrentPrice = currentPrice;
            snapshot.Version = Version;
            snapshot.StorageStatus = StorageStatus;

            if (string.IsNullOrWhiteSpace(asset))
            {
                return snapshot;
            }

            Dictionary<decimal, HeatmapCell> combined = new Dictionary<decimal, HeatmapCell>();

            lock (_lock)
            {
                decimal? storedPrice;

                if (!snapshot.CurrentPrice.HasValue && _currentPriceByAsset.TryGetValue(asset, out storedPrice))
                {
                    snapshot.CurrentPrice = storedPrice;
                }

                Dictionary<decimal, HeatmapCell> book;

                if (_bookByAsset.TryGetValue(asset, out book))
                {
                    foreach (HeatmapCell cell in book.Values)
                    {
                        HeatmapCell target = GetOrCreate(combined, cell.Price);
                        target.BidLiquidity += cell.BidLiquidity;
                        target.AskLiquidity += cell.AskLiquidity;
                        target.BidChange += cell.BidChange;
                        target.AskChange += cell.AskChange;
                    }
                }

                List<TradePrint> trades;

                if (_tradesByAsset.TryGetValue(asset, out trades))
                {
                    DateTimeOffset cutoff = DateTimeOffset.Now.AddMinutes(-15);

                    foreach (TradePrint trade in trades.Where(x => x.LocalTimestamp >= cutoff))
                    {
                        decimal price = Round(trade.Price);
                        HeatmapCell target = GetOrCreate(combined, price);

                        if (trade.Delta > 0m || string.Equals(trade.Aggressor, "Buy", StringComparison.OrdinalIgnoreCase))
                        {
                            target.BuyVolume += trade.Quantity;
                        }
                        else if (trade.Delta < 0m || string.Equals(trade.Aggressor, "Sell", StringComparison.OrdinalIgnoreCase))
                        {
                            target.SellVolume += trade.Quantity;
                        }
                        else
                        {
                            target.NeutralVolume += trade.Quantity;
                        }

                        target.Delta += trade.Delta;
                        snapshot.TradeCount++;
                    }
                }
            }

            List<HeatmapCell> allCells = combined.Values.ToList();

            foreach (HeatmapCell cell in allCells)
            {
                snapshot.TotalBidLiquidity += cell.BidLiquidity;
                snapshot.TotalAskLiquidity += cell.AskLiquidity;
                snapshot.TotalBuyVolume += cell.BuyVolume;
                snapshot.TotalSellVolume += cell.SellVolume;
                snapshot.CumulativeDelta += cell.Delta;
                snapshot.MaxBidLiquidity = Math.Max(snapshot.MaxBidLiquidity, cell.BidLiquidity);
                snapshot.MaxAskLiquidity = Math.Max(snapshot.MaxAskLiquidity, cell.AskLiquidity);
                snapshot.MaxTradeVolume = Math.Max(snapshot.MaxTradeVolume, cell.BuyVolume + cell.SellVolume + cell.NeutralVolume);
                snapshot.MaxStackingScore = Math.Max(snapshot.MaxStackingScore, Math.Max(Math.Max(0m, cell.BidChange), Math.Max(0m, cell.AskChange)));
                snapshot.MaxPullingScore = Math.Max(snapshot.MaxPullingScore, Math.Max(Math.Max(0m, -cell.BidChange), Math.Max(0m, -cell.AskChange)));
            }

            foreach (HeatmapCell cell in allCells)
            {
                ScoreAndClassify(cell, snapshot);
                snapshot.MaxAbsorptionScore = Math.Max(snapshot.MaxAbsorptionScore, cell.AbsorptionScore);
                snapshot.MaxAggressionScore = Math.Max(snapshot.MaxAggressionScore, cell.AggressionScore);
                snapshot.MaxWallScore = Math.Max(snapshot.MaxWallScore, cell.WallScore);
            }

            snapshot.BookLevels = allCells.Count(x => x.BidLiquidity > 0m || x.AskLiquidity > 0m);
            snapshot.Cells = SelectVisibleRows(allCells, snapshot.CurrentPrice, Math.Max(20, maxRows));
            snapshot.InterestCells = SelectInterestRows(allCells, snapshot.CurrentPrice, Math.Max(40, maxRows));
            snapshot.Zones = BuildZones(snapshot.InterestCells, snapshot.CurrentPrice, Math.Max(12, maxRows / 4));
            ApplyDominantRead(snapshot);
            return snapshot;
        }

        public void Dispose()
        {
            _store.Dispose();
        }

        private List<HeatmapBookLevel> BuildBookLevels(MarketSnapshot snapshot)
        {
            List<HeatmapBookLevel> levels = new List<HeatmapBookLevel>();

            for (int index = 0; index <= 49; index++)
            {
                AddBookSide(levels, snapshot, index, BookField("OCP", index), BookField("VOC", index), true);
                AddBookSide(levels, snapshot, index, BookField("OVD", index), BookField("VOV", index), false);
            }

            if (levels.Count == 0)
            {
                AddTopBook(levels, snapshot);
            }

            return levels;
        }

        private void AddTopBook(List<HeatmapBookLevel> levels, MarketSnapshot snapshot)
        {
            if (snapshot.OfertaCompra.HasValue && snapshot.VolumeOfertaCompra.HasValue)
            {
                HeatmapBookLevel level = new HeatmapBookLevel();
                level.Asset = snapshot.Asset;
                level.LocalTimestamp = snapshot.LocalTimestamp;
                level.LevelIndex = 0;
                level.Price = Round(snapshot.OfertaCompra.Value);
                level.BidSize = snapshot.VolumeOfertaCompra.Value;
                levels.Add(level);
            }

            if (snapshot.OfertaVenda.HasValue && snapshot.VolumeOfertaVenda.HasValue)
            {
                HeatmapBookLevel level = new HeatmapBookLevel();
                level.Asset = snapshot.Asset;
                level.LocalTimestamp = snapshot.LocalTimestamp;
                level.LevelIndex = 0;
                level.Price = Round(snapshot.OfertaVenda.Value);
                level.AskSize = snapshot.VolumeOfertaVenda.Value;
                levels.Add(level);
            }
        }

        private void AddBookSide(List<HeatmapBookLevel> levels, MarketSnapshot snapshot, int index, string priceField, string volumeField, bool bid)
        {
            decimal? price = SnapshotDecimal(snapshot, priceField);
            decimal? volume = SnapshotDecimal(snapshot, volumeField);

            if (!price.HasValue || !volume.HasValue || price.Value <= 0m || volume.Value <= 0m)
            {
                return;
            }

            HeatmapBookLevel level = new HeatmapBookLevel();
            level.Asset = snapshot.Asset;
            level.LocalTimestamp = snapshot.LocalTimestamp;
            level.LevelIndex = index;
            level.Price = Round(price.Value);

            if (bid)
            {
                level.BidSize = volume.Value;
            }
            else
            {
                level.AskSize = volume.Value;
            }

            levels.Add(level);
        }

        private Dictionary<decimal, HeatmapCell> AggregateBook(List<HeatmapBookLevel> levels)
        {
            Dictionary<decimal, HeatmapCell> result = new Dictionary<decimal, HeatmapCell>();

            foreach (HeatmapBookLevel level in levels)
            {
                HeatmapCell cell = GetOrCreate(result, level.Price);
                cell.BidLiquidity += level.BidSize;
                cell.AskLiquidity += level.AskSize;
            }

            return result;
        }

        private Dictionary<decimal, HeatmapCell> ApplyBookChanges(string asset, Dictionary<decimal, HeatmapCell> current)
        {
            Dictionary<decimal, HeatmapCell> previous;
            Dictionary<decimal, HeatmapCell> result = new Dictionary<decimal, HeatmapCell>();

            _bookByAsset.TryGetValue(asset, out previous);

            foreach (HeatmapCell cell in current.Values)
            {
                HeatmapCell copy = CopyCell(cell);
                HeatmapCell old;

                if (previous != null && previous.TryGetValue(copy.Price, out old))
                {
                    copy.BidChange = copy.BidLiquidity - old.BidLiquidity;
                    copy.AskChange = copy.AskLiquidity - old.AskLiquidity;
                }
                else
                {
                    copy.BidChange = copy.BidLiquidity;
                    copy.AskChange = copy.AskLiquidity;
                }

                result[copy.Price] = copy;
            }

            if (previous != null)
            {
                foreach (HeatmapCell old in previous.Values)
                {
                    if (result.ContainsKey(old.Price))
                    {
                        continue;
                    }

                    HeatmapCell removed = new HeatmapCell();
                    removed.Price = old.Price;
                    removed.BidChange = -old.BidLiquidity;
                    removed.AskChange = -old.AskLiquidity;
                    result[removed.Price] = removed;
                }
            }

            return result;
        }

        private List<HeatmapCell> SelectVisibleRows(List<HeatmapCell> cells, decimal? currentPrice, int maxRows)
        {
            if (cells.Count <= maxRows)
            {
                return cells.OrderByDescending(x => x.Price).ToList();
            }

            decimal anchor = currentPrice.HasValue
                ? Round(currentPrice.Value)
                : cells.OrderByDescending(x => x.InterestScore).First().Price;

            List<HeatmapCell> centered = cells
                .OrderBy(x => Math.Abs(x.Price - anchor))
                .ThenByDescending(x => x.InterestScore)
                .Take(maxRows)
                .OrderByDescending(x => x.Price)
                .ToList();

            return centered;
        }

        private List<HeatmapCell> SelectInterestRows(List<HeatmapCell> cells, decimal? currentPrice, int maxRows)
        {
            return cells
                .Where(x => x.InterestScore > 0m)
                .OrderByDescending(x => x.InterestScore)
                .ThenBy(x => Math.Abs(x.DistanceTicks))
                .ThenByDescending(x => x.Price)
                .Take(maxRows)
                .ToList();
        }

        private List<HeatmapZone> BuildZones(List<HeatmapCell> cells, decimal? currentPrice, int maxZones)
        {
            List<HeatmapZone> zones = new List<HeatmapZone>();

            if (cells == null || cells.Count == 0)
            {
                return zones;
            }

            List<HeatmapCell> ordered = cells
                .Where(x => x.InterestScore >= 45m && !string.Equals(x.Direction, "Neutro", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Price)
                .ToList();
            List<HeatmapCell> group = new List<HeatmapCell>();

            foreach (HeatmapCell cell in ordered)
            {
                if (group.Count == 0)
                {
                    group.Add(cell);
                    continue;
                }

                HeatmapCell last = group[group.Count - 1];
                bool sameDirection = string.Equals(last.Direction, cell.Direction, StringComparison.OrdinalIgnoreCase);
                bool adjacent = cell.Price - last.Price <= _tickSize * 2m;

                if (sameDirection && adjacent)
                {
                    group.Add(cell);
                }
                else
                {
                    AddZone(zones, group, currentPrice);
                    group = new List<HeatmapCell>();
                    group.Add(cell);
                }
            }

            AddZone(zones, group, currentPrice);

            return zones
                .OrderByDescending(x => x.Score)
                .ThenBy(x => Math.Abs(x.DistanceTicks))
                .Take(maxZones)
                .ToList();
        }

        private void AddZone(List<HeatmapZone> zones, List<HeatmapCell> cells, decimal? currentPrice)
        {
            if (zones == null || cells == null || cells.Count == 0)
            {
                return;
            }

            decimal totalWeight = cells.Sum(x => Math.Max(1m, x.InterestScore));
            decimal weightedPrice = totalWeight <= 0m
                ? cells.Average(x => x.Price)
                : cells.Sum(x => x.Price * Math.Max(1m, x.InterestScore)) / totalWeight;
            HeatmapCell dominant = cells.OrderByDescending(x => x.InterestScore).First();
            HeatmapZone zone = new HeatmapZone();
            zone.LowPrice = cells.Min(x => x.Price);
            zone.HighPrice = cells.Max(x => x.Price);
            zone.CenterPrice = Round(weightedPrice);
            zone.Score = ClampScore(cells.Max(x => x.InterestScore) * 0.72m + cells.Average(x => x.InterestScore) * 0.28m + Math.Min(12m, cells.Count * 2m));
            zone.TotalBidLiquidity = cells.Sum(x => x.BidLiquidity);
            zone.TotalAskLiquidity = cells.Sum(x => x.AskLiquidity);
            zone.BuyVolume = cells.Sum(x => x.BuyVolume);
            zone.SellVolume = cells.Sum(x => x.SellVolume);
            zone.Delta = cells.Sum(x => x.Delta);
            zone.CellCount = cells.Count;
            zone.Direction = dominant.Direction;
            zone.DistanceTicks = currentPrice.HasValue ? (int)Math.Round((double)((zone.CenterPrice - currentPrice.Value) / _tickSize)) : 0;
            zone.Read = ZoneRead(zone.Direction, cells);
            zones.Add(zone);
        }

        private static string ZoneRead(string direction, List<HeatmapCell> cells)
        {
            string side = string.Equals(direction, "Compra", StringComparison.OrdinalIgnoreCase) ? "compra" :
                string.Equals(direction, "Venda", StringComparison.OrdinalIgnoreCase) ? "venda" : "neutra";

            if (cells.Any(x => !string.IsNullOrWhiteSpace(x.Read) && x.Read.IndexOf("absorcao", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return "zona absorcao " + side;
            }

            if (cells.Any(x => !string.IsNullOrWhiteSpace(x.Read) && x.Read.IndexOf("stacking", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return "zona stacking " + side;
            }

            if (cells.Any(x => !string.IsNullOrWhiteSpace(x.Read) && (x.Read.IndexOf("parede", StringComparison.OrdinalIgnoreCase) >= 0 || x.WallScore >= 70m)))
            {
                return "zona parede " + side;
            }

            if (cells.Any(x => !string.IsNullOrWhiteSpace(x.Read) && x.Read.IndexOf("agressao", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return "zona agressao " + side;
            }

            return "zona " + side;
        }

        private void ScoreAndClassify(HeatmapCell cell, HeatmapSnapshot snapshot)
        {
            decimal maxBid = snapshot.MaxBidLiquidity <= 0m ? 1m : snapshot.MaxBidLiquidity;
            decimal maxAsk = snapshot.MaxAskLiquidity <= 0m ? 1m : snapshot.MaxAskLiquidity;
            decimal maxTrade = snapshot.MaxTradeVolume <= 0m ? 1m : snapshot.MaxTradeVolume;
            decimal maxStack = snapshot.MaxStackingScore <= 0m ? 1m : snapshot.MaxStackingScore;
            decimal maxPull = snapshot.MaxPullingScore <= 0m ? 1m : snapshot.MaxPullingScore;
            decimal tradeVolume = cell.BuyVolume + cell.SellVolume + cell.NeutralVolume;
            bool above = snapshot.CurrentPrice.HasValue && cell.Price > snapshot.CurrentPrice.Value;
            bool below = snapshot.CurrentPrice.HasValue && cell.Price < snapshot.CurrentPrice.Value;
            bool strongBid = cell.BidLiquidity > 0m && cell.BidLiquidity >= cell.AskLiquidity * 1.25m;
            bool strongAsk = cell.AskLiquidity > 0m && cell.AskLiquidity >= cell.BidLiquidity * 1.25m;
            decimal bookTotal = cell.BidLiquidity + cell.AskLiquidity;
            decimal maxBookRatio = Math.Max(cell.BidLiquidity / maxBid, cell.AskLiquidity / maxAsk);
            decimal tradeRatio = tradeVolume / maxTrade;
            decimal bidStackRatio = Math.Max(0m, cell.BidChange) / maxStack;
            decimal askStackRatio = Math.Max(0m, cell.AskChange) / maxStack;
            decimal bidPullRatio = Math.Max(0m, -cell.BidChange) / maxPull;
            decimal askPullRatio = Math.Max(0m, -cell.AskChange) / maxPull;
            decimal deltaAbs = tradeVolume <= 0m ? 0m : Math.Abs(cell.Delta) / tradeVolume;
            decimal bidAbsorption = strongBid && cell.SellVolume > cell.BuyVolume && cell.SellVolume > 0m
                ? ClampScore((cell.BidLiquidity / maxBid) * 58m + (cell.SellVolume / maxTrade) * 32m + deltaAbs * 10m)
                : 0m;
            decimal askAbsorption = strongAsk && cell.BuyVolume > cell.SellVolume && cell.BuyVolume > 0m
                ? ClampScore((cell.AskLiquidity / maxAsk) * 58m + (cell.BuyVolume / maxTrade) * 32m + deltaAbs * 10m)
                : 0m;

            cell.NetLiquidity = cell.BidLiquidity - cell.AskLiquidity;
            cell.LiquidityImbalance = bookTotal <= 0m ? 0m : cell.NetLiquidity / bookTotal;
            cell.TradeImbalance = tradeVolume <= 0m ? 0m : cell.Delta / tradeVolume;
            cell.WallScore = ClampScore(maxBookRatio * 100m);
            cell.AbsorptionScore = Math.Max(bidAbsorption, askAbsorption);
            cell.AggressionScore = ClampScore(tradeRatio * deltaAbs * 100m);
            cell.StackingScore = ClampScore(Math.Max(bidStackRatio, askStackRatio) * 100m);
            cell.PullingScore = ClampScore(Math.Max(bidPullRatio, askPullRatio) * 100m);
            cell.DistanceTicks = snapshot.CurrentPrice.HasValue ? (int)Math.Round((double)((cell.Price - snapshot.CurrentPrice.Value) / _tickSize)) : 0;
            cell.InterestScore = ClampScore(
                cell.WallScore * 0.62m +
                tradeRatio * 100m * 0.12m +
                cell.AbsorptionScore * 0.16m +
                cell.AggressionScore * 0.04m +
                Math.Max(cell.StackingScore, cell.PullingScore) * 0.06m);

            if (bidAbsorption >= 55m)
            {
                cell.Direction = "Compra";
                cell.Read = cell.StackingScore >= 50m ? "absorcao compra + stacking" : "absorcao compra";
            }
            else if (askAbsorption >= 55m)
            {
                cell.Direction = "Venda";
                cell.Read = cell.StackingScore >= 50m ? "absorcao venda + stacking" : "absorcao venda";
            }
            else if (tradeVolume > 0m && cell.Delta > tradeVolume * 0.45m)
            {
                cell.Direction = "Compra";
                cell.Read = "agressao compra";
            }
            else if (tradeVolume > 0m && cell.Delta < -tradeVolume * 0.45m)
            {
                cell.Direction = "Venda";
                cell.Read = "agressao venda";
            }
            else if (bidStackRatio >= 0.50m && cell.BidChange > 0m)
            {
                cell.Direction = "Compra";
                cell.Read = "stacking compra";
            }
            else if (askStackRatio >= 0.50m && cell.AskChange > 0m)
            {
                cell.Direction = "Venda";
                cell.Read = "stacking venda";
            }
            else if (bidPullRatio >= 0.50m && cell.BidChange < 0m)
            {
                cell.Direction = "Venda";
                cell.Read = "pulling compra";
            }
            else if (askPullRatio >= 0.50m && cell.AskChange < 0m)
            {
                cell.Direction = "Compra";
                cell.Read = "pulling venda";
            }
            else if (below && strongBid)
            {
                cell.Direction = "Compra";
                cell.Read = "parede compra";
            }
            else if (above && strongAsk)
            {
                cell.Direction = "Venda";
                cell.Read = "parede venda";
            }
            else
            {
                cell.Direction = "Neutro";
                cell.Read = tradeVolume > 0m ? "negocios" : "neutro";
            }
        }

        private void ApplyDominantRead(HeatmapSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            if (snapshot.Zones != null && snapshot.Zones.Count > 0)
            {
                HeatmapZone zone = snapshot.Zones.First();
                snapshot.DominantSide = zone.Direction;
                snapshot.DominantRead = zone.Read + " " + zone.LowPrice.ToString("N1") + "-" + zone.HighPrice.ToString("N1");
                return;
            }

            if (snapshot.InterestCells == null || snapshot.InterestCells.Count == 0)
            {
                return;
            }

            HeatmapCell dominant = snapshot.InterestCells.First();
            snapshot.DominantSide = dominant.Direction;
            snapshot.DominantRead = dominant.Read;
        }

        private static decimal ClampScore(decimal value)
        {
            if (value < 0m)
            {
                return 0m;
            }

            if (value > 100m)
            {
                return 100m;
            }

            return value;
        }

        private static HeatmapCell CopyCell(HeatmapCell source)
        {
            HeatmapCell copy = new HeatmapCell();
            copy.Price = source.Price;
            copy.BidLiquidity = source.BidLiquidity;
            copy.AskLiquidity = source.AskLiquidity;
            copy.BuyVolume = source.BuyVolume;
            copy.SellVolume = source.SellVolume;
            copy.NeutralVolume = source.NeutralVolume;
            copy.Delta = source.Delta;
            copy.BidChange = source.BidChange;
            copy.AskChange = source.AskChange;
            return copy;
        }

        private HeatmapCell GetOrCreate(Dictionary<decimal, HeatmapCell> cells, decimal price)
        {
            HeatmapCell cell;

            if (!cells.TryGetValue(price, out cell))
            {
                cell = new HeatmapCell();
                cell.Price = price;
                cells[price] = cell;
            }

            return cell;
        }

        private decimal Round(decimal price)
        {
            return DomLadderModel.RoundToTick(price, _tickSize);
        }

        private static string BookField(string field, int index)
        {
            return "BOOK_" + field + "_" + index.ToString();
        }

        private static decimal? SnapshotDecimal(MarketSnapshot snapshot, string field)
        {
            if (snapshot == null)
            {
                return null;
            }

            object value;

            if (snapshot.Rtd != null && snapshot.Rtd.TryGetValue(field, out value))
            {
                return ValueParser.ToDecimal(value);
            }

            string raw;

            if (snapshot.Raw != null && snapshot.Raw.TryGetValue(field, out raw))
            {
                return ValueParser.ToDecimal(raw);
            }

            return null;
        }
    }
}
