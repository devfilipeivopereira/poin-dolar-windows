using System;
using System.Collections.Generic;
using RtdDolarNative.Csv;

namespace RtdDolarNative.Quant
{
    public sealed class KeyLevel
    {
        public decimal Price { get; set; }
        public string Label { get; set; }
        public string Type { get; set; }
        public string Source { get; set; }
        public double Score { get; set; }
        public string Evidence { get; set; }
        public string Layer { get; set; }
        public decimal Distance { get; set; }
        public string Tags { get; set; }
    }

    public sealed class IntradayContext
    {
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Price { get; set; }
        public decimal Vwap { get; set; }
        public decimal Volume { get; set; }
        public bool VwapIsProxy { get; set; }
    }

    public sealed class VolatilityMetric
    {
        public string Name { get; set; }
        public int Window { get; set; }
        public decimal Points { get; set; }
        public double Percent { get; set; }
        public double Percentile { get; set; }
    }

    public sealed class ProfileBin
    {
        public decimal Low { get; set; }
        public decimal High { get; set; }
        public decimal Price { get; set; }
        public double Volume { get; set; }
        public bool InValue { get; set; }
        public bool IsHvn { get; set; }
        public bool IsLvn { get; set; }
        public double Rank { get; set; }
    }

    public sealed class VolumeProfileResult
    {
        public VolumeProfileResult()
        {
            Bins = new List<ProfileBin>();
            Hvn = new List<ProfileBin>();
            Lvn = new List<ProfileBin>();
        }

        public List<ProfileBin> Bins { get; set; }
        public ProfileBin Poc { get; set; }
        public decimal Vah { get; set; }
        public decimal Val { get; set; }
        public List<ProfileBin> Hvn { get; set; }
        public List<ProfileBin> Lvn { get; set; }
    }

    public sealed class DeviationLevel
    {
        public string Side { get; set; }
        public string Direction { get; set; }
        public decimal Sigma { get; set; }
        public decimal Price { get; set; }
        public decimal DistanceReference { get; set; }
        public decimal DistanceCurrent { get; set; }
        public string Label { get; set; }
    }

    public sealed class PercentLevel
    {
        public double Percent { get; set; }
        public decimal Price { get; set; }
        public decimal DistanceReference { get; set; }
        public decimal DistanceCurrent { get; set; }
        public string Direction { get; set; }
    }

    public sealed class PercentMap
    {
        public PercentMap()
        {
            Levels = new List<PercentLevel>();
        }

        public string Key { get; set; }
        public string Label { get; set; }
        public string ShortLabel { get; set; }
        public string Status { get; set; }
        public decimal Price { get; set; }
        public List<PercentLevel> Levels { get; set; }
    }

    public sealed class AnchoredVwap
    {
        public string Label { get; set; }
        public decimal Price { get; set; }
        public DateTime AnchorDate { get; set; }
    }

    public sealed class BacktestRow
    {
        public decimal Multiplier { get; set; }
        public int Samples { get; set; }
        public int Touches { get; set; }
        public int Reversals { get; set; }
        public double TouchRate { get; set; }
        public double ReversalRate { get; set; }
    }

    public sealed class TechnicalIndicatorSnapshot
    {
        public string Source { get; set; }
        public int SampleSize { get; set; }
        public decimal? Sma20 { get; set; }
        public decimal? Sma50 { get; set; }
        public decimal? Ema9 { get; set; }
        public decimal? Ema21 { get; set; }
        public decimal? Ema50 { get; set; }
        public decimal? Rsi14 { get; set; }
        public decimal? Macd { get; set; }
        public decimal? MacdSignal { get; set; }
        public decimal? MacdHistogram { get; set; }
        public decimal? BollingerUpper20 { get; set; }
        public decimal? BollingerMiddle20 { get; set; }
        public decimal? BollingerLower20 { get; set; }
        public decimal? ZScore20 { get; set; }
        public decimal? AtrVwapDistance { get; set; }
        public decimal? ReturnMean21Pct { get; set; }
        public decimal? ReturnStd21Pct { get; set; }
        public decimal? DownsideStd21Pct { get; set; }
        public decimal? ValueAtRisk95Pct { get; set; }
        public decimal? ExpectedShortfall95Pct { get; set; }
        public string TrendState { get; set; }
        public string ReversionState { get; set; }
    }

    public sealed class QuantSignal
    {
        public string Setup { get; set; }
        public string Direction { get; set; }
        public decimal Price { get; set; }
        public int Score { get; set; }
        public string LevelName { get; set; }
        public decimal? LevelPrice { get; set; }
        public string Reasons { get; set; }
        public string DataSource { get; set; }
        public int SampleSize { get; set; }
        public string TechnicalState { get; set; }
        public string StatisticalEdge { get; set; }
    }

    public sealed class QuantResult
    {
        public QuantResult()
        {
            Warnings = new List<string>();
            Metrics = new List<VolatilityMetric>();
            WindowMetrics = new List<VolatilityMetric>();
            OpeningLevels = new List<DeviationLevel>();
            PocDeviationLevels = new List<DeviationLevel>();
            PercentMaps = new List<PercentMap>();
            PercentTable = new List<KeyLevel>();
            KeyLevels = new List<KeyLevel>();
            Confluence = new List<KeyLevel>();
            SupportResistance = new List<KeyLevel>();
            Avwaps = new List<AnchoredVwap>();
            Backtest = new List<BacktestRow>();
            QuantSignals = new List<QuantSignal>();
            Bars = new List<DailyBar>();
            Technicals = new TechnicalIndicatorSnapshot();
        }

        public List<string> Warnings { get; set; }
        public List<DailyBar> Bars { get; set; }
        public IntradayContext Intraday { get; set; }
        public DailyBar PreviousDay { get; set; }
        public VolatilityMetric GarmanKlass { get; set; }
        public VolatilityMetric Parkinson { get; set; }
        public VolatilityMetric RogersSatchell { get; set; }
        public VolatilityMetric YangZhang { get; set; }
        public VolatilityMetric CloseToClose { get; set; }
        public VolatilityMetric Atr { get; set; }
        public VolumeProfileResult Profile { get; set; }
        public List<VolatilityMetric> Metrics { get; set; }
        public List<VolatilityMetric> WindowMetrics { get; set; }
        public List<DeviationLevel> OpeningLevels { get; set; }
        public List<DeviationLevel> PocDeviationLevels { get; set; }
        public List<PercentMap> PercentMaps { get; set; }
        public List<KeyLevel> PercentTable { get; set; }
        public List<KeyLevel> KeyLevels { get; set; }
        public List<KeyLevel> Confluence { get; set; }
        public List<KeyLevel> SupportResistance { get; set; }
        public List<AnchoredVwap> Avwaps { get; set; }
        public List<BacktestRow> Backtest { get; set; }
        public TechnicalIndicatorSnapshot Technicals { get; set; }
        public List<QuantSignal> QuantSignals { get; set; }
        public string Regime { get; set; }
    }
}
