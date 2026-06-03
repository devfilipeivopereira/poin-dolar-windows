using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using RtdDolarNative.Config;
using RtdDolarNative.Csv;
using RtdDolarNative.Dom;
using RtdDolarNative.Logging;
using RtdDolarNative.LowLatency;
using RtdDolarNative.MarketData;
using RtdDolarNative.Quant;
using RtdDolarNative.Rtd;

namespace RtdDolarNative
{
    public partial class MainWindow : Window
    {
        private readonly AppConfig _config;
        private readonly Logger _log;
        private readonly LatestSnapshotBuffer _snapshotBuffer;
        private readonly RtdProbeService _probeService;
        private readonly RingBuffer<TickEvent> _ticks;
        private readonly DispatcherTimer _fastTimer;
        private readonly DispatcherTimer _quantTimer;
        private readonly DispatcherTimer _chartTimer;
        private readonly CultureInfo _ptBr = new CultureInfo("pt-BR");
        private readonly List<DailyBar> _dailyBars = new List<DailyBar>();
        private long _lastVersion = -1;
        private long _lastQuantVersion = -1;
        private bool _manualMode;
        private MarketSnapshot _lastSnapshot;
        private QuantResult _result;

        public MainWindow()
        {
            InitializeComponent();

            _config = AppConfig.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json"));
            _log = new Logger(ResolvePath(_config.Diagnostics.LogPath));
            _snapshotBuffer = new LatestSnapshotBuffer();
            _ticks = new RingBuffer<TickEvent>(_config.Ui.TapeCapacity);
            _probeService = new RtdProbeService(_config.Rtd, _config.Diagnostics, _log, _snapshotBuffer);
            _probeService.StatusChanged += ProbeService_StatusChanged;
            _probeService.TickReceived += ProbeService_TickReceived;

            _fastTimer = new DispatcherTimer(DispatcherPriority.Render);
            _fastTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(_config.Ui.FastIntervalMs, 16));
            _fastTimer.Tick += FastTimer_Tick;

            _quantTimer = new DispatcherTimer(DispatcherPriority.Background);
            _quantTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(_config.Ui.QuantIntervalMs, 250));
            _quantTimer.Tick += QuantTimer_Tick;

            _chartTimer = new DispatcherTimer(DispatcherPriority.Background);
            _chartTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(_config.Ui.ChartIntervalMs, 500));
            _chartTimer.Tick += ChartTimer_Tick;

            InitializeStaticText();
            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _fastTimer.Start();
            _quantTimer.Start();
            _chartTimer.Start();
            StartRtd();
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            _fastTimer.Stop();
            _quantTimer.Stop();
            _chartTimer.Stop();
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
                ManualButton.Content = "Sair manual";
                ConnectButton.IsEnabled = false;
                StatusText.Text = "manual";
                StatusBadgeBorder.Background = StatusBrush("manual");
                Recalculate();
            }
            else
            {
                ManualButton.Content = "Modo manual";
                ConnectButton.IsEnabled = true;
                StartRtd();
            }
        }

        private void LoadCsvButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog();
            dialog.Title = "Carregar CSV diario";
            dialog.Filter = "CSV ou texto|*.csv;*.txt|Todos os arquivos|*.*";

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            try
            {
                DailyCsvParseResult parsed = DailyCsvParser.ParseFile(dialog.FileName);
                _dailyBars.Clear();
                _dailyBars.AddRange(parsed.Bars);
                CsvFileText.Text = Path.GetFileName(dialog.FileName) + " (" + parsed.EncodingName + ", delim " + parsed.Delimiter + ")";
                CsvCountText.Text = _dailyBars.Count.ToString(_ptBr) + " pregoes";
                SetWarnings(parsed.Warnings);
                Recalculate();
            }
            catch (Exception ex)
            {
                LastErrorText.Text = ex.GetType().Name + ": " + ex.Message;
                SetWarnings(new[] { "Falha ao carregar CSV: " + ex.Message });
            }
        }

        private void RecalcButton_Click(object sender, RoutedEventArgs e)
        {
            Recalculate();
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

        private void ProbeService_TickReceived(TickEvent tick)
        {
            _ticks.Add(tick);
        }

        private void FastTimer_Tick(object sender, EventArgs e)
        {
            MarketSnapshot snapshot;
            long version;

            if (_snapshotBuffer.TryRead(out snapshot, out version))
            {
                _lastSnapshot = snapshot;

                if (version != _lastVersion)
                {
                    _lastVersion = version;
                    ApplySnapshot(snapshot);
                }

                TimeSpan age = DateTimeOffset.Now - snapshot.LocalTimestamp;
                SnapshotAgeText.Text = Math.Max(0, (int)age.TotalMilliseconds).ToString(_ptBr) + " ms";
                RenderDom(snapshot);
            }
            else
            {
                SnapshotAgeText.Text = "-";
            }

            TapeGrid.ItemsSource = _ticks.SnapshotNewestFirst().Take(500).ToList();
            UpdatesText.Text = _probeService.UpdatesReceived.ToString(_ptBr);
        }

        private void QuantTimer_Tick(object sender, EventArgs e)
        {
            if (_manualMode)
            {
                return;
            }

            if (_dailyBars.Count > 0 && _lastVersion != _lastQuantVersion)
            {
                Recalculate();
            }
        }

        private void ChartTimer_Tick(object sender, EventArgs e)
        {
            ChartControl.SetData(_dailyBars, CurrentSnapshotForCalc(), _result);
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
            FieldsText.Text = string.Join(", ", _config.Rtd.Fields.ToArray());
            PollText.Text = _config.Rtd.PollIntervalMs.ToString(_ptBr) + " ms";
            StatusText.Text = "starting";
            StatusBadgeBorder.Background = StatusBrush("starting");
            LastErrorText.Text = "-";
            CsvFileText.Text = "Nenhum arquivo carregado";
            CsvCountText.Text = "0 pregoes";
        }

        private void ApplySnapshot(MarketSnapshot snapshot)
        {
            AssetText.Text = string.IsNullOrWhiteSpace(snapshot.Asset) ? _config.Rtd.Asset : snapshot.Asset;
            ProfitTimeText.Text = EmptyToDash(snapshot.HoraProfit);
            PriceText.Text = FormatDecimal(snapshot.Ultimo, "N2");
            VolumeText.Text = FormatDecimal(snapshot.Volume, "N0");
            BidAskText.Text = FormatDecimal(snapshot.OfertaCompra, "N2") + " / " + FormatDecimal(snapshot.OfertaVenda, "N2");
            StatusText.Text = EmptyToDash(snapshot.Status);
            StatusBadgeBorder.Background = StatusBrush(snapshot.Status);

            if (!_manualMode)
            {
                WriteInput(OpenInput, snapshot.Abertura, "N2");
                WriteInput(HighInput, snapshot.Maxima, "N2");
                WriteInput(LowInput, snapshot.Minima, "N2");
                WriteInput(ManualPriceInput, snapshot.Ultimo, "N2");
                WriteInput(VwapInput, snapshot.Media, "N2");
                WriteInput(VolumeInput, snapshot.Volume, "N0");
            }
        }

        private void Recalculate()
        {
            try
            {
                MarketSnapshot calcSnapshot = CurrentSnapshotForCalc();
                _result = QuantEngine.Build(_dailyBars, calcSnapshot, _config.Rtd.TickSize);
                _lastQuantVersion = _lastVersion;
                RenderResult(calcSnapshot);
            }
            catch (Exception ex)
            {
                LastErrorText.Text = ex.GetType().Name + ": " + ex.Message;
            }
        }

        private MarketSnapshot CurrentSnapshotForCalc()
        {
            MarketSnapshot snapshot = _lastSnapshot == null ? new MarketSnapshot() : _lastSnapshot.Clone();
            snapshot.Asset = string.IsNullOrWhiteSpace(snapshot.Asset) ? _config.Rtd.Asset : snapshot.Asset;
            ApplyInput(snapshot, "ABE", OpenInput.Text);
            ApplyInput(snapshot, "MAX", HighInput.Text);
            ApplyInput(snapshot, "MIN", LowInput.Text);
            ApplyInput(snapshot, "ULT", ManualPriceInput.Text);
            ApplyInput(snapshot, "MED", VwapInput.Text);
            ApplyInput(snapshot, "VOL", VolumeInput.Text);
            return snapshot;
        }

        private void ApplyInput(MarketSnapshot snapshot, string field, string text)
        {
            decimal? value = ValueParser.ToDecimal(text);

            if (value.HasValue)
            {
                snapshot.Rtd[field] = value.Value;
                snapshot.Raw[field] = text;
            }
        }

        private void RenderResult(MarketSnapshot snapshot)
        {
            if (_result == null)
            {
                return;
            }

            LevelsGrid.ItemsSource = _result.KeyLevels.OrderBy(x => Math.Abs(x.Distance)).ToList();
            DomLevelsGrid.ItemsSource = _result.Confluence.OrderBy(x => Math.Abs(x.Distance)).Take(80).ToList();
            OpeningGrid.ItemsSource = _result.OpeningLevels.OrderBy(x => x.Price).ToList();
            PocGrid.ItemsSource = _result.PocDeviationLevels.OrderBy(x => x.Price).ToList();
            PercentGrid.ItemsSource = _result.PercentTable.OrderBy(x => x.Source).ThenBy(x => x.Price).ToList();
            ProfileGrid.ItemsSource = _result.Profile == null ? null : _result.Profile.Bins.OrderByDescending(x => x.Price).ToList();
            ConfluenceGrid.ItemsSource = _result.Confluence.ToList();
            BacktestGrid.ItemsSource = _result.Backtest.ToList();
            MetricsList.ItemsSource = BuildMetricLines(_result);
            SetWarnings(_result.Warnings);
            RenderDom(snapshot);
            ChartControl.SetData(_dailyBars, snapshot, _result);
        }

        private List<string> BuildMetricLines(QuantResult result)
        {
            List<string> lines = new List<string>();

            foreach (VolatilityMetric metric in result.Metrics)
            {
                lines.Add(metric.Name + " " + metric.Window + ": " + metric.Points.ToString("N1", _ptBr) + " pts (" + metric.Percent.ToString("N2", _ptBr) + "%)");
            }

            if (result.Profile != null && result.Profile.Poc != null)
            {
                lines.Add("POC proxy: " + result.Profile.Poc.Price.ToString("N2", _ptBr));
                lines.Add("VAH/VAL: " + result.Profile.Vah.ToString("N2", _ptBr) + " / " + result.Profile.Val.ToString("N2", _ptBr));
            }

            lines.Add("Regime: " + result.Regime);
            return lines;
        }

        private void RenderDom(MarketSnapshot snapshot)
        {
            IEnumerable<KeyLevel> levels = _result == null ? BasicLevels(snapshot) : _result.KeyLevels.Concat(_result.Confluence);
            DomGrid.ItemsSource = DomLadderModel.Build(snapshot, levels, _config.Rtd.TickSize, _config.Ui.DomTicksEachSide);
        }

        private IEnumerable<KeyLevel> BasicLevels(MarketSnapshot snapshot)
        {
            List<KeyLevel> levels = new List<KeyLevel>();

            if (snapshot == null)
            {
                return levels;
            }

            AddBasic(levels, snapshot.Ultimo, "Preco atual", "Atual", "RTD", 90d);
            AddBasic(levels, snapshot.Abertura, "Abertura", "Valor", "RTD", 55d);
            AddBasic(levels, snapshot.Maxima, "Maxima", "Resistencia", "RTD", 50d);
            AddBasic(levels, snapshot.Minima, "Minima", "Suporte", "RTD", 50d);
            AddBasic(levels, snapshot.Media, "VWAP/MED", "Valor", "RTD", 76d);
            return levels;
        }

        private void AddBasic(List<KeyLevel> levels, decimal? price, string label, string type, string source, double score)
        {
            if (!price.HasValue)
            {
                return;
            }

            KeyLevel level = new KeyLevel();
            level.Price = price.Value;
            level.Label = label;
            level.Type = type;
            level.Source = source;
            level.Score = score;
            levels.Add(level);
        }

        private void SetWarnings(IEnumerable<string> warnings)
        {
            List<string> list = warnings == null ? new List<string>() : warnings.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();

            if (list.Count == 0)
            {
                list.Add("Sem alertas.");
            }

            WarningsList.ItemsSource = list;
        }

        private void WriteInput(System.Windows.Controls.TextBox input, decimal? value, string format)
        {
            if (value.HasValue)
            {
                input.Text = value.Value.ToString(format, _ptBr);
            }
        }

        private string FormatDecimal(decimal? value, string format)
        {
            return value.HasValue ? value.Value.ToString(format, _ptBr) : "-";
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
                return new SolidColorBrush(Color.FromRgb(0, 230, 118));
            }

            if (string.Equals(status, "connecting", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "reconnecting", StringComparison.OrdinalIgnoreCase))
            {
                return new SolidColorBrush(Color.FromRgb(255, 184, 0));
            }

            if (string.Equals(status, "manual", StringComparison.OrdinalIgnoreCase))
            {
                return new SolidColorBrush(Color.FromRgb(48, 56, 68));
            }

            return new SolidColorBrush(Color.FromRgb(255, 59, 48));
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
