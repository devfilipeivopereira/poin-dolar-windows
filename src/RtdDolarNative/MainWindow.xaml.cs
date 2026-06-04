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
using RtdDolarNative.Flow;
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
        private readonly FlowProcessor _flowProcessor;
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
        private long _lastFlowProcessed = -1;
        private MarketSnapshot _lastSnapshot;
        private QuantResult _result;
        private bool _renderingAssets;
        private readonly HashSet<string> _postedTimesKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
            _flowProcessor = new FlowProcessor(_config.Rtd.TickSize, _config.Flow, _log);

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
            _flowProcessor.Start();
            if (_config.Rtd.AutoConnect)
            {
                StartRtd();
            }
            else
            {
                SetIdleDisconnectedStatus();
            }

            TryAutoLoadCsv();
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            _fastTimer.Stop();
            _quantTimer.Stop();
            _chartTimer.Stop();
            _flowProcessor.Dispose();
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
                if (_config.Rtd.AutoConnect)
                {
                    StartRtd();
                }
                else
                {
                    SetIdleDisconnectedStatus();
                    Recalculate();
                }
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

        private void TopNavButton_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Controls.Button button = sender as System.Windows.Controls.Button;

            if (button == null)
            {
                return;
            }

            NavigateToTaggedTab(button.Tag);
        }

        private void NavigateMenuItem_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Controls.MenuItem item = sender as System.Windows.Controls.MenuItem;

            if (item == null)
            {
                return;
            }

            NavigateToTaggedTab(item.Tag);
        }

        private void NavigateToTaggedTab(object tag)
        {
            int index;

            if (tag == null || !int.TryParse(tag.ToString(), out index))
            {
                return;
            }

            NavigateToTab(index);
        }

        private void NavigateToTab(int index)
        {
            if (MainTabs == null || index < 0 || index >= MainTabs.Items.Count)
            {
                return;
            }

            bool alreadySelected = MainTabs.SelectedIndex == index;
            MainTabs.SelectedIndex = index;
            MainTabs.UpdateLayout();

            if (alreadySelected)
            {
                RenderActiveTab();
            }

            MainTabs.Focus();
            UpdateTopNavigation();
        }

        private void MenuConnect_Click(object sender, RoutedEventArgs e)
        {
            ConnectButton_Click(sender, e);
        }

        private void MenuLoadCsv_Click(object sender, RoutedEventArgs e)
        {
            OpenCsvDialog();
        }

        private void MenuRecalculate_Click(object sender, RoutedEventArgs e)
        {
            Recalculate();
        }

        private void MenuManual_Click(object sender, RoutedEventArgs e)
        {
            ManualButton_Click(sender, e);
        }

        private void MainTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, MainTabs))
            {
                return;
            }

            UpdateTopNavigation();
            RenderActiveTab();
        }

        private int CurrentMainTabIndex()
        {
            return MainTabs == null ? 0 : MainTabs.SelectedIndex;
        }

        private void RenderActiveTab()
        {
            if (_config == null || _probeService == null || _flowProcessor == null)
            {
                return;
            }

            MarketSnapshot snapshot = FocusedSnapshot() ?? _lastSnapshot;

            switch (CurrentMainTabIndex())
            {
                case 0:
                    RenderRtdAssets();
                    RenderRtdChannels();
                    break;
                case 1:
                    RenderQuoteFields(snapshot);
                    break;
                case 2:
                    if (snapshot != null)
                    {
                        RenderDom(snapshot);
                        RenderBook(snapshot);
                    }

                    RenderTape();
                    break;
                case 3:
                    RenderTape();
                    break;
                case 4:
                case 5:
                case 6:
                    RenderFlow(snapshot);
                    break;
                case 8:
                    ChartControl.SetData(_dailyBars, CurrentSnapshotForCalc(), _result);
                    break;
                case 10:
                    RenderRtdSources();
                    break;
            }
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
            SaveAssetFromForm(true, true);
        }

        private void SaveAssetButton_Click(object sender, RoutedEventArgs e)
        {
            SaveAssetFromForm(false, false);
        }

        private void NewAssetInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                SaveAssetFromForm(true, true);
            }
        }

        private void FocusAssetButton_Click(object sender, RoutedEventArgs e)
        {
            FocusSelectedAsset();
        }

        private void StartAssetButton_Click(object sender, RoutedEventArgs e)
        {
            if (SetSelectedAssetEnabled(true) && !_probeService.IsRunning && !_manualMode)
            {
                StartRtd();
            }
        }

        private void StopAssetButton_Click(object sender, RoutedEventArgs e)
        {
            SetSelectedAssetEnabled(false);
        }

        private void RemoveAssetButton_Click(object sender, RoutedEventArgs e)
        {
            RemoveSelectedAsset();
        }

        private void StartSourceButton_Click(object sender, RoutedEventArgs e)
        {
            SetSelectedSourceEnabled(true);
        }

        private void StopSourceButton_Click(object sender, RoutedEventArgs e)
        {
            SetSelectedSourceEnabled(false);
        }

        private void DefaultSourcesButton_Click(object sender, RoutedEventArgs e)
        {
            string asset = FocusedAsset();
            _config.Rtd.EnsureDefaultSourcesForAsset(asset);
            _config.Rtd.NormalizeSources();
            SaveRuntimeConfig();
            RenderRtdChannels();
            RenderRtdSources();
            ApplyRtdAssetChange("Fontes padrao verificadas para " + asset + ".");
        }

        private void CotacaoChannelCheckBox_Click(object sender, RoutedEventArgs e)
        {
            SetChannelFromCheckBox(sender, "Cotacao");
        }

        private void BookChannelCheckBox_Click(object sender, RoutedEventArgs e)
        {
            SetChannelFromCheckBox(sender, "Book");
        }

        private void TimesChannelCheckBox_Click(object sender, RoutedEventArgs e)
        {
            SetChannelFromCheckBox(sender, "Times");
        }

        private void RtdAssetsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            FocusSelectedAsset();
        }

        private void RtdAssetsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_renderingAssets)
            {
                return;
            }

            string asset = SelectedAsset();

            if (!string.IsNullOrWhiteSpace(asset))
            {
                PopulateAssetForm(asset);
            }
        }

        private void SaveAssetFromForm(bool addIfMissing, bool focusAfterSave)
        {
            string quoteCode = ReadText(QuoteCodeInput);

            if (string.IsNullOrWhiteSpace(quoteCode))
            {
                quoteCode = ReadText(NewAssetInput);
            }

            string asset = RtdConfig.NormalizeAsset(quoteCode);

            if (string.IsNullOrWhiteSpace(asset))
            {
                SetWarnings(new[] { "Informe o Codigo Cotacao do ativo." });
                return;
            }

            string selected = SelectedAsset();
            RtdAssetConfig existing = addIfMissing ? _config.Rtd.FindAsset(asset) : _config.Rtd.FindAsset(selected);

            if (existing == null)
            {
                existing = _config.Rtd.FindAsset(asset);
            }

            bool isNew = existing == null;

            if (isNew)
            {
                existing = new RtdAssetConfig(asset, true);
                _config.Rtd.Assets.Add(existing);
            }

            string oldAsset = existing.Asset;
            bool wasFocused = string.Equals(FocusedAsset(), oldAsset, StringComparison.OrdinalIgnoreCase);
            existing.Asset = asset;
            existing.Name = string.IsNullOrWhiteSpace(ReadText(AssetNameInput)) ? asset : ReadText(AssetNameInput);
            existing.QuoteCode = asset;
            existing.BookTopic = NormalizeTopic(ReadText(BookTopicInput), "BOOK0");
            existing.TimesTopic = NormalizeTopic(ReadText(TimesTopicInput), "T&T0");
            existing.CsvPath = ReadText(CsvPathInput);
            existing.Enabled = true;
            existing.QuoteEnabled = QuoteEnabledInput == null || QuoteEnabledInput.IsChecked == true;
            existing.BookEnabled = BookEnabledInput == null || BookEnabledInput.IsChecked == true;
            existing.TimesEnabled = TimesEnabledInput != null && TimesEnabledInput.IsChecked == true;
            existing.Normalize();

            if (!string.IsNullOrWhiteSpace(oldAsset) && !string.Equals(oldAsset, existing.Asset, StringComparison.OrdinalIgnoreCase))
            {
                _config.Rtd.Sources.RemoveAll(x => string.Equals(x.Asset, oldAsset, StringComparison.OrdinalIgnoreCase));

                if (string.Equals(_focusedAsset, oldAsset, StringComparison.OrdinalIgnoreCase))
                {
                    _focusedAsset = existing.Asset;
                }
            }

            _config.Rtd.EnsureDefaultSourcesForAsset(existing.Asset);

            if (focusAfterSave || wasFocused || string.IsNullOrWhiteSpace(_focusedAsset))
            {
                SetFocusedAsset(existing.Asset);
            }

            SaveRuntimeConfig();
            RenderRtdAssets();
            RenderRtdChannels();
            RenderRtdSources();
            PopulateAssetForm(existing.Asset);
            ApplyRtdAssetChange((isNew ? "Ativo cadastrado: " : "Ativo salvo: ") + existing.Asset + ".");
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

        private bool SetSelectedAssetEnabled(bool enabled)
        {
            string asset = SelectedAsset();

            if (string.IsNullOrWhiteSpace(asset))
            {
                SetWarnings(new[] { "Selecione um ativo RTD." });
                return false;
            }

            RtdAssetConfig item = _config.Rtd.FindAsset(asset);

            if (item == null)
            {
                SetWarnings(new[] { "Ativo RTD nao encontrado: " + asset + "." });
                return false;
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
            return true;
        }

        private void RemoveSelectedAsset()
        {
            string asset = SelectedAssetFromGrid();

            if (string.IsNullOrWhiteSpace(asset))
            {
                asset = RtdConfig.NormalizeAsset(ReadText(QuoteCodeInput));
            }

            if (string.IsNullOrWhiteSpace(asset))
            {
                asset = FocusedAsset();
            }

            if (string.IsNullOrWhiteSpace(asset))
            {
                SetWarnings(new[] { "Selecione um ativo RTD." });
                return;
            }

            int removedAssets = _config.Rtd.Assets.RemoveAll(x => string.Equals(x.Asset, asset, StringComparison.OrdinalIgnoreCase));
            _config.Rtd.Sources.RemoveAll(x => string.Equals(x.Asset, asset, StringComparison.OrdinalIgnoreCase));
            _postedTimesKeys.RemoveWhere(x => x.StartsWith(asset + "|", StringComparison.OrdinalIgnoreCase));

            if (removedAssets == 0)
            {
                SetWarnings(new[] { "Ativo RTD nao encontrado: " + asset + "." });
                return;
            }

            if (_config.Rtd.Assets.Count == 0)
            {
                _focusedAsset = string.Empty;
                _config.Rtd.Asset = string.Empty;
                _dailyBars.Clear();
                ClearAssetForm();
                CsvFileText.Text = "Nenhum arquivo carregado";
                CsvCountText.Text = "0 pregoes";
                AssetText.Text = "-";
            }
            else if (string.Equals(_focusedAsset, asset, StringComparison.OrdinalIgnoreCase))
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
            RenderRtdChannels();
            RenderRtdSources();
            SetWarnings(new[] { warning });

            if (_probeService.IsRunning && !_manualMode)
            {
                if (_config.Rtd.GetEnabledAssets().Count == 0 || _config.Rtd.GetSubscriptions().Count == 0)
                {
                    StopRtd();
                    SetWarnings(new[] { warning, "Conexao RTD parada: nenhum ativo/canal esta ligado." });
                    return;
                }

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
                _focusedAsset = string.Empty;
                _config.Rtd.Asset = string.Empty;
                AssetText.Text = "-";
                ClearAssetForm();
                LoadCsvForFocusedAsset();
                return;
            }

            _focusedAsset = normalized;
            _config.Rtd.Asset = normalized;
            AssetText.Text = EmptyToDash(normalized);
            PopulateAssetForm(normalized);
            LoadCsvForFocusedAsset();
        }

        private string SelectedAsset()
        {
            string selected = SelectedAssetFromGrid();

            if (!string.IsNullOrWhiteSpace(selected))
            {
                return selected;
            }

            return _focusedAsset;
        }

        private string SelectedAssetFromGrid()
        {
            RtdAssetRow row = RtdAssetsGrid.SelectedItem as RtdAssetRow;

            if (row != null)
            {
                return row.Asset;
            }

            return string.Empty;
        }

        private string SelectedSourceName()
        {
            if (RtdSourcesGrid == null)
            {
                return null;
            }

            RtdSourceRow row = RtdSourcesGrid.SelectedItem as RtdSourceRow;

            if (row != null)
            {
                return row.Name;
            }

            string focused = FocusedAsset();
            RtdSourceConfig source = _config.Rtd.Sources.FirstOrDefault(x => string.Equals(x.Asset, focused, StringComparison.OrdinalIgnoreCase));
            return source == null ? null : source.Name;
        }

        private void SetSelectedSourceEnabled(bool enabled)
        {
            string sourceName = SelectedSourceName();

            if (string.IsNullOrWhiteSpace(sourceName))
            {
                SetWarnings(new[] { "Selecione uma fonte RTD." });
                return;
            }

            RtdSourceConfig source = _config.Rtd.Sources.FirstOrDefault(x => string.Equals(x.Name, sourceName, StringComparison.OrdinalIgnoreCase));

            if (source == null)
            {
                SetWarnings(new[] { "Fonte RTD nao encontrada: " + sourceName + "." });
                return;
            }

            source.Enabled = enabled;
            SetChannelEnabled(source.Asset, ChannelNameForRole(source.Role), enabled);
            _config.Rtd.NormalizeSources();
            SaveRuntimeConfig();
            RenderRtdChannels();
            RenderRtdSources();

            if (_probeService.IsRunning && !_manualMode)
            {
                StatusText.Text = "restarting";
                StatusBadgeBorder.Background = StatusBrush("reconnecting");
                _probeService.Restart();
            }
            else if (enabled && !_manualMode)
            {
                StartRtd();
            }

            SetWarnings(new[] { (enabled ? "Fonte RTD ligada: " : "Fonte RTD desligada: ") + sourceName + "." });
        }

        private string ChannelNameForRole(string role)
        {
            if (string.Equals(role, "PriceVolume", StringComparison.OrdinalIgnoreCase))
            {
                return "Cotacao";
            }

            if (string.Equals(role, "TopBook", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(role, "BookDepth", StringComparison.OrdinalIgnoreCase))
            {
                return "Book";
            }

            if (string.Equals(role, "TimesAndTrades", StringComparison.OrdinalIgnoreCase))
            {
                return "Times";
            }

            return string.Empty;
        }

        private void SetChannelFromCheckBox(object sender, string channel)
        {
            System.Windows.Controls.CheckBox checkBox = sender as System.Windows.Controls.CheckBox;
            RtdChannelRow row = checkBox == null ? null : checkBox.DataContext as RtdChannelRow;

            if (row == null || string.IsNullOrWhiteSpace(row.Asset))
            {
                SetWarnings(new[] { "Selecione um ativo RTD." });
                return;
            }

            bool enabled = checkBox.IsChecked == true;
            bool changed = SetChannelEnabled(row.Asset, channel, enabled);

            if (!changed)
            {
                RenderRtdChannels();
                RenderRtdSources();
                return;
            }

            SaveRuntimeConfig();
            RenderRtdChannels();
            RenderRtdSources();

            if (_probeService.IsRunning && !_manualMode)
            {
                StatusText.Text = "restarting";
                StatusBadgeBorder.Background = StatusBrush("reconnecting");
                _probeService.Restart();
            }
            else if (enabled && !_manualMode)
            {
                StartRtd();
            }

            SetWarnings(new[] { "Canal " + channel + " " + (enabled ? "ligado" : "desligado") + " para " + row.Asset + "." });
        }

        private bool SetChannelEnabled(string asset, string channel, bool enabled)
        {
            string normalizedAsset = RtdConfig.NormalizeAsset(asset);

            if (string.IsNullOrWhiteSpace(normalizedAsset))
            {
                return false;
            }

            bool changed = false;
            RtdAssetConfig assetConfig = _config.Rtd.FindAsset(normalizedAsset);

            if (assetConfig != null)
            {
                if (string.Equals(channel, "Cotacao", StringComparison.OrdinalIgnoreCase) && assetConfig.QuoteEnabled != enabled)
                {
                    assetConfig.QuoteEnabled = enabled;
                    changed = true;
                }
                else if (string.Equals(channel, "Book", StringComparison.OrdinalIgnoreCase) && assetConfig.BookEnabled != enabled)
                {
                    assetConfig.BookEnabled = enabled;
                    changed = true;
                }
                else if (string.Equals(channel, "Times", StringComparison.OrdinalIgnoreCase) && assetConfig.TimesEnabled != enabled)
                {
                    assetConfig.TimesEnabled = enabled;
                    changed = true;
                }
            }

            _config.Rtd.EnsureDefaultSourcesForAsset(normalizedAsset);
            _config.Rtd.NormalizeSources();

            foreach (RtdSourceConfig source in _config.Rtd.Sources.Where(x => string.Equals(x.Asset, normalizedAsset, StringComparison.OrdinalIgnoreCase)))
            {
                if (!ChannelMatchesRole(channel, source.Role))
                {
                    continue;
                }

                if (source.Enabled != enabled)
                {
                    source.Enabled = enabled;
                    changed = true;
                }
            }

            if (string.Equals(normalizedAsset, FocusedAsset(), StringComparison.OrdinalIgnoreCase))
            {
                PopulateAssetForm(normalizedAsset);
            }

            return changed;
        }

        private bool ChannelEnabled(string asset, string channel)
        {
            string normalizedAsset = RtdConfig.NormalizeAsset(asset);
            RtdAssetConfig assetConfig = _config.Rtd.FindAsset(normalizedAsset);

            if (assetConfig == null)
            {
                return false;
            }

            if (string.Equals(channel, "Cotacao", StringComparison.OrdinalIgnoreCase))
            {
                return assetConfig.QuoteEnabled;
            }

            if (string.Equals(channel, "Book", StringComparison.OrdinalIgnoreCase))
            {
                return assetConfig.BookEnabled;
            }

            if (string.Equals(channel, "Times", StringComparison.OrdinalIgnoreCase))
            {
                return assetConfig.TimesEnabled;
            }

            return false;
        }

        private bool ChannelMatchesRole(string channel, string role)
        {
            if (string.Equals(channel, "Cotacao", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(role, "PriceVolume", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(channel, "Book", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(role, "TopBook", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(role, "BookDepth", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(channel, "Times", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(role, "TimesAndTrades", StringComparison.OrdinalIgnoreCase);
            }

            return false;
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
            LoadCsvFromPath(path, true);
        }

        private void LoadCsvFromPath(string path, bool saveToFocusedAsset)
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

                if (saveToFocusedAsset)
                {
                    RtdAssetConfig asset = _config.Rtd.FindAsset(FocusedAsset());

                    if (asset != null)
                    {
                        asset.CsvPath = path;
                        asset.Normalize();
                        SaveRuntimeConfig();
                        RenderRtdAssets();
                        PopulateAssetForm(asset.Asset);
                    }
                }

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
                if (LoadCsvForFocusedAsset())
                {
                    return;
                }

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

        private bool LoadCsvForFocusedAsset()
        {
            RtdAssetConfig asset = _config.Rtd.FindAsset(FocusedAsset());
            string path = asset == null ? null : asset.CsvPath;

            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                LoadCsvFromPath(path, false);
                return true;
            }

            _dailyBars.Clear();

            if (CsvPathInput != null)
            {
                CsvPathInput.Text = string.IsNullOrWhiteSpace(path) ? string.Empty : path;
            }

            if (CsvFileText != null)
            {
                CsvFileText.Text = string.IsNullOrWhiteSpace(path) ? "Nenhum arquivo carregado" : "CSV nao encontrado: " + path;
                CsvFileText.Foreground = new SolidColorBrush(string.IsNullOrWhiteSpace(path) ? Color.FromRgb(169, 179, 191) : Color.FromRgb(255, 184, 0));
            }

            if (CsvCountText != null)
            {
                CsvCountText.Text = "0 pregoes";
            }

            Recalculate();
            return false;
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

            _flowProcessor.Post(snapshot);
        }

        private void FastTimer_Tick(object sender, EventArgs e)
        {
            MarketSnapshot snapshot = FocusedSnapshot();
            long version = _probeService.UpdatesReceived;
            long flowVersion = _flowProcessor.Processed;
            bool changed = version != _lastVersion;
            bool flowChanged = flowVersion != _lastFlowProcessed;
            DateTimeOffset now = DateTimeOffset.Now;
            int selectedTab = CurrentMainTabIndex();
            bool showAssets = selectedTab == 0;
            bool showQuote = selectedTab == 1;
            bool showDomBook = selectedTab == 2;
            bool showTape = selectedTab == 2 || selectedTab == 3;
            bool showFlow = selectedTab == 4 || selectedTab == 5 || selectedTab == 6;
            bool showDiagnostics = selectedTab == 10;

            if (snapshot != null)
            {
                _lastSnapshot = snapshot;

                if (changed)
                {
                    _lastVersion = version;
                    ApplySnapshot(snapshot);

                    if (showQuote)
                    {
                        RenderQuoteFields(snapshot);
                    }
                }

                TimeSpan age = now - snapshot.LocalTimestamp;
                SnapshotAgeText.Text = Math.Max(0, (int)age.TotalMilliseconds).ToString(_ptBr) + " ms";
            }
            else
            {
                SnapshotAgeText.Text = "-";
            }

            if ((changed || flowChanged) && (now - _lastGridRefresh).TotalMilliseconds >= 500)
            {
                if (snapshot != null && showDomBook)
                {
                    RenderDom(snapshot);
                    RenderBook(snapshot);
                }

                if (showTape)
                {
                    RenderTape();
                }

                if (showFlow)
                {
                    RenderFlow(snapshot);
                }

                _lastGridRefresh = now;
                _lastFlowProcessed = flowVersion;
            }

            UpdatesText.Text = _probeService.UpdatesReceived.ToString(_ptBr);
            UpdateRuntimeStatusBar(snapshot);

            if ((now - _lastAssetGridRefresh).TotalMilliseconds >= 1000)
            {
                if (showAssets)
                {
                    RenderRtdAssets();
                    RenderRtdChannels();
                }

                if (showDiagnostics)
                {
                    RenderRtdSources();
                }

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
            MarketSnapshot snapshot = FocusedSnapshot();
            List<TimesTradeRow> realTimes = BuildTimesRows(snapshot);

            if (realTimes.Count > 0)
            {
                PostRealTimes(snapshot, realTimes);
                TapeGrid.ItemsSource = realTimes;

                if (FlowTapeGrid != null)
                {
                    FlowTapeGrid.ItemsSource = realTimes;
                }

                return;
            }

            List<TradePrint> flowTrades = _flowProcessor.GetTrades(focused, 250);

            if (flowTrades.Count > 0)
            {
                TapeGrid.ItemsSource = flowTrades;

                if (FlowTapeGrid != null)
                {
                    FlowTapeGrid.ItemsSource = flowTrades;
                }

                return;
            }

            IEnumerable<TickEvent> ticks = _ticks.SnapshotNewestFirst();

            if (!string.IsNullOrWhiteSpace(focused))
            {
                ticks = ticks.Where(x => string.Equals(x.Asset, focused, StringComparison.OrdinalIgnoreCase));
            }

            List<TickEvent> legacyTicks = ticks.Take(250).ToList();
            TapeGrid.ItemsSource = legacyTicks;

            if (FlowTapeGrid != null)
            {
                FlowTapeGrid.ItemsSource = legacyTicks;
            }
        }

        private void RenderQuoteFields(MarketSnapshot snapshot)
        {
            if (QuoteFieldsGrid == null)
            {
                return;
            }

            if (snapshot == null)
            {
                QuoteFieldsGrid.ItemsSource = null;
                return;
            }

            List<QuoteFieldRow> rows = new List<QuoteFieldRow>();
            IEnumerable<string> fields = RtdConfig.DefaultQuoteFields
                .Concat(_config.Rtd.Fields ?? new List<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (string field in fields)
            {
                string value = RawText(snapshot, field);

                if (string.IsNullOrWhiteSpace(value))
                {
                    value = RtdText(snapshot, field);
                }

                QuoteFieldRow row = new QuoteFieldRow();
                row.Campo = field;
                row.Valor = EmptyToDash(value);
                row.Fonte = "Cotacao";
                rows.Add(row);
            }

            QuoteFieldsGrid.ItemsSource = rows;
        }

        private void RenderBook(MarketSnapshot snapshot)
        {
            if (BookGrid == null)
            {
                return;
            }

            List<BookDepthRow> rows = BuildBookRows(snapshot);
            BookGrid.ItemsSource = rows;
        }

        private List<BookDepthRow> BuildBookRows(MarketSnapshot snapshot)
        {
            List<BookDepthRow> rows = new List<BookDepthRow>();

            if (snapshot == null)
            {
                return rows;
            }

            for (int index = 0; index <= 49; index++)
            {
                BookDepthRow row = new BookDepthRow();
                row.Nivel = index;
                row.HoraCompra = RawText(snapshot, BookField("HORC", index));
                row.Comprador = RawText(snapshot, BookField("ACP", index));
                row.QtdeCompra = RawText(snapshot, BookField("VOC", index));
                row.Compra = RawText(snapshot, BookField("OCP", index));
                row.Venda = RawText(snapshot, BookField("OVD", index));
                row.QtdeVenda = RawText(snapshot, BookField("VOV", index));
                row.Vendedor = RawText(snapshot, BookField("AVD", index));
                row.HoraVenda = RawText(snapshot, BookField("HORV", index));

                if (row.HasData())
                {
                    rows.Add(row);
                }
            }

            return rows;
        }

        private List<TimesTradeRow> BuildTimesRows(MarketSnapshot snapshot)
        {
            List<TimesTradeRow> rows = new List<TimesTradeRow>();

            if (snapshot == null)
            {
                return rows;
            }

            for (int index = 0; index <= 99; index++)
            {
                TimesTradeRow row = new TimesTradeRow();
                row.Linha = index;
                row.Data = RawText(snapshot, TimesField("DAT", index));
                row.Compradora = RawText(snapshot, TimesField("ACP", index));
                row.Preco = RawText(snapshot, TimesField("PRE", index));
                row.Quantidade = RawText(snapshot, TimesField("QUL", index));
                row.Vendedora = RawText(snapshot, TimesField("AVD", index));
                row.Agressor = RawText(snapshot, TimesField("AGR", index));
                row.Qualidade = "FullTimesAndTrades";

                if (row.HasData())
                {
                    rows.Add(row);
                }
            }

            return rows;
        }

        private void PostRealTimes(MarketSnapshot snapshot, List<TimesTradeRow> rows)
        {
            if (snapshot == null || rows == null || rows.Count == 0)
            {
                return;
            }

            if (_postedTimesKeys.Count > 5000)
            {
                _postedTimesKeys.Clear();
            }

            foreach (TimesTradeRow row in rows.OrderByDescending(x => x.Linha))
            {
                decimal? price = ValueParser.ToDecimal(row.Preco);
                decimal? quantity = ValueParser.ToDecimal(row.Quantidade);

                if (!price.HasValue || price.Value <= 0m)
                {
                    continue;
                }

                string key = snapshot.Asset + "|" + row.Data + "|" + row.Compradora + "|" + row.Preco + "|" + row.Quantidade + "|" + row.Vendedora + "|" + row.Agressor;

                if (_postedTimesKeys.Contains(key))
                {
                    continue;
                }

                _postedTimesKeys.Add(key);

                decimal qty = quantity.HasValue && quantity.Value > 0m ? quantity.Value : 1m;
                string aggressor = NormalizeAggressor(row.Agressor);
                TradePrint trade = new TradePrint();
                trade.Asset = snapshot.Asset;
                trade.LocalTimestamp = snapshot.LocalTimestamp;
                trade.ProfitTime = row.Data;
                trade.Price = price.Value;
                trade.Quantity = qty;
                trade.Volume = qty;
                trade.Aggressor = aggressor;
                trade.Classification = aggressor == "Buy" ? "agressao compra" : (aggressor == "Sell" ? "agressao venda" : "neutro");
                trade.Delta = aggressor == "Buy" ? qty : (aggressor == "Sell" ? -qty : 0m);
                trade.Derived = false;
                trade.DataQuality = MarketDataQuality.FullTimesAndTrades;
                trade.Bid = snapshot.OfertaCompra;
                trade.Ask = snapshot.OfertaVenda;
                _flowProcessor.PostTrade(trade, snapshot);
            }
        }

        private string NormalizeAggressor(string aggressor)
        {
            if (string.IsNullOrWhiteSpace(aggressor))
            {
                return "Neutral";
            }

            string value = aggressor.Trim().ToUpperInvariant();

            if (value == "C" || value == "COMPRA" || value == "COMPRADOR" || value.Contains("BUY"))
            {
                return "Buy";
            }

            if (value == "V" || value == "VENDA" || value == "VENDEDOR" || value.Contains("SELL"))
            {
                return "Sell";
            }

            return "Neutral";
        }

        private string BookField(string field, int index)
        {
            return "BOOK_" + field + "_" + index.ToString(_ptBr);
        }

        private string TimesField(string field, int index)
        {
            return "TIMES_" + field + "_" + index.ToString(_ptBr);
        }

        private string RawText(MarketSnapshot snapshot, string field)
        {
            if (snapshot == null || snapshot.Raw == null || string.IsNullOrWhiteSpace(field))
            {
                return string.Empty;
            }

            string value;
            return snapshot.Raw.TryGetValue(field, out value) ? value : string.Empty;
        }

        private string RtdText(MarketSnapshot snapshot, string field)
        {
            if (snapshot == null || snapshot.Rtd == null || string.IsNullOrWhiteSpace(field))
            {
                return string.Empty;
            }

            object value;
            return snapshot.Rtd.TryGetValue(field, out value) && value != null ? value.ToString() : string.Empty;
        }

        private void StartRtd()
        {
            List<string> enabledAssets = _config.Rtd.GetEnabledAssets();
            List<RtdSubscriptionSpec> subscriptions = _config.Rtd.GetSubscriptions();

            if (enabledAssets.Count == 0)
            {
                SetIdleDisconnectedStatus();
                SetWarnings(new[] { "Cadastre e ligue pelo menos um ativo antes de conectar o RTD." });
                return;
            }

            if (subscriptions.Count == 0)
            {
                SetIdleDisconnectedStatus();
                SetWarnings(new[] { "Ligue pelo menos um canal em Ativos: Cotacao, Book ou Times." });
                return;
            }

            _manualMode = false;
            ManualButton.Content = "Modo manual";
            ConnectButton.IsEnabled = true;
            ConnectButton.Content = "Desconectar";
            StatusText.Text = "connecting";
            StatusBadgeBorder.Background = StatusBrush("connecting");
            LastErrorText.Text = "-";
            SetWarnings(new[] { "Conectando RTD: " + enabledAssets.Count.ToString(_ptBr) + " ativo(s), " + subscriptions.Count.ToString(_ptBr) + " assinatura(s)." });
            _log.Info("Solicitando conexao RTD: " + enabledAssets.Count.ToString(_ptBr) + " ativo(s), " + subscriptions.Count.ToString(_ptBr) + " assinatura(s).");
            RenderRtdAssets();
            RenderRtdChannels();
            RenderRtdSources();
            _probeService.Start();
        }

        private void StopRtd()
        {
            _probeService.Stop();
            ConnectButton.Content = "Conectar";
            RenderRtdAssets();
        }

        private void SetIdleDisconnectedStatus()
        {
            _manualMode = false;
            ManualButton.Content = "Modo manual";
            ConnectButton.IsEnabled = true;
            ConnectButton.Content = "Conectar";
            StatusText.Text = "idle";
            StatusBadgeBorder.Background = StatusBrush("idle");
            LastErrorText.Text = "-";
            RenderRtdAssets();
        }

        private void InitializeStaticText()
        {
            AssetText.Text = EmptyToDash(FocusedAsset());
            ArchitectureText.Text = Environment.Is64BitProcess ? "x64" : "x86";
            FieldsText.Text = string.Join(", ", _config.Rtd.Fields.ToArray());
            PollText.Text = _config.Rtd.PollIntervalMs.ToString(_ptBr) + " ms";
            StatusText.Text = "idle";
            StatusBadgeBorder.Background = StatusBrush("idle");
            LastErrorText.Text = "-";
            CsvFileText.Text = "Nenhum arquivo carregado";
            CsvCountText.Text = "0 pregoes";
            RenderRtdAssets();
            RenderRtdChannels();
            RenderRtdSources();
            UpdateTopNavigation();
            UpdateRuntimeStatusBar(null);
        }

        private void UpdateRuntimeStatusBar(MarketSnapshot snapshot)
        {
            if (StatusBarText == null)
            {
                return;
            }

            string focused = FocusedAsset();
            string status = _probeService == null ? "idle" : _probeService.Status;
            long updates = _probeService == null ? 0 : _probeService.UpdatesReceived;

            StatusBarText.Text = "RTD " + EmptyToDash(status) +
                                 " | Ativo " + EmptyToDash(focused) +
                                 " | Updates " + updates.ToString(_ptBr);

            FlowMetrics metrics = _flowProcessor == null ? null : _flowProcessor.GetMetrics(focused);

            if (metrics != null)
            {
                DataQualityText.Text = "Qualidade " + metrics.DataQuality + (metrics.Derived ? " derivado" : " real");
                FlowStateText.Text = "Delta " + metrics.LastDelta.ToString("N0", _ptBr) +
                                     " | CD " + metrics.CumulativeDelta.ToString("N0", _ptBr);
            }
            else
            {
                DataQualityText.Text = snapshot == null ? "Qualidade -" : "Qualidade TopOfBook";
                FlowStateText.Text = "Fluxo -";
            }

            QueueStateText.Text = "Fila drop " + (_flowProcessor == null ? 0 : _flowProcessor.Dropped).ToString(_ptBr);
        }

        private void UpdateTopNavigation()
        {
            if (TopNavigation == null || MainTabs == null)
            {
                return;
            }

            Brush selectedBackground = FindResource("InputBg") as Brush;
            Brush normalBackground = FindResource("Panel2") as Brush;
            Brush selectedBorder = FindResource("Accent") as Brush;
            Brush normalBorder = FindResource("Border") as Brush;

            foreach (object child in TopNavigation.Children)
            {
                System.Windows.Controls.Button button = child as System.Windows.Controls.Button;
                int index;

                if (button == null || button.Tag == null || !int.TryParse(button.Tag.ToString(), out index))
                {
                    continue;
                }

                bool selected = index == MainTabs.SelectedIndex;
                button.Background = selected ? selectedBackground : normalBackground;
                button.BorderBrush = selected ? selectedBorder : normalBorder;
                button.FontWeight = selected ? FontWeights.Bold : FontWeights.Normal;
            }
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

            return string.IsNullOrWhiteSpace(_focusedAsset) ? string.Empty : _focusedAsset;
        }

        private void PopulateAssetForm(string asset)
        {
            if (AssetNameInput == null || QuoteCodeInput == null || BookTopicInput == null || TimesTopicInput == null)
            {
                return;
            }

            RtdAssetConfig item = _config.Rtd.FindAsset(asset);

            if (item == null)
            {
                ClearAssetForm();
                return;
            }

            item.Normalize();
            AssetNameInput.Text = item.Name;
            QuoteCodeInput.Text = item.QuoteCode;
            NewAssetInput.Text = item.QuoteCode;
            BookTopicInput.Text = item.BookTopic;
            TimesTopicInput.Text = item.TimesTopic;
            CsvPathInput.Text = item.CsvPath;

            if (QuoteEnabledInput != null)
            {
                QuoteEnabledInput.IsChecked = item.QuoteEnabled;
            }

            if (BookEnabledInput != null)
            {
                BookEnabledInput.IsChecked = item.BookEnabled;
            }

            if (TimesEnabledInput != null)
            {
                TimesEnabledInput.IsChecked = item.TimesEnabled;
            }
        }

        private void ClearAssetForm()
        {
            if (AssetNameInput != null)
            {
                AssetNameInput.Text = string.Empty;
            }

            if (QuoteCodeInput != null)
            {
                QuoteCodeInput.Text = string.Empty;
            }

            if (NewAssetInput != null)
            {
                NewAssetInput.Text = string.Empty;
            }

            if (BookTopicInput != null)
            {
                BookTopicInput.Text = "BOOK0";
            }

            if (TimesTopicInput != null)
            {
                TimesTopicInput.Text = "T&T0";
            }

            if (CsvPathInput != null)
            {
                CsvPathInput.Text = string.Empty;
            }

            if (QuoteEnabledInput != null)
            {
                QuoteEnabledInput.IsChecked = true;
            }

            if (BookEnabledInput != null)
            {
                BookEnabledInput.IsChecked = false;
            }

            if (TimesEnabledInput != null)
            {
                TimesEnabledInput.IsChecked = false;
            }
        }

        private string ReadText(System.Windows.Controls.TextBox input)
        {
            return input == null || input.Text == null ? string.Empty : input.Text.Trim();
        }

        private string NormalizeTopic(string topic, string fallback)
        {
            return string.IsNullOrWhiteSpace(topic) ? fallback : topic.Trim().ToUpperInvariant();
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
                RenderQuoteFields(snapshot);
                RenderBook(snapshot);
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
                item.Normalize();
                MarketSnapshot snapshot = null;

                lock (_snapshotsLock)
                {
                    _snapshotsByAsset.TryGetValue(item.Asset, out snapshot);
                }

                RtdAssetRow row = new RtdAssetRow();
                row.Name = item.Name;
                row.Asset = item.Asset;
                row.QuoteCode = item.QuoteCode;
                row.BookTopic = item.BookTopic;
                row.TimesTopic = item.TimesTopic;
                row.CsvText = string.IsNullOrWhiteSpace(item.CsvPath) ? "-" : Path.GetFileName(item.CsvPath);
                row.EnabledText = item.Enabled ? "Ligado" : "Off";
                row.ChannelsText = (item.QuoteEnabled ? "C" : "-") + "/" + (item.BookEnabled ? "B" : "-") + "/" + (item.TimesEnabled ? "T" : "-");
                row.FocusText = string.Equals(item.Asset, focused, StringComparison.OrdinalIgnoreCase) ? "Sim" : "";
                row.LastText = snapshot == null ? "-" : FormatDecimal(snapshot.Ultimo, "N2");
                row.Status = snapshot == null ? (item.Enabled ? "aguardando" : "desligado") : EmptyToDash(snapshot.Status);
                rows.Add(row);
            }

            _renderingAssets = true;

            try
            {
                RtdAssetsGrid.ItemsSource = rows;

                RtdAssetRow selectedRow = rows.FirstOrDefault(x => string.Equals(x.Asset, selected, StringComparison.OrdinalIgnoreCase)) ??
                                          rows.FirstOrDefault(x => string.Equals(x.Asset, focused, StringComparison.OrdinalIgnoreCase));

                if (selectedRow != null)
                {
                    RtdAssetsGrid.SelectedItem = selectedRow;
                }
            }
            finally
            {
                _renderingAssets = false;
            }

            RtdAssetSummaryText.Text = enabled.Count.ToString(_ptBr) + " ligado(s), foco " + focused;
        }

        private void RenderRtdChannels()
        {
            if (RtdChannelsGrid == null)
            {
                return;
            }

            _config.Rtd.NormalizeSources();
            string focused = FocusedAsset();
            List<RtdChannelRow> rows = new List<RtdChannelRow>();

            foreach (RtdAssetConfig asset in _config.Rtd.Assets)
            {
                RtdChannelRow row = new RtdChannelRow();
                row.Asset = asset.Asset;
                row.FocusText = string.Equals(asset.Asset, focused, StringComparison.OrdinalIgnoreCase) ? "Sim" : "";
                row.Cotacao = ChannelEnabled(asset.Asset, "Cotacao");
                row.Book = ChannelEnabled(asset.Asset, "Book");
                row.Times = ChannelEnabled(asset.Asset, "Times");
                row.Status = asset.Enabled
                    ? "ativo ligado | cotacao " + (row.Cotacao ? "on" : "off") + " | book " + (row.Book ? "on" : "off") + " | times " + (row.Times ? "on" : "off")
                    : "ativo desligado";
                rows.Add(row);
            }

            RtdChannelsGrid.ItemsSource = rows;

            RtdChannelRow selected = rows.FirstOrDefault(x => string.Equals(x.Asset, focused, StringComparison.OrdinalIgnoreCase));

            if (selected != null)
            {
                RtdChannelsGrid.SelectedItem = selected;
            }
        }

        private void RenderRtdSources()
        {
            if (RtdSourcesGrid == null)
            {
                return;
            }

            _config.Rtd.NormalizeSources();
            string selected = SelectedSourceName();
            List<RtdSourceRow> rows = new List<RtdSourceRow>();
            HashSet<string> enabledAssets = new HashSet<string>(_config.Rtd.GetEnabledAssets(), StringComparer.OrdinalIgnoreCase);

            foreach (RtdSourceConfig source in _config.Rtd.Sources)
            {
                MarketSnapshot snapshot = null;

                lock (_snapshotsLock)
                {
                    _snapshotsByAsset.TryGetValue(source.Asset, out snapshot);
                }

                RtdSourceRow row = new RtdSourceRow();
                row.Name = source.Name;
                row.Asset = source.Asset;
                row.Topic = string.IsNullOrWhiteSpace(source.Topic) ? source.Asset : source.Topic;
                row.Role = source.Role;
                row.EnabledText = source.Enabled ? "Ligado" : "Off";
                row.FieldsText = source.Fields == null || source.Fields.Count == 0 ? "-" : string.Join(", ", source.Fields.ToArray());
                row.IndexText = source.IndexFrom.HasValue || source.IndexTo.HasValue
                    ? (source.IndexFrom.HasValue ? source.IndexFrom.Value.ToString(_ptBr) : "0") + ".." + (source.IndexTo.HasValue ? source.IndexTo.Value.ToString(_ptBr) : "0")
                    : "-";
                row.PollText = source.PollIntervalMs.ToString(_ptBr) + " ms";

                if (!enabledAssets.Contains(source.Asset))
                {
                    row.Status = "ativo off";
                }
                else if (!source.Enabled)
                {
                    row.Status = "fonte off";
                }
                else if (source.Fields == null || source.Fields.Count == 0)
                {
                    row.Status = "sem campos";
                }
                else if (snapshot == null)
                {
                    row.Status = "aguardando";
                }
                else
                {
                    row.Status = EmptyToDash(snapshot.Status);
                }

                row.UpdatesText = source.Enabled && enabledAssets.Contains(source.Asset) ? _probeService.UpdatesReceived.ToString(_ptBr) : "0";
                row.LastError = LastErrorText == null ? "-" : EmptyToDash(LastErrorText.Text);
                rows.Add(row);
            }

            RtdSourcesGrid.ItemsSource = rows;

            RtdSourceRow selectedRow = rows.FirstOrDefault(x => string.Equals(x.Name, selected, StringComparison.OrdinalIgnoreCase));

            if (selectedRow != null)
            {
                RtdSourcesGrid.SelectedItem = selectedRow;
            }
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
            RenderVolumeProfile(snapshot, _flowProcessor.GetMetrics(FocusedAsset()));
            ConfluenceGrid.ItemsSource = _result.Confluence.ToList();
            BacktestGrid.ItemsSource = _result.Backtest.ToList();
            MetricsList.ItemsSource = BuildMetricLines(_result);
            SetWarnings(_result.Warnings);
            RenderDom(snapshot);
            ChartControl.SetData(_dailyBars, snapshot, _result);
        }

        private void RenderFlow(MarketSnapshot snapshot)
        {
            string focused = FocusedAsset();
            FlowMetrics metrics = _flowProcessor.GetMetrics(focused);

            if (metrics == null)
            {
                OrderFlowSummaryGrid.ItemsSource = null;
                OrderFlowWindowsGrid.ItemsSource = null;
                FlowSignalsGrid.ItemsSource = _flowProcessor.GetSignals(focused, 250);
                RenderVolumeProfile(snapshot, null);
                return;
            }

            OrderFlowSummaryGrid.ItemsSource = BuildFlowSummaryRows(metrics);
            OrderFlowWindowsGrid.ItemsSource = metrics.Windows == null ? null : metrics.Windows.ToList();
            FlowSignalsGrid.ItemsSource = _flowProcessor.GetSignals(focused, 250);
            RenderVolumeProfile(snapshot, metrics);
        }

        private void RenderVolumeProfile(MarketSnapshot snapshot, FlowMetrics metrics)
        {
            VolumeProfileMetrics profile = metrics == null ? null : metrics.Profile;
            VolumeProfileResult proxy = _result == null ? null : _result.Profile;

            if (ProfileChartControl != null)
            {
                ProfileChartControl.SetData(profile, proxy, snapshot, _config.Flow.ValueAreaPercent);
            }

            if (profile != null && profile.Bins != null && profile.Bins.Count > 0)
            {
                ProfileSummaryGrid.ItemsSource = BuildProfileSummaryRows(profile, snapshot);
                ProfileNodesGrid.ItemsSource = profile.Nodes == null ? null : profile.Nodes.ToList();
                ProfileGrid.ItemsSource = profile.Bins.OrderByDescending(x => x.Price).ToList();
                return;
            }

            if (proxy != null)
            {
                ProfileSummaryGrid.ItemsSource = BuildProxyProfileSummaryRows(proxy, snapshot);
                ProfileNodesGrid.ItemsSource = BuildProxyProfileNodes(proxy);
                ProfileGrid.ItemsSource = proxy.Bins.OrderByDescending(x => x.Price).ToList();
                return;
            }

            ProfileSummaryGrid.ItemsSource = null;
            ProfileNodesGrid.ItemsSource = null;
            ProfileGrid.ItemsSource = null;
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

        private List<NameValueRow> BuildFlowSummaryRows(FlowMetrics metrics)
        {
            List<NameValueRow> rows = new List<NameValueRow>();

            if (metrics == null)
            {
                return rows;
            }

            AddRow(rows, "Qualidade", metrics.DataQuality.ToString(), metrics.Derived ? "derived" : "real");
            AddRow(rows, "Delta ultimo", metrics.LastDelta.ToString("N0", _ptBr), "ultimo print");
            AddRow(rows, "Cumulative delta", metrics.CumulativeDelta.ToString("N0", _ptBr), "sessao");
            AddRow(rows, "Imbalance top", FormatDecimal(metrics.TopBookImbalance, "N3"), "bid/ask volume");
            AddRow(rows, "Microbias", FormatDecimal(metrics.MicroBias, "N3"), "microprice - mid");
            AddRow(rows, "Spread", FormatDecimal(metrics.Spread, "N2"), "ask - bid");
            AddRow(rows, "VWAP", FormatDecimal(metrics.Vwap, "N2"), "intraday/fallback MED");
            AddRow(rows, "Dist VWAP", FormatDecimal(metrics.VwapDistance, "N2"), "preco - vwap");
            AddRow(rows, "Fila drop", _flowProcessor.Dropped.ToString(_ptBr), "bounded queue");
            return rows;
        }

        private List<NameValueRow> BuildProfileSummaryRows(VolumeProfileMetrics profile, MarketSnapshot snapshot)
        {
            List<NameValueRow> rows = new List<NameValueRow>();

            if (profile == null)
            {
                return rows;
            }

            AddRow(rows, "Fonte", EmptyToDash(profile.Source), "profile");
            AddRow(rows, "POC", FormatDecimal(profile.Poc, "N2"), DistanceText(snapshot, profile.Poc));
            AddRow(rows, "VAH", FormatDecimal(profile.Vah, "N2"), DistanceText(snapshot, profile.Vah));
            AddRow(rows, "VAL", FormatDecimal(profile.Val, "N2"), DistanceText(snapshot, profile.Val));
            AddRow(rows, "Volume total", profile.TotalVolume.ToString("N0", _ptBr), "prints intraday");
            AddRow(rows, "Volume area valor", profile.ValueAreaVolume.ToString("N0", _ptBr), (_config.Flow.ValueAreaPercent * 100m).ToString("N0", _ptBr) + "% alvo");
            AddRow(rows, "HVNs", profile.Nodes.Count(x => string.Equals(x.Type, "hvn", StringComparison.OrdinalIgnoreCase)).ToString(_ptBr), "nos de alto volume");
            AddRow(rows, "LVNs", profile.Nodes.Count(x => string.Equals(x.Type, "lvn", StringComparison.OrdinalIgnoreCase)).ToString(_ptBr), "nos de baixo volume");
            return rows;
        }

        private List<NameValueRow> BuildProxyProfileSummaryRows(VolumeProfileResult profile, MarketSnapshot snapshot)
        {
            List<NameValueRow> rows = new List<NameValueRow>();

            if (profile == null)
            {
                return rows;
            }

            AddRow(rows, "Fonte", "CSV diario proxy", "fallback");
            AddRow(rows, "POC", profile.Poc == null ? "-" : profile.Poc.Price.ToString("N2", _ptBr), profile.Poc == null ? "-" : DistanceText(snapshot, profile.Poc.Price));
            AddRow(rows, "VAH", profile.Vah.ToString("N2", _ptBr), DistanceText(snapshot, profile.Vah));
            AddRow(rows, "VAL", profile.Val.ToString("N2", _ptBr), DistanceText(snapshot, profile.Val));
            AddRow(rows, "Bins", profile.Bins.Count.ToString(_ptBr), "historico");
            AddRow(rows, "HVNs", profile.Hvn.Count.ToString(_ptBr), "proxy");
            AddRow(rows, "LVNs", profile.Lvn.Count.ToString(_ptBr), "proxy");
            return rows;
        }

        private List<VolumeNode> BuildProxyProfileNodes(VolumeProfileResult profile)
        {
            List<VolumeNode> nodes = new List<VolumeNode>();

            if (profile == null)
            {
                return nodes;
            }

            foreach (ProfileBin bin in profile.Hvn.Take(10))
            {
                VolumeNode node = new VolumeNode();
                node.Type = "hvn";
                node.Price = bin.Price;
                node.Low = bin.Low;
                node.High = bin.High;
                node.Volume = (decimal)bin.Volume;
                node.Score = (decimal)Math.Min(100d, bin.Rank * 100d);
                node.Description = "HVN proxy CSV";
                nodes.Add(node);
            }

            foreach (ProfileBin bin in profile.Lvn.Take(10))
            {
                VolumeNode node = new VolumeNode();
                node.Type = "lvn";
                node.Price = bin.Price;
                node.Low = bin.Low;
                node.High = bin.High;
                node.Volume = (decimal)bin.Volume;
                node.Score = (decimal)Math.Min(100d, bin.Rank * 100d);
                node.Description = "LVN proxy CSV";
                nodes.Add(node);
            }

            return nodes;
        }

        private void AddRow(List<NameValueRow> rows, string name, string value, string detail)
        {
            NameValueRow row = new NameValueRow();
            row.Name = name;
            row.Value = string.IsNullOrWhiteSpace(value) ? "-" : value;
            row.Detail = string.IsNullOrWhiteSpace(detail) ? "-" : detail;
            rows.Add(row);
        }

        private string DistanceText(MarketSnapshot snapshot, decimal? level)
        {
            if (snapshot == null || !snapshot.Ultimo.HasValue || !level.HasValue)
            {
                return "-";
            }

            decimal distance = snapshot.Ultimo.Value - level.Value;
            return distance.ToString("N2", _ptBr) + " pts";
        }

        private void RenderDom(MarketSnapshot snapshot)
        {
            List<KeyLevel> levels = (_result == null ? BasicLevels(snapshot) : _result.KeyLevels.Concat(_result.Confluence)).ToList();
            levels.AddRange(FlowProfileKeyLevels(snapshot));
            levels.AddRange(FlowSignalKeyLevels(snapshot));
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

        private IEnumerable<KeyLevel> FlowProfileKeyLevels(MarketSnapshot snapshot)
        {
            List<KeyLevel> result = new List<KeyLevel>();

            if (snapshot == null)
            {
                return result;
            }

            FlowMetrics metrics = _flowProcessor.GetMetrics(FocusedAsset());

            if (metrics == null || metrics.Profile == null || metrics.Profile.Levels == null)
            {
                return result;
            }

            foreach (ProfileLevel profileLevel in metrics.Profile.Levels)
            {
                KeyLevel level = new KeyLevel();
                level.Price = profileLevel.Price;
                level.Label = profileLevel.Label;
                level.Type = profileLevel.Type;
                level.Source = profileLevel.Source;
                level.Score = profileLevel.Score;
                level.Evidence = "Volume Profile intraday";
                level.Layer = "Order Flow";
                level.Tags = profileLevel.Type;
                level.Distance = snapshot.Ultimo.HasValue ? snapshot.Ultimo.Value - profileLevel.Price : 0m;
                result.Add(level);
            }

            return result;
        }

        private IEnumerable<KeyLevel> FlowSignalKeyLevels(MarketSnapshot snapshot)
        {
            List<KeyLevel> result = new List<KeyLevel>();

            if (snapshot == null)
            {
                return result;
            }

            foreach (FlowSignal signal in _flowProcessor.GetSignals(FocusedAsset(), 20).Where(x => x.LevelPrice.HasValue))
            {
                KeyLevel level = new KeyLevel();
                level.Price = signal.LevelPrice.Value;
                level.Label = signal.Setup + " " + signal.Direction;
                level.Type = "setup";
                level.Source = "Setups";
                level.Score = signal.Score;
                level.Evidence = signal.Reasons;
                level.Layer = "Order Flow";
                level.Tags = signal.LevelName;
                level.Distance = snapshot.Ultimo.HasValue ? snapshot.Ultimo.Value - signal.LevelPrice.Value : 0m;
                result.Add(level);
            }

            return result;
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
            public string Name { get; set; }
            public string Asset { get; set; }
            public string QuoteCode { get; set; }
            public string BookTopic { get; set; }
            public string TimesTopic { get; set; }
            public string CsvText { get; set; }
            public string EnabledText { get; set; }
            public string ChannelsText { get; set; }
            public string FocusText { get; set; }
            public string LastText { get; set; }
            public string Status { get; set; }
        }

        private sealed class RtdSourceRow
        {
            public string Name { get; set; }
            public string Asset { get; set; }
            public string Topic { get; set; }
            public string Role { get; set; }
            public string EnabledText { get; set; }
            public string FieldsText { get; set; }
            public string IndexText { get; set; }
            public string PollText { get; set; }
            public string Status { get; set; }
            public string UpdatesText { get; set; }
            public string LastError { get; set; }
        }

        private sealed class RtdChannelRow
        {
            public string Asset { get; set; }
            public string FocusText { get; set; }
            public bool Cotacao { get; set; }
            public bool Book { get; set; }
            public bool Times { get; set; }
            public string Status { get; set; }
        }

        private sealed class NameValueRow
        {
            public string Name { get; set; }
            public string Value { get; set; }
            public string Detail { get; set; }
        }

        private sealed class QuoteFieldRow
        {
            public string Campo { get; set; }
            public string Valor { get; set; }
            public string Fonte { get; set; }
        }

        private sealed class BookDepthRow
        {
            public int Nivel { get; set; }
            public string HoraCompra { get; set; }
            public string Comprador { get; set; }
            public string QtdeCompra { get; set; }
            public string Compra { get; set; }
            public string Venda { get; set; }
            public string QtdeVenda { get; set; }
            public string Vendedor { get; set; }
            public string HoraVenda { get; set; }

            public bool HasData()
            {
                return !string.IsNullOrWhiteSpace(HoraCompra) ||
                       !string.IsNullOrWhiteSpace(Comprador) ||
                       !string.IsNullOrWhiteSpace(QtdeCompra) ||
                       !string.IsNullOrWhiteSpace(Compra) ||
                       !string.IsNullOrWhiteSpace(Venda) ||
                       !string.IsNullOrWhiteSpace(QtdeVenda) ||
                       !string.IsNullOrWhiteSpace(Vendedor) ||
                       !string.IsNullOrWhiteSpace(HoraVenda);
            }
        }

        private sealed class TimesTradeRow
        {
            public int Linha { get; set; }
            public string Data { get; set; }
            public string Compradora { get; set; }
            public string Preco { get; set; }
            public string Quantidade { get; set; }
            public string Vendedora { get; set; }
            public string Agressor { get; set; }
            public string Qualidade { get; set; }

            public bool HasData()
            {
                return !string.IsNullOrWhiteSpace(Data) ||
                       !string.IsNullOrWhiteSpace(Compradora) ||
                       !string.IsNullOrWhiteSpace(Preco) ||
                       !string.IsNullOrWhiteSpace(Quantidade) ||
                       !string.IsNullOrWhiteSpace(Vendedora) ||
                       !string.IsNullOrWhiteSpace(Agressor);
            }
        }
    }
}
