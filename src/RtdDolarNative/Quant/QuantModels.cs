using System;
using System.Collections.Generic;
using System.Linq;
using RtdDolarNative.Csv;

namespace RtdDolarNative.Quant
{
    public sealed class KeyLevel
    {
        private string _direction;

        public decimal Price { get; set; }
        public string Label { get; set; }
        public string Type { get; set; }
        public string Source { get; set; }
        public double Score { get; set; }
        public string Evidence { get; set; }
        public string Layer { get; set; }
        public decimal Distance { get; set; }
        public string Tags { get; set; }

        public string Direction
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_direction))
                {
                    return _direction;
                }

                if (string.Equals(Type, "Suporte", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Type, "val", StringComparison.OrdinalIgnoreCase))
                {
                    return "Compra";
                }

                if (string.Equals(Type, "Resistencia", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Type, "vah", StringComparison.OrdinalIgnoreCase))
                {
                    return "Venda";
                }

                return "Neutro";
            }
            set { _direction = value; }
        }
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
        public double Score { get; set; }
    }

    public sealed class ReferenceMetricSummary
    {
        public string MetricKey { get; set; }
        public string MetricLabel { get; set; }
        public decimal Points { get; set; }
        public DeviationLevel NearestSell { get; set; }
        public DeviationLevel NearestBuy { get; set; }
    }

    public sealed class ReferenceMapResult
    {
        public ReferenceMapResult()
        {
            GarmanLevels = new List<DeviationLevel>();
            GaussLevels = new List<DeviationLevel>();
            StdDevLevels = new List<DeviationLevel>();
            GarchLevels = new List<DeviationLevel>();
        }

        public string ReferenceKey { get; set; }
        public string ReferenceLabel { get; set; }
        public string ReferenceSource { get; set; }
        public decimal ReferencePrice { get; set; }
        public List<DeviationLevel> GarmanLevels { get; set; }
        public List<DeviationLevel> GaussLevels { get; set; }
        public List<DeviationLevel> StdDevLevels { get; set; }
        public List<DeviationLevel> GarchLevels { get; set; }
        public ReferenceMetricSummary GarmanSummary { get; set; }
        public ReferenceMetricSummary GaussSummary { get; set; }
        public ReferenceMetricSummary StdDevSummary { get; set; }
        public ReferenceMetricSummary GarchSummary { get; set; }
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
        public string Direction { get; set; }
        public decimal Multiplier { get; set; }
        public int Samples { get; set; }
        public int Touches { get; set; }
        public int Reversals { get; set; }
        public int Continuations { get; set; }
        public double TouchRate { get; set; }
        public double ReversalRate { get; set; }
        public double ContinuationRate { get; set; }
        public decimal AverageReversalPoints { get; set; }
        public decimal AverageAdversePoints { get; set; }
        public decimal ExpectancyPoints { get; set; }
        public double ProfitFactor { get; set; }
        public double Confidence { get; set; }
        public decimal RiskReward { get; set; }
        public double EdgeScore { get; set; }
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
        public decimal? Momentum10Pct { get; set; }
        public decimal? PositiveReturnRate21Pct { get; set; }
        public decimal? Sharpe21 { get; set; }
        public decimal? Sortino21 { get; set; }
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
        public double ReversalRate { get; set; }
        public double ProfitFactor { get; set; }
        public decimal? ExpectancyPoints { get; set; }
        public string EdgeQuality { get; set; }
        public double Confidence { get; set; }
        public double ExpectedWinRate { get; set; }
        public decimal RiskReward { get; set; }
        public decimal? TargetPoints { get; set; }
        public decimal? StopPoints { get; set; }
        public string RiskModel { get; set; }
        public string RobustnessGate { get; set; }
    }

    public sealed class MarketBiasFactor
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public double Score { get; set; }
        public double Confidence { get; set; }
        public string Value { get; set; }
        public string Note { get; set; }
        public bool Available { get; set; }
    }

    public sealed class MarketBiasCategory
    {
        public string Key { get; set; }
        public string Label { get; set; }
        public double Weight { get; set; }
        public double? Score { get; set; }
        public double Contribution { get; set; }
        public double Confidence { get; set; }
        public int ActiveFactors { get; set; }
    }

    public sealed class MarketBiasSnapshot
    {
        public MarketBiasSnapshot()
        {
            Direction = "Neutro";
            Read = "sem fatores ativos";
            Factors = new List<MarketBiasFactor>();
            Categories = new List<MarketBiasCategory>();
            TopFactors = new List<MarketBiasFactor>();
        }

        public double Score { get; set; }
        public string Direction { get; set; }
        public double ConfidencePct { get; set; }
        public double CoveragePct { get; set; }
        public string Read { get; set; }
        public List<MarketBiasFactor> Factors { get; set; }
        public List<MarketBiasCategory> Categories { get; set; }
        public List<MarketBiasFactor> TopFactors { get; set; }
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
            StandardDeviationLevels = new List<DeviationLevel>();
            GaussLevels = new List<DeviationLevel>();
            ReferenceMaps = new List<ReferenceMapResult>();
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
            Garch = new GarchSnapshot();
            MarketBias = new MarketBiasSnapshot();
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
        public VolatilityMetric StandardDeviation { get; set; }
        public VolatilityMetric Gauss { get; set; }
        public VolatilityMetric Atr { get; set; }
        public VolumeProfileResult Profile { get; set; }
        public List<VolatilityMetric> Metrics { get; set; }
        public List<VolatilityMetric> WindowMetrics { get; set; }
        public List<DeviationLevel> OpeningLevels { get; set; }
        public List<DeviationLevel> PocDeviationLevels { get; set; }
        public List<DeviationLevel> StandardDeviationLevels { get; set; }
        public List<DeviationLevel> GaussLevels { get; set; }
        public List<ReferenceMapResult> ReferenceMaps { get; set; }
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
        public int CalculationDays { get; set; }
        public GarchSnapshot Garch { get; set; }
        public MarketBiasSnapshot MarketBias { get; set; }
    }

    public sealed class GarchConfig
    {
        public bool Enabled { get; set; }
        public int DailyWindowDays { get; set; }
        public int DailyMinSamples { get; set; }
        public int IntradayTimeframeSeconds { get; set; }
        public int IntradayMinBars { get; set; }
        public int MaxIntradayBars { get; set; }
        public double[] BandMultipliers { get; set; }
        public double ReversionMinAbsZDaily { get; set; }
        public double ReversionMinAbsZIntraday { get; set; }
        public double ExtremeAbsZIntraday { get; set; }
        public int MaxEntryDistanceTicks { get; set; }
        public int MaxIterations { get; set; }
        public double Tolerance { get; set; }
        public double StationarityCap { get; set; }

        public GarchConfig()
        {
            Enabled = true;
            DailyWindowDays = 252;
            DailyMinSamples = 126;
            IntradayTimeframeSeconds = 60;
            IntradayMinBars = 90;
            MaxIntradayBars = 1200;
            BandMultipliers = new[] { 0.5d, 1.0d, 1.5d, 2.0d, 2.5d };
            ReversionMinAbsZDaily = 0.75d;
            ReversionMinAbsZIntraday = 1.5d;
            ExtremeAbsZIntraday = 2.0d;
            MaxEntryDistanceTicks = 6;
            MaxIterations = 120;
            Tolerance = 0.00000001d;
            StationarityCap = 0.995d;
        }

        public void Normalize()
        {
            DailyWindowDays = Clamp(DailyWindowDays, 63, 1000);
            DailyMinSamples = Clamp(DailyMinSamples, 63, Math.Max(DailyWindowDays, 63));
            IntradayTimeframeSeconds = NormalizeIntradayTimeframe(IntradayTimeframeSeconds);
            IntradayMinBars = Clamp(IntradayMinBars, 45, Math.Max(90, MaxIntradayBars));
            MaxIntradayBars = Math.Max(300, MaxIntradayBars);
            StationarityCap = Clamp(StationarityCap, 0.90d, 0.999d);
            ReversionMinAbsZDaily = Math.Max(0d, ReversionMinAbsZDaily);
            ReversionMinAbsZIntraday = Math.Max(0d, ReversionMinAbsZIntraday);
            ExtremeAbsZIntraday = Math.Max(0d, ExtremeAbsZIntraday);
            MaxEntryDistanceTicks = Clamp(MaxEntryDistanceTicks, 1, 30);
            MaxIterations = Clamp(MaxIterations, 20, 500);
            Tolerance = Math.Max(1e-12d, Tolerance);

            if (BandMultipliers == null || BandMultipliers.Length == 0)
            {
                BandMultipliers = new[] { 0.5d, 1.0d, 1.5d, 2.0d, 2.5d };
            }

            BandMultipliers = BandMultipliers
                .Where(x => x > 0d)
                .Select(Math.Abs)
                .OrderBy(x => x)
                .Distinct()
                .ToArray();

            if (BandMultipliers.Length == 0)
            {
                BandMultipliers = new[] { 0.5d, 1.0d, 1.5d, 2.0d, 2.5d };
            }
        }

        private static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private static double Clamp(double value, double min, double max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private static int NormalizeIntradayTimeframe(int timeframeSeconds)
        {
            if (timeframeSeconds == 300 || timeframeSeconds == 900)
            {
                return timeframeSeconds;
            }

            return 60;
        }
    }

    public sealed class GarchFitResult
    {
        public bool Success { get; set; }
        public string Status { get; set; }
        public string Scope { get; set; }
        public int Samples { get; set; }
        public double Mu { get; set; }
        public double Omega { get; set; }
        public double Alpha { get; set; }
        public double Beta { get; set; }
        public double Persistence { get; set; }
        public double HalfLifePeriods { get; set; }
        public double LongRunVariance { get; set; }
        public double LongRunSigma { get; set; }
        public double LastVariance { get; set; }
        public double NextVariance { get; set; }
        public double NextSigma { get; set; }
        public double NegativeLogLikelihood { get; set; }
        public int Iterations { get; set; }
        public DateTimeOffset CalculatedAt { get; set; }
        public string Warning { get; set; }
    }

    public sealed class GarchBandLevel
    {
        public GarchBandLevel()
        {
            Source = "GARCH";
            Side = string.Empty;
            Label = string.Empty;
        }

        public string Scope { get; set; }
        public string ReferenceName { get; set; }
        public decimal ReferencePrice { get; set; }
        public double Sigma { get; set; }
        public decimal Price { get; set; }
        public decimal DistanceCurrent { get; set; }
        public decimal DistanceReference { get; set; }
        public string Side { get; set; }
        public string Label { get; set; }
        public string Source { get; set; }
        public string Read { get; set; }
        public int ScoreHint { get; set; }
    }

    public sealed class GarchSnapshot
    {
        public GarchSnapshot()
        {
            DailyBands = new List<GarchBandLevel>();
            IntradayBands = new List<GarchBandLevel>();
            Signals = new List<GarchSignal>();
            Backtest = new List<GarchBacktestRow>();
            DailyFit = new GarchFitResult();
            IntradayFit = new GarchFitResult();
            Warnings = new List<string>();
        }

        public GarchFitResult DailyFit { get; set; }
        public GarchFitResult IntradayFit { get; set; }
        public decimal DailyReference { get; set; }
        public string DailyReferenceName { get; set; }
        public decimal IntradayReference { get; set; }
        public string IntradayReferenceName { get; set; }
        public decimal CurrentPrice { get; set; }
        public double ZDaily { get; set; }
        public double ZIntraday { get; set; }
        public decimal DailySigmaPoints { get; set; }
        public decimal IntradaySigmaPoints { get; set; }
        public string DailyRegime { get; set; }
        public string IntradayRegime { get; set; }
        public string CombinedRead { get; set; }
        public List<GarchBandLevel> DailyBands { get; set; }
        public List<GarchBandLevel> IntradayBands { get; set; }
        public List<GarchSignal> Signals { get; set; }
        public List<GarchBacktestRow> Backtest { get; set; }
        public List<string> Warnings { get; set; }
    }

    public sealed class GarchSignal
    {
        public string Scope { get; set; }
        public string Setup { get; set; }
        public string Direction { get; set; }
        public decimal Price { get; set; }
        public int Score { get; set; }
        public string Robustness { get; set; }
        public string LevelName { get; set; }
        public decimal? LevelPrice { get; set; }
        public double ZDaily { get; set; }
        public double ZIntraday { get; set; }
        public decimal? StopPrice { get; set; }
        public decimal? Target1 { get; set; }
        public decimal? Target2 { get; set; }
        public decimal? RiskPoints { get; set; }
        public decimal? RewardPoints { get; set; }
        public decimal? RiskReward { get; set; }
        public string Confirmation { get; set; }
        public string Gate { get; set; }
        public string Reasons { get; set; }
    }

    public sealed class GarchBacktestRow
    {
        public string Scope { get; set; }
        public string Direction { get; set; }
        public double Sigma { get; set; }
        public int Samples { get; set; }
        public int Touches { get; set; }
        public int Reversals { get; set; }
        public int Continuations { get; set; }
        public double ReversalRate { get; set; }
        public decimal AverageMfePoints { get; set; }
        public decimal AverageMaePoints { get; set; }
        public decimal ExpectancyPoints { get; set; }
        public double ProfitFactor { get; set; }
        public double Confidence { get; set; }
        public decimal RiskReward { get; set; }
        public double EdgeScore { get; set; }
        public string Read { get; set; }
    }
}
