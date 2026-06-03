using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using RtdDolarNative.Config;
using RtdDolarNative.Logging;
using RtdDolarNative.LowLatency;
using RtdDolarNative.MarketData;
using RtdDolarNative.Rtd;

namespace RtdDolarNative
{
    public partial class MainWindow : Window
    {
        private readonly AppConfig _config;
        private readonly Logger _log;
        private readonly LatestSnapshotBuffer _snapshotBuffer;
        private readonly RtdProbeService _probeService;
        private readonly DispatcherTimer _fastTimer;
        private readonly CultureInfo _ptBr = new CultureInfo("pt-BR");
        private long _lastVersion;
        private bool _manualMode;

        public MainWindow()
        {
            InitializeComponent();

            _config = AppConfig.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json"));
            _log = new Logger(ResolvePath(_config.Diagnostics.LogPath));
            _snapshotBuffer = new LatestSnapshotBuffer();
            _probeService = new RtdProbeService(_config.Rtd, _config.Diagnostics, _log, _snapshotBuffer);
            _probeService.StatusChanged += ProbeService_StatusChanged;

            _fastTimer = new DispatcherTimer(DispatcherPriority.Render);
            _fastTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(_config.Ui.FastIntervalMs, 16));
            _fastTimer.Tick += FastTimer_Tick;

            _lastVersion = -1;
            InitializeStaticText();
            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _fastTimer.Start();
            StartRtd();
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            _fastTimer.Stop();
            _probeService.Dispose();
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_probeService.IsRunning)
            {
                StopRtd();
            }
            else
            {
                StartRtd();
            }
        }

        private void ManualButton_Click(object sender, RoutedEventArgs e)
        {
            _manualMode = !_manualMode;

            if (_manualMode)
            {
                StopRtd();

                MarketSnapshot snapshot = new MarketSnapshot();
                snapshot.Asset = _config.Rtd.Asset;
                snapshot.Status = "manual";
                _snapshotBuffer.Publish(snapshot);

                ManualButton.Content = "Sair manual";
                ConnectButton.IsEnabled = false;
            }
            else
            {
                ManualButton.Content = "Modo manual";
                ConnectButton.IsEnabled = true;
                StartRtd();
            }
        }

        private void ProbeService_StatusChanged(string status, Exception error)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                StatusText.Text = status;
                StatusBadgeBorder.Background = StatusBrush(status);
                LastErrorText.Text = FormatError(error);
            }));
        }

        private void FastTimer_Tick(object sender, EventArgs e)
        {
            MarketSnapshot snapshot;
            long version;

            if (!_snapshotBuffer.TryRead(out snapshot, out version))
            {
                SnapshotAgeText.Text = "-";
                UpdatesText.Text = _probeService.UpdatesReceived.ToString(_ptBr);
                return;
            }

            if (version != _lastVersion)
            {
                _lastVersion = version;
                ApplySnapshot(snapshot);
            }

            TimeSpan age = DateTimeOffset.Now - snapshot.LocalTimestamp;
            SnapshotAgeText.Text = Math.Max(0, (int)age.TotalMilliseconds).ToString(_ptBr) + " ms";
            UpdatesText.Text = _probeService.UpdatesReceived.ToString(_ptBr);
        }

        private void StartRtd()
        {
            _manualMode = false;
            ManualButton.Content = "Modo manual";
            ConnectButton.IsEnabled = true;
            ConnectButton.Content = "Desconectar";
            _probeService.Start();
        }

        private void StopRtd()
        {
            _probeService.Stop();
            ConnectButton.Content = "Conectar";
        }

        private void InitializeStaticText()
        {
            AssetText.Text = _config.Rtd.Asset;
            ArchitectureText.Text = Environment.Is64BitProcess ? "x64" : "x86";
            FieldsText.Text = string.Join(", ", _config.Rtd.ProbeFields.ToArray());
            PollText.Text = _config.Rtd.PollIntervalMs.ToString(_ptBr) + " ms";
            StatusText.Text = "starting";
            StatusBadgeBorder.Background = StatusBrush("starting");
            LastErrorText.Text = "-";
        }

        private void ApplySnapshot(MarketSnapshot snapshot)
        {
            AssetText.Text = string.IsNullOrWhiteSpace(snapshot.Asset) ? _config.Rtd.Asset : snapshot.Asset;
            ProfitTimeText.Text = EmptyToDash(snapshot.HoraProfit);
            LocalUpdateText.Text = snapshot.LocalTimestamp.ToString("HH:mm:ss.fff", _ptBr);
            PriceText.Text = FormatDecimal(snapshot.Ultimo, "N2");
            VolumeText.Text = FormatDecimal(snapshot.Volume, "N0");
            RawPriceText.Text = "raw: " + RawValue(snapshot, "ULT");
            RawVolumeText.Text = "raw: " + RawValue(snapshot, "VOL");
            StatusText.Text = EmptyToDash(snapshot.Status);
            StatusBadgeBorder.Background = StatusBrush(snapshot.Status);
        }

        private string FormatDecimal(decimal? value, string format)
        {
            return value.HasValue ? value.Value.ToString(format, _ptBr) : "-";
        }

        private string RawValue(MarketSnapshot snapshot, string field)
        {
            string value;

            if (snapshot.Raw.TryGetValue(field, out value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            return "-";
        }

        private string EmptyToDash(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }

        private string FormatError(Exception error)
        {
            if (error == null)
            {
                return "-";
            }

            return error.GetType().Name + ": " + error.Message;
        }

        private Brush StatusBrush(string status)
        {
            if (string.Equals(status, "connected", StringComparison.OrdinalIgnoreCase))
            {
                return new SolidColorBrush(Color.FromRgb(11, 110, 79));
            }

            if (string.Equals(status, "connecting", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "reconnecting", StringComparison.OrdinalIgnoreCase))
            {
                return new SolidColorBrush(Color.FromRgb(196, 120, 38));
            }

            if (string.Equals(status, "manual", StringComparison.OrdinalIgnoreCase))
            {
                return new SolidColorBrush(Color.FromRgb(49, 90, 140));
            }

            return new SolidColorBrush(Color.FromRgb(137, 63, 54));
        }

        private string ResolvePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
            {
                return path;
            }

            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
        }
    }
}
