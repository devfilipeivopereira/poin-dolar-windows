using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RtdDolarNative.Quant;

namespace RtdDolarNative.Opportunities
{
    public sealed class OpportunityKnowledgeCard
    {
        public OpportunityKnowledgeCard()
        {
            Id = Guid.NewGuid();
            ItemType = "opportunity_card";
            CreatedAtUtc = DateTime.UtcNow;
            AsOfUtc = CreatedAtUtc;
            LastSeenUtc = CreatedAtUtc;
            SeenCount = 1;
            Setup = string.Empty;
            Direction = string.Empty;
            Robustness = string.Empty;
            Confidence = "medium";
            DataQuality = string.Empty;
            SourceKind = "manual";
            SourceKey = string.Empty;
            Reasons = string.Empty;
            Tags = string.Empty;
            LevelName = string.Empty;
            DedupeKey = string.Empty;
        }

        public Guid Id { get; set; }
        public string ItemType { get; set; }
        public string DedupeKey { get; set; }
        public string Asset { get; set; }
        public DateTime AsOfUtc { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public int SeenCount { get; set; }
        public string Setup { get; set; }
        public string Direction { get; set; }
        public decimal Price { get; set; }
        public int Score { get; set; }
        public string Robustness { get; set; }
        public string Confidence { get; set; }
        public string DataQuality { get; set; }
        public string SourceKind { get; set; }
        public string SourceKey { get; set; }
        public string Reasons { get; set; }
        public string Tags { get; set; }
        public string LevelName { get; set; }
        public decimal? LevelPrice { get; set; }
        public double? SnapshotAgeSeconds { get; set; }
        public decimal? FlowDelta { get; set; }
        public decimal? CumulativeDelta { get; set; }
        public decimal? Imbalance { get; set; }
        public decimal? ExpectancyPoints { get; set; }
        public double? ProfitFactor { get; set; }
        public decimal? RiskReward { get; set; }

        public void Normalize(decimal tickSize)
        {
            if (Id == Guid.Empty)
            {
                Id = Guid.NewGuid();
            }

            ItemType = EmptyToDefault(ItemType, "opportunity_card");
            Asset = EmptyToDefault(Asset, string.Empty).Trim().ToUpperInvariant();
            Setup = EmptyToDefault(Setup, "-").Trim();
            Direction = EmptyToDefault(Direction, "-").Trim();
            Robustness = EmptyToDefault(Robustness, "-").Trim();
            Confidence = NormalizeConfidence(Confidence);
            DataQuality = EmptyToDefault(DataQuality, "-").Trim();
            SourceKind = EmptyToDefault(SourceKind, "manual").Trim();
            SourceKey = EmptyToDefault(SourceKey, BuildSourceKey()).Trim();
            Reasons = EmptyToDefault(Reasons, "-").Trim();
            Tags = EmptyToDefault(Tags, string.Empty).Trim();
            LevelName = EmptyToDefault(LevelName, string.Empty).Trim();
            AsOfUtc = ToUtc(AsOfUtc == DateTime.MinValue ? DateTime.UtcNow : AsOfUtc);
            CreatedAtUtc = ToUtc(CreatedAtUtc == DateTime.MinValue ? AsOfUtc : CreatedAtUtc);
            LastSeenUtc = ToUtc(LastSeenUtc == DateTime.MinValue ? AsOfUtc : LastSeenUtc);

            if (SeenCount <= 0)
            {
                SeenCount = 1;
            }

            DedupeKey = BuildDedupeKey(tickSize);
        }

        public string EmbeddingText()
        {
            return string.Join(" | ", new[]
            {
                Asset,
                Setup,
                Direction,
                Robustness,
                Score.ToString(CultureInfo.InvariantCulture),
                Reasons
            }.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray());
        }

        public string BuildDedupeKey(decimal tickSize)
        {
            decimal normalizedTick = tickSize <= 0m ? 0.5m : tickSize;
            decimal roundedPrice = Math.Round(Price / normalizedTick, 0, MidpointRounding.AwayFromZero) * normalizedTick;
            string level = !string.IsNullOrWhiteSpace(LevelName)
                ? LevelName
                : LevelPrice.HasValue ? LevelPrice.Value.ToString("0.####", CultureInfo.InvariantCulture) : string.Empty;

            return string.Join("|", new[]
            {
                NormalizeKey(Asset),
                NormalizeKey(Setup),
                NormalizeKey(Direction),
                roundedPrice.ToString("0.####", CultureInfo.InvariantCulture),
                NormalizeKey(level),
                NormalizeKey(SourceKind)
            });
        }

        private string BuildSourceKey()
        {
            return string.Join("|", new[]
            {
                EmptyToDefault(Asset, "-"),
                EmptyToDefault(Setup, "-"),
                EmptyToDefault(Direction, "-"),
                Price.ToString("0.####", CultureInfo.InvariantCulture)
            });
        }

        private static string EmptyToDefault(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static string NormalizeConfidence(string confidence)
        {
            if (string.Equals(confidence, "low", StringComparison.OrdinalIgnoreCase))
            {
                return "low";
            }

            if (string.Equals(confidence, "high", StringComparison.OrdinalIgnoreCase))
            {
                return "high";
            }

            return "medium";
        }

        private static DateTime ToUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc)
            {
                return value;
            }

            if (value.Kind == DateTimeKind.Unspecified)
            {
                return DateTime.SpecifyKind(value, DateTimeKind.Utc);
            }

            return value.ToUniversalTime();
        }

        private static string NormalizeKey(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? "-"
                : value.Trim().ToUpperInvariant();
        }
    }

    public sealed class OpportunityScoreResult
    {
        public int Score { get; set; }
        public int Cap { get; set; }
        public int Confirmations { get; set; }
        public bool FlowConfirms { get; set; }
        public string Robustness { get; set; }
        public string Detail { get; set; }
    }

    public sealed class OpportunityAssetState
    {
        public OpportunityAssetState()
        {
            Asset = string.Empty;
            Enabled = true;
            QuoteEnabled = true;
            BookEnabled = true;
            TimesEnabled = true;
        }

        public string Asset { get; set; }
        public bool Enabled { get; set; }
        public bool QuoteEnabled { get; set; }
        public bool BookEnabled { get; set; }
        public bool TimesEnabled { get; set; }
    }

    public sealed class OpportunityScoringContext
    {
        public OpportunityScoringContext()
        {
            TickSize = 0.5m;
            SetupScoreThreshold = 60;
            StrongSetupScoreThreshold = 75;
            TopOfBookOnlyScoreCap = 78;
            DerivedTapeScoreCap = 85;
            Backtest = new List<BacktestRow>();
        }

        public decimal TickSize { get; set; }
        public int SetupScoreThreshold { get; set; }
        public int StrongSetupScoreThreshold { get; set; }
        public int TopOfBookOnlyScoreCap { get; set; }
        public int DerivedTapeScoreCap { get; set; }
        public int HistoricalSampleSize { get; set; }
        public long DroppedEvents { get; set; }
        public List<BacktestRow> Backtest { get; set; }

        public static OpportunityScoringContext FromValues(
            decimal tickSize,
            int setupScoreThreshold,
            int strongSetupScoreThreshold,
            int topOfBookOnlyScoreCap,
            int derivedTapeScoreCap,
            QuantResult result,
            int fallbackSampleSize,
            long droppedEvents)
        {
            OpportunityScoringContext context = new OpportunityScoringContext();
            context.TickSize = tickSize <= 0m ? 0.5m : tickSize;
            context.SetupScoreThreshold = setupScoreThreshold;
            context.StrongSetupScoreThreshold = strongSetupScoreThreshold;
            context.TopOfBookOnlyScoreCap = topOfBookOnlyScoreCap;
            context.DerivedTapeScoreCap = derivedTapeScoreCap;
            context.HistoricalSampleSize = result != null && result.Technicals != null && result.Technicals.SampleSize > 0
                ? result.Technicals.SampleSize
                : Math.Max(0, fallbackSampleSize);
            context.DroppedEvents = Math.Max(0, droppedEvents);
            context.Backtest = result == null || result.Backtest == null
                ? new List<BacktestRow>()
                : result.Backtest.ToList();
            return context;
        }
    }
}
