using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using RtdDolarNative.Rtd;

namespace RtdDolarNative.Config
{
    public sealed class AppConfig
    {
        public AppConfig()
        {
            Rtd = new RtdConfig();
            Ui = new UiConfig();
            Storage = new StorageConfig();
            Diagnostics = new DiagnosticsConfig();
            Flow = new FlowConfig();
        }

        public RtdConfig Rtd { get; set; }
        public UiConfig Ui { get; set; }
        public StorageConfig Storage { get; set; }
        public DiagnosticsConfig Diagnostics { get; set; }
        public FlowConfig Flow { get; set; }

        public static AppConfig Load(string path)
        {
            AppConfig config = null;

            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                config = new JavaScriptSerializer().Deserialize<AppConfig>(json);
            }

            if (config == null)
            {
                config = new AppConfig();
            }

            config.Rtd = config.Rtd ?? new RtdConfig();
            config.Ui = config.Ui ?? new UiConfig();
            config.Storage = config.Storage ?? new StorageConfig();
            config.Diagnostics = config.Diagnostics ?? new DiagnosticsConfig();
            config.Flow = config.Flow ?? new FlowConfig();
            config.Rtd.NormalizeAssets();
            config.Rtd.NormalizeSources();
            config.Rtd.Fields = Normalize(config.Rtd.Fields, RtdFieldCatalog.DefaultLiveFields);
            config.Rtd.ProbeFields = Normalize(config.Rtd.ProbeFields, new[] { "HOR", "ULT", "VOL" });
            config.Flow.Normalize();

            if (config.Rtd.PollIntervalMs < 150)
            {
                config.Rtd.PollIntervalMs = 150;
            }

            if (config.Rtd.ReconnectIntervalMs < 1000)
            {
                config.Rtd.ReconnectIntervalMs = 5000;
            }

            return config;
        }

        public void Save(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            Rtd = Rtd ?? new RtdConfig();
            Ui = Ui ?? new UiConfig();
            Storage = Storage ?? new StorageConfig();
            Diagnostics = Diagnostics ?? new DiagnosticsConfig();
            Flow = Flow ?? new FlowConfig();
            Rtd.NormalizeAssets();
            Rtd.NormalizeSources();
            Flow.Normalize();

            string directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = new JavaScriptSerializer().Serialize(this);
            File.WriteAllText(path, json);
        }

        private static List<string> Normalize(IEnumerable<string> fields, IEnumerable<string> fallback)
        {
            IEnumerable<string> source = fields == null ? fallback : fields;

            return source
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public sealed class RtdConfig
    {
        public RtdConfig()
        {
            ProgId = "RTDTrading.RTDServer";
            Asset = "WDOFUT_F_0";
            Assets = new List<RtdAssetConfig> { new RtdAssetConfig("WDOFUT_F_0", true) };
            Sources = new List<RtdSourceConfig>();
            PollIntervalMs = 150;
            ReconnectIntervalMs = 5000;
            TickSize = 0.5m;
            Fields = RtdFieldCatalog.DefaultLiveFields.ToList();
            ProbeFields = new List<string> { "HOR", "ULT", "VOL" };
            EnsureDefaultSourcesForAsset(Asset);
        }

        public string ProgId { get; set; }
        public string Asset { get; set; }
        public List<RtdAssetConfig> Assets { get; set; }
        public List<RtdSourceConfig> Sources { get; set; }
        public int PollIntervalMs { get; set; }
        public int ReconnectIntervalMs { get; set; }
        public decimal TickSize { get; set; }
        public List<string> Fields { get; set; }
        public List<string> ProbeFields { get; set; }

        public void NormalizeAssets()
        {
            if (string.IsNullOrWhiteSpace(Asset))
            {
                Asset = "WDOFUT_F_0";
            }

            Asset = NormalizeAsset(Asset);

            List<RtdAssetConfig> normalized = new List<RtdAssetConfig>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (Assets != null)
            {
                foreach (RtdAssetConfig item in Assets)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.Asset))
                    {
                        continue;
                    }

                    string asset = NormalizeAsset(item.Asset);

                    if (seen.Contains(asset))
                    {
                        RtdAssetConfig existing = normalized.FirstOrDefault(x => string.Equals(x.Asset, asset, StringComparison.OrdinalIgnoreCase));

                        if (existing != null)
                        {
                            existing.Enabled = existing.Enabled || item.Enabled;
                        }

                        continue;
                    }

                    normalized.Add(new RtdAssetConfig(asset, item.Enabled));
                    seen.Add(asset);
                }
            }

            if (normalized.Count == 0)
            {
                normalized.Add(new RtdAssetConfig(Asset, true));
                seen.Add(Asset);
            }

            if (!seen.Contains(Asset))
            {
                Asset = normalized[0].Asset;
            }

            Assets = normalized;
        }

        public void NormalizeSources()
        {
            NormalizeAssets();

            if (Sources == null)
            {
                Sources = new List<RtdSourceConfig>();
            }

            if (Sources.Count == 0)
            {
                foreach (RtdAssetConfig asset in Assets)
                {
                    EnsureDefaultSourcesForAsset(asset.Asset);
                }
            }
            else
            {
                foreach (RtdAssetConfig asset in Assets)
                {
                    bool hasAnySource = Sources.Any(x => x != null && string.Equals(x.Asset, asset.Asset, StringComparison.OrdinalIgnoreCase));

                    if (!hasAnySource)
                    {
                        EnsureDefaultSourcesForAsset(asset.Asset);
                    }
                }
            }

            List<RtdSourceConfig> normalized = new List<RtdSourceConfig>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (RtdSourceConfig source in Sources)
            {
                if (source == null)
                {
                    continue;
                }

                source.Normalize(this);

                if (string.IsNullOrWhiteSpace(source.Name))
                {
                    continue;
                }

                string key = source.Name + "|" + source.Role + "|" + source.Asset;

                if (seen.Contains(key))
                {
                    continue;
                }

                normalized.Add(source);
                seen.Add(key);
            }

            Sources = normalized;
        }

        public void EnsureDefaultSourcesForAsset(string asset)
        {
            string normalizedAsset = NormalizeAsset(asset);

            if (string.IsNullOrWhiteSpace(normalizedAsset))
            {
                normalizedAsset = NormalizeAsset(Asset);
            }

            if (string.IsNullOrWhiteSpace(normalizedAsset))
            {
                normalizedAsset = "WDOFUT_F_0";
            }

            if (Sources == null)
            {
                Sources = new List<RtdSourceConfig>();
            }

            AddDefaultSourceIfMissing("PrecoVolume", "PriceVolume", normalizedAsset, true, new[] { "HOR", "ULT", "QUL", "VOL", "NEG", "ABE", "MAX", "MIN", "MED" });
            AddDefaultSourceIfMissing("TopBook", "TopBook", normalizedAsset, true, new[] { "OCP", "OVD", "VOC", "VOV" });
            AddDefaultSourceIfMissing("BookDepth", "BookDepth", normalizedAsset, false, new string[0]);
            AddDefaultSourceIfMissing("TimesAndTrades", "TimesAndTrades", normalizedAsset, false, new string[0]);
        }

        public List<RtdSourceConfig> GetEnabledSources()
        {
            NormalizeSources();

            HashSet<string> enabledAssets = new HashSet<string>(GetEnabledAssets(), StringComparer.OrdinalIgnoreCase);

            return Sources
                .Where(x => x.Enabled && enabledAssets.Contains(x.Asset))
                .ToList();
        }

        public List<RtdSubscriptionSpec> GetSubscriptions()
        {
            NormalizeSources();

            List<RtdSubscriptionSpec> specs = new List<RtdSubscriptionSpec>();
            HashSet<string> enabledAssets = new HashSet<string>(GetEnabledAssets(), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, RtdSubscriptionSpec> byKey = new Dictionary<string, RtdSubscriptionSpec>(StringComparer.OrdinalIgnoreCase);
            List<RtdSourceConfig> enabledSources = Sources.Where(x => x.Enabled && enabledAssets.Contains(x.Asset)).ToList();

            if (enabledSources.Count == 0)
            {
                foreach (string asset in enabledAssets)
                {
                    foreach (string field in NormalizeFieldList(Fields, RtdFieldCatalog.DefaultLiveFields))
                    {
                        AddSubscription(byKey, specs, asset, field, "Legacy", "PriceVolume");
                    }
                }

                return specs;
            }

            foreach (RtdSourceConfig source in enabledSources)
            {
                foreach (string field in NormalizeFieldList(source.Fields, new string[0]))
                {
                    AddSubscription(byKey, specs, source.Asset, field, source.Name, source.Role);
                }
            }

            return specs;
        }

        public int GetEffectivePollIntervalMs()
        {
            int poll = PollIntervalMs <= 0 ? 150 : PollIntervalMs;

            foreach (RtdSourceConfig source in GetEnabledSources())
            {
                if (source.PollIntervalMs > 0)
                {
                    poll = Math.Min(poll, source.PollIntervalMs);
                }
            }

            return Math.Max(poll, 150);
        }

        public List<string> GetEnabledAssets()
        {
            NormalizeAssets();

            return Assets
                .Where(x => x.Enabled)
                .Select(x => x.Asset)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public RtdAssetConfig FindAsset(string asset)
        {
            if (string.IsNullOrWhiteSpace(asset))
            {
                return null;
            }

            NormalizeAssets();
            string normalized = NormalizeAsset(asset);
            return Assets.FirstOrDefault(x => string.Equals(x.Asset, normalized, StringComparison.OrdinalIgnoreCase));
        }

        public static string NormalizeAsset(string asset)
        {
            return string.IsNullOrWhiteSpace(asset) ? string.Empty : asset.Trim().ToUpperInvariant();
        }

        private void AddDefaultSourceIfMissing(string baseName, string role, string asset, bool enabled, IEnumerable<string> fields)
        {
            string name = baseName + "-" + asset;

            if (Sources.Any(x => x != null && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            RtdSourceConfig source = new RtdSourceConfig();
            source.Name = name;
            source.Role = role;
            source.Enabled = enabled;
            source.ProgId = ProgId;
            source.Asset = asset;
            source.PollIntervalMs = PollIntervalMs;
            source.Fields = fields.ToList();
            Sources.Add(source);
        }

        private static List<string> NormalizeFieldList(IEnumerable<string> fields, IEnumerable<string> fallback)
        {
            IEnumerable<string> source = fields == null ? fallback : fields;

            return source
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void AddSubscription(Dictionary<string, RtdSubscriptionSpec> byKey, List<RtdSubscriptionSpec> specs, string asset, string field, string sourceName, string role)
        {
            string normalizedAsset = NormalizeAsset(asset);
            string normalizedField = string.IsNullOrWhiteSpace(field) ? string.Empty : field.Trim().ToUpperInvariant();

            if (string.IsNullOrWhiteSpace(normalizedAsset) || string.IsNullOrWhiteSpace(normalizedField))
            {
                return;
            }

            string key = normalizedAsset + ":" + normalizedField;

            if (byKey.ContainsKey(key))
            {
                return;
            }

            RtdSubscriptionSpec spec = new RtdSubscriptionSpec();
            spec.Asset = normalizedAsset;
            spec.Field = normalizedField;
            spec.SourceName = sourceName;
            spec.Role = role;
            byKey[key] = spec;
            specs.Add(spec);
        }
    }

    public sealed class RtdSourceConfig
    {
        public RtdSourceConfig()
        {
            Name = string.Empty;
            Role = "PriceVolume";
            Enabled = true;
            Fields = new List<string>();
            PollIntervalMs = 150;
        }

        public string Name { get; set; }
        public string Role { get; set; }
        public bool Enabled { get; set; }
        public string ProgId { get; set; }
        public string Asset { get; set; }
        public List<string> Fields { get; set; }
        public int PollIntervalMs { get; set; }

        public void Normalize(RtdConfig config)
        {
            Role = string.IsNullOrWhiteSpace(Role) ? "PriceVolume" : Role.Trim();
            Asset = RtdConfig.NormalizeAsset(string.IsNullOrWhiteSpace(Asset) ? config.Asset : Asset);
            ProgId = string.IsNullOrWhiteSpace(ProgId) ? config.ProgId : ProgId.Trim();
            PollIntervalMs = PollIntervalMs <= 0 ? config.PollIntervalMs : PollIntervalMs;
            Fields = RtdConfig.NormalizeAsset(Asset).Length == 0
                ? new List<string>()
                : (Fields ?? new List<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim().ToUpperInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

            if (string.IsNullOrWhiteSpace(Name))
            {
                Name = Role + "-" + Asset;
            }
            else
            {
                Name = Name.Trim();
            }
        }
    }

    public sealed class RtdSubscriptionSpec
    {
        public string Asset { get; set; }
        public string Field { get; set; }
        public string SourceName { get; set; }
        public string Role { get; set; }

        public string Key
        {
            get { return Asset + ":" + Field; }
        }
    }

    public sealed class RtdAssetConfig
    {
        public RtdAssetConfig()
        {
        }

        public RtdAssetConfig(string asset, bool enabled)
        {
            Asset = asset;
            Enabled = enabled;
        }

        public string Asset { get; set; }
        public bool Enabled { get; set; }
    }

    public sealed class UiConfig
    {
        public UiConfig()
        {
            FastIntervalMs = 33;
            QuantIntervalMs = 500;
            ChartIntervalMs = 1000;
            DomTicksEachSide = 100;
            TapeCapacity = 500;
        }

        public int FastIntervalMs { get; set; }
        public int QuantIntervalMs { get; set; }
        public int ChartIntervalMs { get; set; }
        public int DomTicksEachSide { get; set; }
        public int TapeCapacity { get; set; }
    }

    public sealed class StorageConfig
    {
        public StorageConfig()
        {
            Enabled = false;
            SnapshotIntervalMs = 1000;
            ConnectionString = "Data Source=data/marketdata.sqlite;Version=3;";
        }

        public bool Enabled { get; set; }
        public int SnapshotIntervalMs { get; set; }
        public string ConnectionString { get; set; }
    }

    public sealed class DiagnosticsConfig
    {
        public DiagnosticsConfig()
        {
            LogPath = "logs/rtd-dolar-native.log";
            LogEveryTick = false;
        }

        public string LogPath { get; set; }
        public bool LogEveryTick { get; set; }
    }

    public sealed class FlowConfig
    {
        public FlowConfig()
        {
            Enabled = true;
            CoalescingMs = 75;
            BroadcastIntervalMs = 500;
            MaxQueueSize = 2048;
            MaxTradeBuffer = 5000;
            SignalCooldownMs = 8000;
            ValueAreaPercent = 0.70m;
            HvnThresholdRatio = 0.70m;
            LvnThresholdRatio = 0.25m;
            TopOfBookOnlyScoreCap = 78;
            DerivedTapeScoreCap = 85;
            SetupScoreThreshold = 60;
            StrongSetupScoreThreshold = 75;
            UseMedAsVwapFallback = true;
        }

        public bool Enabled { get; set; }
        public int CoalescingMs { get; set; }
        public int BroadcastIntervalMs { get; set; }
        public int MaxQueueSize { get; set; }
        public int MaxTradeBuffer { get; set; }
        public int SignalCooldownMs { get; set; }
        public decimal ValueAreaPercent { get; set; }
        public decimal HvnThresholdRatio { get; set; }
        public decimal LvnThresholdRatio { get; set; }
        public int TopOfBookOnlyScoreCap { get; set; }
        public int DerivedTapeScoreCap { get; set; }
        public int SetupScoreThreshold { get; set; }
        public int StrongSetupScoreThreshold { get; set; }
        public bool UseMedAsVwapFallback { get; set; }

        public void Normalize()
        {
            CoalescingMs = Math.Max(25, CoalescingMs);
            BroadcastIntervalMs = Math.Max(250, BroadcastIntervalMs);
            MaxQueueSize = Math.Max(128, MaxQueueSize);
            MaxTradeBuffer = Math.Max(250, MaxTradeBuffer);
            SignalCooldownMs = Math.Max(1000, SignalCooldownMs);

            if (ValueAreaPercent <= 0m || ValueAreaPercent > 1m)
            {
                ValueAreaPercent = 0.70m;
            }

            if (HvnThresholdRatio <= 0m || HvnThresholdRatio > 1m)
            {
                HvnThresholdRatio = 0.70m;
            }

            if (LvnThresholdRatio <= 0m || LvnThresholdRatio > 1m)
            {
                LvnThresholdRatio = 0.25m;
            }

            TopOfBookOnlyScoreCap = Math.Max(1, Math.Min(100, TopOfBookOnlyScoreCap));
            DerivedTapeScoreCap = Math.Max(TopOfBookOnlyScoreCap, Math.Min(100, DerivedTapeScoreCap));
            SetupScoreThreshold = Math.Max(1, Math.Min(100, SetupScoreThreshold));
            StrongSetupScoreThreshold = Math.Max(SetupScoreThreshold, Math.Min(100, StrongSetupScoreThreshold));
        }
    }
}
