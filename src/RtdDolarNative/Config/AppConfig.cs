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

            string json = null;

            if (File.Exists(path))
            {
                json = File.ReadAllText(path);
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
            config.Ui.Normalize();
            config.Flow.Normalize();

            if (!HasToken(json, "ShowChartCandles"))
            {
                config.Ui.ShowChartCandles = true;
            }

            if (!HasToken(json, "ShowChartPriceGrid"))
            {
                config.Ui.ShowChartPriceGrid = true;
            }

            if (!HasToken(json, "ShowChartCurrentPriceLine"))
            {
                config.Ui.ShowChartCurrentPriceLine = true;
            }

            if (!HasToken(json, "ShowChartConfluenceLevels"))
            {
                config.Ui.ShowChartConfluenceLevels = true;
            }

            if (!HasToken(json, "ShowChartKeyLevels"))
            {
                config.Ui.ShowChartKeyLevels = true;
            }

            if (!HasToken(json, "ShowChartRtdLevels"))
            {
                config.Ui.ShowChartRtdLevels = true;
            }

            if (!HasToken(json, "ShowChartProfileLevels"))
            {
                config.Ui.ShowChartProfileLevels = true;
            }

            if (!HasToken(json, "ShowChartTechnicalLevels"))
            {
                config.Ui.ShowChartTechnicalLevels = true;
            }

            if (!HasToken(json, "ShowChartMarketLevels"))
            {
                config.Ui.ShowChartMarketLevels = true;
            }

            if (!HasToken(json, "ShowChartPercentLevels"))
            {
                config.Ui.ShowChartPercentLevels = true;
            }

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

        private static bool HasToken(string json, string token)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            return json.IndexOf("\"" + token + "\"", StringComparison.OrdinalIgnoreCase) >= 0;
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
            Ui.Normalize();
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
        public const string RtdCompleteRole = "RtdComplete";

        public static readonly string[] DefaultQuoteFields = new[]
        {
            "DAT", "HOR", "ULT", "ABE", "MAX", "MIN", "FEC", "VAR", "VARPTS", "NEG", "QTT", "VOL",
            "OCP", "OVD", "AJU", "AJA", "103", "98", "100", "99", "67"
        };

        public static readonly string[] DefaultBookFields = new[]
        {
            "HORC", "ACP", "VOC", "OCP", "OVD", "VOV", "AVD", "HORV"
        };

        public static readonly string[] DefaultTimesFields = new[]
        {
            "DAT", "ACP", "PRE", "QUL", "AVD", "AGR"
        };

        public static readonly string[] DefaultInfoFields = new[]
        {
            "ATV", "TAB"
        };

        public RtdConfig()
        {
            ProgId = "RTDTrading.RTDServer";
            Asset = "WDOFUT_F_0";
            Assets = new List<RtdAssetConfig> { new RtdAssetConfig("WDOFUT_F_0", true) };
            Sources = new List<RtdSourceConfig>();
            AutoConnect = false;
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
        public bool AutoConnect { get; set; }
        public int PollIntervalMs { get; set; }
        public int ReconnectIntervalMs { get; set; }
        public decimal TickSize { get; set; }
        public List<string> Fields { get; set; }
        public List<string> ProbeFields { get; set; }

        public void NormalizeAssets()
        {
            Asset = NormalizeAsset(Asset);

            List<RtdAssetConfig> normalized = new List<RtdAssetConfig>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (Assets != null)
            {
                foreach (RtdAssetConfig item in Assets)
                {
                    if (item == null)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(item.Asset) && string.IsNullOrWhiteSpace(item.QuoteCode))
                    {
                        continue;
                    }

                    item.Normalize();
                    string asset = NormalizeAsset(string.IsNullOrWhiteSpace(item.Asset) ? item.QuoteCode : item.Asset);

                    if (string.IsNullOrWhiteSpace(asset))
                    {
                        continue;
                    }

                    if (seen.Contains(asset))
                    {
                        RtdAssetConfig existing = normalized.FirstOrDefault(x => string.Equals(x.Asset, asset, StringComparison.OrdinalIgnoreCase));

                        if (existing != null)
                        {
                            existing.Enabled = existing.Enabled || item.Enabled;
                            existing.QuoteEnabled = existing.QuoteEnabled || item.QuoteEnabled;
                            existing.BookEnabled = existing.BookEnabled || item.BookEnabled;
                            existing.TimesEnabled = existing.TimesEnabled || item.TimesEnabled;

                            if (!string.IsNullOrWhiteSpace(item.Name))
                            {
                                existing.Name = item.Name;
                            }

                            if (!string.IsNullOrWhiteSpace(item.QuoteCode))
                            {
                                existing.QuoteCode = item.QuoteCode;
                            }

                            if (!string.IsNullOrWhiteSpace(item.BookTopic))
                            {
                                existing.BookTopic = item.BookTopic;
                            }

                            if (!string.IsNullOrWhiteSpace(item.TimesTopic))
                            {
                                existing.TimesTopic = item.TimesTopic;
                            }

                            if (!string.IsNullOrWhiteSpace(item.CsvPath))
                            {
                                existing.CsvPath = item.CsvPath;
                            }
                        }

                        continue;
                    }

                    RtdAssetConfig copy = item.CloneNormalized();
                    normalized.Add(copy);
                    seen.Add(asset);
                }
            }

            if (normalized.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(Asset))
                {
                    normalized.Add(new RtdAssetConfig(Asset, true));
                    seen.Add(Asset);
                }
                else
                {
                    Assets = normalized;
                    return;
                }
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
                    EnsureDefaultSourcesForAsset(asset.Asset);
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

            RtdAssetConfig assetConfig = FindAsset(normalizedAsset) ?? new RtdAssetConfig(normalizedAsset, true);
            assetConfig.Normalize();
            RemoveLegacyGeneratedSources(normalizedAsset);
            AddOrUpdateDefaultSource("Cotacao", "PriceVolume", normalizedAsset, assetConfig.QuoteEnabled, assetConfig.QuoteCode, DefaultQuoteFields, null, null, new string[0]);
            AddOrUpdateDefaultSource("Book", "BookDepth", normalizedAsset, assetConfig.BookEnabled, assetConfig.BookTopic, DefaultBookFields, 0, 49, DefaultInfoFields);
            AddOrUpdateDefaultSource("Times", "TimesAndTrades", normalizedAsset, assetConfig.TimesEnabled, assetConfig.TimesTopic, DefaultTimesFields, 0, 99, DefaultInfoFields);
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
                return specs;
            }

            foreach (RtdSourceConfig source in enabledSources)
            {
                string topic = string.IsNullOrWhiteSpace(source.Topic) ? source.Asset : source.Topic.Trim();
                List<string> fields = NormalizeFieldList(source.Fields, new string[0]);

                if (source.IndexFrom.HasValue || source.IndexTo.HasValue)
                {
                    int first = source.IndexFrom.HasValue ? Math.Max(0, source.IndexFrom.Value) : 0;
                    int last = source.IndexTo.HasValue ? Math.Max(first, source.IndexTo.Value) : first;

                    foreach (string infoField in NormalizeFieldList(source.InfoFields, new string[0]))
                    {
                        AddSubscription(byKey, specs, source.Asset, topic, "INFO", null, infoField, source.Name, source.Role);
                    }

                    for (int index = first; index <= last; index++)
                    {
                        foreach (string field in fields)
                        {
                            AddSubscription(byKey, specs, source.Asset, topic, field, index, null, source.Name, source.Role);
                        }
                    }

                    continue;
                }

                foreach (string field in fields)
                {
                    AddSubscription(byKey, specs, source.Asset, topic, field, null, null, source.Name, source.Role);
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

        public List<string> GetRtdCompleteFields(string asset)
        {
            RtdSourceConfig source = FindRtdCompleteSource(asset);

            if (source == null || !source.Enabled)
            {
                return new List<string>();
            }

            return NormalizeFieldList(source.Fields, new string[0]);
        }

        public void SetRtdCompleteSource(string asset, IEnumerable<string> fields)
        {
            string normalizedAsset = NormalizeAsset(string.IsNullOrWhiteSpace(asset) ? Asset : asset);
            List<string> normalizedFields = NormalizeFieldList(fields, new string[0]);

            if (string.IsNullOrWhiteSpace(normalizedAsset) || normalizedFields.Count == 0)
            {
                ClearRtdCompleteSource(asset);
                return;
            }

            if (Sources == null)
            {
                Sources = new List<RtdSourceConfig>();
            }

            EnsureDefaultSourcesForAsset(normalizedAsset);

            string name = RtdCompleteSourceName(normalizedAsset);
            RtdSourceConfig source = Sources.FirstOrDefault(x => x != null && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

            if (source == null)
            {
                source = new RtdSourceConfig();
                Sources.Add(source);
            }

            source.Name = name;
            source.Role = RtdCompleteRole;
            source.Enabled = true;
            source.ProgId = ProgId;
            source.Asset = normalizedAsset;
            source.Topic = normalizedAsset;
            source.PollIntervalMs = PollIntervalMs;
            source.Fields = normalizedFields;
            source.IndexFrom = null;
            source.IndexTo = null;
            source.InfoFields = new List<string>();
        }

        public void ClearRtdCompleteSource(string asset)
        {
            if (Sources == null)
            {
                return;
            }

            string normalizedAsset = NormalizeAsset(string.IsNullOrWhiteSpace(asset) ? Asset : asset);
            Sources.RemoveAll(x => x != null &&
                string.Equals(x.Role, RtdCompleteRole, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(normalizedAsset) || string.Equals(NormalizeAsset(x.Asset), normalizedAsset, StringComparison.OrdinalIgnoreCase)));
        }

        public static string RtdCompleteSourceName(string asset)
        {
            string normalizedAsset = NormalizeAsset(asset);
            return RtdCompleteRole + "-" + normalizedAsset;
        }

        public static string NormalizeAsset(string asset)
        {
            return string.IsNullOrWhiteSpace(asset) ? string.Empty : asset.Trim().ToUpperInvariant();
        }

        private RtdSourceConfig FindRtdCompleteSource(string asset)
        {
            if (Sources == null)
            {
                return null;
            }

            string normalizedAsset = NormalizeAsset(string.IsNullOrWhiteSpace(asset) ? Asset : asset);
            string name = RtdCompleteSourceName(normalizedAsset);

            return Sources.FirstOrDefault(x => x != null &&
                string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Role, RtdCompleteRole, StringComparison.OrdinalIgnoreCase));
        }

        private void RemoveLegacyGeneratedSources(string asset)
        {
            if (Sources == null)
            {
                return;
            }

            Sources.RemoveAll(x => x != null &&
                string.Equals(NormalizeAsset(x.Asset), NormalizeAsset(asset), StringComparison.OrdinalIgnoreCase) &&
                IsLegacyGeneratedSourceName(x.Name));
        }

        private static bool IsLegacyGeneratedSourceName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            return name.StartsWith("PrecoVolume-", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("TopBook-", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("BookDepth-", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("TimesAndTrades-", StringComparison.OrdinalIgnoreCase);
        }

        private void AddOrUpdateDefaultSource(string baseName, string role, string asset, bool enabled, string topic, IEnumerable<string> fields, int? indexFrom, int? indexTo, IEnumerable<string> infoFields)
        {
            string name = baseName + "-" + asset;
            RtdSourceConfig source = Sources.FirstOrDefault(x => x != null && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

            if (source == null)
            {
                source = new RtdSourceConfig();
                Sources.Add(source);
            }

            source.Name = name;
            source.Role = role;
            source.Enabled = enabled;
            source.ProgId = ProgId;
            source.Asset = asset;
            source.Topic = string.IsNullOrWhiteSpace(topic) ? asset : topic.Trim().ToUpperInvariant();
            source.PollIntervalMs = PollIntervalMs;
            source.Fields = fields.ToList();
            source.IndexFrom = indexFrom;
            source.IndexTo = indexTo;
            source.InfoFields = infoFields == null ? new List<string>() : infoFields.ToList();
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
            AddSubscription(byKey, specs, asset, asset, field, null, null, sourceName, role);
        }

        private static void AddSubscription(Dictionary<string, RtdSubscriptionSpec> byKey, List<RtdSubscriptionSpec> specs, string asset, string topic, string field, int? index, string infoField, string sourceName, string role)
        {
            string normalizedAsset = NormalizeAsset(asset);
            string normalizedTopic = string.IsNullOrWhiteSpace(topic) ? normalizedAsset : topic.Trim().ToUpperInvariant();
            string normalizedField = string.IsNullOrWhiteSpace(field) ? string.Empty : field.Trim().ToUpperInvariant();
            string normalizedInfo = string.IsNullOrWhiteSpace(infoField) ? string.Empty : infoField.Trim().ToUpperInvariant();

            if (string.IsNullOrWhiteSpace(normalizedAsset) || string.IsNullOrWhiteSpace(normalizedTopic) || string.IsNullOrWhiteSpace(normalizedField))
            {
                return;
            }

            string snapshotField = BuildSnapshotField(role, normalizedField, index, normalizedInfo);
            string argsKey = normalizedTopic + ":" + normalizedField + ":" + (index.HasValue ? index.Value.ToString() : normalizedInfo);
            string key = normalizedAsset + ":" + sourceName + ":" + snapshotField + ":" + argsKey;

            if (byKey.ContainsKey(key))
            {
                return;
            }

            RtdSubscriptionSpec spec = new RtdSubscriptionSpec();
            spec.Asset = normalizedAsset;
            spec.Topic = normalizedTopic;
            spec.Field = snapshotField;
            spec.RtdField = normalizedField;
            spec.Index = index;
            spec.InfoField = normalizedInfo;
            spec.SourceName = sourceName;
            spec.Role = role;
            spec.Arguments = new List<object>();
            spec.Arguments.Add(normalizedTopic);
            spec.Arguments.Add(normalizedField);

            if (index.HasValue)
            {
                spec.Arguments.Add(index.Value);
            }
            else if (!string.IsNullOrWhiteSpace(normalizedInfo))
            {
                spec.Arguments.Add(normalizedInfo);
            }

            byKey[key] = spec;
            specs.Add(spec);
        }

        private static string BuildSnapshotField(string role, string field, int? index, string infoField)
        {
            if (string.Equals(role, "BookDepth", StringComparison.OrdinalIgnoreCase))
            {
                return "BOOK_" + field + "_" + (index.HasValue ? index.Value.ToString() : infoField);
            }

            if (string.Equals(role, "TimesAndTrades", StringComparison.OrdinalIgnoreCase))
            {
                return "TIMES_" + field + "_" + (index.HasValue ? index.Value.ToString() : infoField);
            }

            return field;
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
            InfoFields = new List<string>();
            PollIntervalMs = 150;
        }

        public string Name { get; set; }
        public string Role { get; set; }
        public bool Enabled { get; set; }
        public string ProgId { get; set; }
        public string Asset { get; set; }
        public string Topic { get; set; }
        public List<string> Fields { get; set; }
        public List<string> InfoFields { get; set; }
        public int? IndexFrom { get; set; }
        public int? IndexTo { get; set; }
        public int PollIntervalMs { get; set; }

        public void Normalize(RtdConfig config)
        {
            Role = string.IsNullOrWhiteSpace(Role) ? "PriceVolume" : Role.Trim();
            Asset = RtdConfig.NormalizeAsset(string.IsNullOrWhiteSpace(Asset) ? config.Asset : Asset);
            Topic = string.IsNullOrWhiteSpace(Topic) ? Asset : Topic.Trim().ToUpperInvariant();
            ProgId = string.IsNullOrWhiteSpace(ProgId) ? config.ProgId : ProgId.Trim();
            PollIntervalMs = PollIntervalMs <= 0 ? config.PollIntervalMs : PollIntervalMs;
            Fields = RtdConfig.NormalizeAsset(Asset).Length == 0
                ? new List<string>()
                : (Fields ?? new List<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim().ToUpperInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            InfoFields = (InfoFields ?? new List<string>())
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
        public string Topic { get; set; }
        public string Field { get; set; }
        public string RtdField { get; set; }
        public int? Index { get; set; }
        public string InfoField { get; set; }
        public List<object> Arguments { get; set; }
        public string SourceName { get; set; }
        public string Role { get; set; }

        public string Key
        {
            get { return Asset + ":" + Field + ":" + Topic; }
        }
    }

    public sealed class RtdAssetConfig
    {
        public RtdAssetConfig()
        {
            Name = string.Empty;
            QuoteCode = string.Empty;
            BookTopic = "BOOK0";
            TimesTopic = "T&T0";
            CsvPath = string.Empty;
            Enabled = true;
            QuoteEnabled = true;
            BookEnabled = true;
            TimesEnabled = true;
        }

        public RtdAssetConfig(string asset, bool enabled)
        {
            Asset = RtdConfig.NormalizeAsset(asset);
            Name = Asset;
            QuoteCode = Asset;
            BookTopic = "BOOK0";
            TimesTopic = "T&T0";
            CsvPath = string.Empty;
            Enabled = enabled;
            QuoteEnabled = true;
            BookEnabled = true;
            TimesEnabled = true;
        }

        public string Name { get; set; }
        public string Asset { get; set; }
        public string QuoteCode { get; set; }
        public string BookTopic { get; set; }
        public string TimesTopic { get; set; }
        public string CsvPath { get; set; }
        public bool Enabled { get; set; }
        public bool QuoteEnabled { get; set; }
        public bool BookEnabled { get; set; }
        public bool TimesEnabled { get; set; }

        public void Normalize()
        {
            Asset = RtdConfig.NormalizeAsset(string.IsNullOrWhiteSpace(Asset) ? QuoteCode : Asset);

            if (string.IsNullOrWhiteSpace(Asset))
            {
                return;
            }

            Name = string.IsNullOrWhiteSpace(Name) ? Asset : Name.Trim();
            QuoteCode = string.IsNullOrWhiteSpace(QuoteCode) ? Asset : QuoteCode.Trim().ToUpperInvariant();
            BookTopic = string.IsNullOrWhiteSpace(BookTopic) ? "BOOK0" : BookTopic.Trim().ToUpperInvariant();
            TimesTopic = string.IsNullOrWhiteSpace(TimesTopic) ? "T&T0" : TimesTopic.Trim().ToUpperInvariant();
            CsvPath = string.IsNullOrWhiteSpace(CsvPath) ? string.Empty : CsvPath.Trim();
        }

        public RtdAssetConfig CloneNormalized()
        {
            Normalize();

            RtdAssetConfig clone = new RtdAssetConfig();
            clone.Name = Name;
            clone.Asset = Asset;
            clone.QuoteCode = QuoteCode;
            clone.BookTopic = BookTopic;
            clone.TimesTopic = TimesTopic;
            clone.CsvPath = CsvPath;
            clone.Enabled = Enabled;
            clone.QuoteEnabled = QuoteEnabled;
            clone.BookEnabled = BookEnabled;
            clone.TimesEnabled = TimesEnabled;
            return clone;
        }
    }

    public sealed class UiConfig
    {
        public static readonly int DefaultCalculationDays = 45;
        public static readonly int[] AllowedCalculationDays = new[] { 21, 45, 63, 90 };
        public static readonly int DefaultChartTimeframeIndex = 0;
        public static readonly int[] AllowedChartTimeframeIndexes = new[] { 0, 1, 2 };
        public static readonly int DefaultPriceGridTickInterval = 10;
        public static readonly int[] AllowedPriceGridTickIntervals = new[] { 5, 10, 50, 100 };
        public static readonly int DefaultCandleSpacingPercent = 100;
        public static readonly int[] AllowedCandleSpacingPercents = new[] { 75, 100, 125, 150 };

        public UiConfig()
        {
            FastIntervalMs = 33;
            QuantIntervalMs = 500;
            ChartIntervalMs = 1000;
            DomTicksEachSide = 100;
            TapeCapacity = 500;
            CalculationDays = DefaultCalculationDays;
            ChartTimeframeIndex = DefaultChartTimeframeIndex;
            PriceGridTickInterval = DefaultPriceGridTickInterval;
            CandleSpacingPercent = DefaultCandleSpacingPercent;
            ShowChartCandles = true;
            ShowChartPriceGrid = true;
            ShowChartCurrentPriceLine = true;
            ShowChartConfluenceLevels = true;
            ShowChartKeyLevels = true;
            ShowChartRtdLevels = true;
            ShowChartProfileLevels = true;
            ShowChartTechnicalLevels = true;
            ShowChartMarketLevels = true;
            ShowChartPercentLevels = true;
        }

        public int FastIntervalMs { get; set; }
        public int QuantIntervalMs { get; set; }
        public int ChartIntervalMs { get; set; }
        public int DomTicksEachSide { get; set; }
        public int TapeCapacity { get; set; }
        public int CalculationDays { get; set; }
        public int ChartTimeframeIndex { get; set; }
        public int PriceGridTickInterval { get; set; }
        public int CandleSpacingPercent { get; set; }
        public bool ShowChartCandles { get; set; }
        public bool ShowChartPriceGrid { get; set; }
        public bool ShowChartCurrentPriceLine { get; set; }
        public bool ShowChartConfluenceLevels { get; set; }
        public bool ShowChartKeyLevels { get; set; }
        public bool ShowChartRtdLevels { get; set; }
        public bool ShowChartProfileLevels { get; set; }
        public bool ShowChartTechnicalLevels { get; set; }
        public bool ShowChartMarketLevels { get; set; }
        public bool ShowChartPercentLevels { get; set; }

        public void Normalize()
        {
            CalculationDays = NormalizeCalculationDays(CalculationDays);
            ChartTimeframeIndex = NormalizeChartTimeframeIndex(ChartTimeframeIndex);
            PriceGridTickInterval = NormalizePriceGridTickInterval(PriceGridTickInterval);
            CandleSpacingPercent = NormalizeCandleSpacingPercent(CandleSpacingPercent);
        }

        public static int NormalizeCalculationDays(int days)
        {
            return AllowedCalculationDays.Contains(days) ? days : DefaultCalculationDays;
        }

        public static int NormalizeChartTimeframeIndex(int timeframeIndex)
        {
            return AllowedChartTimeframeIndexes.Contains(timeframeIndex) ? timeframeIndex : DefaultChartTimeframeIndex;
        }

        public static int NormalizePriceGridTickInterval(int tickInterval)
        {
            return AllowedPriceGridTickIntervals.Contains(tickInterval) ? tickInterval : DefaultPriceGridTickInterval;
        }

        public static int NormalizeCandleSpacingPercent(int spacingPercent)
        {
            return AllowedCandleSpacingPercents.Contains(spacingPercent) ? spacingPercent : DefaultCandleSpacingPercent;
        }
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
