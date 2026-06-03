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
        }

        public RtdConfig Rtd { get; set; }
        public UiConfig Ui { get; set; }
        public StorageConfig Storage { get; set; }
        public DiagnosticsConfig Diagnostics { get; set; }

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
            config.Rtd.Fields = Normalize(config.Rtd.Fields, RtdFieldCatalog.DefaultLiveFields);
            config.Rtd.ProbeFields = Normalize(config.Rtd.ProbeFields, new[] { "HOR", "ULT", "VOL" });

            if (config.Rtd.PollIntervalMs < 10)
            {
                config.Rtd.PollIntervalMs = 50;
            }

            if (config.Rtd.ReconnectIntervalMs < 1000)
            {
                config.Rtd.ReconnectIntervalMs = 5000;
            }

            return config;
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
            PollIntervalMs = 50;
            ReconnectIntervalMs = 5000;
            TickSize = 0.5m;
            Fields = RtdFieldCatalog.DefaultLiveFields.ToList();
            ProbeFields = new List<string> { "HOR", "ULT", "VOL" };
        }

        public string ProgId { get; set; }
        public string Asset { get; set; }
        public int PollIntervalMs { get; set; }
        public int ReconnectIntervalMs { get; set; }
        public decimal TickSize { get; set; }
        public List<string> Fields { get; set; }
        public List<string> ProbeFields { get; set; }
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
}
