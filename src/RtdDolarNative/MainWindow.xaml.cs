using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;
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
        private readonly string _configPath;
        private readonly Logger _log;
        private readonly LatestSnapshotBuffer _snapshotBuffer;
        private readonly RtdProbeService _probeService;
        private readonly RingBuffer<TickEvent> _ticks;
        private readonly DispatcherTimer _fastTimer;
        private readonly DispatcherTimer _quantTimer;
        private readonly DispatcherTimer _chartTimer;
        private readonly CultureInfo _ptBr = new CultureInfo("pt-BR");
        private readonly List<DailyBar> _dailyBars = new List<DailyBar>();
        private readonly object _snapshotsLock = new object();
        private readonly Dictionary<string, MarketSnapshot> _snapshotsByAsset = new Dictionary<string, MarketSnapshot>(StringComparer.OrdinalIgnoreCase);
        private long _lastVersion = -1;
        private long _lastQuantVersion = -1;
        private bool _manualMode;
        private string _focusedAsset;
        private DateTimeOffset _lastGridRefresh = DateTimeOffset.MinValue;
        private DateTimeOffset _lastAssetGridRefresh = DateTimeOffset.MinValue;
        private MarketSnapshot _lastSnapshot;
        private QuantResult _result;

        public MainWindow()
        {
            InitializeComponent();

            _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            _config = AppConfig.Load(_configPath);
            _focusedAsset = _config.Rtd.Asset;
            _log = new Logger(ResolvePath(_config.Diagnostics.LogPath));
            _snapshotBuffer = new LatestSnapshotBuffer();
            _ticks = new RingBuffer<TickEvent>(_config.Ui.TapeCapacity);
            _probeService = new RtdProbeService(_config.Rtd, _config.Diagnostics, _log, _snapshotBuffer);
            _probeService.StatusChanged += ProbeService_StatusChanged;
            _probeService.TickReceived += ProbeService_TickReceived;
            _probeService.SnapshotReceived += ProbeService_SnapshotReceived;

            _fastTimer = new DispatcherTimer(DispatcherPriority.Render);
            _fastTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(_config.Ui.FastIntervalMs, 200));
            _fastTimer.Tick += FastTimer_Tick;

            _quantTimer = new DispatcherTimer(DispatcherPriority.Background);
            _quantTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(_config.Ui.QuantIntervalMs, 2000));
            _quantTimer.Tick += QuantTimer_Tick;

            _chartTimer = new DispatcherTimer(DispatcherPriority.Background);
            _chartTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(_config.Ui.ChartIntervalMs, 2000));
            _chartTimer.Tick += ChartTimer_Tick;

            InitializeStaticText();
            KeyDown += MainWindow_KeyDown;
            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _fastTimer.Start();
            _quantTimer.Start();
            _chartTimer.Start();
            StartRtd();
            TryAutoLoadCsv();
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
            OpenCsvDialog();
        }

        private void LoadCsvButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            OpenCsvDialog();
        }

        private void SelectCsvPanelButton_Click(object sender, RoutedEventArgs e)
        {
            OpenCsvDialog();
        }

        private void SelectCsvPanelButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            OpenCsvDialog();
        }

        private void LoadCsvPathButton_Click(object sender, RoutedEventArgs e)
        {
            LoadCsvFromPath(CsvPathInput.Text);
        }

        private void LoadCsvPathButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            LoadCsvFromPath(CsvPathInput.Text);
        }

        private void CsvDropZone_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount >= 2)
            {
                OpenCsvDialog();
            }
        }

        private void CsvDropZone_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                return;
            }

            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];

            if (files != null && files.Length > 0)
            {
                LoadCsvFromPath(files[0]);
            }
        }

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.O)
            {
                e.Handled = true;
                OpenCsvDialog();
            }
        }

        private void RecalcButton_Click(object sender, RoutedEventArgs e)
        {
            Recalculate();
        }

        private void AddAssetButton_Click(object sender, RoutedEventArgs e)
        {
            AddOrEnableAsset(NewAssetInput.Text, true);
        }

        private void NewAssetInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                AddOrEnableAsset(NewAssetInput.Text, true);
            }
        }

        private void FocusAssetButton_Click(object sender, RoutedEventArgs e)
        {
            FocusSelectedAsset();
        }

        private void StartAssetButton_Click(object sender, RoutedEventArgs e)
        {
            SetSelectedAssetEnabled(true);
        }

        private void StopAssetButton_Click(object sender, RoutedEventArgs e)
        {
            SetSelectedAssetEnabled(false);
        }

        private void RemoveAssetButton_Click(object sender, RoutedEventArgs e)
        {
            RemoveSelectedAsset();
        }

        private void RtdAssetsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            FocusSelectedAsset();
        }

        private void AddOrEnableAsset(string text, bool focusAfterAdd)
        {
            string asset = RtdConfig.NormalizeAsset(text);

            if (string.IsNullOrWhiteSpace(asset))
            {
                SetWarnings(new[] { "Informe o codigo do ativo RTD." });
                return;
            }

            RtdAssetConfig existing = _config.Rtd.FindAsset(asset);

            if (existing == null)
            {
                _config.Rtd.Assets.Add(new RtdAssetConfig(asset, true));
                _log.Info("Ativo RTD adicionado: " + asset + ".");
            }
            else
            {
                existing.Enabled = true;
                _log.Info("Ativo RTD ligado: " + asset + ".");
            }

            if (focusAfterAdd)
            {
                SetFocusedAsset(asset);
            }

            NewAssetInput.Text = string.Empty;
            ApplyRtdAssetChange("Ativo RTD ligado: " + asset + ".");
        }

        private void FocusSelectedAsset()
        {
            string asset = SelectedAsset();

            if (string.IsNullOrWhiteSpace(asset))
            {
                SetWarnings(new[] { "Selecione um ativo RTD." });
                return;
            }

            SetFocusedAsset(asset);
            SaveRuntimeConfig();
            RenderRtdAssets();
            ApplyFocusedSnapshot();
        }

        private void SetSelectedAssetEnabled(bool enabled)
        {
            string asset = SelectedAsset();

            if (string.IsNullOrWhiteSpace(asset))
            {
                SetWarnings(new[] { "Selecione um ativo RTD." });
                return;
            }

            RtdAssetConfig item = _config.Rtd.FindAsset(asset);

            if (item == null)
            {
                SetWarnings(new[] { "Ativo RTD nao encontrado: " + asset + "." });
                return;
            }

            item.Enabled = enabled;

            if (enabled)
            {
                SetFocusedAsset(asset);
            }
            else if (string.Equals(_focusedAsset, asset, StringComparison.OrdinalIgnoreCase))
            {
                string next = _config.Rtd.Assets.Where(x => x.Enabled && !string.Equals(x.Asset, asset, StringComparison.OrdinalIgnoreCase)).Select(x => x.Asset).FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(next))
                {
                    SetFocusedAsset(next);
                }
            }

            ApplyRtdAssetChange((enabled ? "Ativo RTD ligado: " : "Ativo RTD desligado: ") + asset + ".");
        }

        private void RemoveSelectedAsset()
        {
            string asset = SelectedAsset();

            if (string.IsNullOrWhiteSpace(asset))
            {
                SetWarnings(new[] { "Selecione um ativo RTD." });
                return;
            }

            if (_config.Rtd.Assets.Count <= 1)
            {
                RtdAssetConfig only = _config.Rtd.FindAsset(asset);

                if (only != null)
                {
                    only.Enabled = false;
                }

                ApplyRtdAssetChange("Ultimo ativo mantido na lista e desligado: " + asset + ".");
                return;
            }

            _config.Rtd.Assets.RemoveAll(x => string.Equals(x.Asset, asset, StringComparison.OrdinalIgnoreCase));

            if (string.Equals(_focusedAsset, asset, StringComparison.OrdinalIgnoreCase))
            {
                string next = _config.Rtd.Assets.Where(x => x.Enabled).Select(x => x.Asset).FirstOrDefault() ??
                              _config.Rtd.Assets.Select(x => x.Asset).FirstOrDefault();
                SetFocusedAsset(next);
            }

            ApplyRtdAssetChange("Ativo RTD removido: " + asset + ".");
        }

        private void ApplyRtdAssetChange(string warning)
        {
            _config.Rtd.NormalizeAssets();
            SaveRuntimeConfig();
            RenderRtdAssets();
            SetWarnings(new[] { warning });

            if (_probeService.IsRunning && !_manualMode)
            {
                StatusText.Text = "restarting";
                StatusBadgeBorder.Background = StatusBrush("reconnecting");
                _probeService.Restart();
            }
        }

        private void SetFocusedAsset(string asset)
        {
            string normalized = RtdConfig.NormalizeAsset(asset);

            if (string.IsNullOrWhiteSpace(normalized))
            {
                normalized = _config.Rtd.Assets.Select(x => x.Asset).FirstOrDefault();
            }

            if (string.IsNullOrWhiteSpace(normalized))
            {
                normalized = "WDOFUT_F_0";
            }

            _focusedAsset = normalized;
            _config.Rtd.Asset = normalized;
            AssetText.Text = normalized;
        }

        private string SelectedAsset()
        {
            RtdAssetRow row = RtdAssetsGrid.SelectedItem as RtdAssetRow;

            if (row != null)
            {
                return row.Asset;
            }

            return _focusedAsset;
        }

        private void SaveRuntimeConfig()
        {
            try
            {
                _config.Save(_configPath);
                _log.Info("Configuracao RTD salva em " + _configPath + ".");
            }
            catch (Exception ex)
            {
                LastErrorText.Text = ex.GetType().Name + ": " + ex.Message;
                _log.Error("Falha ao salvar configuracao RTD.", ex);
            }
        }

        private void OpenCsvDialog()
        {
            string previousText = CsvFileText.Text;
            Brush previousBrush = CsvFileText.Foreground;

            try
            {
                _log.Info("Abrindo seletor de CSV.");
                CsvFileText.Text = "Abrindo seletor de CSV...";
                CsvFileText.Foreground = new SolidColorBrush(Color.FromRgb(255, 184, 0));
                CsvDropZone.UpdateLayout();

                using (System.Windows.Forms.OpenFileDialog dialog = new System.Windows.Forms.OpenFileDialog())
                {
                    dialog.Title = "Carregar CSV diario";
                    dialog.Filter = "CSV ou texto (*.csv;*.txt)|*.csv;*.txt|Todos os arquivos (*.*)|*.*";
                    dialog.CheckFileExists = true;
                    dialog.Multiselect = false;

                    System.Windows.Forms.DialogResult result = dialog.ShowDialog();

                    if (result == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.FileName))
                    {
                        LoadCsvFromPath(dialog.FileName);
                    }
                    else
                    {
                        CsvFileText.Text = _dailyBars.Count > 0 ? previousText : "Nenhum arquivo carregado";
                        CsvFileText.Foreground = _dailyBars.Count > 0 ? previousBrush : new SolidColorBrush(Color.FromRgb(169, 179, 191));
                        _log.Info("Selecao de CSV cancelada.");
                    }
                }
            }
            catch (Exception ex)
            {
                LastErrorText.Text = ex.GetType().Name + ": " + ex.Message;
                CsvFileText.Text = "Falha ao abrir seletor de CSV.";
                CsvFileText.Foreground = new SolidColorBrush(Color.FromRgb(255, 59, 48));
                SetWarnings(new[] { "Falha ao abrir seletor de CSV: " + ex.Message });
                _log.Error("Falha ao abrir seletor de CSV.", ex);
            }
        }

        private void LoadCsvFromPath(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw new InvalidOperationException("Informe o caminho do CSV.");
                }

                path = path.Trim().Trim('"');

                if (!File.Exists(path))
                {
                    throw new FileNotFoundException("CSV nao encontrado.", path);
                }

                _log.Info("Carregando CSV: " + path);
                DailyCsvParseResult parsed = DailyCsvParser.ParseFile(path);
                _dailyBars.Clear();
                _dailyBars.AddRange(parsed.Bars);
                CsvPathInput.Text = path;
                CsvFileText.Text = Path.GetFileName(path) + " (" + parsed.EncodingName + ", delim " + parsed.Delimiter + ")";
                CsvFileText.Foreground = new SolidColorBrush(Color.FromRgb(0, 230, 118));
                CsvCountText.Text = _dailyBars.Count.ToString(_ptBr) + " pregoes";
                SetWarnings(parsed.Warnings);
                _log.Info("CSV carregado: " + _dailyBars.Count.ToString(_ptBr) + " pregoes, " + parsed.EncodingName + ", delimitador " + parsed.Delimiter + ".");
                Recalculate();
            }
            catch (Exception ex)
            {
                LastErrorText.Text = ex.GetType().Name + ": " + ex.Message;
                CsvFileText.Text = "Falha ao carregar CSV.";
                CsvFileText.Foreground = new SolidColorBrush(Color.FromRgb(255, 59, 48));
                SetWarnings(new[] { "Falha ao carregar CSV: " + ex.Message });
                _log.Error("Falha ao carregar CSV.", ex);
            }
        }

        private void TryAutoLoadCsv()
        {
            try
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string downloadsData = Path.Combine(profile, "Downloads", "Dados_Dolar");
                string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                List<string> candidates = new List<string>();

                if (Directory.Exists(downloadsData))
                {
                    candidates.AddRange(Directory.GetFiles(downloadsData, "*.csv"));
                }

                if (Directory.Exists(documents))
                {
                    candidates.AddRange(Directory.GetFiles(documents, "*.csv"));
                }

                string asset = FocusedAsset();
                string best = candidates
                    .Where(x => Path.GetFileName(x).IndexOf(asset, StringComparison.OrdinalIgnoreCase) >= 0 || Path.GetFileName(x).IndexOf("WDO", StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderByDescending(x => File.GetLastWriteTimeUtc(x))
                    .FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(best))
                {
                    LoadCsvFromPath(best);
                }
            }
            catch (Exception ex)
            {
                _log.Warn("Auto-load CSV ignorado: " + ex.Message);
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

        private void ProbeService_TickReceived(TickEvent tick)
        {
            _ticks.Add(tick);
        }

        private void ProbeService_SnapshotReceived(MarketSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Asset))
            {
                return;
            }

            lock (_snapshotsLock)
            {
                _snapshotsByAsset[snapshot.Asset] = snapshot;
            }
        }

        private void FastTimer_Tick(object sender, EventArgs e)
        {
            MarketSnapshot snapshot = FocusedSnapshot();
            long version = _probeService.UpdatesReceived;
            bool changed = version != _lastVersion;
            DateTimeOffset now = DateTimeOffset.Now;

            if (snapshot != null)
            {
                _lastSnapshot = snapshot;

                if (changed)
                {
                    _lastVersion = version;
                    ApplySnapshot(snapshot);
                }

                TimeSpan age = now - snapshot.LocalTimestamp;
                SnapshotAgeText.Text = Math.Max(0, (int)age.TotalMilliseconds).ToString(_ptBr) + " ms";
            }
            else
            {
                SnapshotAgeText.Text = "-";
            }

            if (changed && (now - _lastGridRefresh).TotalMilliseconds >= 500)
            {
                if (snapshot != null)
                {
                    RenderDom(snapshot);
                }

                RenderTape();
                _lastGridRefresh = now;
            }

            UpdatesText.Text = _probeService.UpdatesReceived.ToString(_ptBr);

            if ((now - _lastAssetGridRefresh).TotalMilliseconds >= 1000)
            {
                RenderRtdAssets();
                _lastAssetGridRefresh = now;
            }
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

        private void RenderTape()
        {
            string focused = FocusedAsset();
            IEnumerable<TickEvent> ticks = _ticks.SnapshotNewestFirst();

            if (!string.IsNullOrWhiteSpace(focused))
            {
                ticks = ticks.Where(x => string.Equals(x.Asset, focused, StringComparison.OrdinalIgnoreCase));
            }

            TapeGrid.ItemsSource = ticks.Take(250).ToList();
        }

        private void StartRtd()
        {
            _manualMode = false;
            ManualButton.Content = "Modo manual";
            ConnectButton.IsEnabled = true;
            ConnectButton.Content = "Desconectar";
            RenderRtdAssets();
            _probeService.Start();
        }

        private void StopRtd()
        {
            _probeService.Stop();
            ConnectButton.Content = "Conectar";
            RenderRtdAssets();
        }

        private void InitializeStaticText()
        {
            AssetText.Text = FocusedAsset();
            ArchitectureText.Text = Environment.Is64BitProcess ? "x64" : "x86";
            FieldsText.Text = string.Join(", ", _config.Rtd.Fields.ToArray());
            PollText.Text = _config.Rtd.PollIntervalMs.ToString(_ptBr) + " ms";
            StatusText.Text = "starting";
            StatusBadgeBorder.Background = StatusBrush("starting");
            LastErrorText.Text = "-";
            CsvFileText.Text = "Nenhum arquivo carregado";
            CsvCountText.Text = "0 pregoes";
            RenderRtdAssets();
        }

        private string FocusedAsset()
        {
            if (string.IsNullOrWhiteSpace(_focusedAsset))
            {
                _focusedAsset = RtdConfig.NormalizeAsset(_config.Rtd.Asset);
            }

            if (string.IsNullOrWhiteSpace(_focusedAsset))
            {
                _focusedAsset = _config.Rtd.Assets.Select(x => x.Asset).FirstOrDefault();
            }

            return string.IsNullOrWhiteSpace(_focusedAsset) ? "WDOFUT_F_0" : _focusedAsset;
        }

        private MarketSnapshot FocusedSnapshot()
        {
            string focused = FocusedAsset();

            lock (_snapshotsLock)
            {
                MarketSnapshot snapshot;

                if (_snapshotsByAsset.TryGetValue(focused, out snapshot))
                {
                    return snapshot;
                }
            }

            return null;
        }

        private void ApplyFocusedSnapshot()
        {
            MarketSnapshot snapshot = FocusedSnapshot();

            if (snapshot != null)
            {
                _lastSnapshot = snapshot;
                ApplySnapshot(snapshot);
                RenderDom(snapshot);
                Recalculate();
            }
            else
            {
                AssetText.Text = FocusedAsset();
            }
        }

        private void RenderRtdAssets()
        {
            if (RtdAssetsGrid == null)
            {
                return;
            }

            string selected = SelectedAsset();
            string focused = FocusedAsset();
            List<string> enabled = _config.Rtd.GetEnabledAssets();
            List<RtdAssetRow> rows = new List<RtdAssetRow>();

            foreach (RtdAssetConfig item in _config.Rtd.Assets)
            {
                MarketSnapshot snapshot = null;

                lock (_snapshotsLock)
                {
                    _snapshotsByAsset.TryGetValue(item.Asset, out snapshot);
                }

                RtdAssetRow row = new RtdAssetRow();
                row.Asset = item.Asset;
                row.EnabledText = item.Enabled ? "Ligado" : "Off";
                row.FocusText = string.Equals(item.Asset, focused, StringComparison.OrdinalIgnoreCase) ? "Sim" : "";
                row.LastText = snapshot == null ? "-" : FormatDecimal(snapshot.Ultimo, "N2");
                row.Status = snapshot == null ? (item.Enabled ? "aguardando" : "desligado") : EmptyToDash(snapshot.Status);
                rows.Add(row);
            }

            RtdAssetsGrid.ItemsSource = rows;

            RtdAssetRow selectedRow = rows.FirstOrDefault(x => string.Equals(x.Asset, selected, StringComparison.OrdinalIgnoreCase)) ??
                                      rows.FirstOrDefault(x => string.Equals(x.Asset, focused, StringComparison.OrdinalIgnoreCase));

            if (selectedRow != null)
            {
                RtdAssetsGrid.SelectedItem = selectedRow;
            }

            RtdAssetSummaryText.Text = enabled.Count.ToString(_ptBr) + " ligado(s), foco " + focused;
        }

        private void ApplySnapshot(MarketSnapshot snapshot)
        {
            AssetText.Text = string.IsNullOrWhiteSpace(snapshot.Asset) ? FocusedAsset() : snapshot.Asset;
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
            MarketSnapshot focused = FocusedSnapshot();
            MarketSnapshot snapshot = focused == null ? (_lastSnapshot == null ? new MarketSnapshot() : _lastSnapshot.Clone()) : focused.Clone();
            snapshot.Asset = FocusedAsset();
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
                string.Equals(status, "reconnecting", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "restarting", StringComparison.OrdinalIgnoreCase))
            {
                return new SolidColorBrush(Color.FromRgb(255, 184, 0));
            }

            if (string.Equals(status, "manual", StringComparison.OrdinalIgnoreCase))
            {
                return new SolidColorBrush(Color.FromRgb(48, 56, 68));
            }

            if (string.Equals(status, "idle", StringComparison.OrdinalIgnoreCase))
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

        private sealed class RtdAssetRow
        {
            public string Asset { get; set; }
            public string EnabledText { get; set; }
            public string FocusText { get; set; }
            public string LastText { get; set; }
            public string Status { get; set; }
        }
    }
}
