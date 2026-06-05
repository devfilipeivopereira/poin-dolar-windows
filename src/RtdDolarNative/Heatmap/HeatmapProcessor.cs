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
                _bookByAsset[snapshot.Asset] = AggregateBook(levels);
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

            foreach (HeatmapCell cell in combined.Values)
            {
                cell.InterestScore = cell.BidLiquidity + cell.AskLiquidity + ((cell.BuyVolume + cell.SellVolume + cell.NeutralVolume) * 2m);
                Classify(cell, snapshot.CurrentPrice);
                snapshot.TotalBidLiquidity += cell.BidLiquidity;
                snapshot.TotalAskLiquidity += cell.AskLiquidity;
                snapshot.TotalBuyVolume += cell.BuyVolume;
                snapshot.TotalSellVolume += cell.SellVolume;
                snapshot.CumulativeDelta += cell.Delta;
                snapshot.MaxBidLiquidity = Math.Max(snapshot.MaxBidLiquidity, cell.BidLiquidity);
                snapshot.MaxAskLiquidity = Math.Max(snapshot.MaxAskLiquidity, cell.AskLiquidity);
                snapshot.MaxTradeVolume = Math.Max(snapshot.MaxTradeVolume, cell.BuyVolume + cell.SellVolume + cell.NeutralVolume);
            }

            snapshot.BookLevels = combined.Values.Count(x => x.BidLiquidity > 0m || x.AskLiquidity > 0m);
            snapshot.Cells = SelectVisibleRows(combined.Values.ToList(), snapshot.CurrentPrice, Math.Max(20, maxRows));
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

        private void Classify(HeatmapCell cell, decimal? currentPrice)
        {
            decimal tradeVolume = cell.BuyVolume + cell.SellVolume + cell.NeutralVolume;
            bool above = currentPrice.HasValue && cell.Price > currentPrice.Value;
            bool below = currentPrice.HasValue && cell.Price < currentPrice.Value;
            bool strongBid = cell.BidLiquidity > 0m && cell.BidLiquidity >= cell.AskLiquidity * 1.25m;
            bool strongAsk = cell.AskLiquidity > 0m && cell.AskLiquidity >= cell.BidLiquidity * 1.25m;

            if ((below && strongBid) || (tradeVolume > 0m && cell.Delta > tradeVolume * 0.25m))
            {
                cell.Direction = "Compra";
            }
            else if ((above && strongAsk) || (tradeVolume > 0m && cell.Delta < -tradeVolume * 0.25m))
            {
                cell.Direction = "Venda";
            }
            else
            {
                cell.Direction = "Neutro";
            }

            if (strongBid && cell.SellVolume > cell.BuyVolume && cell.SellVolume > 0m)
            {
                cell.Read = "absorcao compra";
            }
            else if (strongAsk && cell.BuyVolume > cell.SellVolume && cell.BuyVolume > 0m)
            {
                cell.Read = "absorcao venda";
            }
            else if (tradeVolume > 0m && Math.Abs(cell.Delta) >= tradeVolume * 0.5m)
            {
                cell.Read = cell.Delta > 0m ? "agressao compra" : "agressao venda";
            }
            else if (strongBid || strongAsk)
            {
                cell.Read = strongBid ? "liquidez compra" : "liquidez venda";
            }
            else
            {
                cell.Read = tradeVolume > 0m ? "negocios" : "neutro";
            }
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
