using System;
using System.Collections.Generic;
using System.Globalization;

namespace RtdDolarNative.Flow
{
    public enum MarketDataQuality
    {
        TopOfBookOnly,
        DerivedTape,
        FullTimesAndTrades,
        FullDepth
    }

    public sealed class NormalizedMarketEvent
    {
        public string Asset { get; set; }
        public DateTimeOffset LocalTimestamp { get; set; }
        public decimal? Price { get; set; }
        public decimal? Quantity { get; set; }
        public decimal? VolumeDelta { get; set; }
        public decimal? TradesDelta { get; set; }
        public decimal? Bid { get; set; }
        public decimal? Ask { get; set; }
        public decimal? BidVolume { get; set; }
        public decimal? AskVolume { get; set; }
        public MarketDataQuality DataQuality { get; set; }
        public bool Derived { get; set; }
    }

    public sealed class TradePrint
    {
        private static readonly CultureInfo PtBr = new CultureInfo("pt-BR");

        public string Asset { get; set; }
        public DateTimeOffset LocalTimestamp { get; set; }
        public string ProfitTime { get; set; }
        public decimal Price { get; set; }
        public decimal Quantity { get; set; }
        public decimal Volume { get; set; }
        public decimal Delta { get; set; }
        public string Aggressor { get; set; }
        public string Classification { get; set; }
        public bool Derived { get; set; }
        public MarketDataQuality DataQuality { get; set; }
        public decimal? Bid { get; set; }
        public decimal? Ask { get; set; }

        public string LocalTimeText
        {
            get { return LocalTimestamp.ToString("HH:mm:ss.fff"); }
        }

        public string PriceText
        {
            get { return Price.ToString("N2", PtBr); }
        }
    }

    public sealed class BookSnapshot
    {
        public string Asset { get; set; }
        public DateTimeOffset LocalTimestamp { get; set; }
        public decimal? Bid { get; set; }
        public decimal? Ask { get; set; }
        public decimal? BidVolume { get; set; }
        public decimal? AskVolume { get; set; }
        public decimal? Spread { get; set; }
        public decimal? Mid { get; set; }
        public decimal? MicroPrice { get; set; }
        public decimal? MicroBias { get; set; }
        public decimal? Imbalance { get; set; }
        public MarketDataQuality DataQuality { get; set; }
    }

    public sealed class FlowWindowMetrics
    {
        public string Window { get; set; }
        public int Seconds { get; set; }
        public int TradeCount { get; set; }
        public decimal BuyVolume { get; set; }
        public decimal SellVolume { get; set; }
        public decimal Delta { get; set; }
        public decimal TotalVolume { get; set; }
        public decimal DeltaRatio { get; set; }
    }

    public sealed class FlowMetrics
    {
        public FlowMetrics()
        {
            Windows = new List<FlowWindowMetrics>();
        }

        public string Asset { get; set; }
        public DateTimeOffset LocalTimestamp { get; set; }
        public decimal? Price { get; set; }
        public decimal? Bid { get; set; }
        public decimal? Ask { get; set; }
        public decimal? Spread { get; set; }
        public decimal? Mid { get; set; }
        public decimal? MicroPrice { get; set; }
        public decimal? MicroBias { get; set; }
        public decimal? TopBookImbalance { get; set; }
        public decimal LastDelta { get; set; }
        public decimal CumulativeDelta { get; set; }
        public decimal? Vwap { get; set; }
        public decimal? VwapDistance { get; set; }
        public MarketDataQuality DataQuality { get; set; }
        public bool Derived { get; set; }
        public List<FlowWindowMetrics> Windows { get; set; }
        public VolumeProfileMetrics Profile { get; set; }
        public TradePrint LastTrade { get; set; }
    }

    public sealed class FlowSignal
    {
        private static readonly CultureInfo PtBr = new CultureInfo("pt-BR");

        public string Asset { get; set; }
        public DateTimeOffset LocalTimestamp { get; set; }
        public string Setup { get; set; }
        public string Direction { get; set; }
        public decimal Price { get; set; }
        public int Score { get; set; }
        public string LevelName { get; set; }
        public decimal? LevelPrice { get; set; }
        public string Reasons { get; set; }
        public bool Derived { get; set; }
        public MarketDataQuality DataQuality { get; set; }
        public string CooldownKey { get; set; }

        public string LocalTimeText
        {
            get { return LocalTimestamp.ToString("HH:mm:ss.fff"); }
        }

        public string PriceText
        {
            get { return Price.ToString("N2", PtBr); }
        }
    }

    public sealed class VolumeProfileMetrics
    {
        public VolumeProfileMetrics()
        {
            Bins = new List<VolumeProfileBin>();
            Nodes = new List<VolumeNode>();
            Levels = new List<ProfileLevel>();
        }

        public string Asset { get; set; }
        public DateTimeOffset LocalTimestamp { get; set; }
        public decimal? Poc { get; set; }
        public decimal? Vah { get; set; }
        public decimal? Val { get; set; }
        public decimal TotalVolume { get; set; }
        public decimal ValueAreaVolume { get; set; }
        public decimal? CurrentDistanceToPoc { get; set; }
        public string Source { get; set; }
        public List<VolumeProfileBin> Bins { get; set; }
        public List<VolumeNode> Nodes { get; set; }
        public List<ProfileLevel> Levels { get; set; }
    }

    public sealed class VolumeProfileBin
    {
        public decimal Price { get; set; }
        public decimal Low { get; set; }
        public decimal High { get; set; }
        public decimal Volume { get; set; }
        public decimal BuyVolume { get; set; }
        public decimal SellVolume { get; set; }
        public decimal Delta { get; set; }
        public bool InValueArea { get; set; }
        public bool IsPoc { get; set; }
        public bool IsHvn { get; set; }
        public bool IsLvn { get; set; }
        public decimal Rank { get; set; }
    }

    public sealed class VolumeNode
    {
        public string Type { get; set; }
        public decimal Price { get; set; }
        public decimal Low { get; set; }
        public decimal High { get; set; }
        public decimal Volume { get; set; }
        public decimal Score { get; set; }
        public string Description { get; set; }
    }

    public sealed class ProfileLevel
    {
        public string Type { get; set; }
        public decimal Price { get; set; }
        public string Label { get; set; }
        public double Score { get; set; }
        public string Source { get; set; }
    }

    public sealed class FlowUpdate
    {
        public FlowMetrics Metrics { get; set; }
        public TradePrint Trade { get; set; }
        public List<FlowSignal> Signals { get; set; }
    }
}
