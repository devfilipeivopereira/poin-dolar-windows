using System;
using System.Collections.Generic;

namespace RtdDolarNative.Heatmap
{
    public sealed class HeatmapBookLevel
    {
        public string Asset { get; set; }
        public DateTimeOffset LocalTimestamp { get; set; }
        public int LevelIndex { get; set; }
        public decimal Price { get; set; }
        public decimal BidSize { get; set; }
        public decimal AskSize { get; set; }
    }

    public sealed class HeatmapCell
    {
        public decimal Price { get; set; }
        public decimal BidLiquidity { get; set; }
        public decimal AskLiquidity { get; set; }
        public decimal BuyVolume { get; set; }
        public decimal SellVolume { get; set; }
        public decimal NeutralVolume { get; set; }
        public decimal Delta { get; set; }
        public decimal BidChange { get; set; }
        public decimal AskChange { get; set; }
        public decimal NetLiquidity { get; set; }
        public decimal LiquidityImbalance { get; set; }
        public decimal TradeImbalance { get; set; }
        public decimal WallScore { get; set; }
        public decimal AbsorptionScore { get; set; }
        public decimal AggressionScore { get; set; }
        public decimal StackingScore { get; set; }
        public decimal PullingScore { get; set; }
        public decimal InterestScore { get; set; }
        public int DistanceTicks { get; set; }
        public string Direction { get; set; }
        public string Read { get; set; }
    }

    public sealed class HeatmapZone
    {
        public decimal LowPrice { get; set; }
        public decimal HighPrice { get; set; }
        public decimal CenterPrice { get; set; }
        public decimal Score { get; set; }
        public decimal TotalBidLiquidity { get; set; }
        public decimal TotalAskLiquidity { get; set; }
        public decimal BuyVolume { get; set; }
        public decimal SellVolume { get; set; }
        public decimal Delta { get; set; }
        public int DistanceTicks { get; set; }
        public int CellCount { get; set; }
        public string Direction { get; set; }
        public string Read { get; set; }
    }

    public sealed class HeatmapSnapshot
    {
        public HeatmapSnapshot()
        {
            Cells = new List<HeatmapCell>();
            InterestCells = new List<HeatmapCell>();
            Zones = new List<HeatmapZone>();
        }

        public string Asset { get; set; }
        public DateTimeOffset LocalTimestamp { get; set; }
        public decimal? CurrentPrice { get; set; }
        public decimal MaxBidLiquidity { get; set; }
        public decimal MaxAskLiquidity { get; set; }
        public decimal MaxTradeVolume { get; set; }
        public decimal MaxAbsorptionScore { get; set; }
        public decimal MaxAggressionScore { get; set; }
        public decimal MaxWallScore { get; set; }
        public decimal MaxStackingScore { get; set; }
        public decimal MaxPullingScore { get; set; }
        public decimal TotalBidLiquidity { get; set; }
        public decimal TotalAskLiquidity { get; set; }
        public decimal TotalBuyVolume { get; set; }
        public decimal TotalSellVolume { get; set; }
        public decimal CumulativeDelta { get; set; }
        public int BookLevels { get; set; }
        public int TradeCount { get; set; }
        public long Version { get; set; }
        public string DominantSide { get; set; }
        public string DominantRead { get; set; }
        public string StorageStatus { get; set; }
        public List<HeatmapCell> Cells { get; set; }
        public List<HeatmapCell> InterestCells { get; set; }
        public List<HeatmapZone> Zones { get; set; }
    }
}
