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
        private const int HistoricalContextMinutes = 360;
        private const int HistoricalContextRows = 180;
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
                _bookByAsset[snapshot.Asset] = ApplyBookChanges(snapshot.Asset, AggregateBook(levels), snapshot.LocalTimestamp);
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

                DateTimeOffset cutoff = EffectiveTimestamp(trade.LocalTimestamp).AddMinutes(-30);
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
                        target.FirstSeen = target.FirstSeen == DateTimeOffset.MinValue || (cell.FirstSeen != DateTimeOffset.MinValue && cell.FirstSeen < target.FirstSeen) ? cell.FirstSeen : target.FirstSeen;
                        target.LastSeen = cell.LastSeen > target.LastSeen ? cell.LastSeen : target.LastSeen;
                        target.SeenCount = Math.Max(target.SeenCount, cell.SeenCount);
                        target.AgeSeconds = Math.Max(target.AgeSeconds, cell.AgeSeconds);
                    }
                }

                List<TradePrint> trades;

                if (_tradesByAsset.TryGetValue(asset, out trades))
                {
                    DateTimeOffset cutoff = ResolveWindowReferenceTimeLocked(asset).AddMinutes(-15);

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

            MergeHistoricalContext(asset, snapshot, combined);

            List<HeatmapCell> allCells = combined.Values.ToList();

            foreach (HeatmapCell cell in allCells)
            {
                decimal historicalLiquidity = cell.HistoricalBidLiquidity + cell.HistoricalAskLiquidity;
                decimal historicalFlowVolume = cell.HistoricalBuyVolume + cell.HistoricalSellVolume + cell.HistoricalNeutralVolume;

                snapshot.TotalBidLiquidity += cell.BidLiquidity;
                snapshot.TotalAskLiquidity += cell.AskLiquidity;
                snapshot.TotalBuyVolume += cell.BuyVolume;
                snapshot.TotalSellVolume += cell.SellVolume;
                snapshot.CumulativeDelta += cell.Delta;
                snapshot.HistoricalBuyVolume += cell.HistoricalBuyVolume;
                snapshot.HistoricalSellVolume += cell.HistoricalSellVolume;
                snapshot.HistoricalCumulativeDelta += cell.HistoricalDelta;
                snapshot.MaxBidLiquidity = Math.Max(snapshot.MaxBidLiquidity, cell.BidLiquidity);
                snapshot.MaxAskLiquidity = Math.Max(snapshot.MaxAskLiquidity, cell.AskLiquidity);
                snapshot.MaxTradeVolume = Math.Max(snapshot.MaxTradeVolume, cell.BuyVolume + cell.SellVolume + cell.NeutralVolume);
                snapshot.MaxStackingScore = Math.Max(snapshot.MaxStackingScore, Math.Max(Math.Max(0m, cell.BidChange), Math.Max(0m, cell.AskChange)));
                snapshot.MaxPullingScore = Math.Max(snapshot.MaxPullingScore, Math.Max(Math.Max(0m, -cell.BidChange), Math.Max(0m, -cell.AskChange)));

                if (cell.HistoricalSamples > 0)
                {
                    snapshot.HistoricalLevels++;
                    snapshot.MaxHistoricalLiquidity = Math.Max(snapshot.MaxHistoricalLiquidity, historicalLiquidity);
                }

                if (cell.HistoricalTradeSamples > 0)
                {
                    snapshot.HistoricalTradeLevels++;
                    snapshot.MaxHistoricalFlowVolume = Math.Max(snapshot.MaxHistoricalFlowVolume, historicalFlowVolume);
                }
            }

            foreach (HeatmapCell cell in allCells)
            {
                ScoreAndClassify(cell, snapshot);
                snapshot.MaxAbsorptionScore = Math.Max(snapshot.MaxAbsorptionScore, cell.AbsorptionScore);
                snapshot.MaxAggressionScore = Math.Max(snapshot.MaxAggressionScore, cell.AggressionScore);
                snapshot.MaxWallScore = Math.Max(snapshot.MaxWallScore, cell.WallScore);
                snapshot.MaxSpoofRiskScore = Math.Max(snapshot.MaxSpoofRiskScore, cell.SpoofRiskScore);
                snapshot.MaxPersistenceScore = Math.Max(snapshot.MaxPersistenceScore, cell.PersistenceScore);
                snapshot.MaxHistoricalScore = Math.Max(snapshot.MaxHistoricalScore, cell.HistoricalScore);
                snapshot.MaxHistoricalFlowScore = Math.Max(snapshot.MaxHistoricalFlowScore, cell.HistoricalFlowScore);
            }

            snapshot.BookLevels = allCells.Count(x => x.BidLiquidity > 0m || x.AskLiquidity > 0m);
            snapshot.Cells = SelectVisibleRows(allCells, snapshot.CurrentPrice, Math.Max(20, maxRows));
            snapshot.InterestCells = SelectInterestRows(allCells, snapshot.CurrentPrice, Math.Max(40, maxRows));
            snapshot.Zones = BuildZones(snapshot.InterestCells, snapshot.CurrentPrice, Math.Max(12, maxRows / 4));
            snapshot.Bias = BuildBias(snapshot);
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

        private DateTimeOffset ResolveWindowReferenceTimeLocked(string asset)
        {
            DateTimeOffset reference = DateTimeOffset.MinValue;
            List<TradePrint> trades;

            if (!string.IsNullOrWhiteSpace(asset) &&
                _tradesByAsset.TryGetValue(asset, out trades) &&
                trades.Count > 0)
            {
                reference = trades.Max(x => EffectiveTimestamp(x.LocalTimestamp));
            }

            Dictionary<decimal, HeatmapCell> book;

            if (!string.IsNullOrWhiteSpace(asset) &&
                _bookByAsset.TryGetValue(asset, out book) &&
                book.Count > 0)
            {
                DateTimeOffset bookReference = book.Values.Max(x => EffectiveTimestamp(x.LastSeen));

                if (bookReference > reference)
                {
                    reference = bookReference;
                }
            }

            return reference == DateTimeOffset.MinValue ? DateTimeOffset.Now : reference;
        }

        private static DateTimeOffset EffectiveTimestamp(DateTimeOffset timestamp)
        {
            return timestamp == DateTimeOffset.MinValue ? DateTimeOffset.Now : timestamp;
        }

        private void MergeHistoricalContext(string asset, HeatmapSnapshot snapshot, Dictionary<decimal, HeatmapCell> combined)
        {
            List<HeatmapHistoricalLevel> levels = _store.LoadRecentBookContext(asset, DateTimeOffset.Now.AddMinutes(-HistoricalContextMinutes), HistoricalContextRows);

            foreach (HeatmapHistoricalLevel level in levels)
            {
                if (level.Price <= 0m || level.Samples <= 0)
                {
                    continue;
                }

                HeatmapCell cell = GetOrCreate(combined, Round(level.Price));
                cell.HistoricalBidLiquidity += level.BidLiquidity;
                cell.HistoricalAskLiquidity += level.AskLiquidity;
                cell.HistoricalSamples += level.Samples;

                if (level.LastSeen > cell.HistoricalLastSeen)
                {
                    cell.HistoricalLastSeen = level.LastSeen;
                }
            }

            List<HeatmapHistoricalTradeLevel> trades = _store.LoadRecentTradeContext(asset, DateTimeOffset.Now.AddMinutes(-HistoricalContextMinutes), HistoricalContextRows);

            foreach (HeatmapHistoricalTradeLevel trade in trades)
            {
                if (trade.Price <= 0m || trade.Samples <= 0)
                {
                    continue;
                }

                HeatmapCell cell = GetOrCreate(combined, Round(trade.Price));
                cell.HistoricalBuyVolume += trade.BuyVolume;
                cell.HistoricalSellVolume += trade.SellVolume;
                cell.HistoricalNeutralVolume += trade.NeutralVolume;
                cell.HistoricalDelta += trade.Delta;
                cell.HistoricalTradeSamples += trade.Samples;

                if (trade.LastSeen > cell.HistoricalTradeLastSeen)
                {
                    cell.HistoricalTradeLastSeen = trade.LastSeen;
                }
            }

            snapshot.StorageStatus = StorageStatus;
        }

        private Dictionary<decimal, HeatmapCell> ApplyBookChanges(string asset, Dictionary<decimal, HeatmapCell> current, DateTimeOffset timestamp)
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
                    bool oldHadLiquidity = old.BidLiquidity > 0m || old.AskLiquidity > 0m;

                    if (oldHadLiquidity)
                    {
                        copy.FirstSeen = old.FirstSeen == DateTimeOffset.MinValue ? timestamp : old.FirstSeen;
                        copy.SeenCount = Math.Max(1, old.SeenCount) + 1;
                    }
                    else
                    {
                        copy.FirstSeen = timestamp;
                        copy.SeenCount = 1;
                    }
                }
                else
                {
                    copy.BidChange = copy.BidLiquidity;
                    copy.AskChange = copy.AskLiquidity;
                    copy.FirstSeen = timestamp;
                    copy.SeenCount = 1;
                }

                copy.LastSeen = timestamp;
                copy.AgeSeconds = Math.Max(0d, (copy.LastSeen - copy.FirstSeen).TotalSeconds);
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
                    removed.FirstSeen = old.FirstSeen;
                    removed.LastSeen = timestamp;
                    removed.SeenCount = old.SeenCount;
                    removed.AgeSeconds = old.FirstSeen == DateTimeOffset.MinValue ? 0d : Math.Max(0d, (timestamp - old.FirstSeen).TotalSeconds);
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
            zone.PersistenceScore = cells.Max(x => x.PersistenceScore);
            zone.HistoricalScore = cells.Max(x => x.HistoricalScore);
            zone.HistoricalSamples = cells.Sum(x => x.HistoricalSamples);
            zone.HistoricalFlowScore = cells.Max(x => x.HistoricalFlowScore);
            zone.HistoricalTradeSamples = cells.Sum(x => x.HistoricalTradeSamples);
            zone.HistoricalDelta = cells.Sum(x => x.HistoricalDelta);
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

            if (cells.Any(x => x.SpoofRiskScore >= 70m || (!string.IsNullOrWhiteSpace(x.Read) && x.Read.IndexOf("retirada", StringComparison.OrdinalIgnoreCase) >= 0)))
            {
                return "zona retirada " + side;
            }

            if (cells.Any(x => x.PersistenceScore >= 70m || (!string.IsNullOrWhiteSpace(x.Read) && x.Read.IndexOf("persistente", StringComparison.OrdinalIgnoreCase) >= 0)))
            {
                return "zona persistente " + side;
            }

            if (cells.Any(x => x.HistoricalFlowScore >= 70m || (!string.IsNullOrWhiteSpace(x.Read) && x.Read.IndexOf("fluxo historico", StringComparison.OrdinalIgnoreCase) >= 0)))
            {
                return "zona fluxo historico " + side;
            }

            if (cells.Any(x => x.HistoricalScore >= 70m || (!string.IsNullOrWhiteSpace(x.Read) && x.Read.IndexOf("historico", StringComparison.OrdinalIgnoreCase) >= 0)))
            {
                return "zona historica " + side;
            }

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
            decimal maxHistorical = snapshot.MaxHistoricalLiquidity <= 0m ? 1m : snapshot.MaxHistoricalLiquidity;
            decimal maxHistoricalFlow = snapshot.MaxHistoricalFlowVolume <= 0m ? 1m : snapshot.MaxHistoricalFlowVolume;
            decimal tradeVolume = cell.BuyVolume + cell.SellVolume + cell.NeutralVolume;
            bool above = snapshot.CurrentPrice.HasValue && cell.Price > snapshot.CurrentPrice.Value;
            bool below = snapshot.CurrentPrice.HasValue && cell.Price < snapshot.CurrentPrice.Value;
            bool strongBid = cell.BidLiquidity > 0m && cell.BidLiquidity >= cell.AskLiquidity * 1.25m;
            bool strongAsk = cell.AskLiquidity > 0m && cell.AskLiquidity >= cell.BidLiquidity * 1.25m;
            bool historicalBid = cell.HistoricalBidLiquidity > 0m && cell.HistoricalBidLiquidity >= cell.HistoricalAskLiquidity * 1.15m;
            bool historicalAsk = cell.HistoricalAskLiquidity > 0m && cell.HistoricalAskLiquidity >= cell.HistoricalBidLiquidity * 1.15m;
            decimal bookTotal = cell.BidLiquidity + cell.AskLiquidity;
            decimal historicalTotal = cell.HistoricalBidLiquidity + cell.HistoricalAskLiquidity;
            decimal historicalFlowTotal = cell.HistoricalBuyVolume + cell.HistoricalSellVolume + cell.HistoricalNeutralVolume;
            decimal maxBookRatio = Math.Max(cell.BidLiquidity / maxBid, cell.AskLiquidity / maxAsk);
            decimal tradeRatio = tradeVolume / maxTrade;
            decimal historicalRatio = historicalTotal / maxHistorical;
            decimal historicalFlowRatio = historicalFlowTotal / maxHistoricalFlow;
            decimal bidStackRatio = Math.Max(0m, cell.BidChange) / maxStack;
            decimal askStackRatio = Math.Max(0m, cell.AskChange) / maxStack;
            decimal bidPullRatio = Math.Max(0m, -cell.BidChange) / maxPull;
            decimal askPullRatio = Math.Max(0m, -cell.AskChange) / maxPull;
            decimal deltaAbs = tradeVolume <= 0m ? 0m : Math.Abs(cell.Delta) / tradeVolume;
            decimal historicalDeltaAbs = historicalFlowTotal <= 0m ? 0m : Math.Abs(cell.HistoricalDelta) / historicalFlowTotal;
            bool bidPulled = cell.BidChange < 0m && bidPullRatio >= 0.50m;
            bool askPulled = cell.AskChange < 0m && askPullRatio >= 0.50m;
            decimal ageScore = ClampScore((decimal)Math.Min(1d, cell.AgeSeconds / 6d) * 55m);
            decimal countScore = ClampScore(Math.Min(45m, Math.Max(0, cell.SeenCount - 1) * 15m));
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
            cell.SpoofRiskScore = (bidPulled || askPulled)
                ? ClampScore(cell.PullingScore * 0.74m + (tradeVolume <= 0m ? 26m : Math.Max(0m, 16m - tradeRatio * 16m)))
                : 0m;
            cell.PersistenceScore = (cell.BidLiquidity > 0m || cell.AskLiquidity > 0m) ? ClampScore(ageScore + countScore) : 0m;
            double historicalAge = 0d;
            decimal historicalFreshness = 0m;
            double historicalTradeAge = 0d;
            decimal historicalFlowFreshness = 0m;
            decimal historicalFreshnessFactor = cell.HistoricalSamples > 0
                ? HistoricalFreshnessFactor(cell.HistoricalLastSeen, snapshot.LocalTimestamp, out historicalAge, out historicalFreshness)
                : 0m;
            decimal historicalFlowFreshnessFactor = cell.HistoricalTradeSamples > 0
                ? HistoricalFreshnessFactor(cell.HistoricalTradeLastSeen, snapshot.LocalTimestamp, out historicalTradeAge, out historicalFlowFreshness)
                : 0m;
            cell.HistoricalScore = cell.HistoricalSamples > 0
                ? ClampScore((historicalRatio * 70m + Math.Min(30m, cell.HistoricalSamples * 6m)) * historicalFreshnessFactor)
                : 0m;
            cell.HistoricalAgeMinutes = cell.HistoricalSamples > 0 ? historicalAge : 0d;
            cell.HistoricalFreshnessScore = cell.HistoricalSamples > 0 ? historicalFreshness : 0m;
            cell.HistoricalFlowScore = cell.HistoricalTradeSamples > 0
                ? ClampScore((historicalFlowRatio * 60m + historicalDeltaAbs * 25m + Math.Min(15m, cell.HistoricalTradeSamples * 3m)) * historicalFlowFreshnessFactor)
                : 0m;
            cell.HistoricalTradeAgeMinutes = cell.HistoricalTradeSamples > 0 ? historicalTradeAge : 0d;
            cell.HistoricalFlowFreshnessScore = cell.HistoricalTradeSamples > 0 ? historicalFlowFreshness : 0m;
            cell.DistanceTicks = snapshot.CurrentPrice.HasValue ? (int)Math.Round((double)((cell.Price - snapshot.CurrentPrice.Value) / _tickSize)) : 0;
            decimal baseInterest = ClampScore(
                cell.WallScore * 0.62m +
                tradeRatio * 100m * 0.12m +
                cell.AbsorptionScore * 0.16m +
                cell.AggressionScore * 0.04m +
                Math.Max(cell.StackingScore, cell.PullingScore) * 0.06m +
                cell.PersistenceScore * 0.10m +
                cell.HistoricalScore * 0.08m +
                cell.HistoricalFlowScore * 0.10m);
            decimal removalInterest = ClampScore(cell.SpoofRiskScore * 0.72m + cell.PullingScore * 0.28m);
            decimal historicalInterest = ClampScore(cell.HistoricalScore * 0.70m);
            decimal historicalFlowInterest = ClampScore(cell.HistoricalFlowScore * 0.62m);
            cell.InterestScore = Math.Max(Math.Max(Math.Max(baseInterest, removalInterest), historicalInterest), historicalFlowInterest);

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
            else if (cell.SpoofRiskScore >= 70m && bidPulled)
            {
                cell.Direction = "Venda";
                cell.Read = "retirada compra / spoof";
            }
            else if (cell.SpoofRiskScore >= 70m && askPulled)
            {
                cell.Direction = "Compra";
                cell.Read = "retirada venda / spoof";
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
            else if (cell.HistoricalFlowScore >= 60m && cell.HistoricalDelta > 0m)
            {
                cell.Direction = "Compra";
                cell.Read = "fluxo historico compra SQL";
            }
            else if (cell.HistoricalFlowScore >= 60m && cell.HistoricalDelta < 0m)
            {
                cell.Direction = "Venda";
                cell.Read = "fluxo historico venda SQL";
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
            else if (cell.PersistenceScore >= 70m && below && strongBid)
            {
                cell.Direction = "Compra";
                cell.Read = "parede compra persistente";
            }
            else if (cell.PersistenceScore >= 70m && above && strongAsk)
            {
                cell.Direction = "Venda";
                cell.Read = "parede venda persistente";
            }
            else if (cell.HistoricalScore >= 60m && below && historicalBid)
            {
                cell.Direction = "Compra";
                cell.Read = "historico compra SQL";
            }
            else if (cell.HistoricalScore >= 60m && above && historicalAsk)
            {
                cell.Direction = "Venda";
                cell.Read = "historico venda SQL";
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

            if (cell.HistoricalScore >= 60m && !string.IsNullOrWhiteSpace(cell.Read) && cell.Read.IndexOf("historico", StringComparison.OrdinalIgnoreCase) < 0)
            {
                cell.Read += " + historico";
            }

            if (cell.HistoricalFlowScore >= 60m && !string.IsNullOrWhiteSpace(cell.Read) && cell.Read.IndexOf("fluxo historico", StringComparison.OrdinalIgnoreCase) < 0)
            {
                cell.Read += " + fluxo historico";
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

        private HeatmapBias BuildBias(HeatmapSnapshot snapshot)
        {
            HeatmapBias bias = new HeatmapBias();
            List<string> reasons = new List<string>();

            if (snapshot == null)
            {
                bias.Direction = "Neutro";
                bias.Read = "sem dados";
                return bias;
            }

            decimal score = 0m;
            decimal bookTotal = snapshot.TotalBidLiquidity + snapshot.TotalAskLiquidity;

            if (bookTotal > 0m)
            {
                decimal bookPressure = ClampSigned((snapshot.TotalBidLiquidity - snapshot.TotalAskLiquidity) / bookTotal * 22m);
                score += bookPressure;
                AddBiasReason(reasons, "book", bookPressure);
            }

            decimal tradeTotal = snapshot.TotalBuyVolume + snapshot.TotalSellVolume;

            if (tradeTotal > 0m)
            {
                decimal flowPressure = ClampSigned(snapshot.CumulativeDelta / tradeTotal * 24m);
                score += flowPressure;
                AddBiasReason(reasons, "delta", flowPressure);
            }

            if (snapshot.Zones != null)
            {
                foreach (HeatmapZone zone in snapshot.Zones.Take(5))
                {
                    decimal sign = DirectionSign(zone.Direction);

                    if (sign == 0m)
                    {
                        continue;
                    }

                    decimal distanceFactor = 1m / (1m + Math.Abs(zone.DistanceTicks) / 24m);
                    decimal zonePressure = sign * zone.Score * distanceFactor * 0.22m;
                    score += zonePressure;
                    AddBiasReason(reasons, zone.Read, zonePressure);
                }
            }

            if (snapshot.InterestCells != null)
            {
                foreach (HeatmapCell cell in snapshot.InterestCells.Take(12))
                {
                    decimal sign = DirectionSign(cell.Direction);

                    if (sign == 0m)
                    {
                        continue;
                    }

                    decimal distanceFactor = 1m / (1m + Math.Abs(cell.DistanceTicks) / 28m);
                    decimal signalScore = Math.Max(Math.Max(cell.AbsorptionScore, cell.SpoofRiskScore), Math.Max(Math.Max(cell.PersistenceScore, cell.AggressionScore), Math.Max(cell.HistoricalScore, cell.HistoricalFlowScore)));
                    decimal signalWeight = cell.AbsorptionScore >= 55m || cell.SpoofRiskScore >= 55m ? 0.36m : 0.10m;
                    decimal signalPressure = sign * signalScore * distanceFactor * signalWeight;
                    score += signalPressure;

                    if (signalScore >= 55m)
                    {
                        AddBiasReason(reasons, BiasSignalLabel(cell), signalPressure);
                    }
                }
            }

            bias.Score = ClampSigned(score);
            bias.Confidence = ClampScore(Math.Abs(bias.Score) + Math.Min(24m, reasons.Count * 4m));

            if (bias.Score > 12m)
            {
                bias.Direction = "Compra";
            }
            else if (bias.Score < -12m)
            {
                bias.Direction = "Venda";
            }
            else
            {
                bias.Direction = "Neutro";
            }

            List<string> visibleReasons = reasons
                .OrderBy(x => BiasReasonRank(x))
                .Take(4)
                .ToList();
            bias.Reasons = visibleReasons.Count == 0 ? "-" : string.Join(" | ", visibleReasons.ToArray());
            bias.Read = bias.Direction + " " + bias.Score.ToString("N0") + " | conf " + bias.Confidence.ToString("N0");
            return bias;
        }

        private static int BiasReasonRank(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return 9;
            }

            if (reason.IndexOf("absorcao", StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason.IndexOf("spoof", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 0;
            }

            if (reason.IndexOf("historico", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 1;
            }

            if (reason.IndexOf("persistencia", StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason.IndexOf("agressao", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 2;
            }

            if (reason.IndexOf("zona", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 3;
            }

            return 4;
        }

        private static string BiasSignalLabel(HeatmapCell cell)
        {
            string side = string.IsNullOrWhiteSpace(cell.Direction) ? string.Empty : cell.Direction.ToLowerInvariant();

            if (cell.AbsorptionScore >= 55m)
            {
                return "absorcao " + side;
            }

            if (cell.SpoofRiskScore >= 55m)
            {
                return "spoof " + side;
            }

            if (cell.HistoricalScore >= 65m)
            {
                return "historico " + side;
            }

            if (cell.HistoricalFlowScore >= 65m)
            {
                return "fluxo historico " + side;
            }

            if (cell.PersistenceScore >= 55m)
            {
                return "persistencia " + side;
            }

            if (cell.AggressionScore >= 55m)
            {
                return "agressao " + side;
            }

            return cell.Read;
        }

        private static void AddBiasReason(List<string> reasons, string label, decimal contribution)
        {
            if (reasons == null || string.IsNullOrWhiteSpace(label) || Math.Abs(contribution) < 5m)
            {
                return;
            }

            string sign = contribution > 0m ? "+" : string.Empty;
            reasons.Add(label + " " + sign + contribution.ToString("N0"));
        }

        private static decimal DirectionSign(string direction)
        {
            if (string.Equals(direction, "Compra", StringComparison.OrdinalIgnoreCase))
            {
                return 1m;
            }

            if (string.Equals(direction, "Venda", StringComparison.OrdinalIgnoreCase))
            {
                return -1m;
            }

            return 0m;
        }

        private static decimal ClampSigned(decimal value)
        {
            if (value < -100m)
            {
                return -100m;
            }

            if (value > 100m)
            {
                return 100m;
            }

            return value;
        }

        private static decimal HistoricalFreshnessFactor(DateTimeOffset lastSeen, DateTimeOffset reference, out double ageMinutes, out decimal freshnessScore)
        {
            if (lastSeen == DateTimeOffset.MinValue)
            {
                ageMinutes = HistoricalContextMinutes;
                freshnessScore = 0m;
                return 0m;
            }

            DateTimeOffset effectiveReference = reference == DateTimeOffset.MinValue ? DateTimeOffset.Now : reference;
            ageMinutes = Math.Max(0d, (effectiveReference - lastSeen).TotalMinutes);
            double freshnessRatio = Math.Max(0d, 1d - ageMinutes / HistoricalContextMinutes);
            decimal factor = 0.25m + (decimal)freshnessRatio * 0.75m;
            freshnessScore = ClampScore(factor * 100m);
            return factor;
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
            copy.PersistenceScore = source.PersistenceScore;
            copy.HistoricalBidLiquidity = source.HistoricalBidLiquidity;
            copy.HistoricalAskLiquidity = source.HistoricalAskLiquidity;
            copy.HistoricalSamples = source.HistoricalSamples;
            copy.HistoricalScore = source.HistoricalScore;
            copy.HistoricalLastSeen = source.HistoricalLastSeen;
            copy.HistoricalBuyVolume = source.HistoricalBuyVolume;
            copy.HistoricalSellVolume = source.HistoricalSellVolume;
            copy.HistoricalNeutralVolume = source.HistoricalNeutralVolume;
            copy.HistoricalDelta = source.HistoricalDelta;
            copy.HistoricalTradeSamples = source.HistoricalTradeSamples;
            copy.HistoricalFlowScore = source.HistoricalFlowScore;
            copy.HistoricalTradeLastSeen = source.HistoricalTradeLastSeen;
            copy.SeenCount = source.SeenCount;
            copy.AgeSeconds = source.AgeSeconds;
            copy.FirstSeen = source.FirstSeen;
            copy.LastSeen = source.LastSeen;
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
