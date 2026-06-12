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
        private const int DefaultHistoricalContextMinutes = 360;
        private const int DefaultHistoricalContextRows = 180;
        private readonly object _lock = new object();
        private readonly decimal _tickSize;
        private readonly MarketHeatmapSqliteStore _store;
        private readonly Dictionary<string, Dictionary<decimal, HeatmapCell>> _bookByAsset = new Dictionary<string, Dictionary<decimal, HeatmapCell>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<TradePrint>> _tradesByAsset = new Dictionary<string, List<TradePrint>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, decimal?> _currentPriceByAsset = new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase);
        private int _historicalContextMinutes;
        private int _historicalContextRows;
        private long _version;

        public HeatmapProcessor(decimal tickSize, string databasePath, Logger log)
        {
            _tickSize = tickSize <= 0m ? 0.5m : tickSize;
            _store = new MarketHeatmapSqliteStore(databasePath, log);
            UseHistoricalContext = true;
            HistoricalContextMinutes = DefaultHistoricalContextMinutes;
            HistoricalContextRows = DefaultHistoricalContextRows;
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

        public bool UseHistoricalContext { get; set; }

        public int HistoricalContextMinutes
        {
            get { return _historicalContextMinutes; }
            set { _historicalContextMinutes = Math.Max(5, Math.Min(1440, value)); }
        }

        public int HistoricalContextRows
        {
            get { return _historicalContextRows; }
            set { _historicalContextRows = Math.Max(20, Math.Min(2000, value)); }
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
            return GetSnapshot(asset, currentPrice, maxRows, null);
        }

        public HeatmapSnapshot GetSnapshot(string asset, decimal? currentPrice, int maxRows, decimal? viewportAnchorPrice)
        {
            HeatmapSnapshot snapshot = new HeatmapSnapshot();
            snapshot.Asset = asset;
            snapshot.LocalTimestamp = DateTimeOffset.Now;
            snapshot.CurrentPrice = currentPrice;
            snapshot.Version = Version;
            snapshot.StorageStatus = StorageStatus;
            snapshot.UseHistoricalContext = UseHistoricalContext;
            snapshot.HistoricalContextMinutes = HistoricalContextMinutes;
            snapshot.ViewportMode = viewportAnchorPrice.HasValue ? "Manual" : "Auto";
            snapshot.ViewportAnchorPrice = viewportAnchorPrice.HasValue ? Round(viewportAnchorPrice.Value) : (decimal?)null;

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

            if (UseHistoricalContext)
            {
                MergeHistoricalContext(asset, snapshot, combined);
            }
            else
            {
                snapshot.StorageStatus = "sqlite desligado | " + StorageStatus;
            }

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
                snapshot.MaxConfluenceScore = Math.Max(snapshot.MaxConfluenceScore, cell.ConfluenceScore);
                snapshot.MaxConflictScore = Math.Max(snapshot.MaxConflictScore, cell.ConflictScore);
                snapshot.MaxConfidenceScore = Math.Max(snapshot.MaxConfidenceScore, cell.ConfidenceScore);
            }

            snapshot.BookLevels = allCells.Count(x => x.BidLiquidity > 0m || x.AskLiquidity > 0m);
            snapshot.TotalPriceLevels = allCells.Count;
            snapshot.Cells = SelectVisibleRows(allCells, snapshot.CurrentPrice, Math.Max(20, maxRows), snapshot.ViewportAnchorPrice);
            if (snapshot.Cells.Count > 0)
            {
                snapshot.VisibleTopPrice = snapshot.Cells.Max(x => x.Price);
                snapshot.VisibleBottomPrice = snapshot.Cells.Min(x => x.Price);
                if (!snapshot.ViewportAnchorPrice.HasValue)
                {
                    snapshot.ViewportAnchorPrice = snapshot.CurrentPrice.HasValue
                        ? Round(snapshot.CurrentPrice.Value)
                        : snapshot.Cells.OrderByDescending(x => x.InterestScore).First().Price;
                }
            }
            snapshot.InterestCells = SelectInterestRows(allCells, snapshot.CurrentPrice, Math.Max(40, maxRows));
            snapshot.Zones = BuildZones(snapshot.InterestCells, snapshot.CurrentPrice, Math.Max(12, maxRows / 4));
            snapshot.SqlMemory = BuildSqlMemory(snapshot);
            snapshot.Corridor = BuildCorridor(snapshot);
            snapshot.Bias = BuildBias(snapshot);
            snapshot.Plan = BuildOperationalPlan(snapshot);
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
            int minutes = HistoricalContextMinutes;
            int rows = HistoricalContextRows;
            DateTimeOffset since = DateTimeOffset.Now.AddMinutes(-minutes);
            List<HeatmapHistoricalLevel> levels = _store.LoadRecentBookContext(asset, since, rows);

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

            List<HeatmapHistoricalTradeLevel> trades = _store.LoadRecentTradeContext(asset, since, rows);

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

            snapshot.StorageStatus = StorageStatus + " | hist " + FormatHistoricalWindow(minutes);
        }

        private static string FormatHistoricalWindow(int minutes)
        {
            if (minutes < 60)
            {
                return minutes.ToString() + "m";
            }

            if (minutes % 60 == 0)
            {
                return (minutes / 60).ToString() + "h";
            }

            return (minutes / 60m).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "h";
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

        private List<HeatmapCell> SelectVisibleRows(List<HeatmapCell> cells, decimal? currentPrice, int maxRows, decimal? viewportAnchorPrice)
        {
            if (cells.Count <= maxRows)
            {
                return cells.OrderByDescending(x => x.Price).ToList();
            }

            decimal anchor = viewportAnchorPrice.HasValue
                ? viewportAnchorPrice.Value
                : currentPrice.HasValue
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
                .ThenBy(x => AbsTicks(x.DistanceTicks))
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
                .ThenBy(x => AbsTicks(x.DistanceTicks))
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
            zone.ConfluenceScore = cells.Max(x => x.ConfluenceScore);
            zone.ConflictScore = cells.Max(x => x.ConflictScore);
            zone.ConfidenceScore = cells.Max(x => x.ConfidenceScore);
            zone.SignalCount = cells.Sum(x => x.SignalCount);
            zone.Quality = dominant.Quality;
            zone.CellCount = cells.Count;
            zone.Direction = dominant.Direction;
            zone.DistanceTicks = PriceDistanceTicks(zone.CenterPrice, currentPrice);
            zone.Read = ZoneRead(zone.Direction, cells);
            ApplyZoneAction(zone);
            zones.Add(zone);
        }

        private static void ApplyZoneAction(HeatmapZone zone)
        {
            if (zone == null)
            {
                return;
            }

            int absDistance = AbsTicks(zone.DistanceTicks);
            decimal distanceBonus = absDistance <= 2 ? 18m : absDistance <= 5 ? 10m : absDistance <= 10 ? 4m : 0m;
            decimal distancePenalty = Math.Min(34m, Math.Max(0, absDistance - 2) * 2.2m);
            decimal qualityBonus = string.Equals(zone.Quality, "Alta", StringComparison.OrdinalIgnoreCase) ? 10m :
                string.Equals(zone.Quality, "Media", StringComparison.OrdinalIgnoreCase) ? 4m : 0m;
            decimal rawScore =
                zone.Score * 0.38m +
                zone.ConfidenceScore * 0.30m +
                zone.ConfluenceScore * 0.14m +
                zone.PersistenceScore * 0.08m +
                Math.Max(zone.HistoricalScore, zone.HistoricalFlowScore) * 0.06m +
                qualityBonus +
                distanceBonus -
                zone.ConflictScore * 0.68m -
                distancePenalty;

            zone.ActionScore = ClampScore(rawScore);

            if (zone.ConflictScore >= 40m ||
                string.Equals(zone.Quality, "Conflito", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(zone.Read) && zone.Read.IndexOf("conflito", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                zone.Action = "Aguardar";
                zone.ActionScore = Math.Min(zone.ActionScore, 35m);
                zone.ActionRead = "conflito entre sinais; esperar confirmacao";
                return;
            }

            if (DirectionSign(zone.Direction) == 0m)
            {
                zone.Action = "Observar";
                zone.ActionRead = "zona neutra; sem lado claro";
                return;
            }

            string side = string.Equals(zone.Direction, "Compra", StringComparison.OrdinalIgnoreCase) ? "compra" : "venda";

            if (zone.ActionScore >= 70m && absDistance <= 5)
            {
                zone.Action = zone.Direction + " defesa";
                zone.ActionRead = "zona perto; defesa de " + side + " com confirmacao";
                return;
            }

            if (zone.ActionScore >= 58m)
            {
                zone.Action = "Monitorar " + side;
                zone.ActionRead = absDistance <= 10 ? "zona proxima; esperar gatilho" : "zona forte distante; preparar contexto";
                return;
            }

            zone.Action = "Observar";
            zone.ActionRead = absDistance <= 10 ? "zona perto sem confirmacao suficiente" : "zona distante ou fraca";
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

        private HeatmapCorridor BuildCorridor(HeatmapSnapshot snapshot)
        {
            HeatmapCorridor corridor = new HeatmapCorridor();

            if (snapshot == null || !snapshot.CurrentPrice.HasValue || snapshot.Zones == null || snapshot.Zones.Count == 0)
            {
                corridor.Read = "sem corredor operacional";
                return corridor;
            }

            decimal current = snapshot.CurrentPrice.Value;
            HeatmapZone support = snapshot.Zones
                .Where(x => string.Equals(x.Direction, "Compra", StringComparison.OrdinalIgnoreCase) && x.CenterPrice <= current)
                .OrderBy(x => Math.Abs(x.CenterPrice - current))
                .ThenByDescending(x => x.ActionScore)
                .FirstOrDefault();
            HeatmapZone resistance = snapshot.Zones
                .Where(x => string.Equals(x.Direction, "Venda", StringComparison.OrdinalIgnoreCase) && x.CenterPrice >= current)
                .OrderBy(x => Math.Abs(x.CenterPrice - current))
                .ThenByDescending(x => x.ActionScore)
                .FirstOrDefault();

            if (support == null || resistance == null || resistance.CenterPrice <= support.CenterPrice)
            {
                corridor.Read = "sem corredor operacional";
                return corridor;
            }

            decimal width = resistance.CenterPrice - support.CenterPrice;
            int ticks = Math.Max(1, (int)Math.Round((double)(width / _tickSize)));
            decimal positionPct = width <= 0m ? 0m : ClampScore((current - support.CenterPrice) / width * 100m);
            decimal scoreDiff = support.ActionScore - resistance.ActionScore;

            corridor.IsAvailable = true;
            corridor.SupportPrice = support.CenterPrice;
            corridor.ResistancePrice = resistance.CenterPrice;
            corridor.WidthTicks = ticks;
            corridor.CurrentPositionPct = positionPct;
            corridor.SupportActionScore = support.ActionScore;
            corridor.ResistanceActionScore = resistance.ActionScore;
            corridor.Bias = scoreDiff > 12m ? "Compra" : scoreDiff < -12m ? "Venda" : "Neutro";
            corridor.Phase = CorridorPhase(ticks);
            corridor.Location = CorridorLocation(positionPct);
            corridor.Read = "corredor " + ticks.ToString() + "t " + corridor.Phase.ToLowerInvariant() + " | " + corridor.Location.ToLowerInvariant() + " " + positionPct.ToString("N0") + "% | " + corridor.Bias.ToLowerInvariant();
            return corridor;
        }

        private static string CorridorPhase(int widthTicks)
        {
            if (widthTicks <= 6)
            {
                return "Comprimido";
            }

            if (widthTicks >= 14)
            {
                return "Amplo";
            }

            return "Equilibrado";
        }

        private static string CorridorLocation(decimal positionPct)
        {
            if (positionPct <= 35m)
            {
                return "Perto suporte";
            }

            if (positionPct >= 65m)
            {
                return "Perto resistencia";
            }

            return "Meio";
        }

        private HeatmapSqlMemory BuildSqlMemory(HeatmapSnapshot snapshot)
        {
            HeatmapSqlMemory memory = new HeatmapSqlMemory();

            if (snapshot == null || !snapshot.UseHistoricalContext)
            {
                memory.Read = "SQL desligado";
                return memory;
            }

            List<HeatmapCell> source = snapshot.InterestCells != null && snapshot.InterestCells.Count > 0
                ? snapshot.InterestCells
                : snapshot.Cells;

            if (source == null || source.Count == 0)
            {
                memory.Read = "sem memoria SQL";
                return memory;
            }

            List<HeatmapCell> sqlCells = source
                .Where(x => x.HistoricalSamples > 0 || x.HistoricalTradeSamples > 0)
                .Where(x => SqlSignalScore(x) >= 25m)
                .ToList();

            memory.BookLevels = sqlCells.Count(x => x.HistoricalSamples > 0);
            memory.FlowLevels = sqlCells.Count(x => x.HistoricalTradeSamples > 0);

            if (sqlCells.Count == 0)
            {
                memory.Read = "sem memoria SQL relevante";
                return memory;
            }

            decimal signedPressure = 0m;
            decimal totalPressure = 0m;

            foreach (HeatmapCell cell in sqlCells)
            {
                decimal sign = SqlSignalSign(cell);

                if (sign == 0m)
                {
                    continue;
                }

                decimal distanceWeight = 1m / (1m + AbsTicks(cell.DistanceTicks) / 24m);
                decimal weighted = SqlSignalScore(cell) * distanceWeight;
                signedPressure += sign * weighted;
                totalPressure += Math.Abs(weighted);
            }

            memory.PressureScore = totalPressure <= 0m ? 0m : ClampSigned(signedPressure / totalPressure * 100m);
            memory.Direction = memory.PressureScore > 12m ? "Compra" : memory.PressureScore < -12m ? "Venda" : "Neutro";

            HeatmapCell support = null;
            HeatmapCell resistance = null;

            if (snapshot.CurrentPrice.HasValue)
            {
                decimal current = snapshot.CurrentPrice.Value;
                support = sqlCells
                    .Where(x => x.Price <= current && SqlSignalSign(x) > 0m)
                    .OrderBy(x => Math.Abs(x.Price - current))
                    .ThenByDescending(SqlSignalScore)
                    .FirstOrDefault();
                resistance = sqlCells
                    .Where(x => x.Price >= current && SqlSignalSign(x) < 0m)
                    .OrderBy(x => Math.Abs(x.Price - current))
                    .ThenByDescending(SqlSignalScore)
                    .FirstOrDefault();
            }

            if (support == null)
            {
                support = sqlCells
                    .Where(x => SqlSignalSign(x) > 0m)
                    .OrderBy(x => AbsTicks(x.DistanceTicks))
                    .ThenByDescending(SqlSignalScore)
                    .FirstOrDefault();
            }

            if (resistance == null)
            {
                resistance = sqlCells
                    .Where(x => SqlSignalSign(x) < 0m)
                    .OrderBy(x => AbsTicks(x.DistanceTicks))
                    .ThenByDescending(SqlSignalScore)
                    .FirstOrDefault();
            }

            if (support != null)
            {
                memory.SupportPrice = support.Price;
                memory.SupportDistanceTicks = support.DistanceTicks;
                memory.SupportScore = SqlSignalScore(support);
            }

            if (resistance != null)
            {
                memory.ResistancePrice = resistance.Price;
                memory.ResistanceDistanceTicks = resistance.DistanceTicks;
                memory.ResistanceScore = SqlSignalScore(resistance);
            }

            decimal topScore = sqlCells.Max(x => SqlSignalScore(x));
            memory.ConfidenceScore = ClampScore(Math.Abs(memory.PressureScore) * 0.54m + topScore * 0.28m + Math.Min(18m, sqlCells.Count * 3m));
            memory.IsAvailable = memory.SupportPrice.HasValue || memory.ResistancePrice.HasValue;
            memory.Read = BuildSqlMemoryRead(memory);
            return memory;
        }

        private static string BuildSqlMemoryRead(HeatmapSqlMemory memory)
        {
            if (memory == null || !memory.IsAvailable)
            {
                return "sem memoria SQL operacional";
            }

            string support = memory.SupportPrice.HasValue
                ? "sup " + memory.SupportPrice.Value.ToString("N2") + " " + memory.SupportDistanceTicks.ToString("+0;-0;0") + "t"
                : "sup -";
            string resistance = memory.ResistancePrice.HasValue
                ? "res " + memory.ResistancePrice.Value.ToString("N2") + " " + memory.ResistanceDistanceTicks.ToString("+0;-0;0") + "t"
                : "res -";
            return "SQL " + memory.Direction.ToLowerInvariant() +
                   " " + memory.PressureScore.ToString("+0;-0;0") +
                   " | " + support +
                   " | " + resistance +
                   " | conf " + memory.ConfidenceScore.ToString("N0");
        }

        private static decimal SqlSignalScore(HeatmapCell cell)
        {
            if (cell == null)
            {
                return 0m;
            }

            return Math.Max(cell.HistoricalScore, cell.HistoricalFlowScore);
        }

        private static decimal SqlSignalSign(HeatmapCell cell)
        {
            if (cell == null)
            {
                return 0m;
            }

            decimal bookSign = 0m;
            decimal flowSign = HistoricalFlowSign(cell);

            if (cell.HistoricalBidLiquidity > 0m || cell.HistoricalAskLiquidity > 0m)
            {
                if (cell.HistoricalBidLiquidity >= cell.HistoricalAskLiquidity * 1.15m)
                {
                    bookSign = 1m;
                }
                else if (cell.HistoricalAskLiquidity >= cell.HistoricalBidLiquidity * 1.15m)
                {
                    bookSign = -1m;
                }
            }

            if (bookSign != 0m && flowSign != 0m)
            {
                return bookSign == flowSign ? bookSign : 0m;
            }

            if (bookSign != 0m)
            {
                return bookSign;
            }

            return flowSign;
        }

        private HeatmapOperationalPlan BuildOperationalPlan(HeatmapSnapshot snapshot)
        {
            HeatmapOperationalPlan plan = new HeatmapOperationalPlan();
            plan.State = "Sem plano";
            plan.Direction = "Neutro";
            plan.Trigger = "aguardar book/times";
            plan.Invalidation = "-";
            plan.Read = "sem contexto operacional suficiente";

            if (snapshot == null)
            {
                return plan;
            }

            HeatmapZone actionZone = BestActionZone(snapshot);
            bool hasConflict =
                snapshot.MaxConflictScore >= 50m ||
                (actionZone != null && (string.Equals(actionZone.Action, "Aguardar", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(actionZone.Quality, "Conflito", StringComparison.OrdinalIgnoreCase))) ||
                DirectionConflict(actionZone == null ? null : actionZone.Direction, snapshot.SqlMemory == null ? null : snapshot.SqlMemory.Direction, snapshot.SqlMemory == null ? 0m : snapshot.SqlMemory.ConfidenceScore);

            if (hasConflict)
            {
                plan.State = "Aguardar conflito";
                plan.Direction = "Neutro";
                plan.ConfidenceScore = ClampScore(Math.Max(snapshot.MaxConflictScore, actionZone == null ? 0m : actionZone.ConflictScore));
                plan.AnchorPrice = actionZone == null ? (decimal?)null : actionZone.CenterPrice;
                plan.AnchorDistanceTicks = actionZone == null ? 0 : actionZone.DistanceTicks;
                plan.Trigger = actionZone == null ? "esperar conflito do SQL/book limpar" : "esperar " + FormatPlanPrice(actionZone.CenterPrice) + " definir";
                plan.Invalidation = "sem operacao enquanto os sinais divergirem";
                plan.Read = "conflito entre liquidez ao vivo, memoria SQL ou fluxo; aguardar confirmacao";
                return plan;
            }

            if (actionZone != null && !string.IsNullOrWhiteSpace(actionZone.Action) && actionZone.Action.IndexOf("defesa", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                plan.State = actionZone.Action;
                plan.Direction = actionZone.Direction;
                plan.AnchorPrice = actionZone.CenterPrice;
                plan.AnchorDistanceTicks = actionZone.DistanceTicks;
                plan.ConfidenceScore = PlanConfidence(actionZone, snapshot);
                ApplyPlanEnvelope(plan, actionZone, snapshot);
                plan.Trigger = "defender " + FormatPlanPrice(actionZone.CenterPrice) + " (" + FormatTicks(actionZone.DistanceTicks) + ")";
                plan.Invalidation = PlanInvalidation(actionZone, snapshot);
                plan.Read = "prioridade na defesa da zona; " + Empty(actionZone.ActionRead) + " | " + PlanContext(snapshot);
                return plan;
            }

            if (snapshot.Corridor != null &&
                snapshot.Corridor.IsAvailable &&
                string.Equals(snapshot.Corridor.Phase, "Comprimido", StringComparison.OrdinalIgnoreCase) &&
                snapshot.Bias != null &&
                DirectionSign(snapshot.Bias.Direction) != 0m &&
                snapshot.Bias.Confidence >= 45m)
            {
                plan.Direction = snapshot.Bias.Direction;
                plan.State = string.Equals(plan.Direction, "Compra", StringComparison.OrdinalIgnoreCase) ? "Rompimento compra" : "Rompimento venda";
                plan.AnchorPrice = string.Equals(plan.Direction, "Compra", StringComparison.OrdinalIgnoreCase)
                    ? snapshot.Corridor.ResistancePrice
                    : snapshot.Corridor.SupportPrice;
                plan.AnchorDistanceTicks = PriceDistanceTicks(plan.AnchorPrice.Value, snapshot.CurrentPrice);
                plan.ConfidenceScore = ClampScore(snapshot.Bias.Confidence * 0.62m + Math.Abs(snapshot.Bias.Score) * 0.24m + Math.Max(snapshot.Corridor.SupportActionScore, snapshot.Corridor.ResistanceActionScore) * 0.14m);
                plan.Trigger = string.Equals(plan.Direction, "Compra", StringComparison.OrdinalIgnoreCase)
                    ? "romper " + FormatPlanPrice(snapshot.Corridor.ResistancePrice)
                    : "perder " + FormatPlanPrice(snapshot.Corridor.SupportPrice);
                plan.Invalidation = "voltar para dentro do corredor";
                plan.Read = "corredor comprimido com vies " + plan.Direction.ToLowerInvariant() + "; preparar ruptura apenas com confirmacao";
                return plan;
            }

            if (actionZone != null)
            {
                plan.State = string.IsNullOrWhiteSpace(actionZone.Action) ? "Observar" : actionZone.Action;
                plan.Direction = string.IsNullOrWhiteSpace(actionZone.Direction) ? "Neutro" : actionZone.Direction;
                plan.AnchorPrice = actionZone.CenterPrice;
                plan.AnchorDistanceTicks = actionZone.DistanceTicks;
                plan.ConfidenceScore = PlanConfidence(actionZone, snapshot);
                ApplyPlanEnvelope(plan, actionZone, snapshot);
                plan.Trigger = "monitorar " + FormatPlanPrice(actionZone.CenterPrice) + " (" + FormatTicks(actionZone.DistanceTicks) + ")";
                plan.Invalidation = "perder leitura da zona";
                plan.Read = Empty(actionZone.ActionRead) + " | " + PlanContext(snapshot);
                return plan;
            }

            if (snapshot.Bias != null && DirectionSign(snapshot.Bias.Direction) != 0m)
            {
                plan.State = "Observar " + snapshot.Bias.Direction.ToLowerInvariant();
                plan.Direction = snapshot.Bias.Direction;
                plan.ConfidenceScore = ClampScore(snapshot.Bias.Confidence * 0.70m);
                plan.Trigger = "aguardar zona proxima confirmar";
                plan.Invalidation = "vies perder forca";
                plan.Read = Empty(snapshot.Bias.Read);
                return plan;
            }

            plan.State = "Observar";
            plan.Read = "sem zona acionavel clara";
            return plan;
        }

        private void ApplyPlanEnvelope(HeatmapOperationalPlan plan, HeatmapZone zone, HeatmapSnapshot snapshot)
        {
            if (plan == null || zone == null)
            {
                return;
            }

            decimal sign = DirectionSign(zone.Direction);

            if (sign == 0m)
            {
                return;
            }

            decimal anchor = plan.AnchorPrice.HasValue ? plan.AnchorPrice.Value : zone.CenterPrice;
            decimal? target = ResolvePlanTarget(zone, snapshot);
            decimal stop = sign > 0m
                ? Round(zone.LowPrice - _tickSize)
                : Round(zone.HighPrice + _tickSize);

            plan.StopPrice = stop;

            if (target.HasValue)
            {
                plan.TargetPrice = target.Value;
            }

            decimal rawRisk = sign > 0m ? anchor - stop : stop - anchor;
            decimal rawReward = target.HasValue
                ? sign > 0m ? target.Value - anchor : anchor - target.Value
                : 0m;
            plan.RiskTicks = rawRisk > 0m ? Math.Max(1, PriceSpanTicks(rawRisk)) : 0;
            plan.RewardTicks = rawReward > 0m ? Math.Max(0, PriceSpanTicks(rawReward)) : 0;
            plan.RiskReward = plan.RiskTicks <= 0 || plan.RewardTicks <= 0
                ? 0m
                : Math.Round(plan.RewardTicks / (decimal)plan.RiskTicks, 2, MidpointRounding.AwayFromZero);
            plan.Envelope = BuildPlanEnvelope(plan);
        }

        private decimal? ResolvePlanTarget(HeatmapZone zone, HeatmapSnapshot snapshot)
        {
            if (zone == null || snapshot == null)
            {
                return null;
            }

            if (snapshot.Corridor != null && snapshot.Corridor.IsAvailable)
            {
                if (string.Equals(zone.Direction, "Compra", StringComparison.OrdinalIgnoreCase) &&
                    snapshot.Corridor.ResistancePrice > zone.CenterPrice)
                {
                    return snapshot.Corridor.ResistancePrice;
                }

                if (string.Equals(zone.Direction, "Venda", StringComparison.OrdinalIgnoreCase) &&
                    snapshot.Corridor.SupportPrice < zone.CenterPrice)
                {
                    return snapshot.Corridor.SupportPrice;
                }
            }

            if (snapshot.Zones == null)
            {
                return null;
            }

            decimal sign = DirectionSign(zone.Direction);

            if (sign > 0m)
            {
                HeatmapZone target = snapshot.Zones
                    .Where(x => DirectionSign(x.Direction) < 0m && x.CenterPrice > zone.CenterPrice)
                    .OrderBy(x => x.CenterPrice)
                    .ThenByDescending(x => x.ActionScore)
                    .FirstOrDefault();
                return target == null ? (decimal?)null : target.CenterPrice;
            }

            HeatmapZone sellTarget = snapshot.Zones
                .Where(x => DirectionSign(x.Direction) > 0m && x.CenterPrice < zone.CenterPrice)
                .OrderByDescending(x => x.CenterPrice)
                .ThenByDescending(x => x.ActionScore)
                .FirstOrDefault();
            return sellTarget == null ? (decimal?)null : sellTarget.CenterPrice;
        }

        private static string BuildPlanEnvelope(HeatmapOperationalPlan plan)
        {
            if (plan == null || !plan.TargetPrice.HasValue || !plan.StopPrice.HasValue || plan.RiskTicks <= 0)
            {
                return "R/R -";
            }

            return "alvo " + FormatPlanPrice(plan.TargetPrice.Value) +
                   " | stop " + FormatPlanPrice(plan.StopPrice.Value) +
                   " | risco " + plan.RiskTicks.ToString() + "t" +
                   " | retorno " + plan.RewardTicks.ToString() + "t" +
                   " | R/R " + plan.RiskReward.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }

        private HeatmapZone BestActionZone(HeatmapSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Zones == null || snapshot.Zones.Count == 0)
            {
                return null;
            }

            return snapshot.Zones
                .OrderByDescending(x => x.ActionScore)
                .ThenBy(x => AbsTicks(x.DistanceTicks))
                .ThenByDescending(x => x.Score)
                .FirstOrDefault();
        }

        private static bool DirectionConflict(string primaryDirection, string sqlDirection, decimal sqlConfidence)
        {
            if (sqlConfidence < 45m)
            {
                return false;
            }

            decimal primary = DirectionSign(primaryDirection);
            decimal sql = DirectionSign(sqlDirection);
            return primary != 0m && sql != 0m && primary != sql;
        }

        private static decimal PlanConfidence(HeatmapZone zone, HeatmapSnapshot snapshot)
        {
            if (zone == null)
            {
                return 0m;
            }

            decimal confidence = zone.ActionScore * 0.58m + zone.ConfidenceScore * 0.24m + zone.Score * 0.12m;

            if (snapshot != null && snapshot.SqlMemory != null && DirectionSign(zone.Direction) == DirectionSign(snapshot.SqlMemory.Direction))
            {
                confidence += Math.Min(14m, snapshot.SqlMemory.ConfidenceScore * 0.18m);
            }

            if (snapshot != null && snapshot.Bias != null && DirectionSign(zone.Direction) == DirectionSign(snapshot.Bias.Direction))
            {
                confidence += Math.Min(10m, snapshot.Bias.Confidence * 0.12m);
            }

            confidence -= zone.ConflictScore * 0.35m;
            return ClampScore(confidence);
        }

        private string PlanInvalidation(HeatmapZone zone, HeatmapSnapshot snapshot)
        {
            if (zone == null)
            {
                return "-";
            }

            if (string.Equals(zone.Direction, "Compra", StringComparison.OrdinalIgnoreCase))
            {
                return "perder " + FormatPlanPrice(Round(zone.LowPrice - _tickSize));
            }

            if (string.Equals(zone.Direction, "Venda", StringComparison.OrdinalIgnoreCase))
            {
                return "romper " + FormatPlanPrice(Round(zone.HighPrice + _tickSize));
            }

            return snapshot != null && snapshot.Corridor != null && snapshot.Corridor.IsAvailable
                ? "sair do corredor"
                : "perder zona";
        }

        private static string PlanContext(HeatmapSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return "-";
            }

            List<string> parts = new List<string>();

            if (snapshot.Corridor != null && snapshot.Corridor.IsAvailable)
            {
                parts.Add("corredor " + snapshot.Corridor.WidthTicks.ToString() + "t " + Empty(snapshot.Corridor.Location).ToLowerInvariant());
            }

            if (snapshot.SqlMemory != null && snapshot.SqlMemory.IsAvailable)
            {
                parts.Add("SQL " + Empty(snapshot.SqlMemory.Direction).ToLowerInvariant() + " " + snapshot.SqlMemory.PressureScore.ToString("+0;-0;0"));
            }

            if (snapshot.Bias != null && !string.IsNullOrWhiteSpace(snapshot.Bias.Direction))
            {
                parts.Add("vies " + snapshot.Bias.Direction.ToLowerInvariant());
            }

            return parts.Count == 0 ? "-" : string.Join(" | ", parts.ToArray());
        }

        private static string FormatTicks(int ticks)
        {
            return ticks == 0 ? "0t" : ticks.ToString("+0;-0;0") + "t";
        }

        private static string FormatPlanPrice(decimal price)
        {
            return price.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string Empty(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
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
            int historicalWindowMinutes = snapshot.HistoricalContextMinutes <= 0 ? DefaultHistoricalContextMinutes : snapshot.HistoricalContextMinutes;
            decimal historicalFreshnessFactor = cell.HistoricalSamples > 0
                ? HistoricalFreshnessFactor(cell.HistoricalLastSeen, snapshot.LocalTimestamp, historicalWindowMinutes, out historicalAge, out historicalFreshness)
                : 0m;
            decimal historicalFlowFreshnessFactor = cell.HistoricalTradeSamples > 0
                ? HistoricalFreshnessFactor(cell.HistoricalTradeLastSeen, snapshot.LocalTimestamp, historicalWindowMinutes, out historicalTradeAge, out historicalFlowFreshness)
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
            cell.DistanceTicks = PriceDistanceTicks(cell.Price, snapshot.CurrentPrice);
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

            ApplyOperationalQuality(cell, snapshot);
        }

        private void ApplyOperationalQuality(HeatmapCell cell, HeatmapSnapshot snapshot)
        {
            decimal mainSign = DirectionSign(cell.Direction);
            decimal alignedTotal = 0m;
            decimal conflictTotal = 0m;
            int alignedCount = 0;
            int conflictCount = 0;
            int signalCount = 0;

            AddQualitySignal(mainSign, DirectionSign(cell.Direction), cell.AbsorptionScore, ref alignedTotal, ref conflictTotal, ref alignedCount, ref conflictCount, ref signalCount);
            AddQualitySignal(mainSign, DirectionSign(cell.Direction), cell.SpoofRiskScore, ref alignedTotal, ref conflictTotal, ref alignedCount, ref conflictCount, ref signalCount);
            AddQualitySignal(mainSign, DirectionSign(cell.Direction), Math.Max(cell.StackingScore, cell.PullingScore), ref alignedTotal, ref conflictTotal, ref alignedCount, ref conflictCount, ref signalCount);
            AddQualitySignal(mainSign, LiveBookSign(cell, snapshot), cell.WallScore, ref alignedTotal, ref conflictTotal, ref alignedCount, ref conflictCount, ref signalCount);
            if (cell.AbsorptionScore < 55m)
            {
                AddQualitySignal(mainSign, LiveTradeSign(cell), cell.AggressionScore, ref alignedTotal, ref conflictTotal, ref alignedCount, ref conflictCount, ref signalCount);
            }
            AddQualitySignal(mainSign, PersistenceSign(cell, snapshot), cell.PersistenceScore, ref alignedTotal, ref conflictTotal, ref alignedCount, ref conflictCount, ref signalCount);
            AddQualitySignal(mainSign, HistoricalBookSign(cell, snapshot), cell.HistoricalScore, ref alignedTotal, ref conflictTotal, ref alignedCount, ref conflictCount, ref signalCount);
            AddQualitySignal(mainSign, HistoricalFlowSign(cell), cell.HistoricalFlowScore, ref alignedTotal, ref conflictTotal, ref alignedCount, ref conflictCount, ref signalCount);

            cell.SignalCount = signalCount;
            cell.ConfluenceScore = alignedCount <= 0 ? 0m : ClampScore(alignedTotal / alignedCount + Math.Min(18m, alignedCount * 4m));
            cell.ConflictScore = conflictCount <= 0 ? 0m : ClampScore(conflictTotal / conflictCount + Math.Min(12m, conflictCount * 4m));
            cell.ConfidenceScore = ClampScore(cell.ConfluenceScore - cell.ConflictScore * 0.70m + Math.Min(12m, alignedCount * 2m));

            if (cell.ConflictScore >= 40m && cell.ConflictScore >= cell.ConfluenceScore * 0.55m)
            {
                cell.Quality = "Conflito";

                if (!string.IsNullOrWhiteSpace(cell.Read) && cell.Read.IndexOf("conflito", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    cell.Read += " + conflito";
                }
            }
            else if (cell.ConfidenceScore >= 70m && cell.ConfluenceScore >= 70m && alignedCount >= 3)
            {
                cell.Quality = "Alta";

                if (!string.IsNullOrWhiteSpace(cell.Read) && cell.Read.IndexOf("confluencia", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    cell.Read += " + confluencia";
                }
            }
            else if (cell.ConfidenceScore >= 50m)
            {
                cell.Quality = "Media";
            }
            else if (cell.ConfluenceScore > 0m || cell.ConflictScore > 0m)
            {
                cell.Quality = "Baixa";
            }
            else
            {
                cell.Quality = "-";
            }
        }

        private static void AddQualitySignal(decimal mainSign, decimal signalSign, decimal score, ref decimal alignedTotal, ref decimal conflictTotal, ref int alignedCount, ref int conflictCount, ref int signalCount)
        {
            if (mainSign == 0m || signalSign == 0m || score < 35m)
            {
                return;
            }

            signalCount++;

            if (signalSign == mainSign)
            {
                alignedTotal += score;
                alignedCount++;
            }
            else
            {
                conflictTotal += score;
                conflictCount++;
            }
        }

        private decimal LiveBookSign(HeatmapCell cell, HeatmapSnapshot snapshot)
        {
            if (cell.BidLiquidity <= 0m && cell.AskLiquidity <= 0m)
            {
                return 0m;
            }

            bool below = snapshot.CurrentPrice.HasValue && cell.Price < snapshot.CurrentPrice.Value;
            bool above = snapshot.CurrentPrice.HasValue && cell.Price > snapshot.CurrentPrice.Value;

            if (below && cell.BidLiquidity >= cell.AskLiquidity * 1.15m)
            {
                return 1m;
            }

            if (above && cell.AskLiquidity >= cell.BidLiquidity * 1.15m)
            {
                return -1m;
            }

            return cell.BidLiquidity > cell.AskLiquidity ? 1m : cell.AskLiquidity > cell.BidLiquidity ? -1m : 0m;
        }

        private static decimal LiveTradeSign(HeatmapCell cell)
        {
            return cell.Delta > 0m ? 1m : cell.Delta < 0m ? -1m : 0m;
        }

        private decimal PersistenceSign(HeatmapCell cell, HeatmapSnapshot snapshot)
        {
            if (cell.PersistenceScore <= 0m)
            {
                return 0m;
            }

            return LiveBookSign(cell, snapshot);
        }

        private decimal HistoricalBookSign(HeatmapCell cell, HeatmapSnapshot snapshot)
        {
            if (cell.HistoricalBidLiquidity <= 0m && cell.HistoricalAskLiquidity <= 0m)
            {
                return 0m;
            }

            bool below = snapshot.CurrentPrice.HasValue && cell.Price < snapshot.CurrentPrice.Value;
            bool above = snapshot.CurrentPrice.HasValue && cell.Price > snapshot.CurrentPrice.Value;

            if (below && cell.HistoricalBidLiquidity >= cell.HistoricalAskLiquidity * 1.15m)
            {
                return 1m;
            }

            if (above && cell.HistoricalAskLiquidity >= cell.HistoricalBidLiquidity * 1.15m)
            {
                return -1m;
            }

            return cell.HistoricalBidLiquidity > cell.HistoricalAskLiquidity ? 1m : cell.HistoricalAskLiquidity > cell.HistoricalBidLiquidity ? -1m : 0m;
        }

        private static decimal HistoricalFlowSign(HeatmapCell cell)
        {
            return cell.HistoricalDelta > 0m ? 1m : cell.HistoricalDelta < 0m ? -1m : 0m;
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

                    decimal distanceFactor = 1m / (1m + AbsTicks(zone.DistanceTicks) / 24m);
                    decimal zonePressure = sign * zone.Score * distanceFactor * 0.22m;
                    score += zonePressure;
                    AddBiasReason(reasons, zone.Read, zonePressure);
                }
            }

            if (snapshot.SqlMemory != null && snapshot.SqlMemory.IsAvailable)
            {
                decimal sqlSign = DirectionSign(snapshot.SqlMemory.Direction);

                if (sqlSign != 0m && snapshot.SqlMemory.ConfidenceScore >= 30m)
                {
                    decimal sqlPressure = sqlSign * snapshot.SqlMemory.ConfidenceScore * 0.24m;
                    score += sqlPressure;
                    AddBiasReason(reasons, "memoria SQL", sqlPressure);
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

                    decimal distanceFactor = 1m / (1m + AbsTicks(cell.DistanceTicks) / 28m);
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

        private static decimal HistoricalFreshnessFactor(DateTimeOffset lastSeen, DateTimeOffset reference, int contextMinutes, out double ageMinutes, out decimal freshnessScore)
        {
            int safeContextMinutes = Math.Max(5, contextMinutes);

            if (lastSeen == DateTimeOffset.MinValue)
            {
                ageMinutes = safeContextMinutes;
                freshnessScore = 0m;
                return 0m;
            }

            DateTimeOffset effectiveReference = reference == DateTimeOffset.MinValue ? DateTimeOffset.Now : reference;
            ageMinutes = Math.Max(0d, (effectiveReference - lastSeen).TotalMinutes);
            double freshnessRatio = Math.Max(0d, 1d - ageMinutes / safeContextMinutes);
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

        private int PriceDistanceTicks(decimal price, decimal? currentPrice)
        {
            if (!currentPrice.HasValue)
            {
                return 0;
            }

            return PriceSpanTicks(price - currentPrice.Value);
        }

        private int PriceSpanTicks(decimal distance)
        {
            decimal rawTicks = distance / _tickSize;
            decimal rounded = Math.Round(rawTicks, 0, MidpointRounding.AwayFromZero);

            if (rounded > int.MaxValue)
            {
                return int.MaxValue;
            }

            if (rounded < -int.MaxValue)
            {
                return -int.MaxValue;
            }

            return (int)rounded;
        }

        private static int AbsTicks(int ticks)
        {
            return ticks == int.MinValue ? int.MaxValue : Math.Abs(ticks);
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
