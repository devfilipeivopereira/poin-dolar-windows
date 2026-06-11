using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;
using RtdDolarNative.Config;
using RtdDolarNative.Charts;
using RtdDolarNative.Csv;
using RtdDolarNative.Dom;
using RtdDolarNative.Flow;
using RtdDolarNative.Heatmap;
using RtdDolarNative.Logging;
using RtdDolarNative.LowLatency;
using RtdDolarNative.MarketData;
using RtdDolarNative.Quant;
using RtdDolarNative.Rtd;

namespace RtdDolarNative
{
    public partial class MainWindow : Window
    {
        private const int TabDashboard = 0;
        private const int TabAssets = 1;
        private const int TabQuote = 2;
        private const int TabDomBook = 3;
        private const int TabTape = 4;
        private const int TabOrderFlow = 5;
        private const int TabVolumeProfile = 6;
        private const int TabSetups = 7;
        private const int TabLevels = 8;
        private const int TabChart = 9;
        private const int TabBacktest = 10;
        private const int TabRisk = 11;
        private const int TabAlerts = 12;
        private const int TabDiagnostics = 13;
        private const int TabMonitor = 14;
        private const int TabShortcuts = 15;
        private const int TabOpportunities = 16;
        private const int TabHistory = 17;
        private const int TabScanner = 18;
        private const int TabFlowMap = 19;
        private const int TabIndicators = 20;
        private const int TabHeatmap = 21;
        private const int TabRtdComplete = 22;
        private const int TabGarch = 23;
        private const int DashboardChartRefreshMs = 1500;
        private const int DashboardHeavyRefreshMs = 1000;

        private readonly AppConfig _config;
        private readonly string _configPath;
        private readonly Logger _log;
        private readonly LatestSnapshotBuffer _snapshotBuffer;
        private readonly RtdProbeService _probeService;
        private readonly FlowProcessor _flowProcessor;
        private readonly HeatmapProcessor _heatmapProcessor;
        private readonly CsvHistorySqliteStore _csvHistoryStore;
        private readonly PtaxHistorySqliteStore _ptaxHistoryStore;
        private readonly RingBuffer<TickEvent> _ticks;
        private readonly IntradayBarAggregator _intradayAggregator;
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
        private bool _renderActiveTabQueued;
        private bool _domCenterQueued;
        private bool _dashboardDomCenterQueued;
        private bool _levelsMapCenterQueued;
        private DateTimeOffset _lastGridRefresh = DateTimeOffset.MinValue;
        private DateTimeOffset _lastAssetGridRefresh = DateTimeOffset.MinValue;
        private DateTimeOffset _lastDashboardChartRefresh = DateTimeOffset.MinValue;
        private long _lastFlowProcessed = -1;
        private long _lastDashboardChartVersion = -1;
        private long _lastDashboardChartQuantVersion = -1;
        private int _lastDashboardChartBarCount = -1;
        private string _lastDashboardChartAsset;
        private DateTimeOffset _lastDashboardHeavyRefresh = DateTimeOffset.MinValue;
        private long _lastDashboardHeavyVersion = -1;
        private long _lastDashboardHeavyFlowVersion = -1;
        private long _lastDashboardHeavyQuantVersion = -1;
        private int _lastDashboardHeavyBarCount = -1;
        private string _lastDashboardHeavyAsset;
        private MarketSnapshot _lastSnapshot;
        private QuantResult _result;
        private bool _renderingAssets;
        private string _lastHistoryRtdStatus;
        private readonly List<HistoryRow> _historyRows = new List<HistoryRow>();
        private readonly HashSet<string> _postedTimesKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _postedTimesLock = new object();
        private bool _syncCalculationDaysSelection = true;
        private bool _syncChartTimeframeSelection = true;
        private bool _syncChartPriceGridSelection = true;
        private bool _syncChartCandleSpacingSelection = true;
        private bool _syncChartLineVisibilitySelection = true;
        private DateTime _ptaxTradeDate = DateTime.Today;
        private decimal? _appliedPtaxValue;
        private readonly List<PtaxHistoryViewRow> _ptaxHistoryRows = new List<PtaxHistoryViewRow>();
        private readonly HashSet<string> _activeRtdCompleteGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _selectedRtdCompleteGroup = RtdCompleteFieldCatalog.GroupMarket;
        private string _rtdCompleteSearch = string.Empty;
        private bool _syncRtdCompleteGroupSelection;

        public MainWindow()
        {
            InitializeComponent();

            _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            _config = AppConfig.Load(_configPath);
            _focusedAsset = _config.Rtd.Asset;
            _log = new Logger(ResolvePath(_config.Diagnostics.LogPath));
            _snapshotBuffer = new LatestSnapshotBuffer();
            _ticks = new RingBuffer<TickEvent>(_config.Ui.TapeCapacity);
            _intradayAggregator = new IntradayBarAggregator(_config.Garch.MaxIntradayBars, _config.Garch.IntradayTimeframeSeconds);
            _probeService = new RtdProbeService(_config.Rtd, _config.Diagnostics, _log, _snapshotBuffer);
            _probeService.StatusChanged += ProbeService_StatusChanged;
            _probeService.TickReceived += ProbeService_TickReceived;
            _probeService.SnapshotReceived += ProbeService_SnapshotReceived;
            _flowProcessor = new FlowProcessor(_config.Rtd.TickSize, _config.Flow, _log);
            _heatmapProcessor = new HeatmapProcessor(_config.Rtd.TickSize, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoinDolarWindows", "data", "market_heatmap.sqlite"), _log);
            string historyDatabasePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoinDolarWindows", "data", "csv_history.sqlite");
            _csvHistoryStore = new CsvHistorySqliteStore(historyDatabasePath);
            _ptaxHistoryStore = new PtaxHistorySqliteStore(historyDatabasePath);

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
            InitializeCalculationDaysSelection();
            InitializeChartTimeframeSelection();
            InitializeChartPriceGridSelection();
            InitializeChartCandleSpacingSelection();
            InitializeChartLineVisibilitySelection();
            InitializePtaxEditor();
            PreviewKeyDown += MainWindow_KeyDown;
            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _fastTimer.Start();
            _quantTimer.Start();
            _chartTimer.Start();
            _flowProcessor.Start();
            _heatmapProcessor.Start();
            if (MainTabs != null && MainTabs.SelectedIndex < 0)
            {
                MainTabs.SelectedIndex = TabDashboard;
            }

            UpdateWorkspaceContext();
            UpdateTopNavigation();
            ScheduleRenderActiveTab();
            AddHistory("App", "Aberto", "Janela principal carregada.");
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
            _heatmapProcessor.Dispose();
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

            if (index == TabDashboard)
            {
                _lastDashboardChartRefresh = DateTimeOffset.MinValue;
            }

            MainTabs.SelectedIndex = index;

            if (alreadySelected)
            {
                ScheduleRenderActiveTab();
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

        private void MenuFocusAsset_Click(object sender, RoutedEventArgs e)
        {
            FocusSelectedAsset();
        }

        private void MenuDefaultSources_Click(object sender, RoutedEventArgs e)
        {
            EnsureDefaultSourcesForFocusedAsset();
        }

        private void MenuOpenConfig_Click(object sender, RoutedEventArgs e)
        {
            OpenFileWithShell(_configPath, "Configuracao nao encontrada: " + _configPath);
        }

        private void MenuOpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            string logPath = ResolvePath(_config.Diagnostics.LogPath);
            string folder = string.IsNullOrWhiteSpace(logPath) ? AppDomain.CurrentDomain.BaseDirectory : Path.GetDirectoryName(logPath);

            if (string.IsNullOrWhiteSpace(folder))
            {
                folder = AppDomain.CurrentDomain.BaseDirectory;
            }

            OpenFolderWithShell(folder);
        }

        private void MenuPreviousTab_Click(object sender, RoutedEventArgs e)
        {
            NavigateRelativeTab(-1);
        }

        private void MenuNextTab_Click(object sender, RoutedEventArgs e)
        {
            NavigateRelativeTab(1);
        }

        private void MainTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, MainTabs))
            {
                return;
            }

            UpdateTopNavigation();
            ScheduleRenderActiveTab();
        }

        private int CurrentMainTabIndex()
        {
            return MainTabs == null ? 0 : MainTabs.SelectedIndex;
        }

        private string CurrentWorkspaceName()
        {
            return WorkspaceName(CurrentMainTabIndex());
        }

        private string WorkspaceName(int index)
        {
            switch (index)
            {
                case TabDashboard:
                    return "Mesa";
                case TabAssets:
                    return "Ativos";
                case TabQuote:
                    return "Cotacao";
                case TabDomBook:
                    return "DOM / Book";
                case TabTape:
                    return "Tape";
                case TabOrderFlow:
                    return "Order Flow";
                case TabVolumeProfile:
                    return "Volume Profile";
                case TabSetups:
                    return "Setups";
                case TabLevels:
                    return "Niveis";
                case TabChart:
                    return "Grafico";
                case TabBacktest:
                    return "Backtest";
                case TabRisk:
                    return "Risco";
                case TabAlerts:
                    return "Alertas";
                case TabDiagnostics:
                    return "Diagnostico";
                case TabMonitor:
                    return "Monitor";
                case TabShortcuts:
                    return "Atalhos";
                case TabOpportunities:
                    return "Oportunidades";
                case TabHistory:
                    return "Historico";
                case TabScanner:
                    return "Scanner";
                case TabFlowMap:
                    return "Mapa de Fluxo";
                case TabIndicators:
                    return "Indicadores";
                case TabHeatmap:
                    return "Heatmap";
                case TabRtdComplete:
                    return "RTD Completo";
                default:
                    return "Mesa";
            }
        }

        private string WorkspaceHint(int index)
        {
            switch (index)
            {
                case TabDashboard:
                    return "Mesa operacional de analise";
                case TabAssets:
                    return "Cadastro de ativo, RTDs e CSV";
                case TabQuote:
                    return "Campos de cotacao e indicadores";
                case TabDomBook:
                    return "Ladder, book e niveis relevantes";
                case TabTape:
                    return "Times and trades real ou derivado";
                case TabOrderFlow:
                    return "Delta, microbias, VWAP e janelas";
                case TabVolumeProfile:
                    return "POC, VAH, VAL, HVN e LVN";
                case TabSetups:
                    return "Sinais do motor de fluxo";
                case TabLevels:
                    return "Niveis calculados e confluencias";
                case TabChart:
                    return "Grafico com niveis do ativo";
                case TabBacktest:
                    return "Validacao historica por CSV";
                case TabRisk:
                    return "Risco operacional e qualidade de dados";
                case TabAlerts:
                    return "Alertas de RTD, CSV, fluxo e setups";
                case TabDiagnostics:
                    return "Fontes RTD, updates e erros";
                case TabMonitor:
                    return "Todos os ativos cadastrados";
                case TabShortcuts:
                    return "Mapa de teclas e fluxo de trabalho";
                case TabOpportunities:
                    return "Triagem de setups e confluencias";
                case TabHistory:
                    return "Eventos locais do app, RTD e CSV";
                case TabScanner:
                    return "Ranking de ativos por oportunidade";
                case TabFlowMap:
                    return "Liquidez, delta e niveis em uma leitura";
                case TabIndicators:
                    return "Indicadores tecnicos, estatistica e sinais quant";
                case TabHeatmap:
                    return "Mapa de calor de book e negocios";
                case TabRtdComplete:
                    return "Catalogo RTD completo sob demanda";
                default:
                    return "Mesa operacional de analise";
            }
        }

        private string WorkspaceGroupName(int index)
        {
            switch (index)
            {
                case TabDashboard:
                case TabAssets:
                case TabMonitor:
                case TabDiagnostics:
                    return "Operacao";
                case TabQuote:
                case TabDomBook:
                case TabTape:
                case TabChart:
                case TabRtdComplete:
                    return "Mercado";
                case TabOrderFlow:
                case TabFlowMap:
                case TabHeatmap:
                case TabVolumeProfile:
                case TabSetups:
                    return "Fluxo";
                case TabScanner:
                case TabOpportunities:
                case TabIndicators:
                case TabLevels:
                case TabBacktest:
                case TabGarch:
                    return "Analise";
                case TabRisk:
                case TabAlerts:
                case TabHistory:
                case TabShortcuts:
                    return "Controle";
                default:
                    return "Operacao";
            }
        }

        private string WorkspaceHeaderText(int index)
        {
            switch (index)
            {
                case TabDashboard:
                    return "Oper / Mesa";
                case TabAssets:
                    return "Oper / Ativos";
                case TabMonitor:
                    return "Oper / Monitor";
                case TabDiagnostics:
                    return "Oper / Diag";
                case TabQuote:
                    return "Merc / Cotacao";
                case TabDomBook:
                    return "Merc / DOM";
                case TabTape:
                    return "Merc / Tape";
                case TabChart:
                    return "Merc / Grafico";
                case TabRtdComplete:
                    return "Merc / RTD+";
                case TabOrderFlow:
                    return "Fluxo / Order";
                case TabFlowMap:
                    return "Fluxo / Mapa";
                case TabHeatmap:
                    return "Fluxo / Heat";
                case TabVolumeProfile:
                    return "Fluxo / Profile";
                case TabSetups:
                    return "Fluxo / Setups";
                case TabScanner:
                    return "Anal / Scanner";
                case TabOpportunities:
                    return "Anal / Oportun.";
                case TabIndicators:
                    return "Anal / Indic.";
                case TabLevels:
                    return "Anal / Niveis";
                case TabBacktest:
                    return "Anal / Back";
                case TabGarch:
                    return "Anal / GARCH";
                case TabRisk:
                    return "Ctrl / Risco";
                case TabAlerts:
                    return "Ctrl / Alertas";
                case TabHistory:
                    return "Ctrl / Hist";
                case TabShortcuts:
                    return "Ctrl / Atalhos";
                default:
                    return "Oper / Mesa";
            }
        }

        private void UpdateWorkspaceContext()
        {
            int index = CurrentMainTabIndex();
            string group = WorkspaceGroupName(index);
            string name = WorkspaceName(index);
            string hint = WorkspaceHint(index);

            if (WorkspaceText != null)
            {
                WorkspaceText.Text = WorkspaceHeaderText(index);
            }

            if (WorkspaceHintText != null)
            {
                WorkspaceHintText.Text = group + " / " + name + " | " + hint;
            }
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
                case TabDashboard:
                    RenderDashboard(snapshot);
                    break;
                case TabMonitor:
                    RenderMonitor();
                    break;
                case TabShortcuts:
                    RenderShortcuts();
                    break;
                case TabOpportunities:
                    RenderOpportunities(snapshot);
                    break;
                case TabScanner:
                    RenderScanner();
                    break;
                case TabHistory:
                    RenderHistory();
                    break;
                case TabAssets:
                    RenderRtdAssets();
                    RenderRtdChannels();
                    RenderRtdReadiness();
                    break;
                case TabQuote:
                    RenderQuoteFields(snapshot);
                    break;
                case TabRtdComplete:
                    RenderRtdComplete(snapshot);
                    break;
                case TabDomBook:
                    RenderDomBook(snapshot);
                    break;
                case TabTape:
                    RenderTape();
                    break;
                case TabOrderFlow:
                case TabVolumeProfile:
                case TabSetups:
                    RenderFlow(snapshot);
                    break;
                case TabFlowMap:
                    RenderFlowMap(snapshot);
                    break;
                case TabHeatmap:
                    RenderHeatmap(snapshot);
                    break;
                case TabIndicators:
                    RenderIndicators(snapshot);
                    break;
                case TabLevels:
                    RenderLevelsWorkspace(snapshot);
                    break;
                case TabChart:
                    ChartControl.SetData(_dailyBars, CurrentSnapshotForCalc(), _result);
                    break;
                case TabRisk:
                    RenderRisk(snapshot);
                    break;
                case TabAlerts:
                    RenderAlerts(snapshot);
                    break;
                case TabDiagnostics:
                    RenderRtdSources();
                    break;
                case TabGarch:
                    RenderGarch(snapshot);
                    break;
            }
        }

        private void ScheduleRenderActiveTab()
        {
            if (_renderActiveTabQueued)
            {
                return;
            }

            _renderActiveTabQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _renderActiveTabQueued = false;
                RenderActiveTab();
            }), DispatcherPriority.Background);
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
            bool control = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

            if (e.Key == Key.F1)
            {
                e.Handled = true;
                NavigateToTab(TabShortcuts);
                return;
            }

            if (e.Key == Key.F2)
            {
                e.Handled = true;
                NavigateToTab(TabOpportunities);
                return;
            }

            if (e.Key == Key.F3)
            {
                e.Handled = true;
                NavigateToTab(TabScanner);
                return;
            }

            if (control && shift && e.Key == Key.F)
            {
                e.Handled = true;
                NavigateToTab(TabFlowMap);
                return;
            }

            if (control && shift && e.Key == Key.I)
            {
                e.Handled = true;
                NavigateToTab(TabIndicators);
                return;
            }

            if (control && shift && e.Key == Key.H)
            {
                e.Handled = true;
                NavigateToTab(TabHeatmap);
                return;
            }

            if (control && shift && e.Key == Key.R)
            {
                e.Handled = true;
                NavigateToTab(TabRtdComplete);
                return;
            }

            if (control)
            {
                int tabIndex = ShortcutTabIndex(e.Key);

                if (tabIndex >= 0)
                {
                    e.Handled = true;
                    NavigateToTab(tabIndex);
                    return;
                }
            }

            if (control && e.Key == Key.Tab)
            {
                e.Handled = true;
                NavigateRelativeTab(shift ? -1 : 1);
                return;
            }

            if (e.Key == Key.F5)
            {
                e.Handled = true;
                ConnectButton_Click(sender, e);
                return;
            }

            if (e.Key == Key.F6)
            {
                e.Handled = true;
                Recalculate();
                return;
            }

            if (e.Key == Key.F8)
            {
                e.Handled = true;
                NavigateToTab(TabAlerts);
                return;
            }

            if (e.Key == Key.F9)
            {
                e.Handled = true;
                NavigateToTab(TabRisk);
                return;
            }

            if (e.Key == Key.F10 || e.SystemKey == Key.F10)
            {
                e.Handled = true;
                NavigateToTab(TabHistory);
                return;
            }

            if (e.Key == Key.F11)
            {
                e.Handled = true;
                NavigateToTab(TabMonitor);
                return;
            }

            if (control && e.Key == Key.M)
            {
                e.Handled = true;
                ManualButton_Click(sender, e);
                return;
            }

            if (control && e.Key == Key.F)
            {
                e.Handled = true;
                FocusSelectedAsset();
                return;
            }

            if (control && e.Key == Key.R)
            {
                e.Handled = true;
                NavigateToTab(TabAssets);
                RenderRtdReadiness();
                return;
            }

            if (control && e.Key == Key.O)
            {
                e.Handled = true;
                OpenCsvDialog();
            }
        }

        private int ShortcutTabIndex(Key key)
        {
            switch (key)
            {
                case Key.D1:
                case Key.NumPad1:
                    return TabDashboard;
                case Key.D2:
                case Key.NumPad2:
                    return TabAssets;
                case Key.D3:
                case Key.NumPad3:
                    return TabDomBook;
                case Key.D4:
                case Key.NumPad4:
                    return TabTape;
                case Key.D5:
                case Key.NumPad5:
                    return TabOrderFlow;
                case Key.D6:
                case Key.NumPad6:
                    return TabVolumeProfile;
                case Key.D7:
                case Key.NumPad7:
                    return TabSetups;
                case Key.D8:
                case Key.NumPad8:
                    return TabLevels;
                case Key.D9:
                case Key.NumPad9:
                    return TabChart;
                case Key.D0:
                case Key.NumPad0:
                    return TabDiagnostics;
                default:
                    return -1;
            }
        }

        private void NavigateRelativeTab(int delta)
        {
            if (MainTabs == null || MainTabs.Items.Count == 0)
            {
                return;
            }

            int index = MainTabs.SelectedIndex;

            if (index < 0)
            {
                index = 0;
            }

            int next = (index + delta + MainTabs.Items.Count) % MainTabs.Items.Count;
            NavigateToTab(next);
        }

        private void RecalcButton_Click(object sender, RoutedEventArgs e)
        {
            Recalculate();
        }

        private void CalculationDaysComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncCalculationDaysSelection || CalculationDaysComboBox == null)
            {
                return;
            }

            int selectedDays = SelectedCalculationDays();

            if (_config.Ui.CalculationDays != selectedDays)
            {
                _config.Ui.CalculationDays = selectedDays;
                _config.Ui.Normalize();
                SaveRuntimeConfig();
                AddHistory("App", "Janela de calculo", selectedDays.ToString(_ptBr) + " dias selecionados.");
            }

            Recalculate();
        }

        private void ChartTimeframeButton_Checked(object sender, RoutedEventArgs e)
        {
            if (_syncChartTimeframeSelection)
            {
                return;
            }

            RadioButton button = sender as RadioButton;
            int selectedIndex = button == null ? _config.Ui.ChartTimeframeIndex : ParseChartTimeframeIndex(button.Tag);

            selectedIndex = UiConfig.NormalizeChartTimeframeIndex(selectedIndex);

            if (_config.Ui.ChartTimeframeIndex != selectedIndex)
            {
                _config.Ui.ChartTimeframeIndex = selectedIndex;
                _config.Ui.Normalize();
                SaveRuntimeConfig();
                AddHistory("App", "Timeframe de grafico", ChartTimeframeText(SelectedChartTimeframe()) + " selecionado.");
            }

            ApplyChartDisplaySelection();
        }

        private void ChartPriceGridButton_Checked(object sender, RoutedEventArgs e)
        {
            if (_syncChartPriceGridSelection)
            {
                return;
            }

            RadioButton button = sender as RadioButton;
            int selectedTicks = button == null ? _config.Ui.PriceGridTickInterval : ParseChartPriceGridTicks(button.Tag);
            selectedTicks = UiConfig.NormalizePriceGridTickInterval(selectedTicks);

            if (_config.Ui.PriceGridTickInterval != selectedTicks)
            {
                _config.Ui.PriceGridTickInterval = selectedTicks;
                _config.Ui.Normalize();
                SaveRuntimeConfig();
                AddHistory("App", "Ticks do grafico", "Linhas de preco a cada " + selectedTicks.ToString(_ptBr) + " ticks.");
            }

            ApplyChartDisplaySelection();
        }

        private void ChartCandleSpacingComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncChartCandleSpacingSelection)
            {
                return;
            }

            ComboBox combo = sender as ComboBox;
            ComboBoxItem selectedItem = combo == null ? null : combo.SelectedItem as ComboBoxItem;
            int selectedPercent = combo == null ? _config.Ui.CandleSpacingPercent : ParseChartCandleSpacingPercent(selectedItem == null ? null : selectedItem.Tag);
            selectedPercent = UiConfig.NormalizeCandleSpacingPercent(selectedPercent);

            if (_config.Ui.CandleSpacingPercent != selectedPercent)
            {
                _config.Ui.CandleSpacingPercent = selectedPercent;
                _config.Ui.Normalize();
                SaveRuntimeConfig();
                AddHistory("App", "Espaco do grafico", "Espacamento horizontal a " + selectedPercent.ToString(_ptBr) + "%.");
            }

            ApplyChartDisplaySelection();
        }

        private void InitializeChartLineVisibilitySelection()
        {
            if (ChartShowCandlesCheckBox == null ||
                ChartShowPriceGridCheckBox == null ||
                ChartShowCurrentPriceLineCheckBox == null ||
                ChartShowConfluenceCheckBox == null ||
                ChartShowKeyLevelsCheckBox == null ||
                ChartShowRtdLevelsCheckBox == null ||
                ChartShowProfileLevelsCheckBox == null ||
                ChartShowTechnicalLevelsCheckBox == null ||
                ChartShowMarketLevelsCheckBox == null ||
                ChartShowPercentLevelsCheckBox == null ||
                ChartShowGarmanLevelsCheckBox == null ||
                ChartShowGaussLevelsCheckBox == null ||
                ChartShowStdDevLevelsCheckBox == null ||
                ChartShowGarchLevelsCheckBox == null)
            {
                return;
            }

            _syncChartLineVisibilitySelection = true;

            try
            {
                ChartShowCandlesCheckBox.IsChecked = _config.Ui.ShowChartCandles;
                ChartShowPriceGridCheckBox.IsChecked = _config.Ui.ShowChartPriceGrid;
                ChartShowCurrentPriceLineCheckBox.IsChecked = _config.Ui.ShowChartCurrentPriceLine;
                ChartShowConfluenceCheckBox.IsChecked = _config.Ui.ShowChartConfluenceLevels;
                ChartShowKeyLevelsCheckBox.IsChecked = _config.Ui.ShowChartKeyLevels;
                ChartShowRtdLevelsCheckBox.IsChecked = _config.Ui.ShowChartRtdLevels;
                ChartShowProfileLevelsCheckBox.IsChecked = _config.Ui.ShowChartProfileLevels;
                ChartShowTechnicalLevelsCheckBox.IsChecked = _config.Ui.ShowChartTechnicalLevels;
                ChartShowMarketLevelsCheckBox.IsChecked = _config.Ui.ShowChartMarketLevels;
                ChartShowPercentLevelsCheckBox.IsChecked = _config.Ui.ShowChartPercentLevels;
                ChartShowGarmanLevelsCheckBox.IsChecked = _config.Ui.ShowChartGarmanLevels;
                ChartShowGaussLevelsCheckBox.IsChecked = _config.Ui.ShowChartGaussLevels;
                ChartShowStdDevLevelsCheckBox.IsChecked = _config.Ui.ShowChartStdDevLevels;
                ChartShowGarchLevelsCheckBox.IsChecked = _config.Ui.ShowChartGarchLevels;
            }
            finally
            {
                _syncChartLineVisibilitySelection = false;
            }

            ApplyChartDisplaySelection();
        }

        private void ChartLineVisibility_Checked(object sender, RoutedEventArgs e)
        {
            if (_syncChartLineVisibilitySelection)
            {
                return;
            }

            bool showCandles = ChartShowCandlesCheckBox != null && ChartShowCandlesCheckBox.IsChecked == true;
            bool showPriceGrid = ChartShowPriceGridCheckBox != null && ChartShowPriceGridCheckBox.IsChecked == true;
            bool showCurrentPriceLine = ChartShowCurrentPriceLineCheckBox != null && ChartShowCurrentPriceLineCheckBox.IsChecked == true;
            bool showConfluence = ChartShowConfluenceCheckBox != null && ChartShowConfluenceCheckBox.IsChecked == true;
            bool showKeyLevels = ChartShowKeyLevelsCheckBox != null && ChartShowKeyLevelsCheckBox.IsChecked == true;
            bool showRtdLevels = ChartShowRtdLevelsCheckBox != null && ChartShowRtdLevelsCheckBox.IsChecked == true;
            bool showProfileLevels = ChartShowProfileLevelsCheckBox != null && ChartShowProfileLevelsCheckBox.IsChecked == true;
            bool showTechnicalLevels = ChartShowTechnicalLevelsCheckBox != null && ChartShowTechnicalLevelsCheckBox.IsChecked == true;
            bool showMarketLevels = ChartShowMarketLevelsCheckBox != null && ChartShowMarketLevelsCheckBox.IsChecked == true;
            bool showPercentLevels = ChartShowPercentLevelsCheckBox != null && ChartShowPercentLevelsCheckBox.IsChecked == true;
            bool showGarmanLevels = ChartShowGarmanLevelsCheckBox != null && ChartShowGarmanLevelsCheckBox.IsChecked == true;
            bool showGaussLevels = ChartShowGaussLevelsCheckBox != null && ChartShowGaussLevelsCheckBox.IsChecked == true;
            bool showStdDevLevels = ChartShowStdDevLevelsCheckBox != null && ChartShowStdDevLevelsCheckBox.IsChecked == true;
            bool showGarchLevels = ChartShowGarchLevelsCheckBox != null && ChartShowGarchLevelsCheckBox.IsChecked == true;

            if (_config.Ui.ShowChartCandles != showCandles ||
                _config.Ui.ShowChartPriceGrid != showPriceGrid ||
                _config.Ui.ShowChartCurrentPriceLine != showCurrentPriceLine ||
                _config.Ui.ShowChartConfluenceLevels != showConfluence ||
                _config.Ui.ShowChartKeyLevels != showKeyLevels ||
                _config.Ui.ShowChartRtdLevels != showRtdLevels ||
                _config.Ui.ShowChartProfileLevels != showProfileLevels ||
                _config.Ui.ShowChartTechnicalLevels != showTechnicalLevels ||
                _config.Ui.ShowChartMarketLevels != showMarketLevels ||
                _config.Ui.ShowChartPercentLevels != showPercentLevels ||
                _config.Ui.ShowChartGarmanLevels != showGarmanLevels ||
                _config.Ui.ShowChartGaussLevels != showGaussLevels ||
                _config.Ui.ShowChartStdDevLevels != showStdDevLevels ||
                _config.Ui.ShowChartGarchLevels != showGarchLevels)
            {
                _config.Ui.ShowChartCandles = showCandles;
                _config.Ui.ShowChartPriceGrid = showPriceGrid;
                _config.Ui.ShowChartCurrentPriceLine = showCurrentPriceLine;
                _config.Ui.ShowChartConfluenceLevels = showConfluence;
                _config.Ui.ShowChartKeyLevels = showKeyLevels;
                _config.Ui.ShowChartRtdLevels = showRtdLevels;
                _config.Ui.ShowChartProfileLevels = showProfileLevels;
                _config.Ui.ShowChartTechnicalLevels = showTechnicalLevels;
                _config.Ui.ShowChartMarketLevels = showMarketLevels;
                _config.Ui.ShowChartPercentLevels = showPercentLevels;
                _config.Ui.ShowChartGarmanLevels = showGarmanLevels;
                _config.Ui.ShowChartGaussLevels = showGaussLevels;
                _config.Ui.ShowChartStdDevLevels = showStdDevLevels;
                _config.Ui.ShowChartGarchLevels = showGarchLevels;
                SaveRuntimeConfig();
                AddHistory("App", "Exibicao grafico", "Configuracao de linhas atualizada.");
            }

            ApplyChartDisplaySelection();
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

        private void FocusMonitorAssetButton_Click(object sender, RoutedEventArgs e)
        {
            FocusSelectedMonitorAsset();
        }

        private void RefreshScannerButton_Click(object sender, RoutedEventArgs e)
        {
            RenderScanner();
        }

        private void RefreshFlowMapButton_Click(object sender, RoutedEventArgs e)
        {
            RenderFlowMap(FocusedSnapshot() ?? _lastSnapshot);
        }

        private void RefreshRtdCompleteButton_Click(object sender, RoutedEventArgs e)
        {
            RenderRtdComplete(FocusedSnapshot() ?? _lastSnapshot);
        }

        private void LoadSelectedRtdCompleteGroupButton_Click(object sender, RoutedEventArgs e)
        {
            ActivateRtdCompleteGroup(_selectedRtdCompleteGroup);
        }

        private void LoadAllRtdCompleteButton_Click(object sender, RoutedEventArgs e)
        {
            ActivateAllRtdCompleteGroups();
        }

        private void ClearRtdCompleteExtrasButton_Click(object sender, RoutedEventArgs e)
        {
            ClearRtdCompleteExtras();
        }

        private void RtdCompleteGroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncRtdCompleteGroupSelection)
            {
                return;
            }

            RtdCompleteGroupRow row = RtdCompleteGroupList == null ? null : RtdCompleteGroupList.SelectedItem as RtdCompleteGroupRow;

            if (row == null || string.IsNullOrWhiteSpace(row.Group))
            {
                return;
            }

            _selectedRtdCompleteGroup = row.Group;
            RenderRtdComplete(FocusedSnapshot() ?? _lastSnapshot);
        }

        private void RtdCompleteSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _rtdCompleteSearch = RtdCompleteSearchBox == null ? string.Empty : RtdCompleteSearchBox.Text;
            RenderRtdComplete(FocusedSnapshot() ?? _lastSnapshot);
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
            EnsureDefaultSourcesForFocusedAsset();
        }

        private void EnsureDefaultSourcesForFocusedAsset()
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

        private void ToggleFocusedQuoteButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleFocusedChannel("Cotacao");
        }

        private void ToggleFocusedBookButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleFocusedChannel("Book");
        }

        private void ToggleFocusedTimesButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleFocusedChannel("Times");
        }

        private void RtdAssetsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            FocusSelectedAsset();
        }

        private void MonitorGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            FocusSelectedMonitorAsset();
        }

        private void ScannerGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            FocusSelectedScannerAsset();
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

        private void FocusSelectedMonitorAsset()
        {
            string asset = SelectedMonitorAsset();

            if (string.IsNullOrWhiteSpace(asset))
            {
                SetWarnings(new[] { "Selecione um ativo no Monitor." });
                return;
            }

            SetFocusedAsset(asset);
            SaveRuntimeConfig();
            RenderMonitor();
            RenderRtdAssets();
            ApplyFocusedSnapshot();
            SetWarnings(new[] { "Ativo em foco: " + asset + "." });
        }

        private void FocusSelectedScannerAsset()
        {
            string asset = SelectedScannerAsset();

            if (string.IsNullOrWhiteSpace(asset))
            {
                SetWarnings(new[] { "Selecione um ativo no Scanner." });
                return;
            }

            SetFocusedAsset(asset);
            SaveRuntimeConfig();
            RenderScanner();
            RenderMonitor();
            RenderRtdAssets();
            ApplyFocusedSnapshot();
            SetWarnings(new[] { "Ativo em foco: " + asset + "." });
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

        private string SelectedMonitorAsset()
        {
            if (MonitorGrid == null)
            {
                return string.Empty;
            }

            MonitorRow row = MonitorGrid.SelectedItem as MonitorRow;

            if (row != null)
            {
                return row.Asset;
            }

            return string.Empty;
        }

        private string SelectedScannerAsset()
        {
            if (ScannerGrid == null)
            {
                return string.Empty;
            }

            ScannerRow row = ScannerGrid.SelectedItem as ScannerRow;

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

        private void ToggleFocusedChannel(string channel)
        {
            string asset = FocusedAsset();

            if (string.IsNullOrWhiteSpace(asset))
            {
                SetWarnings(new[] { "Selecione um ativo RTD." });
                return;
            }

            RtdAssetConfig assetConfig = _config.Rtd.FindAsset(asset);

            if (assetConfig == null)
            {
                SetWarnings(new[] { "Ativo RTD nao encontrado: " + asset + "." });
                return;
            }

            bool enabled = !ChannelEnabled(asset, channel);
            bool changed = SetChannelEnabled(asset, channel, enabled);

            if (!changed)
            {
                RenderRtdChannels();
                RenderRtdSources();
                UpdateFocusedAssetPanel();
                return;
            }

            ApplyRtdAssetChange("Canal " + channel + " " + (enabled ? "ligado" : "desligado") + " para " + asset + ".");
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
                string focusedAsset = FocusedAsset();
                string historyAsset = DetermineCsvHistoryAsset(parsed.Bars, focusedAsset);
                string storageAsset = string.IsNullOrWhiteSpace(historyAsset) ? focusedAsset : historyAsset;
                List<string> warnings = parsed.Warnings == null ? new List<string>() : parsed.Warnings.ToList();
                List<DailyBar> consolidatedBars = new List<DailyBar>(parsed.Bars);
                bool loadedFromSql = false;

                try
                {
                    if (parsed.Bars.Count > 0)
                    {
                        int persisted = _csvHistoryStore.UpsertBars(parsed.Bars, storageAsset, path);
                        _log.Info("Historico CSV gravado em SQL: " + persisted.ToString(_ptBr) + " linha(s) para " + storageAsset + ".");
                    }

                    List<DailyBar> storedBars = _csvHistoryStore.LoadBars(storageAsset);

                    if (storedBars.Count > 0)
                    {
                        consolidatedBars = storedBars;
                        loadedFromSql = parsed.Bars.Count == 0;
                    }
                }
                catch (Exception historyEx)
                {
                    warnings.Add("Falha ao sincronizar historico SQL: " + historyEx.Message);
                    _log.Error("Falha ao sincronizar historico CSV em SQL.", historyEx);
                }

                if (consolidatedBars == null || consolidatedBars.Count == 0)
                {
                    throw new InvalidOperationException("CSV nao possui pregões validos.");
                }

                warnings.RemoveAll(w => !string.IsNullOrWhiteSpace(w) && w.IndexOf("menos de 21 pregoes validos", StringComparison.OrdinalIgnoreCase) >= 0);
                if (loadedFromSql)
                {
                    warnings.RemoveAll(w => string.Equals(w, "Arquivo CSV vazio.", StringComparison.OrdinalIgnoreCase) ||
                                             string.Equals(w, "Nenhuma linha valida no CSV.", StringComparison.OrdinalIgnoreCase));
                }

                _dailyBars.Clear();
                _dailyBars.AddRange(consolidatedBars);
                CsvPathInput.Text = path;
                string encodingText = string.IsNullOrWhiteSpace(parsed.EncodingName) ? "-" : parsed.EncodingName;
                string delimiterText = parsed.Delimiter == '\0' ? "-" : parsed.Delimiter.ToString();
                CsvFileText.Text = Path.GetFileName(path) + " (" + encodingText + ", delim " + delimiterText + (loadedFromSql ? ", historico SQL" : string.Empty) + ")";
                CsvFileText.Foreground = new SolidColorBrush(Color.FromRgb(0, 230, 118));
                CsvCountText.Text = _dailyBars.Count.ToString(_ptBr) + " pregoes";

                if (saveToFocusedAsset)
                {
                    RtdAssetConfig asset = _config.Rtd.FindAsset(focusedAsset);

                    if (asset != null)
                    {
                        asset.CsvPath = path;
                        asset.Normalize();
                        SaveRuntimeConfig();
                        RenderRtdAssets();
                        PopulateAssetForm(asset.Asset);
                    }
                }

                if (_dailyBars.Count < 21)
                {
                    warnings.Add("Historico CSV consolidado tem menos de 21 pregoes validos.");
                }

                if (parsed.Bars.Count == 0 && loadedFromSql)
                {
                    warnings.Add("CSV sem novas barras; usando historico SQL.");
                }

                SetWarnings(warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
                AddHistory("CSV", "Sincronizado", Path.GetFileName(path) + " | " + _dailyBars.Count.ToString(_ptBr) + " pregoes.");
                _log.Info("CSV sincronizado: " + _dailyBars.Count.ToString(_ptBr) + " pregoes consolidados, " + parsed.EncodingName + ", delimitador " + parsed.Delimiter + ".");
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

        private string DetermineCsvHistoryAsset(IEnumerable<DailyBar> bars, string fallbackAsset)
        {
            List<string> assets = new List<string>();

            if (bars != null)
            {
                assets = bars
                    .Where(x => x != null)
                    .Select(x => RtdConfig.NormalizeAsset(x.Asset))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (assets.Count == 1)
            {
                return assets[0];
            }

            return RtdConfig.NormalizeAsset(fallbackAsset);
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

            if (!string.IsNullOrWhiteSpace(FocusedAsset()))
            {
                try
                {
                    List<DailyBar> stored = _csvHistoryStore.LoadBars(FocusedAsset());

                    if (stored.Count > 0)
                    {
                        _dailyBars.Clear();
                        _dailyBars.AddRange(stored);

                        if (CsvPathInput != null)
                        {
                            CsvPathInput.Text = string.Empty;
                        }

                        if (CsvFileText != null)
                        {
                            CsvFileText.Text = "Historico SQL (" + FocusedAsset() + ")";
                            CsvFileText.Foreground = new SolidColorBrush(Color.FromRgb(0, 230, 118));
                        }

                        if (CsvCountText != null)
                        {
                            CsvCountText.Text = _dailyBars.Count.ToString(_ptBr) + " pregoes";
                        }

                        SetWarnings(new[] { "Carregado do historico SQL salvo para " + FocusedAsset() + "." });
                        AddHistory("CSV", "Restaurado", "Historico SQL de " + FocusedAsset() + " | " + _dailyBars.Count.ToString(_ptBr) + " pregoes.");
                        Recalculate();
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn("Falha ao carregar historico CSV do SQL: " + ex.Message);
                }
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

                if (!string.Equals(_lastHistoryRtdStatus, status, StringComparison.OrdinalIgnoreCase))
                {
                    _lastHistoryRtdStatus = status;
                    AddHistory("RTD", "Status", EmptyToDash(status) + (error == null ? string.Empty : " | " + error.Message));
                }
            }));
        }

        private void ProbeService_TickReceived(TickEvent tick)
        {
            _ticks.Add(tick);
            _intradayAggregator.Add(tick);
        }

        private void ProbeService_SnapshotReceived(MarketSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Asset))
            {
                return;
            }

            _intradayAggregator.AddFromSnapshot(snapshot);

            MarketSnapshot previousSnapshot = null;

            lock (_snapshotsLock)
            {
                _snapshotsByAsset.TryGetValue(snapshot.Asset, out previousSnapshot);
                _snapshotsByAsset[snapshot.Asset] = snapshot;
            }

            bool bookChanged = SnapshotHasPrefixChanged(previousSnapshot, snapshot, "BOOK_");
            bool timesChanged = SnapshotHasPrefixChanged(previousSnapshot, snapshot, "TIMES_");
            bool quoteChanged = SnapshotHasAnyNonPrefixedChange(previousSnapshot, snapshot, new[] { "BOOK_", "TIMES_" });

            if (previousSnapshot == null || bookChanged || quoteChanged)
            {
                _heatmapProcessor.PostSnapshot(snapshot);
            }

            if (previousSnapshot == null || timesChanged)
            {
                PostRealTimes(snapshot, BuildTimesRows(snapshot));
            }

            if (previousSnapshot == null || quoteChanged)
            {
                _flowProcessor.Post(snapshot);
            }
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
            bool showDashboard = selectedTab == TabDashboard;
            bool showMonitor = selectedTab == TabMonitor;
            bool showAssets = selectedTab == TabAssets;
            bool showQuote = selectedTab == TabQuote;
            bool showDomBook = selectedTab == TabDomBook;
            bool showTape = selectedTab == TabDomBook || selectedTab == TabTape;
            bool showFlow = selectedTab == TabOrderFlow || selectedTab == TabVolumeProfile || selectedTab == TabSetups;
            bool showFlowMap = selectedTab == TabFlowMap;
            bool showHeatmap = selectedTab == TabHeatmap;
            bool showRisk = selectedTab == TabRisk;
            bool showAlerts = selectedTab == TabAlerts;
            bool showDiagnostics = selectedTab == TabDiagnostics;
            bool showOpportunities = selectedTab == TabOpportunities;
            bool showHistory = selectedTab == TabHistory;
            bool showScanner = selectedTab == TabScanner;
            bool showIndicators = selectedTab == TabIndicators;
            bool showLevels = selectedTab == TabLevels;
            bool showRtdComplete = selectedTab == TabRtdComplete;
            bool renderedDashboard = false;
            bool renderedMonitor = false;
            bool renderedRisk = false;
            bool renderedAlerts = false;
            bool renderedOpportunities = false;
            bool renderedScanner = false;
            bool renderedFlowMap = false;
            bool renderedHeatmap = false;
            bool renderedIndicators = false;
            bool renderedLevels = false;
            bool renderedRtdComplete = false;

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

                    if (showLevels)
                    {
                        RenderLevelsWorkspace(snapshot);
                        renderedLevels = true;
                    }

                    if (showRtdComplete)
                    {
                        RenderRtdComplete(snapshot);
                        renderedRtdComplete = true;
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
                if (showDashboard)
                {
                    RenderDashboard(snapshot);
                    renderedDashboard = true;
                }

                if (showMonitor)
                {
                    RenderMonitor();
                    renderedMonitor = true;
                }

                if (showDomBook)
                {
                    RenderDomBook(snapshot);
                }
                else if (showTape)
                {
                    RenderTape();
                }

                if (showFlow)
                {
                    RenderFlow(snapshot);
                }

                if (showFlowMap)
                {
                    RenderFlowMap(snapshot);
                    renderedFlowMap = true;
                }

                if (showHeatmap)
                {
                    RenderHeatmap(snapshot);
                    renderedHeatmap = true;
                }

                if (showRisk)
                {
                    RenderRisk(snapshot);
                    renderedRisk = true;
                }

                if (showAlerts)
                {
                    RenderAlerts(snapshot);
                    renderedAlerts = true;
                }

                if (showOpportunities)
                {
                    RenderOpportunities(snapshot);
                    renderedOpportunities = true;
                }

                if (showScanner)
                {
                    RenderScanner();
                    renderedScanner = true;
                }

                if (showIndicators)
                {
                    RenderIndicators(snapshot);
                    renderedIndicators = true;
                }

                if (showLevels && !renderedLevels)
                {
                    RenderLevelsWorkspace(snapshot);
                    renderedLevels = true;
                }

                if (showRtdComplete && !renderedRtdComplete)
                {
                    RenderRtdComplete(snapshot);
                    renderedRtdComplete = true;
                }

                _lastGridRefresh = now;
                _lastFlowProcessed = flowVersion;
            }

            UpdatesText.Text = _probeService.UpdatesReceived.ToString(_ptBr);
            UpdateRuntimeStatusBar(snapshot);

            if ((now - _lastAssetGridRefresh).TotalMilliseconds >= 1000)
            {
                if (showDashboard && !renderedDashboard)
                {
                    RenderDashboard(snapshot);
                }

                if (showMonitor && !renderedMonitor)
                {
                    RenderMonitor();
                }

                if (showAssets)
                {
                    RenderRtdAssets();
                    RenderRtdChannels();
                }

                if (showDiagnostics)
                {
                    RenderRtdSources();
                }

                if (showRisk && !renderedRisk)
                {
                    RenderRisk(snapshot);
                }

                if (showAlerts && !renderedAlerts)
                {
                    RenderAlerts(snapshot);
                }

                if (showOpportunities && !renderedOpportunities)
                {
                    RenderOpportunities(snapshot);
                }

                if (showScanner && !renderedScanner)
                {
                    RenderScanner();
                }

                if (showFlowMap && !renderedFlowMap)
                {
                    RenderFlowMap(snapshot);
                }

                if (showHeatmap && !renderedHeatmap)
                {
                    RenderHeatmap(snapshot);
                }

                if (showIndicators && !renderedIndicators)
                {
                    RenderIndicators(snapshot);
                }

                if (showLevels && !renderedLevels)
                {
                    RenderLevelsWorkspace(snapshot);
                }

                if (showRtdComplete && !renderedRtdComplete)
                {
                    RenderRtdComplete(snapshot);
                }

                if (showHistory)
                {
                    RenderHistory();
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
            if (CurrentMainTabIndex() != TabChart)
            {
                return;
            }

            ChartControl.SetData(_dailyBars, CurrentSnapshotForCalc(), _result);
        }

        private void RenderDashboard(MarketSnapshot snapshot)
        {
            if (DashboardAssetText == null)
            {
                return;
            }

            string focused = FocusedAsset();
            RtdAssetConfig asset = _config.Rtd.FindAsset(focused);
            FlowMetrics metrics = _flowProcessor.GetMetrics(focused);

            if (snapshot == null)
            {
                snapshot = FocusedSnapshot() ?? _lastSnapshot;
            }

            DashboardAssetText.Text = EmptyToDash(focused);
            DashboardPriceText.Text = snapshot == null ? "-" : FormatDecimal(snapshot.Ultimo, "N2");
            DashboardBidAskText.Text = snapshot == null
                ? "Bid / Ask -"
                : "Bid / Ask " + FormatDecimal(snapshot.OfertaCompra, "N2") + " / " + FormatDecimal(snapshot.OfertaVenda, "N2");
            DashboardVolumeText.Text = snapshot == null ? "Volume -" : "Volume " + FormatDecimal(snapshot.Volume, "N0");
            DashboardSnapshotText.Text = snapshot == null
                ? "Snapshot -"
                : "Snapshot " + Math.Max(0, (int)(DateTimeOffset.Now - snapshot.LocalTimestamp).TotalMilliseconds).ToString(_ptBr) + " ms";

            UpdateDashboardWorkflow(asset, snapshot, metrics);

            DashboardRtdText.Text = "RTD " + EmptyToDash(_probeService.Status) + " | Updates " + _probeService.UpdatesReceived.ToString(_ptBr);
            DashboardCsvText.Text = "CSV " + FocusedAssetCsvText(asset);

            if (metrics == null)
            {
                DashboardFlowText.Text = "Qualidade -";
                DashboardDeltaText.Text = "Delta -";
                DashboardMicroText.Text = "Microbias -";
                DashboardVwapText.Text = "VWAP -";
                DashboardWindowsGrid.ItemsSource = null;
            }
            else
            {
                DashboardFlowText.Text = "Qualidade " + metrics.DataQuality + (metrics.Derived ? " derivado" : " real");
                DashboardDeltaText.Text = "Delta " + metrics.LastDelta.ToString("N0", _ptBr) + " | CD " + metrics.CumulativeDelta.ToString("N0", _ptBr);
                DashboardMicroText.Text = "Microbias " + FormatDecimal(metrics.MicroBias, "N3") + " | Imbalance " + FormatDecimal(metrics.TopBookImbalance, "N3");
                DashboardVwapText.Text = "VWAP " + FormatDecimal(metrics.Vwap, "N2") + " | Dist " + FormatDecimal(metrics.VwapDistance, "N2");
                DashboardWindowsGrid.ItemsSource = BuildDashboardWindowRows(metrics);
            }

            UpdateDashboardProfile(snapshot, metrics);
            RefreshDashboardHeavyGrids(snapshot, asset);

            RefreshDashboardChart(snapshot);
        }

        private void UpdateDashboardWorkflow(RtdAssetConfig asset, MarketSnapshot snapshot, FlowMetrics metrics)
        {
            if (WorkflowAssetValueText == null)
            {
                return;
            }

            string focused = FocusedAsset();
            string assetState = asset == null
                ? "cadastrar ativo"
                : asset.Enabled ? "ativo ligado" : "ativo desligado";
            Brush assetBrush = asset == null
                ? FindResource("Danger") as Brush
                : asset.Enabled ? FindResource("Accent") as Brush : FindResource("Warn") as Brush;
            SetWorkflowStep(WorkflowAssetButton, WorkflowAssetValueText, WorkflowAssetStateText, EmptyToDash(focused), assetState, assetBrush);

            string rtdStatus = _probeService == null ? "idle" : EmptyToDash(_probeService.Status);
            string rtdState = RtdWorkflowState(snapshot);
            Brush rtdBrush = WorkflowStateBrush(rtdState, rtdStatus);
            SetWorkflowStep(WorkflowRtdButton, WorkflowRtdValueText, WorkflowRtdStateText, rtdStatus, rtdState, rtdBrush);

            string historyValue = _dailyBars.Count.ToString(_ptBr) + " pregoes";
            string historyState = HistoryWorkflowState(asset);
            Brush historyBrush = WorkflowHistoryBrush(historyState);
            SetWorkflowStep(WorkflowHistoryButton, WorkflowHistoryValueText, WorkflowHistoryStateText, historyValue, historyState, historyBrush);

            string flowValue = metrics == null ? "sem fluxo" : metrics.DataQuality.ToString();
            string flowState = FlowWorkflowState(metrics);
            Brush flowBrush = WorkflowFlowBrush(metrics);
            SetWorkflowStep(WorkflowFlowButton, WorkflowFlowValueText, WorkflowFlowStateText, flowValue, flowState, flowBrush);

            WorkflowOpportunity opportunity = BuildWorkflowOpportunity(asset, snapshot, metrics);
            SetWorkflowStep(
                WorkflowOpportunityButton,
                WorkflowOpportunityValueText,
                WorkflowOpportunityStateText,
                opportunity.Value,
                opportunity.State,
                opportunity.Brush);
        }

        private void SetWorkflowStep(Button button, TextBlock valueText, TextBlock stateText, string value, string state, Brush brush)
        {
            Brush effective = brush ?? FindResource("Muted") as Brush ?? Brushes.Gray;

            if (button != null)
            {
                button.BorderBrush = effective;
            }

            if (valueText != null)
            {
                valueText.Text = EmptyToDash(value);
                valueText.Foreground = effective;
            }

            if (stateText != null)
            {
                stateText.Text = EmptyToDash(state);
                stateText.Foreground = effective;
            }
        }

        private string RtdWorkflowState(MarketSnapshot snapshot)
        {
            if (_probeService == null || !_probeService.IsRunning)
            {
                return "conectar RTD";
            }

            if (snapshot == null)
            {
                return "aguardando snapshot";
            }

            double ageSeconds = Math.Max(0d, (DateTimeOffset.Now - snapshot.LocalTimestamp).TotalSeconds);

            if (ageSeconds >= 15d)
            {
                return "snapshot atrasado";
            }

            if (ageSeconds >= 5d)
            {
                return "snapshot lento";
            }

            return "snapshot fresco";
        }

        private Brush WorkflowStateBrush(string state, string status)
        {
            if (string.Equals(state, "snapshot fresco", StringComparison.OrdinalIgnoreCase))
            {
                return FindResource("Accent") as Brush;
            }

            if (string.Equals(state, "snapshot atrasado", StringComparison.OrdinalIgnoreCase))
            {
                return FindResource("Danger") as Brush;
            }

            return StateBrush(status);
        }

        private string HistoryWorkflowState(RtdAssetConfig asset)
        {
            if (asset == null)
            {
                return "sem cadastro";
            }

            int calculationDays = SelectedCalculationDays();

            if (_dailyBars.Count >= calculationDays * 3)
            {
                return "amostra forte";
            }

            if (_dailyBars.Count >= calculationDays * 2)
            {
                return "amostra boa";
            }

            if (_dailyBars.Count >= calculationDays)
            {
                return "amostra minima";
            }

            if (string.IsNullOrWhiteSpace(asset.CsvPath))
            {
                return "selecionar CSV";
            }

            return "CSV insuficiente";
        }

        private Brush WorkflowHistoryBrush(string state)
        {
            if (string.Equals(state, "amostra forte", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(state, "amostra boa", StringComparison.OrdinalIgnoreCase))
            {
                return FindResource("Accent") as Brush;
            }

            if (string.Equals(state, "amostra minima", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(state, "selecionar CSV", StringComparison.OrdinalIgnoreCase))
            {
                return FindResource("Warn") as Brush;
            }

            return FindResource("Danger") as Brush;
        }

        private string FlowWorkflowState(FlowMetrics metrics)
        {
            if (metrics == null)
            {
                return "aguardar feed";
            }

            if (metrics.DataQuality == MarketDataQuality.FullDepth)
            {
                return "book profundo real";
            }

            if (metrics.DataQuality == MarketDataQuality.FullTimesAndTrades)
            {
                return "times real";
            }

            if (metrics.DataQuality == MarketDataQuality.DerivedTape)
            {
                return "tape derivado";
            }

            return "top book";
        }

        private Brush WorkflowFlowBrush(FlowMetrics metrics)
        {
            if (metrics == null)
            {
                return FindResource("Warn") as Brush;
            }

            if (!metrics.Derived &&
                (metrics.DataQuality == MarketDataQuality.FullDepth ||
                 metrics.DataQuality == MarketDataQuality.FullTimesAndTrades))
            {
                return FindResource("Accent") as Brush;
            }

            if (metrics.DataQuality == MarketDataQuality.DerivedTape)
            {
                return FindResource("Warn") as Brush;
            }

            return FindResource("Muted") as Brush;
        }

        private WorkflowOpportunity BuildWorkflowOpportunity(RtdAssetConfig asset, MarketSnapshot snapshot, FlowMetrics metrics)
        {
            WorkflowOpportunity result = new WorkflowOpportunity();
            result.Value = "sem setup";
            result.State = "monitorar";
            result.Brush = FindResource("Muted") as Brush;

            string focused = FocusedAsset();
            List<FlowSignal> flowSignals = _flowProcessor.GetSignals(focused, 1) ?? new List<FlowSignal>();
            FlowSignal flow = flowSignals.OrderByDescending(x => x.Score).ThenByDescending(x => x.LocalTimestamp).FirstOrDefault();
            QuantSignal quant = BestQuantSignalForAsset(focused, metrics);

            if (flow == null && quant == null)
            {
                OpportunityScore waiting = ScoreOpportunity(asset, snapshot, metrics, null, null, null);
                result.State = waiting.Robustness;
                result.Brush = WorkflowRobustnessBrush(waiting.Robustness);
                return result;
            }

            OpportunityScore score = ScoreOpportunity(asset, snapshot, metrics, flow, quant, null);
            string setup = flow != null ? flow.Setup : quant.Setup;
            string direction = flow != null ? flow.Direction : quant.Direction;
            result.Value = score.Score.ToString(_ptBr) + " " + score.Robustness;
            result.State = EmptyToDash(setup) + " " + TranslateDirection(direction);
            result.Brush = WorkflowRobustnessBrush(score.Robustness);
            return result;
        }

        private Brush WorkflowRobustnessBrush(string robustness)
        {
            if (string.Equals(robustness, "Robusto", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(robustness, "Acionavel", StringComparison.OrdinalIgnoreCase))
            {
                return FindResource("Accent") as Brush;
            }

            if (string.Equals(robustness, "Monitorar", StringComparison.OrdinalIgnoreCase))
            {
                return FindResource("Warn") as Brush;
            }

            if (string.Equals(robustness, "Bloqueado", StringComparison.OrdinalIgnoreCase))
            {
                return FindResource("Danger") as Brush;
            }

            return FindResource("Muted") as Brush;
        }

        private void RefreshDashboardHeavyGrids(MarketSnapshot snapshot, RtdAssetConfig asset)
        {
            if (!ShouldRefreshDashboardHeavy())
            {
                return;
            }

            DashboardChannelsGrid.ItemsSource = BuildDashboardChannelRows(asset, snapshot);
            DashboardLevelsGrid.ItemsSource = BuildOpportunityLevelRows(snapshot).Take(40).ToList();
            DashboardSignalsGrid.ItemsSource = BuildOpportunityRows().Take(40).ToList();

            if (DashboardDomGrid != null)
            {
                DashboardDomGrid.ItemsSource = BuildDashboardDomRows(snapshot);
                ScheduleDashboardDomCenter();
            }

            if (DashboardTapeGrid != null)
            {
                DashboardTapeGrid.ItemsSource = BuildDashboardTapeRows(snapshot);
            }
        }

        private bool ShouldRefreshDashboardHeavy()
        {
            DateTimeOffset now = DateTimeOffset.Now;
            string focused = FocusedAsset();
            long version = _probeService == null ? -1 : _probeService.UpdatesReceived;
            long flowVersion = _flowProcessor == null ? -1 : _flowProcessor.Processed;
            bool first = _lastDashboardHeavyRefresh == DateTimeOffset.MinValue;
            bool assetChanged = !string.Equals(_lastDashboardHeavyAsset, focused, StringComparison.OrdinalIgnoreCase);
            bool quantChanged = _lastDashboardHeavyQuantVersion != _lastQuantVersion;
            bool csvChanged = _lastDashboardHeavyBarCount != _dailyBars.Count;
            bool staleInterval = (now - _lastDashboardHeavyRefresh).TotalMilliseconds >= DashboardHeavyRefreshMs;
            bool marketChanged = _lastDashboardHeavyVersion != version || _lastDashboardHeavyFlowVersion != flowVersion;

            if (!first && !assetChanged && !quantChanged && !csvChanged && !(marketChanged && staleInterval))
            {
                return false;
            }

            _lastDashboardHeavyRefresh = now;
            _lastDashboardHeavyVersion = version;
            _lastDashboardHeavyFlowVersion = flowVersion;
            _lastDashboardHeavyQuantVersion = _lastQuantVersion;
            _lastDashboardHeavyBarCount = _dailyBars.Count;
            _lastDashboardHeavyAsset = focused;
            return true;
        }

        private void RenderMonitor()
        {
            if (MonitorGrid == null || MonitorSummaryText == null)
            {
                return;
            }

            string selected = SelectedMonitorAsset();
            string focused = FocusedAsset();
            List<MonitorRow> rows = new List<MonitorRow>();
            int enabledCount = 0;
            int connectedCount = 0;
            int staleCount = 0;
            int waitingCount = 0;

            foreach (RtdAssetConfig item in _config.Rtd.Assets)
            {
                item.Normalize();
                MarketSnapshot snapshot = SnapshotForAsset(item.Asset);
                FlowMetrics metrics = _flowProcessor.GetMetrics(item.Asset);
                string status = MonitorAssetStatus(item, snapshot);

                if (item.Enabled)
                {
                    enabledCount++;
                }

                if (string.Equals(status, "connected", StringComparison.OrdinalIgnoreCase))
                {
                    connectedCount++;
                }
                else if (string.Equals(status, "atrasado", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(status, "lento", StringComparison.OrdinalIgnoreCase))
                {
                    staleCount++;
                }
                else if (string.Equals(status, "aguardando", StringComparison.OrdinalIgnoreCase))
                {
                    waitingCount++;
                }

                MonitorRow row = new MonitorRow();
                row.Asset = item.Asset;
                row.Name = item.Name;
                row.FocusText = string.Equals(item.Asset, focused, StringComparison.OrdinalIgnoreCase) ? "Sim" : "";
                row.EnabledText = item.Enabled ? "On" : "Off";
                row.ChannelsText = (item.QuoteEnabled ? "C" : "-") + "/" + (item.BookEnabled ? "B" : "-") + "/" + (item.TimesEnabled ? "T" : "-");
                row.LastText = snapshot == null ? "-" : FormatDecimal(snapshot.Ultimo, "N2");
                row.BidAskText = snapshot == null ? "-" : FormatDecimal(snapshot.OfertaCompra, "N2") + " / " + FormatDecimal(snapshot.OfertaVenda, "N2");
                row.VolumeText = snapshot == null ? "-" : FormatDecimal(snapshot.Volume, "N0");
                row.SnapshotAgeText = snapshot == null ? "-" : AgeText(snapshot.LocalTimestamp);
                row.FlowQuality = metrics == null ? "-" : metrics.DataQuality.ToString() + (metrics.Derived ? " derivado" : " real");
                row.DeltaText = metrics == null ? "-" : metrics.LastDelta.ToString("N0", _ptBr) + " / " + metrics.CumulativeDelta.ToString("N0", _ptBr);
                row.CsvText = FocusedAssetCsvText(item);
                row.Status = status;
                rows.Add(row);
            }

            MonitorGrid.ItemsSource = rows;

            MonitorRow selectedRow = rows.FirstOrDefault(x => string.Equals(x.Asset, selected, StringComparison.OrdinalIgnoreCase)) ??
                                     rows.FirstOrDefault(x => string.Equals(x.Asset, focused, StringComparison.OrdinalIgnoreCase));

            if (selectedRow != null)
            {
                MonitorGrid.SelectedItem = selectedRow;
            }

            MonitorSummaryText.Text = rows.Count.ToString(_ptBr) +
                                      " ativo(s) | ligados " + enabledCount.ToString(_ptBr) +
                                      " | connected " + connectedCount.ToString(_ptBr) +
                                      " | aguardando " + waitingCount.ToString(_ptBr) +
                                      " | lento/atrasado " + staleCount.ToString(_ptBr) +
                                      " | foco " + EmptyToDash(focused) +
                                      " | RTD " + EmptyToDash(_probeService.Status);
        }

        private string MonitorAssetStatus(RtdAssetConfig asset, MarketSnapshot snapshot)
        {
            if (asset == null)
            {
                return "-";
            }

            if (!asset.Enabled)
            {
                return "desligado";
            }

            if (!_probeService.IsRunning)
            {
                return "pronto";
            }

            if (snapshot == null)
            {
                return "aguardando";
            }

            TimeSpan age = DateTimeOffset.Now - snapshot.LocalTimestamp;

            if (age.TotalSeconds >= 15)
            {
                return "atrasado";
            }

            if (age.TotalSeconds >= 5)
            {
                return "lento";
            }

            return EmptyToDash(snapshot.Status);
        }

        private void RenderShortcuts()
        {
            if (ShortcutsGrid == null || WorkflowGrid == null || ShortcutsSummaryText == null)
            {
                return;
            }

            List<ShortcutRow> shortcuts = BuildShortcutRows();
            List<WorkflowRow> workflow = BuildWorkflowRows();

            ShortcutsGrid.ItemsSource = shortcuts;
            WorkflowGrid.ItemsSource = workflow;
            ShortcutsSummaryText.Text =
                shortcuts.Count.ToString(_ptBr) +
                " comando(s) | F1 abre esta tela | Ctrl+Tab alterna abas | foco " +
                EmptyToDash(FocusedAsset()) +
                " | RTD " +
                EmptyToDash(_probeService.Status);
        }

        private List<ShortcutRow> BuildShortcutRows()
        {
            List<ShortcutRow> rows = new List<ShortcutRow>();

            AddShortcut(rows, "F1", "Atalhos", "Abrir atalhos", "Consultar mapa de telas e comandos.");
            AddShortcut(rows, "F2", "Oportunidades", "Abrir Oportunidades", "Ver setups, niveis e confluencias acionaveis.");
            AddShortcut(rows, "F3", "Scanner", "Abrir Scanner", "Comparar ativos por score, fluxo, nivel e qualidade dos dados.");
            AddShortcut(rows, "F5", "Operacao", "Conectar / Desconectar", "Inicia ou para as assinaturas RTD ligadas.");
            AddShortcut(rows, "F6", "Analise", "Calcular", "Reprocessa niveis, metricas, profile proxy e backtest.");
            AddShortcut(rows, "F8", "Alertas", "Abrir Alertas", "Ver alertas operacionais, RTD, CSV, fluxo e setups.");
            AddShortcut(rows, "F9", "Risco", "Abrir Risco", "Ver checklist de risco operacional e qualidade dos dados.");
            AddShortcut(rows, "F10", "Historico", "Abrir Historico", "Ver eventos locais do app, RTD e CSV.");
            AddShortcut(rows, "F11", "Monitor", "Abrir Monitor", "Acompanhar todos os ativos cadastrados.");
            AddShortcut(rows, "Ctrl+1", "Mesa", "Abrir Mesa", "Grafico, DOM, tape, fluxo e sinais do ativo em foco.");
            AddShortcut(rows, "Ctrl+2", "Ativos", "Abrir Ativos", "Cadastrar ativos, RTDs e CSV historico.");
            AddShortcut(rows, "Ctrl+3", "DOM / Book", "Abrir DOM / Book", "Ver ladder, book e marcacoes de niveis.");
            AddShortcut(rows, "Ctrl+4", "Tape", "Abrir Tape", "Ver times and trades real ou derivado.");
            AddShortcut(rows, "Ctrl+5", "Order Flow", "Abrir Order Flow", "Ver delta, microbias, VWAP e janelas.");
            AddShortcut(rows, "Ctrl+Shift+F", "Mapa de Fluxo", "Abrir Mapa de Fluxo", "Ver liquidez, delta, profile e setups na mesma tela.");
            AddShortcut(rows, "Ctrl+Shift+H", "Heatmap", "Abrir Heatmap", "Ver mapa de calor do book, prints, delta e niveis de interesse.");
            AddShortcut(rows, "Ctrl+Shift+I", "Indicadores", "Abrir Indicadores", "Auditar RSI, EMAs, MACD, Bollinger, z-score, ATR/VWAP e sinais quant.");
            AddShortcut(rows, "Ctrl+Shift+R", "RTD Completo", "Abrir RTD Completo", "Auditar todos os campos RTD e carregar grupos extras sob demanda.");
            AddShortcut(rows, "Ctrl+6", "Volume Profile", "Abrir Volume Profile", "Ver POC, VAH, VAL, HVN, LVN e bins.");
            AddShortcut(rows, "Ctrl+7", "Setups", "Abrir Setups", "Ver sinais e motivos do motor de fluxo.");
            AddShortcut(rows, "Ctrl+8", "Niveis", "Abrir Niveis", "Ver niveis calculados e confluencias.");
            AddShortcut(rows, "Ctrl+9", "Grafico", "Abrir Grafico", "Ver grafico nativo com niveis.");
            AddShortcut(rows, "Ctrl+0", "Diagnostico", "Abrir Diagnostico", "Ver fontes RTD, campos, updates e erros.");
            AddShortcut(rows, "Ctrl+O", "CSV", "Carregar CSV", "Seleciona CSV historico para o ativo em foco.");
            AddShortcut(rows, "Ctrl+M", "Manual", "Modo manual", "Para RTD e permite preencher valores manualmente.");
            AddShortcut(rows, "Ctrl+F", "Ativos", "Focar ativo selecionado", "Troca o ativo em foco na grade Ativos.");
            AddShortcut(rows, "Ctrl+R", "Ativos", "Prontidao RTD", "Abre Ativos e mostra prontidao das assinaturas.");
            AddShortcut(rows, "Ctrl+Tab", "Janelas", "Proxima aba", "Avanca para a proxima tela.");
            AddShortcut(rows, "Ctrl+Shift+Tab", "Janelas", "Aba anterior", "Volta para a tela anterior.");

            return rows;
        }

        private List<WorkflowRow> BuildWorkflowRows()
        {
            List<WorkflowRow> rows = new List<WorkflowRow>();

            AddWorkflow(rows, "1", "Cadastro", "Ativos", "Cadastrar Codigo Cotacao, Canal Book, Canal Times e CSV historico por ativo.");
            AddWorkflow(rows, "2", "Prontidao", "Ativos", "Conferir se Cotacao, Book e Times estao ligados para alimentar preco, book e prints.");
            AddWorkflow(rows, "3", "Conexao", "Operacao", "Usar F5 para conectar e acompanhar status, fila, updates e erros sem travar a UI.");
            AddWorkflow(rows, "4", "Mesa", "Mesa", "Usar Ctrl+1 como tela principal para grafico, DOM, tape, fluxo, niveis e oportunidades.");
            AddWorkflow(rows, "5", "Qualidade", "Diagnostico", "Usar Ctrl+0 para auditar formulas RTD, indices, updates e ultimo erro por fonte.");
            AddWorkflow(rows, "6", "Mercado", "DOM / Book", "Usar Ctrl+3 para validar liquidez, spread, book real e marcacoes de POC/VAH/VAL/HVN/LVN.");
            AddWorkflow(rows, "7", "Tape", "Tape", "Usar Ctrl+4 para separar prints reais de tape derivado e agressao compra/venda.");
            AddWorkflow(rows, "8", "Fluxo", "Order Flow", "Usar Ctrl+5 para acompanhar delta, cumulative delta, imbalance, microbias e VWAP.");
            AddWorkflow(rows, "9", "Fluxo", "Mapa de Fluxo", "Usar Ctrl+Shift+F para cruzar liquidez, delta, niveis e setups no mesmo mapa.");
            AddWorkflow(rows, "10", "Fluxo", "Heatmap", "Usar Ctrl+Shift+H para ver liquidez passiva, negocios efetivados, delta e absorcao por preco.");
            AddWorkflow(rows, "11", "Profile", "Volume Profile", "Usar Ctrl+6 para checar POC, VAH, VAL, HVN, LVN, bins e distancia do preco.");
            AddWorkflow(rows, "12", "Quant", "Indicadores", "Usar Ctrl+Shift+I para auditar RSI, medias, MACD, Bollinger, z-score, ATR/VWAP e edge.");
            AddWorkflow(rows, "13", "Sinais", "Setups", "Usar Ctrl+7 para revisar score, direcao, preco, nivel, motivo e qualidade dos dados.");
            AddWorkflow(rows, "14", "Triagem", "Scanner", "Usar F3 para escolher qual ativo merece atencao por score, fluxo, nivel e qualidade.");
            AddWorkflow(rows, "15", "Triagem", "Oportunidades", "Usar F2 para priorizar oportunidades por confluencia e idade do sinal.");
            AddWorkflow(rows, "16", "Controle", "Risco", "Usar F9 para ver qualidade dos dados, CSV, fila, canais e limites antes de decidir.");
            AddWorkflow(rows, "17", "Controle", "Alertas", "Usar F8 para tratar alertas operacionais antes de agir fora do app.");
            AddWorkflow(rows, "18", "Auditoria", "Historico", "Usar F10 para conferir eventos locais, RTD, CSV, calculos e telas.");

            return rows;
        }

        private void AddShortcut(List<ShortcutRow> rows, string shortcut, string workspace, string command, string use)
        {
            ShortcutRow row = new ShortcutRow();
            row.Shortcut = shortcut;
            row.Workspace = workspace;
            row.Command = command;
            row.Use = use;
            rows.Add(row);
        }

        private void AddWorkflow(List<WorkflowRow> rows, string step, string area, string workspace, string action)
        {
            WorkflowRow row = new WorkflowRow();
            row.Step = step;
            row.Area = area;
            row.Workspace = workspace;
            row.Action = action;
            rows.Add(row);
        }

        private void RefreshOpportunitiesButton_Click(object sender, RoutedEventArgs e)
        {
            RenderOpportunities(FocusedSnapshot() ?? _lastSnapshot);
        }

        private void RefreshIndicatorsButton_Click(object sender, RoutedEventArgs e)
        {
            RenderIndicators(FocusedSnapshot() ?? _lastSnapshot);
        }

        private void RefreshHeatmapButton_Click(object sender, RoutedEventArgs e)
        {
            RenderHeatmap(FocusedSnapshot() ?? _lastSnapshot);
        }

        private void RefreshGarchButton_Click(object sender, RoutedEventArgs e)
        {
            Recalculate();
        }

        private void RenderGarch(MarketSnapshot snapshot)
        {
            if (GarchStateText == null ||
                GarchAssetText == null ||
                GarchCurrentPriceText == null ||
                GarchDailySigmaText == null ||
                GarchZDailyText == null ||
                GarchDailyReferenceText == null ||
                GarchDailyDistanceText == null ||
                GarchIntradaySigmaText == null ||
                GarchZIntradayText == null ||
                GarchIntradayReferenceText == null ||
                GarchIntradayDistanceText == null ||
                GarchSignalText == null ||
                GarchReadingText == null ||
                GarchSetupRiskText == null ||
                GarchFlowText == null ||
                GarchConfigText == null ||
                GarchDailyBandsGrid == null ||
                GarchIntradayBandsGrid == null ||
                GarchParametersGrid == null ||
                GarchAuditGrid == null ||
                GarchSignalsGrid == null ||
                GarchForecastGrid == null ||
                GarchBacktestGrid == null)
            {
                return;
            }

            if (_result == null || _result.Garch == null)
            {
                GarchStateText.Text = "Aguardando processamento quant/GARCH.";
                GarchAssetText.Text = "-";
                GarchCurrentPriceText.Text = "-";
                GarchDailySigmaText.Text = "-";
                GarchZDailyText.Text = "-";
                GarchDailyReferenceText.Text = "-";
                GarchDailyDistanceText.Text = "-";
                GarchIntradaySigmaText.Text = "-";
                GarchZIntradayText.Text = "-";
                GarchIntradayReferenceText.Text = "-";
                GarchIntradayDistanceText.Text = "-";
                GarchSignalText.Text = "-";
                GarchReadingText.Text = "-";
                GarchSetupRiskText.Text = "-";
                GarchFlowText.Text = "-";
                GarchConfigText.Text = "-";
                GarchDailyBandsGrid.ItemsSource = null;
                GarchIntradayBandsGrid.ItemsSource = null;
                GarchParametersGrid.ItemsSource = null;
                GarchAuditGrid.ItemsSource = null;
                GarchSignalsGrid.ItemsSource = null;
                GarchForecastGrid.ItemsSource = null;
                GarchBacktestGrid.ItemsSource = null;
                return;
            }

            GarchSnapshot garch = _result.Garch;
            MarketSnapshot effective = snapshot ?? CurrentSnapshotForCalc();
            decimal currentPrice = garch.CurrentPrice > 0m ? garch.CurrentPrice : ResolveLevelsCurrentPrice(effective);
            List<GarchSignal> signals = garch.Signals ?? new List<GarchSignal>();
            FlowMetrics garchFlow = _flowProcessor.GetMetrics(FocusedAsset());

            GarchStateText.Text = garch.Warnings != null && garch.Warnings.Count > 0 ? string.Join(" | ", garch.Warnings) : "GARCH(1,1) ativo e recalculando em tempo real.";
            GarchAssetText.Text = effective != null ? effective.Asset : "-";
            GarchCurrentPriceText.Text = currentPrice > 0m ? currentPrice.ToString("N2", _ptBr) : "-";

            GarchDailySigmaText.Text = FormatGarchSigma(garch.DailyFit, garch.DailySigmaPoints);
            GarchZDailyText.Text = garch.DailyFit != null && garch.DailyFit.Success ? "z " + garch.ZDaily.ToString("N2", _ptBr) : "-";
            GarchDailyReferenceText.Text = BuildGarchReferenceText("Ref diaria", garch.DailyReference, garch.DailyReferenceName);
            GarchDailyDistanceText.Text = BuildGarchReferenceDistanceText(currentPrice, garch.DailyReference);

            GarchIntradaySigmaText.Text = FormatGarchSigma(garch.IntradayFit, garch.IntradaySigmaPoints);
            GarchZIntradayText.Text = garch.IntradayFit != null && garch.IntradayFit.Success ? "z " + garch.ZIntraday.ToString("N2", _ptBr) : "-";
            GarchIntradayReferenceText.Text = BuildGarchReferenceText("Ref intraday", garch.IntradayReference, garch.IntradayReferenceName);
            GarchIntradayDistanceText.Text = BuildGarchReferenceDistanceText(currentPrice, garch.IntradayReference);
            GarchReadingText.Text = EmptyToDash(garch.CombinedRead);

            if (signals.Count > 0)
            {
                var topSignal = signals.OrderByDescending(s => s.Score).First();
                GarchSignalText.Text = topSignal.Setup + " " + topSignal.Direction + " (Score " + topSignal.Score + ")";
            }
            else
            {
                GarchSignalText.Text = "Nenhum setup ativo";
            }

            GarchSetupRiskText.Text = BuildGarchRiskText(garch, currentPrice);
            GarchFlowText.Text = BuildGarchFlowText(garchFlow);
            GarchConfigText.Text = BuildGarchConfigText();

            List<GarchBandViewRow> dailyBands = new List<GarchBandViewRow>();
            foreach (var b in garch.DailyBands)
            {
                dailyBands.Add(new GarchBandViewRow
                {
                    Side = b.Side,
                    Sigma = b.Sigma.ToString("N1", _ptBr) + "σ",
                    Price = b.Price.ToString("N2", _ptBr),
                    DistanceCurrent = FormatPoints(b.DistanceCurrent),
                    Score = b.ScoreHint.ToString("N0", _ptBr),
                    Read = b.Read
                });
            }
            GarchDailyBandsGrid.ItemsSource = dailyBands;

            List<GarchBandViewRow> intradayBands = new List<GarchBandViewRow>();
            foreach (var b in garch.IntradayBands)
            {
                intradayBands.Add(new GarchBandViewRow
                {
                    Side = b.Side,
                    Sigma = b.Sigma.ToString("N1", _ptBr) + "σ",
                    Price = b.Price.ToString("N2", _ptBr),
                    DistanceCurrent = FormatPoints(b.DistanceCurrent),
                    Score = b.ScoreHint.ToString("N0", _ptBr),
                    Read = b.Read
                });
            }
            GarchIntradayBandsGrid.ItemsSource = intradayBands;

            List<GarchParameterRow> parameters = new List<GarchParameterRow>();
            if (garch.DailyFit.Success)
            {
                parameters.Add(new GarchParameterRow { Parameter = "Mu (Média)", Value = garch.DailyFit.Mu.ToString("E4", _ptBr) });
                parameters.Add(new GarchParameterRow { Parameter = "Omega (Constante)", Value = garch.DailyFit.Omega.ToString("E4", _ptBr) });
                parameters.Add(new GarchParameterRow { Parameter = "Alpha (ARCH)", Value = garch.DailyFit.Alpha.ToString("N4", _ptBr) });
                parameters.Add(new GarchParameterRow { Parameter = "Beta (GARCH)", Value = garch.DailyFit.Beta.ToString("N4", _ptBr) });
                parameters.Add(new GarchParameterRow { Parameter = "Persistência (α+β)", Value = garch.DailyFit.Persistence.ToString("N4", _ptBr) });
                parameters.Add(new GarchParameterRow { Parameter = "Meia-Vida (Dias)", Value = garch.DailyFit.HalfLifePeriods.ToString("N1", _ptBr) });
                parameters.Add(new GarchParameterRow { Parameter = "Log-Likelihood", Value = (-garch.DailyFit.NegativeLogLikelihood).ToString("N2", _ptBr) });
            }
            GarchParametersGrid.ItemsSource = parameters;

            List<GarchAuditRow> audit = new List<GarchAuditRow>();
            if (garch.DailyFit.Success)
            {
                audit.Add(new GarchAuditRow { Item = "Daily Retornos Medianos", Value = garch.DailyFit.Status });
                audit.Add(new GarchAuditRow { Item = "Daily Amostras Usadas", Value = garch.DailyFit.Samples.ToString(_ptBr) });
                audit.Add(new GarchAuditRow { Item = "Daily Próxima Vol Diária", Value = (garch.DailyFit.NextSigma * 100d).ToString("N4", _ptBr) + "%" });
            }
            if (garch.IntradayFit.Success)
            {
                audit.Add(new GarchAuditRow { Item = "Intraday Amostras Usadas", Value = garch.IntradayFit.Samples.ToString(_ptBr) });
                audit.Add(new GarchAuditRow { Item = "Intraday Próxima Vol", Value = (garch.IntradayFit.NextSigma * 100d).ToString("N4", _ptBr) + "%" });
                audit.Add(new GarchAuditRow { Item = "Intraday Timeframe (Seg)", Value = _config.Garch.IntradayTimeframeSeconds.ToString(_ptBr) });
            }
            GarchAuditGrid.ItemsSource = audit;

            GarchSignalsGrid.ItemsSource = signals;
            GarchForecastGrid.ItemsSource = BuildGarchForecastRows(garch);

            List<GarchBacktestViewRow> backtestRows = new List<GarchBacktestViewRow>();
            foreach (var r in garch.Backtest)
            {
                backtestRows.Add(new GarchBacktestViewRow
                {
                    Scope = r.Scope,
                    Direction = r.Direction,
                    Sigma = r.Sigma.ToString("N1", _ptBr) + "σ",
                    Touches = r.Touches.ToString(_ptBr),
                    Reversals = r.Reversals.ToString(_ptBr),
                    ReversalRateText = r.ReversalRate.ToString("P1", _ptBr),
                    EdgeText = "Exp: " + r.ExpectancyPoints.ToString("N1", _ptBr) + " pts | PF: " + r.ProfitFactor.ToString("N2", _ptBr)
                });
            }
            GarchBacktestGrid.ItemsSource = backtestRows;
        }

        private string FormatGarchSigma(GarchFitResult fit, decimal points)
        {
            if (fit == null || !fit.Success)
            {
                return "-";
            }

            return (fit.NextSigma * 100d).ToString("N3", _ptBr) + "% | " + points.ToString("N2", _ptBr) + " pts";
        }

        private string BuildGarchReferenceText(string label, decimal reference, string source)
        {
            if (reference <= 0m)
            {
                return label + " indisponivel";
            }

            return label + " " + reference.ToString("N2", _ptBr) + " | " + EmptyToDash(source);
        }

        private string BuildGarchReferenceDistanceText(decimal currentPrice, decimal reference)
        {
            if (currentPrice <= 0m || reference <= 0m)
            {
                return "Atual x ref -";
            }

            return "Atual x ref " + FormatPoints(currentPrice - reference);
        }

        private string BuildGarchRiskText(GarchSnapshot garch, decimal currentPrice)
        {
            if (garch == null)
            {
                return "-";
            }

            List<GarchSignal> signals = garch.Signals ?? new List<GarchSignal>();
            if (signals.Count > 0)
            {
                GarchSignal signal = signals.OrderByDescending(s => s.Score).First();
                string rr = signal.RiskReward.HasValue ? signal.RiskReward.Value.ToString("N2", _ptBr) : "-";
                string stop = signal.StopPrice.HasValue ? signal.StopPrice.Value.ToString("N2", _ptBr) : "-";
                string target = signal.Target1.HasValue ? signal.Target1.Value.ToString("N2", _ptBr) : "-";
                string robustness = EmptyToDash(signal.Robustness);

                return "Risco: stop " + stop + " | alvo " + target + " | R/R " + rr + " | " + robustness;
            }

            List<GarchBandLevel> bands = new List<GarchBandLevel>();
            if (garch.DailyBands != null)
            {
                bands.AddRange(garch.DailyBands);
            }
            if (garch.IntradayBands != null)
            {
                bands.AddRange(garch.IntradayBands);
            }

            if (bands.Count == 0)
            {
                return "Setup aguardando bandas validas.";
            }

            GarchBandLevel nearest = bands
                .OrderBy(b => Math.Abs(b.DistanceCurrent))
                .FirstOrDefault();

            if (nearest == null)
            {
                return "Setup aguardando bandas validas.";
            }

            decimal distance = currentPrice > 0m ? nearest.Price - currentPrice : nearest.DistanceCurrent;
            return "Proxima banda: " + EmptyToDash(nearest.Scope) + " " + EmptyToDash(nearest.Side) +
                   " " + nearest.Price.ToString("N2", _ptBr) +
                   " | dist " + FormatPoints(distance) +
                   " | score " + nearest.ScoreHint.ToString("N0", _ptBr);
        }

        private string BuildGarchFlowText(FlowMetrics metrics)
        {
            if (metrics == null)
            {
                return "Fluxo aguardando book/times.";
            }

            return "Fluxo: CD " + metrics.CumulativeDelta.ToString("N0", _ptBr) +
                   " | imb " + FormatDecimal(metrics.TopBookImbalance, "N3") +
                   " | micro " + FormatDecimal(metrics.MicroBias, "N3") +
                   " | VWAP " + FormatDecimal(metrics.VwapDistance, "N2");
        }

        private string BuildGarchConfigText()
        {
            if (_config == null || _config.Garch == null)
            {
                return "Config GARCH indisponivel.";
            }

            GarchConfig config = _config.Garch;
            return "Config: " + config.DailyWindowDays.ToString(_ptBr) + "d / min " +
                   config.DailyMinSamples.ToString(_ptBr) +
                   " | intra " + config.IntradayTimeframeSeconds.ToString(_ptBr) + "s / min " +
                   config.IntradayMinBars.ToString(_ptBr) +
                   " | bandas " + BuildGarchMultiplierText(config.BandMultipliers);
        }

        private string BuildGarchMultiplierText(double[] multipliers)
        {
            if (multipliers == null || multipliers.Length == 0)
            {
                return "-";
            }

            return string.Join(", ", multipliers.Select(x => x.ToString("N1", _ptBr) + "s").ToArray());
        }

        private List<GarchForecastViewRow> BuildGarchForecastRows(GarchSnapshot garch)
        {
            List<GarchForecastViewRow> rows = new List<GarchForecastViewRow>();
            if (garch == null)
            {
                return rows;
            }

            AddGarchForecastRow(rows, "Diario", "1 pregao", garch.DailyFit, garch.DailyReference, 1);
            AddGarchForecastRow(rows, "Diario", "2 pregoes", garch.DailyFit, garch.DailyReference, 2);
            AddGarchForecastRow(rows, "Diario", "5 pregoes", garch.DailyFit, garch.DailyReference, 5);

            int frame = _config == null || _config.Garch == null ? 60 : Math.Max(1, _config.Garch.IntradayTimeframeSeconds);
            AddGarchForecastRow(rows, "Intraday", "1 barra (" + frame.ToString(_ptBr) + "s)", garch.IntradayFit, garch.IntradayReference, 1);
            AddGarchForecastRow(rows, "Intraday", "5 barras", garch.IntradayFit, garch.IntradayReference, 5);
            AddGarchForecastRow(rows, "Intraday", "15 barras", garch.IntradayFit, garch.IntradayReference, 15);

            if (rows.Count == 0)
            {
                rows.Add(new GarchForecastViewRow
                {
                    Scope = "GARCH",
                    Horizon = "-",
                    SigmaPercent = "-",
                    Points = "-",
                    Read = "Forecast aguardando fit valido e referencia positiva."
                });
            }

            return rows;
        }

        private void AddGarchForecastRow(List<GarchForecastViewRow> rows, string scope, string horizon, GarchFitResult fit, decimal reference, int periods)
        {
            if (rows == null || fit == null || !fit.Success || reference <= 0m)
            {
                return;
            }

            double variance = fit.NextVariance;
            if (periods > 1 && fit.LongRunVariance > 0d)
            {
                double persistence = Math.Max(0d, Math.Min(0.999999d, fit.Persistence));
                variance = fit.LongRunVariance + Math.Pow(persistence, Math.Max(0, periods - 1)) * (fit.NextVariance - fit.LongRunVariance);
            }

            if (variance <= 0d || double.IsNaN(variance) || double.IsInfinity(variance))
            {
                return;
            }

            double sigma = Math.Sqrt(variance);
            if (double.IsNaN(sigma) || double.IsInfinity(sigma))
            {
                return;
            }

            decimal points = Convert.ToDecimal(Math.Abs(sigma * decimal.ToDouble(reference)));
            rows.Add(new GarchForecastViewRow
            {
                Scope = scope,
                Horizon = horizon,
                SigmaPercent = (sigma * 100d).ToString("N3", _ptBr) + "%",
                Points = points.ToString("N2", _ptBr) + " pts",
                Read = "Ref " + reference.ToString("N2", _ptBr) + " | vol cond. h-step"
            });
        }

        private void RenderIndicators(MarketSnapshot snapshot)
        {
            if (IndicatorsSummaryGrid == null ||
                IndicatorsTechnicalGrid == null ||
                IndicatorsSignalsGrid == null ||
                IndicatorsStatsGrid == null ||
                IndicatorsBacktestGrid == null)
            {
                return;
            }

            string focused = FocusedAsset();
            MarketSnapshot effective = snapshot ?? FocusedSnapshot();

            if (effective == null &&
                _lastSnapshot != null &&
                (string.IsNullOrWhiteSpace(focused) || string.Equals(_lastSnapshot.Asset, focused, StringComparison.OrdinalIgnoreCase)))
            {
                effective = _lastSnapshot;
            }

            FlowMetrics metrics = _flowProcessor.GetMetrics(focused);
            List<QuantSignalAuditRow> quantRows = BuildQuantSignalAuditRows(metrics, effective);
            int quantCount = _result == null || _result.QuantSignals == null ? 0 : _result.QuantSignals.Count;

            IndicatorsSummaryGrid.ItemsSource = BuildIndicatorSummaryRows(effective, metrics, quantCount);
            IndicatorsTechnicalGrid.ItemsSource = BuildTechnicalIndicatorRows(effective);
            IndicatorsSignalsGrid.ItemsSource = quantRows;
            IndicatorsStatsGrid.ItemsSource = BuildIndicatorStatRows();
            IndicatorsBacktestGrid.ItemsSource = BuildBacktestAuditRows();

            if (IndicatorsAssetText != null)
            {
                IndicatorsAssetText.Text = EmptyToDash(focused);
            }

            if (IndicatorsRegimeText != null)
            {
                IndicatorsRegimeText.Text = _result == null ? "-" : EmptyToDash(_result.Regime);
            }

            if (IndicatorsSignalCountText != null)
            {
                IndicatorsSignalCountText.Text = quantCount.ToString(_ptBr);
            }

            if (IndicatorsStateText != null)
            {
                string source = _result == null || _result.Technicals == null ? "-" : EmptyToDash(_result.Technicals.Source);
                string quality = metrics == null ? "sem fluxo" : metrics.DataQuality + (metrics.Derived ? " derivado" : " real");
                string sample = _result == null || _result.Technicals == null ? "0" : _result.Technicals.SampleSize.ToString(_ptBr);
                IndicatorsStateText.Text = "RTD " + EmptyToDash(_probeService.Status) +
                                           " | CSV " + _dailyBars.Count.ToString(_ptBr) +
                                           " pregoes | fonte " + source +
                                           " | fluxo " + quality +
                                           " | amostra " + sample;
            }
        }

        private List<NameValueRow> BuildIndicatorSummaryRows(MarketSnapshot snapshot, FlowMetrics metrics, int quantCount)
        {
            List<NameValueRow> rows = new List<NameValueRow>();
            string focused = FocusedAsset();
            RtdAssetConfig asset = _config.Rtd.FindAsset(focused);
            TechnicalIndicatorSnapshot technicals = _result == null ? null : _result.Technicals;
            QuantSignal bestQuant = BestQuantSignalForAsset(focused, metrics);
            string warnings = _result == null || _result.Warnings == null || _result.Warnings.Count == 0
                ? "-"
                : _result.Warnings.Count.ToString(_ptBr) + " aviso(s)";

            AddRow(rows, "Ativo", EmptyToDash(focused), asset == null ? "nao cadastrado" : "cadastrado");
            AddRow(rows, "RTD", EmptyToDash(_probeService.Status), "updates " + _probeService.UpdatesReceived.ToString(_ptBr) + " | snapshot " + (snapshot == null ? "-" : AgeText(snapshot.LocalTimestamp)));
            AddRow(rows, "Canais", IndicatorChannelText(asset), "Cotacao/Book/Times configurados por ativo");
            AddRow(rows, "CSV", FocusedAssetCsvText(asset), _dailyBars.Count.ToString(_ptBr) + " pregoes carregados");
            AddRow(rows, "Calculo", _result == null ? "aguardando" : EmptyToDash(_result.Regime), warnings);
            AddRow(rows, "Fonte tecnica", technicals == null ? "-" : EmptyToDash(technicals.Source), technicals == null ? "sem calculo" : "amostra " + technicals.SampleSize.ToString(_ptBr));
            AddRow(rows, "Fluxo", metrics == null ? "sem metricas" : metrics.DataQuality.ToString(), metrics == null ? "aguardando RTD" : (metrics.Derived ? "tape derivado" : "tape/book real"));
            AddRow(rows, "Delta", metrics == null ? "-" : metrics.CumulativeDelta.ToString("N0", _ptBr), metrics == null ? "-" : "imb " + FormatDecimal(metrics.TopBookImbalance, "N3") + " | microbias " + FormatDecimal(metrics.MicroBias, "N3"));
            AddRow(rows, "Sinais Quant", quantCount.ToString(_ptBr), IndicatorScoreCapText(metrics));
            AddRow(rows, "Edge Quant", QuantEdgeValue(bestQuant), QuantEdgeDetail(bestQuant));
            AddRow(rows, "Risco/Retorno", QuantRiskRewardValue(bestQuant), bestQuant == null ? "sem sinal quant" : EmptyToDash(bestQuant.RiskModel));
            AddRow(rows, "Uso", "analise", "plataforma nao envia ordens");

            return rows;
        }

        private string IndicatorChannelText(RtdAssetConfig asset)
        {
            if (asset == null)
            {
                return "-";
            }

            return "C " + (ChannelEnabled(asset.Asset, "Cotacao") ? "on" : "off") +
                   " | B " + (ChannelEnabled(asset.Asset, "Book") ? "on" : "off") +
                   " | T " + (ChannelEnabled(asset.Asset, "Times") ? "on" : "off");
        }

        private string IndicatorScoreCapText(FlowMetrics metrics)
        {
            if (metrics == null)
            {
                return "score limitado sem confirmacao de fluxo";
            }

            if (metrics.DataQuality == MarketDataQuality.FullTimesAndTrades ||
                metrics.DataQuality == MarketDataQuality.FullDepth)
            {
                return "score ajustado por fluxo real";
            }

            if (metrics.DataQuality == MarketDataQuality.DerivedTape)
            {
                return "score limitado por tape derivado";
            }

            return "score limitado por top-of-book";
        }

        private string QuantEdgeValue(QuantSignal signal)
        {
            if (signal == null)
            {
                return "-";
            }

            return EmptyToDash(signal.EdgeQuality) +
                   " | exp " + FormatDecimal(signal.ExpectancyPoints, "N1") +
                   " pts | conf " + signal.Confidence.ToString("N1", _ptBr) + "%";
        }

        private string QuantEdgeDetail(QuantSignal signal)
        {
            if (signal == null)
            {
                return "sem sinal quant no contexto atual";
            }

            return "rev " + signal.ReversalRate.ToString("N1", _ptBr) +
                   "% | PF " + signal.ProfitFactor.ToString("N2", _ptBr) +
                   " | R/R " + signal.RiskReward.ToString("N2", _ptBr) +
                   " | " + EmptyToDash(signal.StatisticalEdge);
        }

        private string QuantRiskRewardValue(QuantSignal signal)
        {
            if (signal == null)
            {
                return "-";
            }

            return "R/R " + signal.RiskReward.ToString("N2", _ptBr) +
                   " | alvo " + FormatDecimal(signal.TargetPoints, "N1") +
                   " | risco " + FormatDecimal(signal.StopPoints, "N1");
        }

        private List<IndicatorAuditRow> BuildTechnicalIndicatorRows(MarketSnapshot snapshot)
        {
            List<IndicatorAuditRow> rows = new List<IndicatorAuditRow>();

            if (_result == null || _result.Technicals == null)
            {
                AddIndicatorRow(rows, "Calculo", "-", "aguardando", "carregar CSV e calcular");
                return rows;
            }

            TechnicalIndicatorSnapshot technicals = _result.Technicals;
            decimal? price = snapshot == null ? null : snapshot.Ultimo;

            if (!price.HasValue && _result.Intraday != null && _result.Intraday.Price > 0m)
            {
                price = _result.Intraday.Price;
            }

            string source = EmptyToDash(technicals.Source);
            int calculationDays = _result != null && _result.CalculationDays > 0 ? _result.CalculationDays : SelectedCalculationDays();
            string calculationDaysText = calculationDays.ToString(_ptBr);
            AddIndicatorRow(rows, "Preco RTD", FormatDecimal(price, "N2"), snapshot == null ? "sem snapshot" : AgeText(snapshot.LocalTimestamp), "ULT");
            AddIndicatorRow(rows, "RSI14", FormatDecimal(technicals.Rsi14, "N1"), RsiState(technicals.Rsi14), source);
            AddIndicatorRow(rows, "SMA20", FormatDecimal(technicals.Sma20, "N2"), VsPriceState(technicals.Sma20, price), source);
            AddIndicatorRow(rows, "SMA50", FormatDecimal(technicals.Sma50, "N2"), VsPriceState(technicals.Sma50, price), source);
            AddIndicatorRow(rows, "EMA9", FormatDecimal(technicals.Ema9, "N2"), VsPriceState(technicals.Ema9, price), source);
            AddIndicatorRow(rows, "EMA21", FormatDecimal(technicals.Ema21, "N2"), VsPriceState(technicals.Ema21, price), source);
            AddIndicatorRow(rows, "EMA50", FormatDecimal(technicals.Ema50, "N2"), VsPriceState(technicals.Ema50, price), source);
            AddIndicatorRow(rows, "MACD", FormatMacd(technicals), MacdState(technicals), source);
            AddIndicatorRow(rows, "Bollinger20", FormatBollinger(technicals), BollingerState(technicals, price), source);
            AddIndicatorRow(rows, "ZScore20", FormatDecimal(technicals.ZScore20, "N2"), ZScoreState(technicals.ZScore20), source);
            AddIndicatorRow(rows, "ATR/VWAP", FormatDecimal(technicals.AtrVwapDistance, "N2"), AtrVwapState(technicals.AtrVwapDistance), "ATR historico + RTD MED/VWAP");
            AddIndicatorRow(rows, "Momentum10", FormatPercent(technicals.Momentum10Pct, "N2"), MomentumState(technicals.Momentum10Pct), source);
            AddIndicatorRow(rows, "Janela calculo", calculationDaysText + " dias", "historico selecionado", source);
            AddIndicatorRow(rows, "Retorno medio " + calculationDaysText, FormatPercent(technicals.ReturnMean21Pct, "N2"), ReturnState(technicals.ReturnMean21Pct), source);
            AddIndicatorRow(rows, "Vol retorno " + calculationDaysText, FormatPercent(technicals.ReturnStd21Pct, "N2"), "desvio de retornos", source);
            AddIndicatorRow(rows, "Positivos " + calculationDaysText, FormatPercent(technicals.PositiveReturnRate21Pct, "N1"), PositiveRateState(technicals.PositiveReturnRate21Pct), source);
            AddIndicatorRow(rows, "Sharpe" + calculationDaysText, FormatDecimal(technicals.Sharpe21, "N2"), SharpeState(technicals.Sharpe21), source);
            AddIndicatorRow(rows, "Sortino" + calculationDaysText, FormatDecimal(technicals.Sortino21, "N2"), SharpeState(technicals.Sortino21), source);
            AddIndicatorRow(rows, "Downside " + calculationDaysText, FormatPercent(technicals.DownsideStd21Pct, "N2"), "risco de cauda curta", source);
            AddIndicatorRow(rows, "VaR95 " + calculationDaysText, FormatPercent(technicals.ValueAtRisk95Pct, "N2"), "percentil 5%", source);
            AddIndicatorRow(rows, "ES95 " + calculationDaysText, FormatPercent(technicals.ExpectedShortfall95Pct, "N2"), "media da cauda", source);
            AddIndicatorRow(rows, "Tendencia", EmptyToDash(technicals.TrendState), "estado", source);
            AddIndicatorRow(rows, "Reversao", EmptyToDash(technicals.ReversionState), "estado", source);
            AddIndicatorRow(rows, "Amostra", technicals.SampleSize.ToString(_ptBr), "barras usadas", source);

            return rows;
        }

        private void AddIndicatorRow(List<IndicatorAuditRow> rows, string indicator, string value, string state, string source)
        {
            IndicatorAuditRow row = new IndicatorAuditRow();
            row.Indicator = EmptyToDash(indicator);
            row.Value = EmptyToDash(value);
            row.State = EmptyToDash(state);
            row.Source = EmptyToDash(source);
            rows.Add(row);
        }

        private List<QuantSignalAuditRow> BuildQuantSignalAuditRows(FlowMetrics metrics, MarketSnapshot snapshot)
        {
            List<QuantSignalAuditRow> rows = new List<QuantSignalAuditRow>();

            if (_result == null || _result.QuantSignals == null || _result.QuantSignals.Count == 0)
            {
                QuantSignalAuditRow waiting = new QuantSignalAuditRow();
                waiting.Setup = "Aguardando sinal quant";
                waiting.Direction = "-";
                waiting.Price = snapshot == null ? "-" : FormatDecimal(snapshot.Ultimo, "N2");
                waiting.Score = "-";
                waiting.Level = "-";
                waiting.Edge = _result == null ? "sem calculo" : "sem setup no contexto atual";
                waiting.Confidence = "-";
                waiting.RiskReward = "-";
                waiting.Gate = _result == null ? "carregar CSV/RTD" : "aguardando confluencia";
                waiting.TechnicalState = _result == null || _result.Technicals == null ? "-" : EmptyToDash(_result.Technicals.TrendState) + " / " + EmptyToDash(_result.Technicals.ReversionState);
                waiting.Reasons = _dailyBars.Count == 0 ? "carregar CSV historico do ativo" : "aguardando confluencia estatistica e fluxo";
                rows.Add(waiting);
                return rows;
            }

            foreach (QuantSignal signal in _result.QuantSignals.OrderByDescending(x => x.Score).Take(30))
            {
                QuantSignalAuditRow row = new QuantSignalAuditRow();
                row.Setup = EmptyToDash(signal.Setup);
                row.Direction = TranslateDirection(signal.Direction);
                row.Price = signal.Price.ToString("N2", _ptBr);
                row.Score = QuantFlowAdjustedScore(signal, metrics).ToString(_ptBr);
                row.Level = EmptyToDash(signal.LevelName) + (signal.LevelPrice.HasValue ? " @ " + signal.LevelPrice.Value.ToString("N2", _ptBr) : string.Empty);
                row.Edge = EmptyToDash(signal.StatisticalEdge) + " | amostra " + signal.SampleSize.ToString(_ptBr);
                row.Confidence = signal.Confidence.ToString("N1", _ptBr) + "%";
                row.RiskReward = signal.RiskReward.ToString("N2", _ptBr);
                row.Gate = EmptyToDash(signal.RobustnessGate);
                row.TechnicalState = EmptyToDash(signal.TechnicalState);
                row.Reasons = EmptyToDash(signal.Reasons) + "; " + EmptyToDash(signal.RiskModel) + "; " + QuantFlowConfirmation(signal, metrics);
                rows.Add(row);
            }

            return rows;
        }

        private string TranslateDirection(string direction)
        {
            if (string.Equals(direction, "Buy", StringComparison.OrdinalIgnoreCase))
            {
                return "Compra";
            }

            if (string.Equals(direction, "Sell", StringComparison.OrdinalIgnoreCase))
            {
                return "Venda";
            }

            return EmptyToDash(direction);
        }

        private List<MetricAuditRow> BuildIndicatorStatRows()
        {
            List<MetricAuditRow> rows = new List<MetricAuditRow>();

            if (_result == null)
            {
                AddMetricAuditRow(rows, "Calculo", "-", "-", "-", "-");
                return rows;
            }

            List<VolatilityMetric> metrics = new List<VolatilityMetric>();

            if (_result.Metrics != null)
            {
                metrics.AddRange(_result.Metrics);
            }

            if (_result.WindowMetrics != null)
            {
                metrics.AddRange(_result.WindowMetrics);
            }

            foreach (VolatilityMetric metric in metrics.Take(100))
            {
                AddMetricAuditRow(
                    rows,
                    MetricDisplayLabel(metric.Name),
                    metric.Window.ToString(_ptBr),
                    metric.Points.ToString("N2", _ptBr),
                    metric.Percent.ToString("N2", _ptBr) + "%",
                    metric.Percentile.ToString("N1", _ptBr));
            }

            if (rows.Count == 0)
            {
                AddMetricAuditRow(rows, "Sem metrica", "-", "-", "-", "CSV insuficiente");
            }

            return rows;
        }

        private void AddMetricAuditRow(List<MetricAuditRow> rows, string metric, string window, string points, string percent, string percentile)
        {
            MetricAuditRow row = new MetricAuditRow();
            row.Metric = EmptyToDash(metric);
            row.Window = EmptyToDash(window);
            row.Points = EmptyToDash(points);
            row.Percent = EmptyToDash(percent);
            row.Percentile = EmptyToDash(percentile);
            rows.Add(row);
        }

        private List<BacktestAuditRow> BuildBacktestAuditRows()
        {
            List<BacktestAuditRow> rows = new List<BacktestAuditRow>();

            if (_result == null || _result.Backtest == null || _result.Backtest.Count == 0)
            {
                BacktestAuditRow waiting = new BacktestAuditRow();
                waiting.Direction = "-";
                waiting.Multiplier = "-";
                waiting.Samples = _dailyBars.Count.ToString(_ptBr);
                waiting.Touches = "-";
                waiting.TouchRate = "-";
                waiting.ReversalRate = "-";
                waiting.ContinuationRate = "-";
                waiting.Expectancy = "-";
                waiting.ProfitFactor = "-";
                waiting.Confidence = "-";
                waiting.RiskReward = "-";
                waiting.EdgeScore = "-";
                waiting.EdgeQuality = "CSV insuficiente";
                rows.Add(waiting);
                return rows;
            }

            foreach (BacktestRow rowSource in _result.Backtest.OrderByDescending(x => x.ExpectancyPoints).ThenByDescending(x => x.ReversalRate).Take(80))
            {
                BacktestAuditRow row = new BacktestAuditRow();
                row.Direction = TranslateDirection(rowSource.Direction);
                row.Multiplier = rowSource.Multiplier.ToString("N1", _ptBr);
                row.Samples = rowSource.Samples.ToString(_ptBr);
                row.Touches = rowSource.Touches.ToString(_ptBr);
                row.TouchRate = rowSource.TouchRate.ToString("N1", _ptBr) + "%";
                row.ReversalRate = rowSource.ReversalRate.ToString("N1", _ptBr) + "%";
                row.ContinuationRate = rowSource.ContinuationRate.ToString("N1", _ptBr) + "%";
                row.Expectancy = rowSource.ExpectancyPoints.ToString("N1", _ptBr);
                row.ProfitFactor = rowSource.ProfitFactor.ToString("N2", _ptBr);
                row.Confidence = rowSource.Confidence.ToString("N1", _ptBr) + "%";
                row.RiskReward = rowSource.RiskReward.ToString("N2", _ptBr);
                row.EdgeScore = rowSource.EdgeScore.ToString("N0", _ptBr);
                row.EdgeQuality = BacktestEdgeQuality(rowSource);
                rows.Add(row);
            }

            return rows;
        }

        private string BacktestEdgeQuality(BacktestRow row)
        {
            if (row == null || row.Touches == 0)
            {
                return "sem toques";
            }

            if (row.Touches < 5)
            {
                return "amostra baixa";
            }

            if (row.ExpectancyPoints > 0m &&
                row.ReversalRate >= 58d &&
                row.ProfitFactor >= 1.25d &&
                row.Confidence >= 45d &&
                row.RiskReward >= 1m)
            {
                return "edge positivo";
            }

            if (row.ExpectancyPoints > 0m &&
                row.ProfitFactor >= 1.05d &&
                row.Confidence >= 30d)
            {
                return "edge moderado";
            }

            return "edge fragil";
        }

        private string FormatMacd(TechnicalIndicatorSnapshot technicals)
        {
            if (technicals == null)
            {
                return "-";
            }

            return FormatDecimal(technicals.Macd, "N2") +
                   " / " +
                   FormatDecimal(technicals.MacdSignal, "N2") +
                   " / hist " +
                   FormatDecimal(technicals.MacdHistogram, "N2");
        }

        private string FormatBollinger(TechnicalIndicatorSnapshot technicals)
        {
            if (technicals == null)
            {
                return "-";
            }

            return FormatDecimal(technicals.BollingerLower20, "N2") +
                   " / " +
                   FormatDecimal(technicals.BollingerMiddle20, "N2") +
                   " / " +
                   FormatDecimal(technicals.BollingerUpper20, "N2");
        }

        private string RsiState(decimal? value)
        {
            if (!value.HasValue)
            {
                return "-";
            }

            if (value.Value <= 30m)
            {
                return "sobrevenda";
            }

            if (value.Value >= 70m)
            {
                return "sobrecompra";
            }

            if (value.Value <= 40m)
            {
                return "pressao vendedora";
            }

            if (value.Value >= 60m)
            {
                return "pressao compradora";
            }

            return "neutro";
        }

        private string VsPriceState(decimal? indicator, decimal? price)
        {
            if (!indicator.HasValue || !price.HasValue)
            {
                return "-";
            }

            decimal distance = price.Value - indicator.Value;

            if (Math.Abs(distance) < _config.Rtd.TickSize)
            {
                return "testando nivel";
            }

            return distance > 0m ? "preco acima" : "preco abaixo";
        }

        private string MacdState(TechnicalIndicatorSnapshot technicals)
        {
            if (technicals == null || !technicals.MacdHistogram.HasValue)
            {
                return "-";
            }

            if (technicals.MacdHistogram.Value > 0m)
            {
                return "momentum comprador";
            }

            if (technicals.MacdHistogram.Value < 0m)
            {
                return "momentum vendedor";
            }

            return "neutro";
        }

        private string BollingerState(TechnicalIndicatorSnapshot technicals, decimal? price)
        {
            if (technicals == null || !price.HasValue)
            {
                return "-";
            }

            if (technicals.BollingerLower20.HasValue && price.Value <= technicals.BollingerLower20.Value)
            {
                return "fora/inferior";
            }

            if (technicals.BollingerUpper20.HasValue && price.Value >= technicals.BollingerUpper20.Value)
            {
                return "fora/superior";
            }

            return "dentro da banda";
        }

        private string ZScoreState(decimal? value)
        {
            if (!value.HasValue)
            {
                return "-";
            }

            if (value.Value <= -2m)
            {
                return "desvio extremo para baixo";
            }

            if (value.Value >= 2m)
            {
                return "desvio extremo para cima";
            }

            if (value.Value <= -1m)
            {
                return "desvio para baixo";
            }

            if (value.Value >= 1m)
            {
                return "desvio para cima";
            }

            return "centro estatistico";
        }

        private string AtrVwapState(decimal? value)
        {
            if (!value.HasValue)
            {
                return "-";
            }

            decimal distance = Math.Abs(value.Value);

            if (distance >= 2m)
            {
                return "distancia elevada";
            }

            if (distance >= 1m)
            {
                return "distancia moderada";
            }

            return "perto da VWAP";
        }

        private string ReturnState(decimal? value)
        {
            if (!value.HasValue)
            {
                return "-";
            }

            if (value.Value > 0.15m)
            {
                return "vies positivo";
            }

            if (value.Value < -0.15m)
            {
                return "vies negativo";
            }

            return "neutro";
        }

        private string MomentumState(decimal? value)
        {
            if (!value.HasValue)
            {
                return "-";
            }

            if (value.Value >= 0.35m)
            {
                return "momentum comprador";
            }

            if (value.Value <= -0.35m)
            {
                return "momentum vendedor";
            }

            return "neutro";
        }

        private string PositiveRateState(decimal? value)
        {
            if (!value.HasValue)
            {
                return "-";
            }

            if (value.Value >= 58m)
            {
                return "assimetria positiva";
            }

            if (value.Value <= 42m)
            {
                return "assimetria negativa";
            }

            return "equilibrado";
        }

        private string SharpeState(decimal? value)
        {
            if (!value.HasValue)
            {
                return "-";
            }

            if (value.Value >= 1m)
            {
                return "favoravel";
            }

            if (value.Value <= -1m)
            {
                return "desfavoravel";
            }

            return "neutro";
        }

        private void RefreshHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            RenderHistory();
        }

        private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            _historyRows.Clear();
            AddHistory("Historico", "Limpar", "Historico local limpo.");
            RenderHistory();
        }

        private void RenderOpportunities(MarketSnapshot snapshot)
        {
            if (OpportunitiesGrid == null || OpportunityLevelsGrid == null || OpportunitiesSummaryText == null)
            {
                return;
            }

            if (snapshot == null)
            {
                snapshot = FocusedSnapshot() ?? _lastSnapshot;
            }

            string focused = FocusedAsset();
            FlowMetrics metrics = _flowProcessor.GetMetrics(focused);
            List<OpportunityRow> rows = BuildOpportunityRows();
            List<OpportunityLevelRow> levels = BuildOpportunityLevelRows(snapshot);
            int actionable = rows.Count(x => !string.Equals(x.Setup, "Aguardando setup", StringComparison.OrdinalIgnoreCase));

            OpportunitiesGrid.ItemsSource = rows;
            OpportunityLevelsGrid.ItemsSource = levels;
            OpportunityAssetText.Text = EmptyToDash(focused);
            OpportunitySignalCountText.Text = actionable.ToString(_ptBr);
            OpportunityQualityText.Text = metrics == null ? "-" : metrics.DataQuality + (metrics.Derived ? " derivado" : " real");
            OpportunityLevelCountText.Text = levels.Count.ToString(_ptBr);
            OpportunitiesSummaryText.Text = actionable.ToString(_ptBr) +
                                            " oportunidade(s) | foco " +
                                            EmptyToDash(focused) +
                                            " | RTD " +
                                            EmptyToDash(_probeService.Status);
        }

        private List<OpportunityRow> BuildOpportunityRows()
        {
            List<OpportunityRow> rows = new List<OpportunityRow>();
            IEnumerable<RtdAssetConfig> assets = _config.Rtd.Assets.Where(x => x.Enabled).ToList();

            if (!assets.Any())
            {
                RtdAssetConfig focusedConfig = _config.Rtd.FindAsset(FocusedAsset());

                if (focusedConfig != null)
                {
                    assets = new[] { focusedConfig };
                }
            }

            foreach (RtdAssetConfig asset in assets)
            {
                string assetName = asset.Asset;
                List<FlowSignal> signals = _flowProcessor.GetSignals(assetName, 8) ?? new List<FlowSignal>();
                MarketSnapshot snapshot = SnapshotForAsset(assetName);
                FlowMetrics metrics = _flowProcessor.GetMetrics(assetName);
                List<OpportunityRow> quantRows = BuildQuantOpportunityRows(asset, snapshot, metrics, signals);
                rows.AddRange(quantRows);

                bool hasGarchSignals = string.Equals(assetName, FocusedAsset(), StringComparison.OrdinalIgnoreCase) &&
                                       _result != null && _result.Garch != null &&
                                       _result.Garch.Signals != null && _result.Garch.Signals.Count > 0;

                if (hasGarchSignals)
                {
                    foreach (GarchSignal garchSignal in _result.Garch.Signals)
                    {
                        OpportunityRow row = new OpportunityRow();
                        row.Asset = assetName;
                        row.Setup = "GARCH " + EmptyToDash(garchSignal.Setup);
                        row.Direction = EmptyToDash(garchSignal.Direction);
                        row.Price = garchSignal.Price.ToString("N2", _ptBr);
                        row.Score = garchSignal.Score.ToString(_ptBr);
                        row.Robustness = EmptyToDash(garchSignal.Gate);
                        row.Level = EmptyToDash(garchSignal.LevelName) + (garchSignal.LevelPrice.HasValue ? " @ " + garchSignal.LevelPrice.Value.ToString("N2", _ptBr) : string.Empty);
                        row.Quality = "GARCH (" + EmptyToDash(garchSignal.Scope) + ")";
                        row.Age = "calc atual";
                        row.Reasons = EmptyToDash(garchSignal.Reasons);
                        row.SortScore = garchSignal.Score;
                        rows.Add(row);
                    }
                }

                if (signals.Count == 0 && quantRows.Count == 0 && !hasGarchSignals)
                {
                    OpportunityScore waitingScore = ScoreOpportunity(asset, snapshot, metrics, null, null, null);
                    OpportunityRow waiting = new OpportunityRow();
                    waiting.Asset = assetName;
                    waiting.Setup = "Aguardando setup";
                    waiting.Direction = "-";
                    waiting.Price = snapshot == null ? "-" : FormatDecimal(snapshot.Ultimo, "N2");
                    waiting.Score = waitingScore.Score.ToString(_ptBr);
                    waiting.Robustness = waitingScore.Robustness;
                    waiting.Level = "-";
                    waiting.Quality = metrics == null ? "-" : metrics.DataQuality.ToString();
                    waiting.Age = snapshot == null ? "sem snapshot" : AgeText(snapshot.LocalTimestamp);
                    waiting.Reasons = (ChannelEnabled(assetName, "Times") ? "sem sinal no cooldown atual" : "dados limitados ou sem tape") + "; " + waitingScore.Detail;
                    waiting.SortScore = waitingScore.Score;
                    rows.Add(waiting);
                    continue;
                }

                foreach (FlowSignal signal in signals.OrderByDescending(x => x.Score).ThenByDescending(x => x.LocalTimestamp).Take(8))
                {
                    QuantSignal matchingQuant = FindMatchingQuantSignal(assetName, signal);
                    OpportunityScore score = ScoreOpportunity(asset, snapshot, metrics, signal, matchingQuant, null);
                    OpportunityRow row = new OpportunityRow();
                    row.Asset = assetName;
                    row.Setup = EmptyToDash(signal.Setup);
                    row.Direction = EmptyToDash(signal.Direction);
                    row.Price = signal.Price.ToString("N2", _ptBr);
                    row.Score = score.Score.ToString(_ptBr);
                    row.Robustness = score.Robustness;
                    row.Level = EmptyToDash(signal.LevelName) + (signal.LevelPrice.HasValue ? " @ " + signal.LevelPrice.Value.ToString("N2", _ptBr) : string.Empty);
                    row.Quality = signal.DataQuality + (signal.Derived ? " derivado" : " real");
                    row.Age = AgeText(signal.LocalTimestamp);
                    row.Reasons = EmptyToDash(signal.Reasons) + "; " + score.Detail;
                    row.SortScore = score.Score;
                    rows.Add(row);
                }
            }

            return rows
                .OrderByDescending(x => x.SortScore)
                .ThenBy(x => x.Asset)
                .Take(80)
                .ToList();
        }

        private List<OpportunityRow> BuildQuantOpportunityRows(RtdAssetConfig asset, MarketSnapshot snapshot, FlowMetrics metrics, List<FlowSignal> flowSignals)
        {
            List<OpportunityRow> rows = new List<OpportunityRow>();
            string assetName = asset == null ? string.Empty : asset.Asset;
            string focused = FocusedAsset();

            if (_result == null ||
                _result.QuantSignals == null ||
                _result.QuantSignals.Count == 0 ||
                !string.Equals(assetName, focused, StringComparison.OrdinalIgnoreCase))
            {
                return rows;
            }

            foreach (QuantSignal signal in _result.QuantSignals.OrderByDescending(x => x.Score).Take(8))
            {
                FlowSignal matchingFlow = FindMatchingFlowSignal(flowSignals, signal);
                OpportunityScore score = ScoreOpportunity(asset, snapshot, metrics, matchingFlow, signal, null);
                OpportunityRow row = new OpportunityRow();
                row.Asset = assetName;
                row.Setup = EmptyToDash(signal.Setup);
                row.Direction = EmptyToDash(signal.Direction);
                row.Price = signal.Price.ToString("N2", _ptBr);
                row.Score = score.Score.ToString(_ptBr);
                row.Robustness = score.Robustness;
                row.Level = EmptyToDash(signal.LevelName) + (signal.LevelPrice.HasValue ? " @ " + signal.LevelPrice.Value.ToString("N2", _ptBr) : string.Empty);
                row.Quality = "Quant " + EmptyToDash(signal.DataSource) + (metrics == null ? "" : " + " + metrics.DataQuality);
                row.Age = snapshot == null ? "calc atual" : AgeText(snapshot.LocalTimestamp);
                row.Reasons = EmptyToDash(signal.Reasons) +
                              "; " +
                              EmptyToDash(signal.TechnicalState) +
                              "; " +
                              EmptyToDash(signal.StatisticalEdge) +
                              "; " +
                              QuantFlowConfirmation(signal, metrics) +
                              "; " +
                              score.Detail;
                row.SortScore = score.Score;
                rows.Add(row);
            }

            return rows;
        }

        private int QuantFlowAdjustedScore(QuantSignal signal, FlowMetrics metrics)
        {
            if (signal == null)
            {
                return 0;
            }

            int score = signal.Score;

            if (metrics == null)
            {
                return Math.Min(85, score);
            }

            decimal imbalance = metrics.TopBookImbalance.HasValue ? metrics.TopBookImbalance.Value : 0m;
            decimal delta = metrics.CumulativeDelta;
            bool buy = string.Equals(signal.Direction, "Buy", StringComparison.OrdinalIgnoreCase);
            bool sell = string.Equals(signal.Direction, "Sell", StringComparison.OrdinalIgnoreCase);
            bool confirms = (buy && (delta > 0m || imbalance > 0.08m)) ||
                            (sell && (delta < 0m || imbalance < -0.08m));
            bool conflicts = (buy && (delta < 0m && imbalance < -0.08m)) ||
                             (sell && (delta > 0m && imbalance > 0.08m));

            if (confirms)
            {
                score += metrics.DataQuality == MarketDataQuality.TopOfBookOnly ? 4 : 8;
            }
            else if (conflicts)
            {
                score -= 8;
            }

            int cap = metrics.DataQuality == MarketDataQuality.FullTimesAndTrades || metrics.DataQuality == MarketDataQuality.FullDepth ? 95 : 88;

            if (!QuantSignalHasUsableEdge(signal))
            {
                cap = Math.Min(cap, 74);
                score -= 4;
            }
            else if (!QuantSignalHasPositiveEdge(signal))
            {
                cap = Math.Min(cap, 86);
            }

            return Math.Max(0, Math.Min(cap, score));
        }

        private string QuantFlowConfirmation(QuantSignal signal, FlowMetrics metrics)
        {
            if (signal == null)
            {
                return "-";
            }

            if (metrics == null)
            {
                return "fluxo ainda sem confirmacao";
            }

            decimal imbalance = metrics.TopBookImbalance.HasValue ? metrics.TopBookImbalance.Value : 0m;
            bool buy = string.Equals(signal.Direction, "Buy", StringComparison.OrdinalIgnoreCase);
            bool sell = string.Equals(signal.Direction, "Sell", StringComparison.OrdinalIgnoreCase);

            if ((buy && (metrics.CumulativeDelta > 0m || imbalance > 0.08m)) ||
                (sell && (metrics.CumulativeDelta < 0m || imbalance < -0.08m)))
            {
                return "fluxo confirma direcao";
            }

            if ((buy && (metrics.CumulativeDelta < 0m && imbalance < -0.08m)) ||
                (sell && (metrics.CumulativeDelta > 0m && imbalance > 0.08m)))
            {
                return "fluxo conflita; aguardar confirmacao";
            }

            return "fluxo neutro";
        }

        private OpportunityScore ScoreOpportunity(RtdAssetConfig asset, MarketSnapshot snapshot, FlowMetrics metrics, FlowSignal flowSignal, QuantSignal quantSignal, KeyLevel nearestLevel)
        {
            OpportunityScore result = new OpportunityScore();
            List<string> evidence = new List<string>();
            int score = Math.Max(flowSignal == null ? 0 : flowSignal.Score, quantSignal == null ? 0 : QuantFlowAdjustedScore(quantSignal, metrics));
            int cap = OpportunityScoreCap(asset, snapshot, metrics, quantSignal != null, evidence);
            string direction = OpportunityDirection(flowSignal, quantSignal);
            int confirmations = 0;

            if (flowSignal != null)
            {
                confirmations++;
                evidence.Add("setup fluxo " + flowSignal.Score.ToString(_ptBr));
            }

            if (quantSignal != null)
            {
                confirmations++;
                evidence.Add("setup quant " + QuantFlowAdjustedScore(quantSignal, metrics).ToString(_ptBr));
            }

            if (flowSignal != null && quantSignal != null)
            {
                if (SameDirection(flowSignal.Direction, quantSignal.Direction))
                {
                    score += 10;
                    confirmations++;
                    evidence.Add("quant+fluxo alinhados");
                }
                else
                {
                    score -= 12;
                    evidence.Add("quant/fluxo divergentes");
                }
            }

            string flowAlignment = FlowDirectionAlignment(direction, metrics);
            bool flowConfirms = string.Equals(flowAlignment, "confirma", StringComparison.OrdinalIgnoreCase);

            if (flowConfirms)
            {
                score += metrics != null && metrics.DataQuality == MarketDataQuality.TopOfBookOnly ? 4 : 8;
                confirmations++;
                evidence.Add("delta/imbalance confirmam");
            }
            else if (string.Equals(flowAlignment, "conflita", StringComparison.OrdinalIgnoreCase))
            {
                score -= 12;
                evidence.Add("delta/imbalance conflitam");
            }

            if (HasProfileReference(flowSignal, quantSignal, nearestLevel))
            {
                score += 5;
                confirmations++;
                evidence.Add("nivel profile/estatistico");
            }

            int sampleSize = _result == null || _result.Technicals == null ? _dailyBars.Count : _result.Technicals.SampleSize;

            if (sampleSize >= 126)
            {
                score += 8;
                confirmations++;
                evidence.Add("amostra >=126");
            }
            else if (sampleSize >= 63)
            {
                score += 5;
                confirmations++;
                evidence.Add("amostra >=63");
            }
            else if (sampleSize >= 21)
            {
                score += 2;
                evidence.Add("amostra >=21");
            }
            else if (quantSignal != null)
            {
                score -= 10;
                evidence.Add("amostra historica baixa");
            }

            BacktestRow bestBacktest = BestBacktestRow(direction);

            if (bestBacktest != null &&
                bestBacktest.Touches >= 5 &&
                bestBacktest.ReversalRate >= 55d &&
                bestBacktest.ExpectancyPoints > 0m &&
                bestBacktest.ProfitFactor >= 1.05d)
            {
                score += 5;
                confirmations++;
                evidence.Add("backtest direcional favoravel");
            }
            else if (quantSignal != null)
            {
                evidence.Add("edge historico sem confirmacao forte");
            }

            if (quantSignal != null)
            {
                if (QuantSignalHasPositiveEdge(quantSignal))
                {
                    score += 6;
                    confirmations++;
                    evidence.Add("edge direcional positivo");
                }
                else if (QuantSignalHasUsableEdge(quantSignal))
                {
                    score += 3;
                    evidence.Add("edge direcional moderado");
                    cap = Math.Min(cap, 88);
                }
                else
                {
                    score -= 10;
                    cap = Math.Min(cap, 74);
                    evidence.Add("edge direcional fragil");
                }
            }

            if (metrics != null && metrics.Profile != null && metrics.Profile.Poc.HasValue)
            {
                confirmations++;
                evidence.Add("volume profile ativo");
            }

            if (nearestLevel != null && Math.Abs(nearestLevel.Distance) <= _config.Rtd.TickSize * 10m)
            {
                score += 3;
                evidence.Add("preco perto de nivel");
            }

            if (_flowProcessor.Dropped > 0)
            {
                score -= 4;
                evidence.Add("fila descartou eventos");
            }

            if (flowSignal == null && quantSignal == null)
            {
                score = snapshot == null ? 0 : Math.Min(30, cap);
                evidence.Add("sem gatilho estatistico/fluxo");
            }

            score = Math.Max(0, Math.Min(cap, score));
            result.Score = score;
            result.Robustness = OpportunityRobustness(score, cap, confirmations, quantSignal, metrics, flowConfirms);
            result.Detail = string.Join("; ", evidence.Where(x => !string.IsNullOrWhiteSpace(x)).Take(8).ToArray());
            return result;
        }

        private int OpportunityScoreCap(RtdAssetConfig asset, MarketSnapshot snapshot, FlowMetrics metrics, bool requiresHistory, List<string> evidence)
        {
            int cap = 100;

            if (asset == null || !asset.Enabled)
            {
                evidence.Add("ativo desligado");
                return 35;
            }

            if (!asset.QuoteEnabled)
            {
                cap = Math.Min(cap, 48);
                evidence.Add("cotacao desligada");
            }

            if (snapshot == null || !snapshot.Ultimo.HasValue)
            {
                cap = Math.Min(cap, 45);
                evidence.Add("sem ULT/snapshot");
            }
            else
            {
                double ageSeconds = Math.Max(0d, (DateTimeOffset.Now - snapshot.LocalTimestamp).TotalSeconds);

                if (ageSeconds >= 15d)
                {
                    cap = Math.Min(cap, 50);
                    evidence.Add("snapshot atrasado");
                }
                else if (ageSeconds >= 5d)
                {
                    cap = Math.Min(cap, 72);
                    evidence.Add("snapshot lento");
                }
                else
                {
                    evidence.Add("snapshot fresco");
                }
            }

            if (metrics == null)
            {
                cap = Math.Min(cap, 62);
                evidence.Add("sem metricas de fluxo");
            }
            else if (metrics.DataQuality == MarketDataQuality.TopOfBookOnly)
            {
                cap = Math.Min(cap, Math.Min(_config.Flow.TopOfBookOnlyScoreCap, 78));
                evidence.Add("cap top-of-book");
            }
            else if (metrics.DataQuality == MarketDataQuality.DerivedTape)
            {
                cap = Math.Min(cap, Math.Min(_config.Flow.DerivedTapeScoreCap, 85));
                evidence.Add("cap tape derivado");
            }
            else if (metrics.DataQuality == MarketDataQuality.FullTimesAndTrades)
            {
                cap = Math.Min(cap, 96);
                evidence.Add("times real");
            }
            else if (metrics.DataQuality == MarketDataQuality.FullDepth)
            {
                evidence.Add("book profundo real");
            }

            if (requiresHistory && _dailyBars.Count < 21)
            {
                cap = Math.Min(cap, 60);
                evidence.Add("CSV <21 pregoes");
            }

            if (!asset.BookEnabled && !asset.TimesEnabled)
            {
                cap = Math.Min(cap, 78);
                evidence.Add("sem Book/Times ligados");
            }

            return Math.Max(0, cap);
        }

        private string OpportunityRobustness(int score, int cap, int confirmations, QuantSignal quantSignal, FlowMetrics metrics, bool flowConfirms)
        {
            if (cap < 55)
            {
                return "Bloqueado";
            }

            bool dataCanBeRobust = metrics != null &&
                                   !metrics.Derived &&
                                   (metrics.DataQuality == MarketDataQuality.FullTimesAndTrades ||
                                    metrics.DataQuality == MarketDataQuality.FullDepth);
            bool quantEdgePositive = QuantSignalHasPositiveEdge(quantSignal);
            bool quantEdgeUsable = QuantSignalHasUsableEdge(quantSignal);

            if (score >= 85 && cap >= 90 && confirmations >= 4 && dataCanBeRobust && flowConfirms && quantEdgePositive)
            {
                return "Robusto";
            }

            if (score >= _config.Flow.StrongSetupScoreThreshold && confirmations >= 3 && (flowConfirms || quantEdgeUsable))
            {
                return "Acionavel";
            }

            if (score >= _config.Flow.SetupScoreThreshold)
            {
                return "Monitorar";
            }

            return "Fraco";
        }

        private bool QuantSignalHasPositiveEdge(QuantSignal signal)
        {
            return signal != null &&
                   signal.ExpectancyPoints.HasValue &&
                   signal.ExpectancyPoints.Value > 0m &&
                   signal.ReversalRate >= 58d &&
                   signal.ProfitFactor >= 1.25d &&
                   signal.Confidence >= 45d &&
                   signal.RiskReward >= 1m;
        }

        private bool QuantSignalHasUsableEdge(QuantSignal signal)
        {
            return signal != null &&
                   signal.ExpectancyPoints.HasValue &&
                   signal.ExpectancyPoints.Value > 0m &&
                   signal.ReversalRate >= 52d &&
                   signal.ProfitFactor >= 1.05d &&
                   signal.Confidence >= 30d &&
                   signal.RiskReward >= 0.85m;
        }

        private QuantSignal FindMatchingQuantSignal(string assetName, FlowSignal flowSignal)
        {
            if (flowSignal == null ||
                _result == null ||
                _result.QuantSignals == null ||
                !string.Equals(assetName, FocusedAsset(), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            decimal reference = flowSignal.LevelPrice.HasValue ? flowSignal.LevelPrice.Value : flowSignal.Price;
            decimal tolerance = Math.Max(_config.Rtd.TickSize * 16m, 4m);

            return _result.QuantSignals
                .Where(x => SameDirection(x.Direction, flowSignal.Direction))
                .Where(x => Math.Abs((x.LevelPrice.HasValue ? x.LevelPrice.Value : x.Price) - reference) <= tolerance)
                .OrderByDescending(x => QuantFlowAdjustedScore(x, _flowProcessor.GetMetrics(assetName)))
                .FirstOrDefault();
        }

        private FlowSignal FindMatchingFlowSignal(List<FlowSignal> flowSignals, QuantSignal quantSignal)
        {
            if (quantSignal == null)
            {
                return null;
            }

            decimal reference = quantSignal.LevelPrice.HasValue ? quantSignal.LevelPrice.Value : quantSignal.Price;
            decimal tolerance = Math.Max(_config.Rtd.TickSize * 16m, 4m);

            return (flowSignals ?? new List<FlowSignal>())
                .Where(x => SameDirection(x.Direction, quantSignal.Direction))
                .Where(x => Math.Abs((x.LevelPrice.HasValue ? x.LevelPrice.Value : x.Price) - reference) <= tolerance)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.LocalTimestamp)
                .FirstOrDefault();
        }

        private QuantSignal BestQuantSignalForAsset(string assetName, FlowMetrics metrics)
        {
            if (_result == null ||
                _result.QuantSignals == null ||
                !string.Equals(assetName, FocusedAsset(), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return _result.QuantSignals
                .OrderByDescending(x => QuantFlowAdjustedScore(x, metrics))
                .FirstOrDefault();
        }

        private bool SameDirection(string left, string right)
        {
            return !string.IsNullOrWhiteSpace(left) &&
                   !string.IsNullOrWhiteSpace(right) &&
                   string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private string OpportunityDirection(FlowSignal flowSignal, QuantSignal quantSignal)
        {
            if (flowSignal != null && !string.IsNullOrWhiteSpace(flowSignal.Direction))
            {
                return flowSignal.Direction;
            }

            return quantSignal == null ? string.Empty : quantSignal.Direction;
        }

        private string FlowDirectionAlignment(string direction, FlowMetrics metrics)
        {
            if (string.IsNullOrWhiteSpace(direction) || metrics == null)
            {
                return "neutro";
            }

            decimal imbalance = metrics.TopBookImbalance.HasValue ? metrics.TopBookImbalance.Value : 0m;
            bool buy = string.Equals(direction, "Buy", StringComparison.OrdinalIgnoreCase);
            bool sell = string.Equals(direction, "Sell", StringComparison.OrdinalIgnoreCase);

            if ((buy && (metrics.CumulativeDelta > 0m || imbalance > 0.08m)) ||
                (sell && (metrics.CumulativeDelta < 0m || imbalance < -0.08m)))
            {
                return "confirma";
            }

            if ((buy && (metrics.CumulativeDelta < 0m && imbalance < -0.08m)) ||
                (sell && (metrics.CumulativeDelta > 0m && imbalance > 0.08m)))
            {
                return "conflita";
            }

            return "neutro";
        }

        private bool HasProfileReference(FlowSignal flowSignal, QuantSignal quantSignal, KeyLevel nearestLevel)
        {
            string text = string.Join(" ", new[]
            {
                flowSignal == null ? string.Empty : flowSignal.LevelName,
                quantSignal == null ? string.Empty : quantSignal.LevelName,
                nearestLevel == null ? string.Empty : nearestLevel.Label,
                nearestLevel == null ? string.Empty : nearestLevel.Source
            }).ToUpperInvariant();

            return text.Contains("POC") ||
                   text.Contains("VAH") ||
                   text.Contains("VAL") ||
                   text.Contains("HVN") ||
                   text.Contains("LVN") ||
                   text.Contains("PROFILE");
        }

        private BacktestRow BestBacktestRow(string direction)
        {
            if (_result == null || _result.Backtest == null || _result.Backtest.Count == 0)
            {
                return null;
            }

            return _result.Backtest
                .Where(x => string.IsNullOrWhiteSpace(direction) || string.Equals(x.Direction, direction, StringComparison.OrdinalIgnoreCase))
                .Where(x => x.Touches > 0)
                .OrderByDescending(x => x.ExpectancyPoints)
                .ThenByDescending(x => x.ReversalRate)
                .FirstOrDefault();
        }

        private List<OpportunityLevelRow> BuildOpportunityLevelRows(MarketSnapshot snapshot)
        {
            List<OpportunityLevelRow> rows = new List<OpportunityLevelRow>();

            foreach (KeyLevel level in BuildDashboardLevels(snapshot).OrderBy(x => Math.Abs(x.Distance)).Take(80))
            {
                OpportunityLevelRow row = new OpportunityLevelRow();
                row.Price = level.Price.ToString("N2", _ptBr);
                row.Label = EmptyToDash(level.Label);
                row.Source = EmptyToDash(level.Source);
                row.Score = level.Score.ToString("N0", _ptBr);
                row.Distance = level.Distance.ToString("N2", _ptBr);
                row.Evidence = EmptyToDash(level.Evidence);
                row.Direction = level.Direction;
                rows.Add(row);
            }

            return rows;
        }

        private int ParseIntOrZero(string value)
        {
            int result;
            return int.TryParse(value, NumberStyles.Integer, _ptBr, out result) ? result : 0;
        }

        private void AddHistory(string area, string evt, string detail)
        {
            HistoryRow row = new HistoryRow();
            row.Time = DateTimeOffset.Now.ToString("HH:mm:ss", _ptBr);
            row.Asset = EmptyToDash(FocusedAsset());
            row.Area = EmptyToDash(area);
            row.Event = EmptyToDash(evt);
            row.Detail = EmptyToDash(detail);
            _historyRows.Insert(0, row);

            while (_historyRows.Count > 500)
            {
                _historyRows.RemoveAt(_historyRows.Count - 1);
            }

            if (CurrentMainTabIndex() == TabHistory)
            {
                RenderHistory();
            }
        }

        private void RenderHistory()
        {
            if (HistoryGrid == null || HistorySummaryText == null)
            {
                return;
            }

            HistoryGrid.ItemsSource = _historyRows.ToList();
            HistorySummaryText.Text = _historyRows.Count.ToString(_ptBr) +
                                      " evento(s) | foco " +
                                      EmptyToDash(FocusedAsset()) +
                                      " | RTD " +
                                      EmptyToDash(_probeService.Status);
        }

        private void RenderScanner()
        {
            if (ScannerGrid == null || ScannerSummaryText == null)
            {
                return;
            }

            string selected = SelectedScannerAsset();
            string focused = FocusedAsset();
            List<ScannerRow> rows = BuildScannerRows();
            int signalCount = rows.Count(x => ParseIntOrZero(x.Score) > 0);
            int enabledCount = rows.Count(x => string.Equals(x.EnabledText, "On", StringComparison.OrdinalIgnoreCase));

            ScannerGrid.ItemsSource = rows;

            ScannerRow selectedRow = rows.FirstOrDefault(x => string.Equals(x.Asset, selected, StringComparison.OrdinalIgnoreCase)) ??
                                     rows.FirstOrDefault(x => string.Equals(x.Asset, focused, StringComparison.OrdinalIgnoreCase));

            if (selectedRow != null)
            {
                ScannerGrid.SelectedItem = selectedRow;
            }

            ScannerAssetCountText.Text = rows.Count.ToString(_ptBr);
            ScannerSignalCountText.Text = signalCount.ToString(_ptBr);
            ScannerFocusText.Text = EmptyToDash(focused);
            ScannerSummaryText.Text = rows.Count.ToString(_ptBr) +
                                      " ativo(s) | ligados " +
                                      enabledCount.ToString(_ptBr) +
                                      " | com sinal " +
                                      signalCount.ToString(_ptBr) +
                                      " | RTD " +
                                      EmptyToDash(_probeService.Status);
        }

        private List<ScannerRow> BuildScannerRows()
        {
            List<ScannerRow> rows = new List<ScannerRow>();
            string focused = FocusedAsset();

            foreach (RtdAssetConfig item in _config.Rtd.Assets)
            {
                item.Normalize();
                string assetName = item.Asset;
                MarketSnapshot snapshot = SnapshotForAsset(assetName);
                FlowMetrics metrics = _flowProcessor.GetMetrics(assetName);
                List<FlowSignal> signals = _flowProcessor.GetSignals(assetName, 12) ?? new List<FlowSignal>();
                FlowSignal bestSignal = signals.OrderByDescending(x => x.Score).ThenByDescending(x => x.LocalTimestamp).FirstOrDefault();
                QuantSignal bestQuant = BestQuantSignalForAsset(assetName, metrics);
                KeyLevel nearestLevel = NearestScannerLevel(assetName, snapshot, metrics, signals);
                decimal? distance = nearestLevel == null ? null : (decimal?)nearestLevel.Distance;
                OpportunityScore opportunity = ScoreOpportunity(item, snapshot, metrics, bestSignal, bestQuant, nearestLevel);
                bool preferQuant = bestQuant != null && (bestSignal == null || QuantFlowAdjustedScore(bestQuant, metrics) >= bestSignal.Score);

                ScannerRow row = new ScannerRow();
                row.Rank = "0";
                row.Asset = assetName;
                row.EnabledText = item.Enabled ? "On" : "Off";
                row.Status = MonitorAssetStatus(item, snapshot);
                row.Last = snapshot == null ? "-" : FormatDecimal(snapshot.Ultimo, "N2");
                row.BidAsk = snapshot == null ? "-" : FormatDecimal(snapshot.OfertaCompra, "N2") + " / " + FormatDecimal(snapshot.OfertaVenda, "N2");
                row.SnapshotAge = snapshot == null ? "-" : AgeText(snapshot.LocalTimestamp);
                row.Channels = (item.QuoteEnabled ? "C" : "-") + "/" + (item.BookEnabled ? "B" : "-") + "/" + (item.TimesEnabled ? "T" : "-");
                row.Quality = metrics == null ? "-" : metrics.DataQuality + (metrics.Derived ? " derivado" : " real");
                row.BestSetup = preferQuant ? EmptyToDash(bestQuant.Setup) : (bestSignal == null ? "Aguardando setup" : EmptyToDash(bestSignal.Setup));
                row.Direction = preferQuant ? EmptyToDash(bestQuant.Direction) : (bestSignal == null ? "-" : EmptyToDash(bestSignal.Direction));
                row.Score = opportunity.Score.ToString(_ptBr);
                row.Level = nearestLevel == null ? "-" : EmptyToDash(nearestLevel.Label);
                row.Distance = distance.HasValue ? distance.Value.ToString("N2", _ptBr) : "-";
                row.Delta = metrics == null ? "-" : metrics.LastDelta.ToString("N0", _ptBr) + " / " + metrics.CumulativeDelta.ToString("N0", _ptBr);
                row.VwapDistance = metrics == null ? "-" : FormatDecimal(metrics.VwapDistance, "N2");
                row.Read = opportunity.Robustness + " | " + ScannerReadText(item, snapshot, metrics, bestSignal, nearestLevel) + " | " + opportunity.Detail;
                row.FocusText = string.Equals(assetName, focused, StringComparison.OrdinalIgnoreCase) ? "Sim" : string.Empty;
                row.SortScore = opportunity.Score + (ScannerSortScore(item, snapshot, metrics, bestSignal, nearestLevel) * 0.05d);
                rows.Add(row);
            }

            List<ScannerRow> ordered = rows
                .OrderByDescending(x => x.SortScore)
                .ThenBy(x => x.Asset)
                .ToList();

            for (int i = 0; i < ordered.Count; i++)
            {
                ordered[i].Rank = (i + 1).ToString(_ptBr);
            }

            return ordered;
        }

        private KeyLevel NearestScannerLevel(string assetName, MarketSnapshot snapshot, FlowMetrics metrics, List<FlowSignal> signals)
        {
            List<KeyLevel> levels = new List<KeyLevel>();

            if (snapshot != null)
            {
                levels.AddRange(BasicLevels(snapshot));
            }

            if (metrics != null && metrics.Profile != null && metrics.Profile.Levels != null)
            {
                foreach (ProfileLevel profileLevel in metrics.Profile.Levels)
                {
                    KeyLevel level = new KeyLevel();
                    level.Price = profileLevel.Price;
                    level.Label = profileLevel.Label;
                    level.Type = profileLevel.Type;
                    level.Source = profileLevel.Source;
                    level.Score = profileLevel.Score;
                    level.Evidence = "Volume Profile intraday";
                    levels.Add(level);
                }
            }

            foreach (FlowSignal signal in (signals ?? new List<FlowSignal>()).Where(x => x.LevelPrice.HasValue))
            {
                KeyLevel level = new KeyLevel();
                level.Price = signal.LevelPrice.Value;
                level.Label = EmptyToDash(signal.LevelName);
                level.Type = "setup";
                level.Source = "Setups";
                level.Score = signal.Score;
                level.Evidence = signal.Reasons;
                level.Direction = signal.Direction;
                levels.Add(level);
            }

            foreach (KeyLevel level in levels)
            {
                level.Distance = snapshot != null && snapshot.Ultimo.HasValue ? snapshot.Ultimo.Value - level.Price : 0m;
            }

            return levels
                .OrderBy(x => Math.Abs(x.Distance))
                .ThenByDescending(x => x.Score)
                .FirstOrDefault();
        }

        private double ScannerSortScore(RtdAssetConfig item, MarketSnapshot snapshot, FlowMetrics metrics, FlowSignal signal, KeyLevel nearestLevel)
        {
            double score = signal == null ? 0d : signal.Score;

            if (item != null && item.Enabled)
            {
                score += 8d;
            }

            if (snapshot != null)
            {
                score += 8d;
            }

            if (metrics != null)
            {
                score += 6d;

                if (!metrics.Derived)
                {
                    score += 4d;
                }

                score += Math.Min(10d, Math.Abs((double)metrics.CumulativeDelta) / 1000d);
            }

            if (nearestLevel != null)
            {
                score += Math.Max(0d, 10d - Math.Min(10d, Math.Abs((double)nearestLevel.Distance)));
            }

            return score;
        }

        private string ScannerReadText(RtdAssetConfig item, MarketSnapshot snapshot, FlowMetrics metrics, FlowSignal signal, KeyLevel nearestLevel)
        {
            if (item == null || !item.Enabled)
            {
                return "ativo desligado";
            }

            if (snapshot == null)
            {
                return "aguardando snapshot";
            }

            if (signal != null)
            {
                return EmptyToDash(signal.Setup) + " | " + EmptyToDash(signal.Reasons);
            }

            if (metrics != null && metrics.DataQuality == MarketDataQuality.TopOfBookOnly)
            {
                return "dados limitados; priorize Book/Times se disponivel";
            }

            if (nearestLevel != null)
            {
                return "sem setup; observando " + EmptyToDash(nearestLevel.Label);
            }

            return "sem setup ativo";
        }

        private List<NameValueRow> BuildDashboardChannelRows(RtdAssetConfig asset, MarketSnapshot snapshot)
        {
            List<NameValueRow> rows = new List<NameValueRow>();

            AddRow(rows, "Cotacao", ChannelTopicText(asset, "Cotacao"), ChannelState(asset, asset != null && ChannelEnabled(asset.Asset, "Cotacao"), snapshot));
            AddRow(rows, "Book", ChannelTopicText(asset, "Book"), ChannelState(asset, asset != null && ChannelEnabled(asset.Asset, "Book"), snapshot));
            AddRow(rows, "Times", ChannelTopicText(asset, "Times"), ChannelState(asset, asset != null && ChannelEnabled(asset.Asset, "Times"), snapshot));

            return rows;
        }

        private List<DashboardWindowRow> BuildDashboardWindowRows(FlowMetrics metrics)
        {
            List<DashboardWindowRow> rows = new List<DashboardWindowRow>();

            if (metrics == null || metrics.Windows == null)
            {
                return rows;
            }

            foreach (FlowWindowMetrics window in metrics.Windows.OrderBy(x => x.Seconds))
            {
                DashboardWindowRow row = new DashboardWindowRow();
                row.Window = EmptyToDash(window.Window);
                row.TradeCount = window.TradeCount.ToString("N0", _ptBr);
                row.BuyVolume = window.BuyVolume.ToString("N0", _ptBr);
                row.SellVolume = window.SellVolume.ToString("N0", _ptBr);
                row.Delta = window.Delta.ToString("N0", _ptBr);
                row.DeltaRatio = window.DeltaRatio.ToString("N3", _ptBr);
                rows.Add(row);
            }

            return rows;
        }

        private void RefreshDashboardChart(MarketSnapshot snapshot)
        {
            if (DashboardChartControl == null)
            {
                return;
            }

            DateTimeOffset now = DateTimeOffset.Now;
            string focused = FocusedAsset();
            long version = _probeService == null ? -1 : _probeService.UpdatesReceived;
            bool assetChanged = !string.Equals(_lastDashboardChartAsset, focused, StringComparison.OrdinalIgnoreCase);
            bool versionChanged = _lastDashboardChartVersion != version;
            bool quantChanged = _lastDashboardChartQuantVersion != _lastQuantVersion;
            bool csvChanged = _lastDashboardChartBarCount != _dailyBars.Count;
            bool intervalElapsed = (now - _lastDashboardChartRefresh).TotalMilliseconds >= DashboardChartRefreshMs;
            bool firstRender = _lastDashboardChartRefresh == DateTimeOffset.MinValue;

            if (!firstRender && !assetChanged && !quantChanged && !csvChanged && !(versionChanged && intervalElapsed))
            {
                return;
            }

            DashboardChartControl.SetData(_dailyBars, CurrentSnapshotForCalc(), _result);
            _lastDashboardChartRefresh = now;
            _lastDashboardChartVersion = version;
            _lastDashboardChartQuantVersion = _lastQuantVersion;
            _lastDashboardChartBarCount = _dailyBars.Count;
            _lastDashboardChartAsset = focused;
        }

        private List<DomRow> BuildDashboardDomRows(MarketSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return new List<DomRow>();
            }

            List<KeyLevel> levels = new List<KeyLevel>();

            if (_result != null)
            {
                levels.AddRange(_result.KeyLevels);
                levels.AddRange(_result.Confluence);
            }
            else
            {
                levels.AddRange(BasicLevels(snapshot));
            }

            levels.AddRange(FlowProfileKeyLevels(snapshot));
            levels.AddRange(FlowSignalKeyLevels(snapshot));

            int eachSide = Math.Max(4, Math.Min(10, _config.Ui.DomTicksEachSide));
            return DomLadderModel.Build(snapshot, levels, _config.Rtd.TickSize, eachSide);
        }

        private List<DashboardTapeRow> BuildDashboardTapeRows(MarketSnapshot snapshot)
        {
            List<DashboardTapeRow> rows = new List<DashboardTapeRow>();
            List<TimesTradeRow> realTimes = BuildTimesRows(snapshot);

            foreach (TimesTradeRow trade in realTimes.Take(60))
            {
                DashboardTapeRow row = new DashboardTapeRow();
                row.Time = EmptyToDash(trade.Data);
                row.Price = EmptyToDash(trade.Preco);
                row.Quantity = EmptyToDash(trade.Quantidade);
                row.Aggressor = EmptyToDash(trade.Agressor);
                row.Quality = "Times";
                rows.Add(row);
            }

            if (rows.Count > 0)
            {
                return rows;
            }

            string focused = FocusedAsset();

            foreach (TradePrint trade in _flowProcessor.GetTrades(focused, 60))
            {
                DashboardTapeRow row = new DashboardTapeRow();
                row.Time = trade.LocalTimeText;
                row.Price = trade.Price.ToString("N2", _ptBr);
                row.Quantity = trade.Quantity.ToString("N0", _ptBr);
                row.Aggressor = TranslateDirection(trade.Aggressor);
                row.Quality = trade.DataQuality.ToString();
                rows.Add(row);
            }

            if (rows.Count > 0)
            {
                return rows;
            }

            IEnumerable<TickEvent> ticks = _ticks.SnapshotNewestFirst();

            if (!string.IsNullOrWhiteSpace(focused))
            {
                ticks = ticks.Where(x => string.Equals(x.Asset, focused, StringComparison.OrdinalIgnoreCase));
            }

            foreach (TickEvent tick in ticks.Take(60))
            {
                DashboardTapeRow row = new DashboardTapeRow();
                row.Time = tick.LocalTimeText;
                row.Price = tick.Price.ToString("N2", _ptBr);
                row.Quantity = tick.Quantity.HasValue ? tick.Quantity.Value.ToString("N0", _ptBr) : "-";
                row.Aggressor = EmptyToDash(tick.Side);
                row.Quality = "Tick";
                rows.Add(row);
            }

            return rows;
        }

        private void UpdateDashboardProfile(MarketSnapshot snapshot, FlowMetrics metrics)
        {
            VolumeProfileMetrics flowProfile = metrics == null ? null : metrics.Profile;
            VolumeProfileResult proxy = _result == null ? null : _result.Profile;

            if (flowProfile != null && flowProfile.Poc.HasValue)
            {
                DashboardProfileText.Text = "POC " + flowProfile.Poc.Value.ToString("N2", _ptBr) + " | " + EmptyToDash(flowProfile.Source);
                DashboardProfileDetailText.Text = "VAH / VAL " + FormatDecimal(flowProfile.Vah, "N2") + " / " + FormatDecimal(flowProfile.Val, "N2") +
                                                  " | " + DistanceText(snapshot, flowProfile.Poc);
                return;
            }

            if (proxy != null && proxy.Poc != null)
            {
                DashboardProfileText.Text = "POC " + proxy.Poc.Price.ToString("N2", _ptBr) + " | CSV proxy";
                DashboardProfileDetailText.Text = "VAH / VAL " + proxy.Vah.ToString("N2", _ptBr) + " / " + proxy.Val.ToString("N2", _ptBr) +
                                                  " | " + DistanceText(snapshot, proxy.Poc.Price);
                return;
            }

            DashboardProfileText.Text = "POC -";
            DashboardProfileDetailText.Text = "VAH / VAL -";
        }

        private List<KeyLevel> BuildDashboardLevels(MarketSnapshot snapshot)
        {
            List<KeyLevel> levels = new List<KeyLevel>();

            if (_result != null)
            {
                levels.AddRange(_result.Confluence);
            }
            else
            {
                levels.AddRange(BasicLevels(snapshot));
            }

            levels.AddRange(FlowProfileKeyLevels(snapshot));
            levels.AddRange(FlowSignalKeyLevels(snapshot));

            return levels
                .OrderByDescending(x => x.Score)
                .ThenBy(x => Math.Abs(x.Distance))
                .Take(40)
                .ToList();
        }

        private void RenderDomBook(MarketSnapshot snapshot)
        {
            string focused = FocusedAsset();
            MarketSnapshot effective = snapshot ?? FocusedSnapshot();

            if (effective == null &&
                _lastSnapshot != null &&
                (string.IsNullOrWhiteSpace(focused) || string.Equals(_lastSnapshot.Asset, focused, StringComparison.OrdinalIgnoreCase)))
            {
                effective = _lastSnapshot;
            }

            if (effective == null)
            {
                if (DomGrid != null)
                {
                    DomGrid.ItemsSource = new List<DomRow>();
                }

                if (BookGrid != null)
                {
                    BookGrid.ItemsSource = new List<BookDepthRow>();
                }

                if (DomLevelsGrid != null)
                {
                    DomLevelsGrid.ItemsSource = new List<KeyLevel>();
                }

                RenderTape();
                UpdateDomBookState(focused, null, 0);
                return;
            }

            RenderDom(effective);

            List<BookDepthRow> bookRows = BuildBookRows(effective);
            if (BookGrid != null)
            {
                BookGrid.ItemsSource = bookRows;
            }

            RenderDomBookLevels(effective);
            RenderTape();
            UpdateDomBookState(focused, effective, bookRows.Count);
        }

        private void RenderDomBookLevels(MarketSnapshot snapshot)
        {
            if (DomLevelsGrid == null)
            {
                return;
            }

            List<KeyLevel> levels = new List<KeyLevel>();

            if (_result != null)
            {
                levels.AddRange(_result.Confluence);
            }
            else if (snapshot != null)
            {
                levels.AddRange(BasicLevels(snapshot));
            }

            levels.AddRange(FlowProfileKeyLevels(snapshot));
            levels.AddRange(FlowSignalKeyLevels(snapshot));

            DomLevelsGrid.ItemsSource = levels
                .OrderBy(x => Math.Abs(x.Distance))
                .Take(80)
                .ToList();
        }

        private void UpdateDomBookState(string focused, MarketSnapshot snapshot, int bookRows)
        {
            if (DomBookStateText == null)
            {
                return;
            }

            RtdAssetConfig asset = _config.Rtd.FindAsset(focused);
            string bookState = ChannelEnabled(focused, "Book") ? "book ligado" : "book desligado";
            string timesState = ChannelEnabled(focused, "Times") ? "times ligado" : "times desligado";
            string topic = asset == null ? "-" : EmptyToDash(asset.BookTopic);
            string age = snapshot == null ? "sem snapshot" : "snapshot " + AgeText(snapshot.LocalTimestamp);
            string rows = bookRows <= 0 ? "sem linhas book" : bookRows.ToString(_ptBr) + " linhas book";

            DomBookStateText.Text = EmptyToDash(focused) +
                                    " | canal " + topic +
                                    " | " + bookState +
                                    " | " + timesState +
                                    " | " + age +
                                    " | " + rows;
        }

        private void RenderTape()
        {
            string focused = FocusedAsset();
            MarketSnapshot snapshot = FocusedSnapshot();
            List<TimesTradeRow> realTimes = BuildTimesRows(snapshot);

            if (realTimes.Count > 0)
            {
                PostRealTimes(snapshot, realTimes);

                if (TapeGrid != null)
                {
                    TapeGrid.ItemsSource = realTimes;
                }

                if (FlowTapeGrid != null)
                {
                    FlowTapeGrid.ItemsSource = realTimes;
                }

                return;
            }

            List<TradePrint> flowTrades = _flowProcessor.GetTrades(focused, 250);

            if (flowTrades.Count > 0)
            {
                if (TapeGrid != null)
                {
                    TapeGrid.ItemsSource = flowTrades;
                }

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
            if (TapeGrid != null)
            {
                TapeGrid.ItemsSource = legacyTicks;
            }

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

        private void RenderRtdComplete(MarketSnapshot snapshot)
        {
            if (RtdCompleteGrid == null || RtdCompleteGroupList == null)
            {
                return;
            }

            string asset = FocusedAsset();
            DateTimeOffset now = DateTimeOffset.Now;
            HashSet<string> baselineFields = BuildRtdCompleteBaselineFields(asset);
            HashSet<string> extraFields = new HashSet<string>(_config.Rtd.GetRtdCompleteFields(asset), StringComparer.OrdinalIgnoreCase);
            HashSet<string> subscribedFields = new HashSet<string>(baselineFields, StringComparer.OrdinalIgnoreCase);
            subscribedFields.UnionWith(extraFields);

            List<RtdCompleteGroupRow> groups = BuildRtdCompleteGroupRows(subscribedFields, extraFields);
            _syncRtdCompleteGroupSelection = true;
            try
            {
                RtdCompleteGroupList.ItemsSource = groups;
                SelectRtdCompleteGroup(groups);
            }
            finally
            {
                _syncRtdCompleteGroupSelection = false;
            }

            string search = string.IsNullOrWhiteSpace(_rtdCompleteSearch) ? string.Empty : _rtdCompleteSearch.Trim();
            List<RtdCompleteFieldRow> rows = new List<RtdCompleteFieldRow>();

            foreach (RtdCompleteFieldInfo field in RtdCompleteFieldCatalog.Fields.OrderBy(x => RtdCompleteGroupRank(x.Group)).ThenBy(x => x.Priority))
            {
                if (!RtdCompleteMatchesSearch(field, search))
                {
                    continue;
                }

                bool subscribed = subscribedFields.Contains(field.Code);
                bool fromBaseline = baselineFields.Contains(field.Code);
                bool fromExtra = extraFields.Contains(field.Code);
                string value = RtdCompleteValue(snapshot, field.Code);
                DateTimeOffset updatedAt;
                bool hasUpdatedAt = TryGetRtdFieldUpdatedAt(snapshot, field.Code, out updatedAt);
                bool hasValue = !string.IsNullOrWhiteSpace(value);

                RtdCompleteFieldRow row = new RtdCompleteFieldRow();
                row.Field = field.Label;
                row.Code = field.Code;
                row.Value = EmptyToDash(value);
                row.Group = field.Group;
                row.Subgroup = field.Subgroup;
                row.Status = RtdCompleteStatus(subscribed, hasValue, hasUpdatedAt, updatedAt, now);
                row.Updated = hasUpdatedAt ? AgeText(updatedAt) : "-";
                row.Source = fromBaseline ? "Cotacao" : (fromExtra ? "RtdComplete" : "Nao assinado");
                row.Direction = RtdCompleteDirection(value);
                row.SortPriority = field.Priority;
                rows.Add(row);
            }

            RtdCompleteGrid.ItemsSource = rows;

            int loadedCount = RtdCompleteFieldCatalog.Fields.Count(x => subscribedFields.Contains(x.Code));
            string activeGroups = _activeRtdCompleteGroups.Count == 0
                ? "-"
                : string.Join(", ", RtdCompleteFieldCatalog.Groups.Where(x => _activeRtdCompleteGroups.Contains(x)).ToArray());

            if (RtdCompleteStatusText != null)
            {
                RtdCompleteStatusText.Text = "Ativo " + EmptyToDash(asset) + " | RTD " + EmptyToDash(_probeService.Status) + " | selecionado " + EmptyToDash(_selectedRtdCompleteGroup);
            }

            if (RtdCompleteLoadedText != null)
            {
                RtdCompleteLoadedText.Text = loadedCount.ToString(_ptBr) + "/" + RtdCompleteFieldCatalog.Fields.Count.ToString(_ptBr);
            }

            if (RtdCompleteGroupsText != null)
            {
                RtdCompleteGroupsText.Text = activeGroups;
            }

            if (RtdCompleteAgeText != null)
            {
                RtdCompleteAgeText.Text = snapshot == null ? "-" : AgeText(snapshot.LocalTimestamp);
            }

            if (RtdCompleteSourceText != null)
            {
                RtdCompleteSourceText.Text = extraFields.Count == 0 ? "Nao assinada" : extraFields.Count.ToString(_ptBr) + " extras";
            }

            if (RtdCompleteFilterText != null)
            {
                string filterText = string.IsNullOrWhiteSpace(search)
                    ? "Mostrando todos os campos do catalogo."
                    : "Filtro: " + search + " | " + rows.Count.ToString(_ptBr) + " campo(s).";
                RtdCompleteFilterText.Text = filterText;
            }
        }

        private List<RtdCompleteGroupRow> BuildRtdCompleteGroupRows(HashSet<string> subscribedFields, HashSet<string> extraFields)
        {
            List<RtdCompleteGroupRow> rows = new List<RtdCompleteGroupRow>();

            foreach (string group in RtdCompleteFieldCatalog.Groups)
            {
                List<RtdCompleteFieldInfo> fields = RtdCompleteFieldCatalog.Fields.Where(x => string.Equals(x.Group, group, StringComparison.OrdinalIgnoreCase)).ToList();
                int loaded = fields.Count(x => subscribedFields.Contains(x.Code));
                int extras = fields.Count(x => extraFields.Contains(x.Code));

                RtdCompleteGroupRow row = new RtdCompleteGroupRow();
                row.Group = group;
                row.Detail = _activeRtdCompleteGroups.Contains(group)
                    ? "ativo | extras " + extras.ToString(_ptBr)
                    : "sob demanda";
                row.CountText = loaded.ToString(_ptBr) + "/" + fields.Count.ToString(_ptBr);
                rows.Add(row);
            }

            return rows;
        }

        private void SelectRtdCompleteGroup(List<RtdCompleteGroupRow> groups)
        {
            if (RtdCompleteGroupList == null || groups == null || groups.Count == 0)
            {
                return;
            }

            RtdCompleteGroupRow selected = groups.FirstOrDefault(x => string.Equals(x.Group, _selectedRtdCompleteGroup, StringComparison.OrdinalIgnoreCase)) ?? groups[0];
            RtdCompleteGroupList.SelectedItem = selected;
        }

        private bool RtdCompleteMatchesSearch(RtdCompleteFieldInfo field, string search)
        {
            if (field == null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(search))
            {
                return true;
            }

            return ContainsIgnoreCase(field.Code, search) ||
                   ContainsIgnoreCase(field.Label, search) ||
                   ContainsIgnoreCase(field.Group, search) ||
                   ContainsIgnoreCase(field.Subgroup, search) ||
                   ContainsIgnoreCase(field.DisplayKind, search);
        }

        private static bool ContainsIgnoreCase(string text, string search)
        {
            return !string.IsNullOrWhiteSpace(text) &&
                   !string.IsNullOrWhiteSpace(search) &&
                   text.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private string RtdCompleteValue(MarketSnapshot snapshot, string code)
        {
            string raw = RawText(snapshot, code);

            if (!string.IsNullOrWhiteSpace(raw))
            {
                return raw;
            }

            return RtdText(snapshot, code);
        }

        private bool TryGetRtdFieldUpdatedAt(MarketSnapshot snapshot, string code, out DateTimeOffset updatedAt)
        {
            updatedAt = DateTimeOffset.MinValue;

            if (snapshot == null || snapshot.FieldUpdatedAt == null || string.IsNullOrWhiteSpace(code))
            {
                return false;
            }

            return snapshot.FieldUpdatedAt.TryGetValue(code, out updatedAt);
        }

        private string RtdCompleteStatus(bool subscribed, bool hasValue, bool hasUpdatedAt, DateTimeOffset updatedAt, DateTimeOffset now)
        {
            if (!subscribed)
            {
                return "Nao assinado";
            }

            if (!hasValue)
            {
                return "Sem valor";
            }

            if (hasUpdatedAt && (now - updatedAt).TotalSeconds > 60)
            {
                return "Desatualizado";
            }

            return "Ao vivo";
        }

        private string RtdCompleteDirection(string value)
        {
            decimal numeric;

            if (!ValueParser.ToDecimal(value).HasValue)
            {
                return "Neutro";
            }

            numeric = ValueParser.ToDecimal(value).Value;
            return numeric > 0m ? "Compra" : (numeric < 0m ? "Venda" : "Neutro");
        }

        private HashSet<string> BuildRtdCompleteBaselineFields(string asset)
        {
            HashSet<string> fields = new HashSet<string>(RtdConfig.DefaultQuoteFields, StringComparer.OrdinalIgnoreCase);

            foreach (string field in _config.Rtd.Fields ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(field))
                {
                    fields.Add(field.Trim().ToUpperInvariant());
                }
            }

            foreach (RtdSourceConfig source in _config.Rtd.GetEnabledSources())
            {
                if (source == null ||
                    !string.Equals(RtdConfig.NormalizeAsset(source.Asset), RtdConfig.NormalizeAsset(asset), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(source.Role, RtdConfig.RtdCompleteRole, StringComparison.OrdinalIgnoreCase) ||
                    source.IndexFrom.HasValue ||
                    source.IndexTo.HasValue)
                {
                    continue;
                }

                foreach (string field in source.Fields ?? new List<string>())
                {
                    if (!string.IsNullOrWhiteSpace(field))
                    {
                        fields.Add(field.Trim().ToUpperInvariant());
                    }
                }
            }

            return fields;
        }

        private void ActivateRtdCompleteGroup(string group)
        {
            if (string.IsNullOrWhiteSpace(group))
            {
                group = RtdCompleteFieldCatalog.GroupMarket;
            }

            if (!RtdCompleteFieldCatalog.Groups.Contains(group, StringComparer.OrdinalIgnoreCase))
            {
                SetWarnings(new[] { "Grupo RTD invalido: " + group });
                return;
            }

            _selectedRtdCompleteGroup = group;
            _activeRtdCompleteGroups.Add(group);
            ApplyRtdCompleteSubscriptions("Grupo " + group + " carregado");
        }

        private void ActivateAllRtdCompleteGroups()
        {
            foreach (string group in RtdCompleteFieldCatalog.Groups)
            {
                _activeRtdCompleteGroups.Add(group);
            }

            ApplyRtdCompleteSubscriptions("Todos os grupos carregados");
        }

        private void ClearRtdCompleteExtras()
        {
            string asset = FocusedAsset();
            _activeRtdCompleteGroups.Clear();
            _config.Rtd.ClearRtdCompleteSource(asset);
            RestartRtdAfterRtdCompleteChange("Extras RTD Completo limpos", 0);
            RenderRtdSources();
            RenderRtdComplete(FocusedSnapshot() ?? _lastSnapshot);
        }

        private void ApplyRtdCompleteSubscriptions(string reason)
        {
            string asset = FocusedAsset();
            HashSet<string> baselineFields = BuildRtdCompleteBaselineFields(asset);
            List<string> requested = RtdCompleteFieldCatalog.ForGroups(_activeRtdCompleteGroups)
                .Select(x => x.Code)
                .Where(x => !baselineFields.Contains(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (requested.Count == 0)
            {
                _config.Rtd.ClearRtdCompleteSource(asset);
            }
            else
            {
                _config.Rtd.SetRtdCompleteSource(asset, requested);
            }

            RestartRtdAfterRtdCompleteChange(reason, requested.Count);
            RenderRtdSources();
            RenderRtdComplete(FocusedSnapshot() ?? _lastSnapshot);
        }

        private void RestartRtdAfterRtdCompleteChange(string reason, int extraCount)
        {
            string detail = reason + " | " + extraCount.ToString(_ptBr) + " campo(s) extra(s).";
            AddHistory("RTD", "RTD Completo", detail);
            SetWarnings(new[] { detail });
            _log.Info("RTD Completo: " + detail);

            if (!_probeService.IsRunning)
            {
                return;
            }

            StatusText.Text = "restarting";
            StatusBadgeBorder.Background = StatusBrush("connecting");
            _probeService.Restart();
            ConnectButton.Content = "Desconectar";
        }

        private int RtdCompleteGroupRank(string group)
        {
            for (int i = 0; i < RtdCompleteFieldCatalog.Groups.Count; i++)
            {
                if (string.Equals(RtdCompleteFieldCatalog.Groups[i], group, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return 999;
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

                if (row.HasData() && TimesRowHasValidTradeData(row))
                {
                    rows.Add(row);
                }
            }

            return rows;
        }

        private bool TimesRowHasValidTradeData(TimesTradeRow row)
        {
            if (row == null)
            {
                return false;
            }

            decimal? price = ValueParser.ToDecimal(row.Preco);
            decimal? quantity = ValueParser.ToDecimal(row.Quantidade);

            if (!price.HasValue && !quantity.HasValue)
            {
                return false;
            }

            return !TimesRowLooksLikePlaceholder(row.Data) &&
                   !TimesRowLooksLikePlaceholder(row.Compradora) &&
                   !TimesRowLooksLikePlaceholder(row.Vendedora) &&
                   !TimesRowLooksLikePlaceholder(row.Agressor);
        }

        private bool TimesRowLooksLikePlaceholder(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.IndexOf("Ferramenta", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("Comando Inv", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void PostRealTimes(MarketSnapshot snapshot, List<TimesTradeRow> rows)
        {
            if (snapshot == null || rows == null || rows.Count == 0)
            {
                return;
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

                lock (_postedTimesLock)
                {
                    if (_postedTimesKeys.Count > 5000)
                    {
                        _postedTimesKeys.Clear();
                    }

                    if (_postedTimesKeys.Contains(key))
                    {
                        continue;
                    }

                    _postedTimesKeys.Add(key);
                }

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
                _heatmapProcessor.PostTrade(trade);
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

        private void CenteredTimesGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            DataGridTextColumn textColumn = e.Column as DataGridTextColumn;

            if (textColumn == null)
            {
                return;
            }

            Style textElementStyle = TryFindResource("CenteredDataGridTextElementStyle") as Style;
            Style editingElementStyle = TryFindResource("CenteredDataGridEditingElementStyle") as Style;
            Style cellStyle = TryFindResource("CenteredDataGridCellStyle") as Style;

            if (textElementStyle != null)
            {
                textColumn.ElementStyle = textElementStyle;
            }

            if (editingElementStyle != null)
            {
                textColumn.EditingElementStyle = editingElementStyle;
            }

            if (cellStyle != null)
            {
                textColumn.CellStyle = cellStyle;
            }
        }

        private bool SnapshotHasPrefixChanged(MarketSnapshot previousSnapshot, MarketSnapshot currentSnapshot, string prefix)
        {
            return SnapshotHasChanges(previousSnapshot, currentSnapshot, key => !string.IsNullOrWhiteSpace(key) && key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        private bool SnapshotHasAnyNonPrefixedChange(MarketSnapshot previousSnapshot, MarketSnapshot currentSnapshot, string[] excludedPrefixes)
        {
            return SnapshotHasChanges(previousSnapshot, currentSnapshot, key => !string.IsNullOrWhiteSpace(key) && !HasAnyPrefix(key, excludedPrefixes));
        }

        private bool SnapshotHasChanges(MarketSnapshot previousSnapshot, MarketSnapshot currentSnapshot, Func<string, bool> predicate)
        {
            if (currentSnapshot == null || currentSnapshot.Raw == null)
            {
                return false;
            }

            Dictionary<string, string> previousRaw = previousSnapshot == null ? null : previousSnapshot.Raw;
            Dictionary<string, string> currentRaw = currentSnapshot.Raw;
            HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, string> item in currentRaw)
            {
                if (predicate(item.Key))
                {
                    keys.Add(item.Key);
                }
            }

            if (previousRaw != null)
            {
                foreach (KeyValuePair<string, string> item in previousRaw)
                {
                    if (predicate(item.Key))
                    {
                        keys.Add(item.Key);
                    }
                }
            }

            foreach (string key in keys)
            {
                string previousValue = string.Empty;
                string currentValue = string.Empty;

                if (previousRaw != null)
                {
                    previousRaw.TryGetValue(key, out previousValue);
                }

                currentRaw.TryGetValue(key, out currentValue);

                if (!string.Equals(previousValue ?? string.Empty, currentValue ?? string.Empty, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private bool HasAnyPrefix(string key, string[] prefixes)
        {
            if (string.IsNullOrWhiteSpace(key) || prefixes == null || prefixes.Length == 0)
            {
                return false;
            }

            foreach (string prefix in prefixes)
            {
                if (!string.IsNullOrWhiteSpace(prefix) && key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
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
                NavigateToTab(TabAssets);
                RenderRtdReadiness();
                SetWarnings(new[] { "Cadastre e ligue pelo menos um ativo antes de conectar o RTD." });
                return;
            }

            if (subscriptions.Count == 0)
            {
                SetIdleDisconnectedStatus();
                NavigateToTab(TabAssets);
                RenderRtdReadiness();
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
            AddHistory("RTD", "Conectar", enabledAssets.Count.ToString(_ptBr) + " ativo(s), " + subscriptions.Count.ToString(_ptBr) + " assinatura(s).");
            _log.Info("Solicitando conexao RTD: " + enabledAssets.Count.ToString(_ptBr) + " ativo(s), " + subscriptions.Count.ToString(_ptBr) + " assinatura(s).");
            RenderRtdAssets();
            RenderRtdChannels();
            RenderRtdSources();
            RenderRtdReadiness();
            _probeService.Start();
        }

        private void StopRtd()
        {
            _probeService.Stop();
            ConnectButton.Content = "Conectar";
            AddHistory("RTD", "Parar", "Assinaturas RTD paradas pelo usuario.");
            RenderRtdAssets();
            RenderRtdChannels();
            RenderRtdReadiness();
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
            RenderRtdReadiness();
            RenderOpportunities(null);
            RenderHistory();
            UpdateWorkspaceContext();
            UpdateTopNavigation();
            UpdateRuntimeStatusBar(null);
            RenderActiveTab();
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
                                 " | Proc " + (Environment.Is64BitProcess ? "x64" : "x86") +
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

            UpdateWorkspaceContext();

            Brush selectedBackground = FindResource("InputBg") as Brush;
            Brush normalBackground = FindResource("Panel2") as Brush;
            Brush selectedBorder = FindResource("Accent") as Brush;
            Brush normalBorder = FindResource("Border") as Brush;
            Brush selectedForeground = FindResource("Accent") as Brush;
            Brush normalForeground = FindResource("Text") as Brush;

            UpdateTopNavigationButtons(TopNavigation, selectedBackground, normalBackground, selectedBorder, normalBorder, selectedForeground, normalForeground);
            UpdateTopNavigationGroupLabels(CurrentMainTabIndex(), selectedForeground, FindResource("Muted") as Brush);
        }

        private void UpdateTopNavigationGroupLabels(int index, Brush selectedForeground, Brush normalForeground)
        {
            string group = WorkspaceGroupName(index);
            Brush selected = selectedForeground ?? FindResource("Accent") as Brush ?? Brushes.LimeGreen;
            Brush normal = normalForeground ?? FindResource("Muted") as Brush ?? Brushes.Gray;

            SetTopNavigationGroupLabel(TopNavOperationLabel, group, "Operacao", selected, normal);
            SetTopNavigationGroupLabel(TopNavMarketLabel, group, "Mercado", selected, normal);
            SetTopNavigationGroupLabel(TopNavFlowLabel, group, "Fluxo", selected, normal);
            SetTopNavigationGroupLabel(TopNavAnalysisLabel, group, "Analise", selected, normal);
            SetTopNavigationGroupLabel(TopNavControlLabel, group, "Controle", selected, normal);
        }

        private void SetTopNavigationGroupLabel(TextBlock label, string currentGroup, string group, Brush selected, Brush normal)
        {
            if (label == null)
            {
                return;
            }

            bool isSelected = string.Equals(currentGroup, group, StringComparison.OrdinalIgnoreCase);
            label.Foreground = isSelected ? selected : normal;
            label.FontWeight = isSelected ? FontWeights.Bold : FontWeights.Normal;
        }

        private void UpdateTopNavigationButtons(DependencyObject root, Brush selectedBackground, Brush normalBackground, Brush selectedBorder, Brush normalBorder, Brush selectedForeground, Brush normalForeground)
        {
            if (root == null)
            {
                return;
            }

            System.Windows.Controls.Button button = root as System.Windows.Controls.Button;

            if (button != null)
            {
                int index;

                if (button.Tag != null && int.TryParse(button.Tag.ToString(), out index))
                {
                    bool selected = index == MainTabs.SelectedIndex;
                    button.Background = selected ? selectedBackground : normalBackground;
                    button.BorderBrush = selected ? selectedBorder : normalBorder;
                    button.Foreground = selected ? selectedForeground : normalForeground;
                    button.FontWeight = selected ? FontWeights.Bold : FontWeights.Normal;
                }

                return;
            }

            int children = VisualTreeHelper.GetChildrenCount(root);

            for (int i = 0; i < children; i++)
            {
                UpdateTopNavigationButtons(VisualTreeHelper.GetChild(root, i), selectedBackground, normalBackground, selectedBorder, normalBorder, selectedForeground, normalForeground);
            }
        }

        private void OpenFileWithShell(string path, string missingMessage)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    SetWarnings(new[] { missingMessage });
                    return;
                }

                System.Diagnostics.ProcessStartInfo info = new System.Diagnostics.ProcessStartInfo(path);
                info.UseShellExecute = true;
                System.Diagnostics.Process.Start(info);
                SetWarnings(new[] { "Arquivo aberto: " + path });
            }
            catch (Exception ex)
            {
                LastErrorText.Text = ex.GetType().Name + ": " + ex.Message;
                _log.Error("Falha ao abrir arquivo.", ex);
                SetWarnings(new[] { "Falha ao abrir arquivo: " + ex.Message });
            }
        }

        private void OpenFolderWithShell(string folder)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folder))
                {
                    folder = AppDomain.CurrentDomain.BaseDirectory;
                }

                Directory.CreateDirectory(folder);

                System.Diagnostics.ProcessStartInfo info = new System.Diagnostics.ProcessStartInfo(folder);
                info.UseShellExecute = true;
                System.Diagnostics.Process.Start(info);
                SetWarnings(new[] { "Pasta aberta: " + folder });
            }
            catch (Exception ex)
            {
                LastErrorText.Text = ex.GetType().Name + ": " + ex.Message;
                _log.Error("Falha ao abrir pasta.", ex);
                SetWarnings(new[] { "Falha ao abrir pasta: " + ex.Message });
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
                BookEnabledInput.IsChecked = true;
            }

            if (TimesEnabledInput != null)
            {
                TimesEnabledInput.IsChecked = true;
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

        private void InitializeCalculationDaysSelection()
        {
            if (CalculationDaysComboBox == null)
            {
                return;
            }

            _syncCalculationDaysSelection = true;

            try
            {
                CalculationDaysComboBox.SelectedIndex = CalculationDaysIndex(_config.Ui == null ? UiConfig.DefaultCalculationDays : _config.Ui.CalculationDays);
            }
            finally
            {
                _syncCalculationDaysSelection = false;
            }
        }

        private void InitializeChartTimeframeSelection()
        {
            if (ChartTimeframeDailyButton == null)
            {
                return;
            }

            _syncChartTimeframeSelection = true;

            try
            {
                int selectedIndex = UiConfig.NormalizeChartTimeframeIndex(_config.Ui == null ? UiConfig.DefaultChartTimeframeIndex : _config.Ui.ChartTimeframeIndex);

                if (ChartTimeframeDailyButton != null)
                {
                    ChartTimeframeDailyButton.IsChecked = selectedIndex == 0;
                }

                if (ChartTimeframeWeeklyButton != null)
                {
                    ChartTimeframeWeeklyButton.IsChecked = selectedIndex == 1;
                }

                if (ChartTimeframeMonthlyButton != null)
                {
                    ChartTimeframeMonthlyButton.IsChecked = selectedIndex == 2;
                }
            }
            finally
            {
                _syncChartTimeframeSelection = false;
            }

            ApplyChartDisplaySelection();
        }

        private void InitializeChartPriceGridSelection()
        {
            if (ChartPriceGrid10Button == null)
            {
                return;
            }

            _syncChartPriceGridSelection = true;

            try
            {
                int selectedTicks = UiConfig.NormalizePriceGridTickInterval(_config.Ui == null ? UiConfig.DefaultPriceGridTickInterval : _config.Ui.PriceGridTickInterval);

                if (ChartPriceGrid5Button != null)
                {
                    ChartPriceGrid5Button.IsChecked = selectedTicks == 5;
                }

                if (ChartPriceGrid10Button != null)
                {
                    ChartPriceGrid10Button.IsChecked = selectedTicks == 10;
                }

                if (ChartPriceGrid50Button != null)
                {
                    ChartPriceGrid50Button.IsChecked = selectedTicks == 50;
                }

                if (ChartPriceGrid100Button != null)
                {
                    ChartPriceGrid100Button.IsChecked = selectedTicks == 100;
                }
            }
            finally
            {
                _syncChartPriceGridSelection = false;
            }

            ApplyChartDisplaySelection();
        }

        private void InitializeChartCandleSpacingSelection()
        {
            if (ChartCandleSpacingComboBox == null)
            {
                return;
            }

            _syncChartCandleSpacingSelection = true;

            try
            {
                int selectedPercent = UiConfig.NormalizeCandleSpacingPercent(_config.Ui == null ? UiConfig.DefaultCandleSpacingPercent : _config.Ui.CandleSpacingPercent);
                ChartCandleSpacingComboBox.SelectedIndex = ChartCandleSpacingIndex(selectedPercent);
            }
            finally
            {
                _syncChartCandleSpacingSelection = false;
            }

            ApplyChartDisplaySelection();
        }

        private void InitializePtaxEditor()
        {
            _ptaxTradeDate = DateTime.Today;
            RefreshPtaxHistoryRows();
            LoadPtaxForDate(_ptaxTradeDate, false);
        }

        private void PtaxTodayButton_Click(object sender, RoutedEventArgs e)
        {
            LoadPtaxForDate(DateTime.Today, true);
        }

        private void PtaxLoadButton_Click(object sender, RoutedEventArgs e)
        {
            DateTime tradeDate;

            if (!TryReadPtaxDate(out tradeDate))
            {
                return;
            }

            LoadPtaxForDate(tradeDate, true);
        }

        private void PtaxSaveButton_Click(object sender, RoutedEventArgs e)
        {
            DateTime tradeDate;

            if (!TryReadPtaxDate(out tradeDate))
            {
                return;
            }

            decimal? value = ValueParser.ToDecimal(PtaxValueInput == null ? null : PtaxValueInput.Text);

            if (!value.HasValue || value.Value <= 0m)
            {
                SetPtaxEditorStatus("Informe um PTAX valido para salvar.", FindResource("Warn") as Brush);
                return;
            }

            try
            {
                _ptaxHistoryStore.Upsert(tradeDate, value.Value);
                _ptaxTradeDate = tradeDate.Date;
                _appliedPtaxValue = value.Value;
                RefreshPtaxHistoryRows();
                WritePtaxEditorFields(_ptaxTradeDate, _appliedPtaxValue);
                SetPtaxEditorStatus("PTAX aplicado e salvo para " + _ptaxTradeDate.ToString("dd/MM/yyyy", _ptBr) + ".", FindResource("Accent") as Brush);
                AddHistory("Niveis", "PTAX", "PTAX " + value.Value.ToString("N2", _ptBr) + " salvo para " + _ptaxTradeDate.ToString("dd/MM/yyyy", _ptBr) + ".");
                _log.Info("PTAX salvo: " + value.Value.ToString("N2", _ptBr) + " em " + _ptaxTradeDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".");
                Recalculate();
            }
            catch (Exception ex)
            {
                SetPtaxEditorStatus("Falha ao salvar PTAX: " + ex.Message, FindResource("Danger") as Brush);
                _log.Error("Falha ao salvar PTAX.", ex);
            }
        }

        private bool TryReadPtaxDate(out DateTime tradeDate)
        {
            tradeDate = DateTime.Today;
            string text = PtaxDateInput == null ? null : PtaxDateInput.Text;

            if (string.IsNullOrWhiteSpace(text))
            {
                tradeDate = DateTime.Today;
                return true;
            }

            DateTime parsed;

            if (DateTime.TryParseExact(text.Trim(), "dd/MM/yyyy", _ptBr, DateTimeStyles.None, out parsed) ||
                DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed) ||
                DateTime.TryParse(text.Trim(), _ptBr, DateTimeStyles.None, out parsed))
            {
                tradeDate = parsed.Date;
                return true;
            }

            SetPtaxEditorStatus("Data PTAX invalida. Use dd/MM/yyyy.", FindResource("Warn") as Brush);
            return false;
        }

        private void LoadPtaxForDate(DateTime tradeDate, bool recalculate)
        {
            try
            {
                PtaxHistoryEntry entry = _ptaxHistoryStore.Load(tradeDate.Date);
                _ptaxTradeDate = tradeDate.Date;
                _appliedPtaxValue = entry == null ? (decimal?)null : entry.Value;
                WritePtaxEditorFields(_ptaxTradeDate, _appliedPtaxValue);

                if (entry == null)
                {
                    SetPtaxEditorStatus("Sem PTAX salvo para " + _ptaxTradeDate.ToString("dd/MM/yyyy", _ptBr) + ".", FindResource("Warn") as Brush);
                }
                else
                {
                    SetPtaxEditorStatus("PTAX carregado de SQL local. Atualizado em " + entry.UpdatedUtc.ToLocalTime().ToString("dd/MM HH:mm", _ptBr) + ".", FindResource("Accent") as Brush);
                }

                if (recalculate)
                {
                    Recalculate();
                }
            }
            catch (Exception ex)
            {
                SetPtaxEditorStatus("Falha ao carregar PTAX: " + ex.Message, FindResource("Danger") as Brush);
                _log.Error("Falha ao carregar PTAX.", ex);
            }
        }

        private void WritePtaxEditorFields(DateTime tradeDate, decimal? value)
        {
            if (PtaxDateInput != null)
            {
                PtaxDateInput.Text = tradeDate.ToString("dd/MM/yyyy", _ptBr);
            }

            if (PtaxValueInput != null)
            {
                PtaxValueInput.Text = value.HasValue ? value.Value.ToString("N2", _ptBr) : string.Empty;
            }
        }

        private void SetPtaxEditorStatus(string text, Brush brush)
        {
            if (PtaxEditorStatusText == null)
            {
                return;
            }

            PtaxEditorStatusText.Text = EmptyToDash(text);
            PtaxEditorStatusText.Foreground = brush ?? (FindResource("Muted") as Brush ?? PtaxEditorStatusText.Foreground);
        }

        private void RefreshPtaxHistoryRows()
        {
            _ptaxHistoryRows.Clear();

            try
            {
                foreach (PtaxHistoryEntry entry in _ptaxHistoryStore.LoadAll())
                {
                    PtaxHistoryViewRow row = new PtaxHistoryViewRow();
                    row.Data = entry.TradeDate == DateTime.MinValue ? "-" : entry.TradeDate.ToString("dd/MM/yyyy", _ptBr);
                    row.Ptax = entry.Value.ToString("N2", _ptBr);
                    row.AtualizadoEm = entry.UpdatedUtc == DateTime.MinValue ? "-" : entry.UpdatedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", _ptBr);
                    _ptaxHistoryRows.Add(row);
                }
            }
            catch (Exception ex)
            {
                _log.Error("Falha ao atualizar grade de PTAX historico.", ex);
            }
        }

        private int SelectedCalculationDays()
        {
            if (CalculationDaysComboBox == null)
            {
                return UiConfig.NormalizeCalculationDays(_config.Ui == null ? UiConfig.DefaultCalculationDays : _config.Ui.CalculationDays);
            }

            return CalculationDaysFromIndex(CalculationDaysComboBox.SelectedIndex);
        }

        private int CalculationDaysIndex(int days)
        {
            int normalized = UiConfig.NormalizeCalculationDays(days);

            switch (normalized)
            {
                case 21:
                    return 0;
                case 45:
                    return 1;
                case 63:
                    return 2;
                case 90:
                    return 3;
                default:
                    return 1;
            }
        }

        private int CalculationDaysFromIndex(int index)
        {
            switch (index)
            {
                case 0:
                    return 21;
                case 1:
                    return 45;
                case 2:
                    return 63;
                case 3:
                    return 90;
                default:
                    return UiConfig.DefaultCalculationDays;
            }
        }

        private ChartTimeframe SelectedChartTimeframe()
        {
            int selectedIndex = UiConfig.NormalizeChartTimeframeIndex(_config.Ui == null ? UiConfig.DefaultChartTimeframeIndex : _config.Ui.ChartTimeframeIndex);
            return ChartTimeframeFromIndex(selectedIndex);
        }

        private void ApplyChartTimeframeSelection()
        {
            ApplyChartDisplaySelection();
        }

        private void ApplyChartDisplaySelection()
        {
            decimal tickSize = _config != null && _config.Rtd != null && _config.Rtd.TickSize > 0m ? _config.Rtd.TickSize : 0.5m;
            int selectedPriceGridTicks = SelectedChartPriceGridTicks();
            int selectedCandleSpacingPercent = SelectedChartCandleSpacingPercent();
            ChartTimeframe timeframe = SelectedChartTimeframe();
            bool showCandles = _config.Ui.ShowChartCandles;
            bool showPriceGrid = _config.Ui.ShowChartPriceGrid;
            bool showCurrentPrice = _config.Ui.ShowChartCurrentPriceLine;
            bool showConfluence = _config.Ui.ShowChartConfluenceLevels;
            bool showKeyLevels = _config.Ui.ShowChartKeyLevels;
            bool showRtdLevels = _config.Ui.ShowChartRtdLevels;
            bool showProfileLevels = _config.Ui.ShowChartProfileLevels;
            bool showTechnicalLevels = _config.Ui.ShowChartTechnicalLevels;
            bool showMarketLevels = _config.Ui.ShowChartMarketLevels;
            bool showPercentLevels = _config.Ui.ShowChartPercentLevels;
            bool showGarmanLevels = _config.Ui.ShowChartGarmanLevels;
            bool showGaussLevels = _config.Ui.ShowChartGaussLevels;
            bool showStdDevLevels = _config.Ui.ShowChartStdDevLevels;
            bool showGarchLevels = _config.Ui.ShowChartGarchLevels;

            if (DashboardChartControl != null)
            {
                DashboardChartControl.TickSize = tickSize;
                DashboardChartControl.PriceGridTickInterval = selectedPriceGridTicks;
                DashboardChartControl.CandleSpacingPercent = selectedCandleSpacingPercent;
                DashboardChartControl.Timeframe = timeframe;
                DashboardChartControl.ShowCandles = showCandles;
                DashboardChartControl.ShowPriceGrid = showPriceGrid;
                DashboardChartControl.ShowCurrentPriceLine = showCurrentPrice;
                DashboardChartControl.ShowConfluenceLevels = showConfluence;
                DashboardChartControl.ShowKeyLevels = showKeyLevels;
                DashboardChartControl.ShowRtdLevels = showRtdLevels;
                DashboardChartControl.ShowProfileLevels = showProfileLevels;
                DashboardChartControl.ShowTechnicalLevels = showTechnicalLevels;
                DashboardChartControl.ShowMarketLevels = showMarketLevels;
                DashboardChartControl.ShowPercentLevels = showPercentLevels;
                DashboardChartControl.ShowGarmanLevels = showGarmanLevels;
                DashboardChartControl.ShowGaussLevels = showGaussLevels;
                DashboardChartControl.ShowStdDevLevels = showStdDevLevels;
                DashboardChartControl.ShowGarchLevels = showGarchLevels;
                DashboardChartControl.InvalidateVisual();
            }

            if (ChartControl != null)
            {
                ChartControl.TickSize = tickSize;
                ChartControl.PriceGridTickInterval = selectedPriceGridTicks;
                ChartControl.CandleSpacingPercent = selectedCandleSpacingPercent;
                ChartControl.Timeframe = timeframe;
                ChartControl.ShowCandles = showCandles;
                ChartControl.ShowPriceGrid = showPriceGrid;
                ChartControl.ShowCurrentPriceLine = showCurrentPrice;
                ChartControl.ShowConfluenceLevels = showConfluence;
                ChartControl.ShowKeyLevels = showKeyLevels;
                ChartControl.ShowRtdLevels = showRtdLevels;
                ChartControl.ShowProfileLevels = showProfileLevels;
                ChartControl.ShowTechnicalLevels = showTechnicalLevels;
                ChartControl.ShowMarketLevels = showMarketLevels;
                ChartControl.ShowPercentLevels = showPercentLevels;
                ChartControl.ShowGarmanLevels = showGarmanLevels;
                ChartControl.ShowGaussLevels = showGaussLevels;
                ChartControl.ShowStdDevLevels = showStdDevLevels;
                ChartControl.ShowGarchLevels = showGarchLevels;
                ChartControl.InvalidateVisual();
            }
        }

        private void ChartZoomInButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyChartCommand(x => x.ZoomHorizontalSteps(1));
        }

        private void ChartZoomOutButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyChartCommand(x => x.ZoomHorizontalSteps(-1));
        }

        private void ChartPanLeftButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyChartCommand(x => x.PanHorizontalCandles(8));
        }

        private void ChartPanRightButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyChartCommand(x => x.PanHorizontalCandles(-8));
        }

        private void ChartPanUpButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyChartCommand(x => x.PanVerticalFraction(-0.12d));
        }

        private void ChartPanDownButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyChartCommand(x => x.PanVerticalFraction(0.12d));
        }

        private void ChartResetButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyChartCommand(x => x.ResetViewport());
        }

        private void ApplyChartCommand(Action<NativeChartControl> command)
        {
            if (command == null)
            {
                return;
            }

            if (DashboardChartControl != null)
            {
                command(DashboardChartControl);
            }

            if (ChartControl != null && !ReferenceEquals(ChartControl, DashboardChartControl))
            {
                command(ChartControl);
            }
        }

        private static int ParseChartTimeframeIndex(object tag)
        {
            if (tag == null)
            {
                return UiConfig.DefaultChartTimeframeIndex;
            }

            int parsed;
            return int.TryParse(Convert.ToString(tag, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                ? parsed
                : UiConfig.DefaultChartTimeframeIndex;
        }

        private static int ParseChartPriceGridTicks(object tag)
        {
            if (tag == null)
            {
                return UiConfig.DefaultPriceGridTickInterval;
            }

            int parsed;
            return int.TryParse(Convert.ToString(tag, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                ? parsed
                : UiConfig.DefaultPriceGridTickInterval;
        }

        private static int ParseChartCandleSpacingPercent(object tag)
        {
            if (tag == null)
            {
                return UiConfig.DefaultCandleSpacingPercent;
            }

            int parsed;
            return int.TryParse(Convert.ToString(tag, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                ? parsed
                : UiConfig.DefaultCandleSpacingPercent;
        }

        private ChartTimeframe ChartTimeframeFromIndex(int index)
        {
            switch (index)
            {
                case 1:
                    return ChartTimeframe.Weekly;
                case 2:
                    return ChartTimeframe.Monthly;
                default:
                    return ChartTimeframe.Daily;
            }
        }

        private int SelectedChartPriceGridTicks()
        {
            return UiConfig.NormalizePriceGridTickInterval(_config.Ui == null ? UiConfig.DefaultPriceGridTickInterval : _config.Ui.PriceGridTickInterval);
        }

        private int ChartCandleSpacingIndex(int percent)
        {
            int normalized = UiConfig.NormalizeCandleSpacingPercent(percent);

            switch (normalized)
            {
                case 75:
                    return 0;
                case 100:
                    return 1;
                case 125:
                    return 2;
                case 150:
                    return 3;
                default:
                    return 1;
            }
        }

        private int SelectedChartCandleSpacingPercent()
        {
            return UiConfig.NormalizeCandleSpacingPercent(_config.Ui == null ? UiConfig.DefaultCandleSpacingPercent : _config.Ui.CandleSpacingPercent);
        }

        private string ChartTimeframeText(ChartTimeframe timeframe)
        {
            switch (timeframe)
            {
                case ChartTimeframe.Weekly:
                    return "1W";
                case ChartTimeframe.Monthly:
                    return "1M";
                default:
                    return "1D";
            }
        }

        private MarketSnapshot FocusedSnapshot()
        {
            string focused = FocusedAsset();

            return SnapshotForAsset(focused);
        }

        private MarketSnapshot SnapshotForAsset(string asset)
        {
            string normalized = RtdConfig.NormalizeAsset(asset);

            lock (_snapshotsLock)
            {
                MarketSnapshot snapshot;

                if (!string.IsNullOrWhiteSpace(normalized) && _snapshotsByAsset.TryGetValue(normalized, out snapshot))
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
            RtdAssetRow selectedRow = null;

            try
            {
                RtdAssetsGrid.ItemsSource = rows;

                selectedRow = rows.FirstOrDefault(x => string.Equals(x.Asset, selected, StringComparison.OrdinalIgnoreCase)) ??
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

            if (selectedRow != null && ShouldSyncAssetForm())
            {
                PopulateAssetForm(selectedRow.Asset);
            }

            RtdAssetSummaryText.Text = enabled.Count.ToString(_ptBr) + " ligado(s), foco " + focused;
            UpdateFocusedAssetPanel();
        }

        private bool ShouldSyncAssetForm()
        {
            return !(AssetNameInput != null && AssetNameInput.IsKeyboardFocusWithin) &&
                   !(QuoteCodeInput != null && QuoteCodeInput.IsKeyboardFocusWithin) &&
                   !(BookTopicInput != null && BookTopicInput.IsKeyboardFocusWithin) &&
                   !(TimesTopicInput != null && TimesTopicInput.IsKeyboardFocusWithin) &&
                   !(CsvPathInput != null && CsvPathInput.IsKeyboardFocusWithin);
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

            UpdateFocusedAssetPanel();
        }

        private void UpdateFocusedAssetPanel()
        {
            if (FocusedAssetPanelText == null)
            {
                return;
            }

            string focused = FocusedAsset();
            RtdAssetConfig asset = _config.Rtd.FindAsset(focused);
            MarketSnapshot snapshot = null;

            if (!string.IsNullOrWhiteSpace(focused))
            {
                lock (_snapshotsLock)
                {
                    _snapshotsByAsset.TryGetValue(focused, out snapshot);
                }
            }

            FocusedAssetPanelText.Text = EmptyToDash(focused);

            string status = FocusedAssetStatus(asset, snapshot);
            FocusedAssetStatusText.Text = "Status " + status + " | ULT " + (snapshot == null ? "-" : FormatDecimal(snapshot.Ultimo, "N2"));
            FocusedAssetStatusText.Foreground = StateBrush(status);
            FocusedAssetCsvPanelText.Text = "CSV " + FocusedAssetCsvText(asset);

            UpdateChannelPanel(asset, snapshot, "Cotacao", QuoteChannelBorder, QuoteChannelStatusText, QuoteChannelTopicText, QuoteChannelToggleButton);
            UpdateChannelPanel(asset, snapshot, "Book", BookChannelBorder, BookChannelStatusText, BookChannelTopicText, BookChannelToggleButton);
            UpdateChannelPanel(asset, snapshot, "Times", TimesChannelBorder, TimesChannelStatusText, TimesChannelTopicText, TimesChannelToggleButton);
            RenderRtdReadiness();
        }

        private void RenderRtdReadiness()
        {
            if (RtdReadinessGrid == null || RtdReadinessSummaryText == null || RtdReadinessCountsText == null)
            {
                return;
            }

            _config.Rtd.NormalizeSources();

            string focused = FocusedAsset();
            RtdAssetConfig asset = _config.Rtd.FindAsset(focused);
            List<string> enabledAssets = _config.Rtd.GetEnabledAssets();
            List<RtdSubscriptionSpec> subscriptions = _config.Rtd.GetSubscriptions();
            List<RtdSubscriptionSpec> focusedSubscriptions = subscriptions
                .Where(x => string.Equals(x.Asset, focused, StringComparison.OrdinalIgnoreCase))
                .ToList();
            MarketSnapshot snapshot = FocusedSnapshot();

            RtdReadinessGrid.ItemsSource = BuildRtdReadinessRows(asset, snapshot, focusedSubscriptions);

            string summary = ReadinessSummary(asset, focusedSubscriptions);
            RtdReadinessSummaryText.Text = summary;
            RtdReadinessSummaryText.Foreground = StateBrush(summary);
            RtdReadinessCountsText.Text =
                "Ativos ligados " + enabledAssets.Count.ToString(_ptBr) +
                " | Assinaturas totais " + subscriptions.Count.ToString(_ptBr) +
                " | Foco " + focusedSubscriptions.Count.ToString(_ptBr) +
                " | ProgId " + EmptyToDash(_config.Rtd.ProgId) +
                Environment.NewLine +
                "Excel: RTD(\"ProgId\";; topico; campo; indice)";
        }

        private List<RtdReadinessRow> BuildRtdReadinessRows(RtdAssetConfig asset, MarketSnapshot snapshot, List<RtdSubscriptionSpec> focusedSubscriptions)
        {
            List<RtdReadinessRow> rows = new List<RtdReadinessRow>();
            List<RtdSubscriptionSpec> subscriptions = focusedSubscriptions ?? new List<RtdSubscriptionSpec>();

            AddRtdReadinessRow(rows, asset, snapshot, subscriptions, "Cotacao", "PriceVolume", null, "ULT");
            AddRtdReadinessRow(rows, asset, snapshot, subscriptions, "Book", "BookDepth", "TopBook", "OCP");
            AddRtdReadinessRow(rows, asset, snapshot, subscriptions, "Times", "TimesAndTrades", null, "PRE");

            return rows;
        }

        private void AddRtdReadinessRow(
            List<RtdReadinessRow> rows,
            RtdAssetConfig asset,
            MarketSnapshot snapshot,
            List<RtdSubscriptionSpec> subscriptions,
            string channel,
            string role,
            string alternateRole,
            string sampleField)
        {
            bool enabled = asset != null && ChannelEnabled(asset.Asset, channel);
            int count = subscriptions.Count(x =>
                string.Equals(x.Role, role, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(alternateRole) && string.Equals(x.Role, alternateRole, StringComparison.OrdinalIgnoreCase)));

            RtdReadinessRow row = new RtdReadinessRow();
            row.Channel = channel;
            row.Topic = ChannelTopicText(asset, channel);
            row.SubscriptionCount = enabled ? count.ToString(_ptBr) : "off";
            row.Status = ChannelReadinessStatus(asset, enabled, count, snapshot);
            row.Formula = enabled ? RtdArgumentExample(asset, channel, sampleField) : "-";
            rows.Add(row);
        }

        private string ReadinessSummary(RtdAssetConfig asset, List<RtdSubscriptionSpec> focusedSubscriptions)
        {
            if (asset == null)
            {
                return "Sem ativo";
            }

            if (!asset.Enabled)
            {
                return "Ativo off";
            }

            if (focusedSubscriptions == null || focusedSubscriptions.Count == 0)
            {
                return "Sem canal";
            }

            if (!_probeService.IsRunning)
            {
                return "Pronto";
            }

            return EmptyToDash(_probeService.Status);
        }

        private string ChannelReadinessStatus(RtdAssetConfig asset, bool enabled, int subscriptions, MarketSnapshot snapshot)
        {
            if (asset == null)
            {
                return "-";
            }

            if (!asset.Enabled)
            {
                return "ativo off";
            }

            if (!enabled)
            {
                return "off";
            }

            if (subscriptions == 0)
            {
                return "sem assin.";
            }

            return ChannelState(asset, enabled, snapshot);
        }

        private string RtdArgumentExample(RtdAssetConfig asset, string channel, string field)
        {
            if (asset == null)
            {
                return "-";
            }

            string topic = ChannelTopicText(asset, channel);

            if (string.Equals(channel, "Book", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(channel, "Times", StringComparison.OrdinalIgnoreCase))
            {
                return topic + " | " + field + " | 0";
            }

            return topic + " | " + field;
        }

        private string FocusedAssetStatus(RtdAssetConfig asset, MarketSnapshot snapshot)
        {
            if (asset == null)
            {
                return "-";
            }

            if (!asset.Enabled)
            {
                return "desligado";
            }

            if (!_probeService.IsRunning)
            {
                return "pronto";
            }

            if (snapshot == null)
            {
                return "aguardando";
            }

            return EmptyToDash(snapshot.Status);
        }

        private string FocusedAssetCsvText(RtdAssetConfig asset)
        {
            if (asset == null || string.IsNullOrWhiteSpace(asset.CsvPath))
            {
                return "-";
            }

            return Path.GetFileName(asset.CsvPath);
        }

        private void UpdateChannelPanel(
            RtdAssetConfig asset,
            MarketSnapshot snapshot,
            string channel,
            Border border,
            TextBlock statusText,
            TextBlock topicText,
            Button toggleButton)
        {
            if (border == null || statusText == null || topicText == null || toggleButton == null)
            {
                return;
            }

            bool enabled = asset != null && ChannelEnabled(asset.Asset, channel);
            string state = ChannelState(asset, enabled, snapshot);
            Brush brush = StateBrush(state);

            statusText.Text = state;
            statusText.Foreground = brush;
            topicText.Text = ChannelTopicText(asset, channel);
            border.BorderBrush = brush;
            toggleButton.Content = enabled ? "Desligar" : "Ligar";
        }

        private string ChannelState(RtdAssetConfig asset, bool enabled, MarketSnapshot snapshot)
        {
            if (asset == null)
            {
                return "-";
            }

            if (!asset.Enabled)
            {
                return "ativo off";
            }

            if (!enabled)
            {
                return "desligado";
            }

            if (!_probeService.IsRunning)
            {
                return "pronto";
            }

            if (snapshot == null)
            {
                return "aguardando";
            }

            return "connected";
        }

        private string ChannelTopicText(RtdAssetConfig asset, string channel)
        {
            if (asset == null)
            {
                return "-";
            }

            if (string.Equals(channel, "Cotacao", StringComparison.OrdinalIgnoreCase))
            {
                return EmptyToDash(asset.QuoteCode);
            }

            if (string.Equals(channel, "Book", StringComparison.OrdinalIgnoreCase))
            {
                return EmptyToDash(asset.BookTopic);
            }

            if (string.Equals(channel, "Times", StringComparison.OrdinalIgnoreCase))
            {
                return EmptyToDash(asset.TimesTopic);
            }

            return "-";
        }

        private Brush StateBrush(string state)
        {
            if (string.Equals(state, "connected", StringComparison.OrdinalIgnoreCase))
            {
                return FindResource("Accent") as Brush ?? Brushes.LimeGreen;
            }

            if (string.Equals(state, "aguardando", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(state, "pronto", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(state, "connecting", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(state, "reconnecting", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(state, "restarting", StringComparison.OrdinalIgnoreCase))
            {
                return FindResource("Warn") as Brush ?? Brushes.Goldenrod;
            }

            if (string.Equals(state, "desligado", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(state, "ativo off", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(state, "idle", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(state, "-", StringComparison.OrdinalIgnoreCase))
            {
                return FindResource("Border") as Brush ?? Brushes.DimGray;
            }

            return FindResource("Danger") as Brush ?? Brushes.OrangeRed;
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
            decimal? dayVariation = snapshot.VariacaoPercentual;
            DayVariationText.Text = dayVariation.HasValue ? FormatSignedDecimal(dayVariation, "N2") + "%" : "-";
            DayHighText.Text = FormatDecimal(snapshot.Maxima, "N2");
            DayLowText.Text = FormatDecimal(snapshot.Minima, "N2");
            DayAmplitudeText.Text = FormatDecimal(snapshot.AmplitudeDia, "N2");
            OpenDayText.Text = FormatDecimal(snapshot.Abertura, "N2");
            OpenDistanceText.Text = snapshot.DistanciaAbertura.HasValue ? FormatSignedDecimal(snapshot.DistanciaAbertura, "N2") : "-";
            LowDistanceText.Text = snapshot.DistanciaMinima.HasValue ? FormatSignedDecimal(snapshot.DistanciaMinima, "N2") : "-";
            HighDistanceText.Text = snapshot.DistanciaMaxima.HasValue ? FormatSignedDecimal(snapshot.DistanciaMaxima, "N2") : "-";
            DayVariationText.Foreground = VariationBrush(dayVariation);
            DayHighText.Foreground = FindResource("Warn") as Brush ?? DayHighText.Foreground;
            DayLowText.Foreground = FindResource("Accent") as Brush ?? DayLowText.Foreground;
            DayAmplitudeText.Foreground = FindResource("Text") as Brush ?? DayAmplitudeText.Foreground;
            OpenDayText.Foreground = FindResource("Text") as Brush ?? OpenDayText.Foreground;
            OpenDistanceText.Foreground = VariationBrush(snapshot.DistanciaAbertura);
            LowDistanceText.Foreground = VariationBrush(snapshot.DistanciaMinima);
            HighDistanceText.Foreground = VariationBrush(snapshot.DistanciaMaxima);
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
                IEnumerable<TickEvent> focusedTicks = FilterFocusedTicks(FocusedAsset());
                var intradayBars = _intradayAggregator != null ? _intradayAggregator.GetBars(FocusedAsset(), _config.Garch.IntradayTimeframeSeconds) : null;
                _result = QuantEngine.Build(_dailyBars, calcSnapshot, _config.Rtd.TickSize, SelectedCalculationDays(), focusedTicks, intradayBars, _config.Garch);
                _lastQuantVersion = _lastVersion;
                RenderResult(calcSnapshot);
            }
            catch (Exception ex)
            {
                LastErrorText.Text = ex.GetType().Name + ": " + ex.Message;
            }
        }

        private IEnumerable<TickEvent> FilterFocusedTicks(string focusedAsset)
        {
            IEnumerable<TickEvent> ticks = _ticks.SnapshotNewestFirst();

            if (string.IsNullOrWhiteSpace(focusedAsset))
            {
                return ticks;
            }

            return ticks.Where(x => string.Equals(x.Asset, focusedAsset, StringComparison.OrdinalIgnoreCase));
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

            if (_appliedPtaxValue.HasValue && _appliedPtaxValue.Value > 0m)
            {
                snapshot.Rtd["PTAX"] = _appliedPtaxValue.Value;
                snapshot.Raw["PTAX"] = _appliedPtaxValue.Value.ToString("N2", _ptBr);
            }

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

            DomLevelsGrid.ItemsSource = _result.Confluence.OrderBy(x => Math.Abs(x.Distance)).Take(80).ToList();
            RenderLevelsWorkspace(snapshot);
            RenderVolumeProfile(snapshot, _flowProcessor.GetMetrics(FocusedAsset()));
            BacktestGrid.ItemsSource = _result.Backtest.ToList();
            MetricsList.ItemsSource = BuildMetricLines(_result);
            SetWarnings(_result.Warnings);
            RenderDom(snapshot);
            ChartControl.SetData(_dailyBars, snapshot, _result);

            if (CurrentMainTabIndex() == TabIndicators)
            {
                RenderIndicators(snapshot);
            }
            else if (CurrentMainTabIndex() == TabGarch)
            {
                RenderGarch(snapshot);
            }
        }

        private void RenderLevelsWorkspace(MarketSnapshot snapshot)
        {
            if (LevelsMapStateText == null ||
                PrimaryLevelsGrid == null ||
                ReferenceConfluenceGrid == null ||
                OpeningReferenceGrid == null ||
                ClosingReferenceGrid == null ||
                PocReferenceGrid == null ||
                AdjustmentReferenceGrid == null ||
                PtaxReferenceGrid == null ||
                PtaxHistoryGrid == null)
            {
                return;
            }

            if (_result == null)
            {
                PrimaryLevelsGrid.ItemsSource = null;
                ReferenceConfluenceGrid.ItemsSource = null;
                ClearReferenceTab(OpeningReferenceStateText, OpeningReferencePriceText, OpeningCurrentPriceText, OpeningReferenceDistanceText, OpeningMetricCards, OpeningReferenceGrid, "Carregue o CSV diario para montar os niveis.");
                ClearReferenceTab(ClosingReferenceStateText, ClosingReferencePriceText, ClosingCurrentPriceText, ClosingReferenceDistanceText, ClosingMetricCards, ClosingReferenceGrid, "Carregue o CSV diario para montar os niveis.");
                ClearReferenceTab(PocReferenceStateText, PocReferencePriceText, PocCurrentPriceText, PocReferenceDistanceText, PocMetricCards, PocReferenceGrid, "Carregue o CSV diario para montar os niveis.");
                ClearReferenceTab(AdjustmentReferenceStateText, AdjustmentReferencePriceText, AdjustmentCurrentPriceText, AdjustmentReferenceDistanceText, AdjustmentMetricCards, AdjustmentReferenceGrid, "Carregue o CSV diario para montar os niveis.");
                ClearReferenceTab(PtaxReferenceStateText, PtaxReferencePriceText, PtaxCurrentPriceText, PtaxReferenceDistanceText, PtaxMetricCards, PtaxReferenceGrid, "Carregue o CSV diario para montar os niveis.");
                PtaxHistoryGrid.ItemsSource = _ptaxHistoryRows.ToList();
                PtaxHistoryStateText.Text = _ptaxHistoryRows.Count == 0 ? "Sem PTAX salvo no SQL local." : _ptaxHistoryRows.Count.ToString(_ptBr) + " registro(s) de PTAX no SQL local.";
                LevelsMapStateText.Text = "Carregue o CSV diario para montar os niveis por referencia.";
                return;
            }

            MarketSnapshot effective = snapshot ?? CurrentSnapshotForCalc();
            LevelsMapStateText.Text = BuildLevelsWorkspaceStateText(effective);
            PrimaryLevelsGrid.ItemsSource = BuildLevelRows(_result.KeyLevels, effective);
            ReferenceConfluenceGrid.ItemsSource = BuildLevelRows(_result.Confluence, effective);
            RenderReferenceTab(effective, FindReferenceMap("opening"), OpeningReferenceStateText, OpeningReferencePriceText, OpeningCurrentPriceText, OpeningReferenceDistanceText, OpeningMetricCards, OpeningReferenceGrid, "Referencia de abertura indisponivel.");
            RenderReferenceTab(effective, FindReferenceMap("closing"), ClosingReferenceStateText, ClosingReferencePriceText, ClosingCurrentPriceText, ClosingReferenceDistanceText, ClosingMetricCards, ClosingReferenceGrid, "Referencia de fechamento indisponivel.");
            RenderReferenceTab(effective, FindReferenceMap("poc"), PocReferenceStateText, PocReferencePriceText, PocCurrentPriceText, PocReferenceDistanceText, PocMetricCards, PocReferenceGrid, "Referencia de POC indisponivel.");
            RenderReferenceTab(effective, FindReferenceMap("adjustment"), AdjustmentReferenceStateText, AdjustmentReferencePriceText, AdjustmentCurrentPriceText, AdjustmentReferenceDistanceText, AdjustmentMetricCards, AdjustmentReferenceGrid, "Referencia de ajuste indisponivel.");
            RenderReferenceTab(effective, FindReferenceMap("ptax"), PtaxReferenceStateText, PtaxReferencePriceText, PtaxCurrentPriceText, PtaxReferenceDistanceText, PtaxMetricCards, PtaxReferenceGrid, "Sem PTAX salvo para a data selecionada.");
            PtaxHistoryGrid.ItemsSource = _ptaxHistoryRows.ToList();
            PtaxHistoryStateText.Text = _ptaxHistoryRows.Count == 0
                ? "Sem PTAX salvo no SQL local."
                : _ptaxHistoryRows.Count.ToString(_ptBr) + " registro(s) carregados do SQL local.";
        }

        private void ClearReferenceTab(TextBlock stateText, TextBlock referenceText, TextBlock currentText, TextBlock distanceText, ItemsControl metricCards, System.Windows.Controls.DataGrid grid, string stateMessage)
        {
            if (stateText != null)
            {
                stateText.Text = EmptyToDash(stateMessage);
            }

            if (referenceText != null)
            {
                referenceText.Text = "-";
            }

            if (currentText != null)
            {
                currentText.Text = "-";
            }

            if (distanceText != null)
            {
                distanceText.Text = "-";
                distanceText.Foreground = FindResource("Muted") as Brush ?? distanceText.Foreground;
            }

            if (metricCards != null)
            {
                metricCards.ItemsSource = null;
            }

            if (grid != null)
            {
                grid.ItemsSource = null;
            }
        }

        private string BuildLevelsWorkspaceStateText(MarketSnapshot snapshot)
        {
            string text = EmptyToDash(FocusedAsset()) +
                          " | snapshot " + (snapshot == null ? "-" : AgeText(snapshot.LocalTimestamp)) +
                          " | janela " + SelectedCalculationDays().ToString(_ptBr) + " dias" +
                          " | RTD " + EmptyToDash(_probeService.Status);

            if (_result != null)
            {
                text += " | GK " + MetricPointsText(_result.GarmanKlass == null ? 0m : _result.GarmanKlass.Points) +
                        " | Gauss " + MetricPointsText(_result.Gauss == null ? 0m : _result.Gauss.Points) +
                        " | DP " + MetricPointsText(_result.StandardDeviation == null ? 0m : _result.StandardDeviation.Points);
            }

            if (_appliedPtaxValue.HasValue)
            {
                text += " | PTAX " + _appliedPtaxValue.Value.ToString("N2", _ptBr) + " @ " + _ptaxTradeDate.ToString("dd/MM", _ptBr);
            }

            return text;
        }

        private void RenderReferenceTab(
            MarketSnapshot snapshot,
            ReferenceMapResult map,
            TextBlock stateText,
            TextBlock referenceText,
            TextBlock currentText,
            TextBlock distanceText,
            ItemsControl metricCards,
            System.Windows.Controls.DataGrid grid,
            string unavailableText)
        {
            decimal currentPrice = ResolveLevelsCurrentPrice(snapshot);

            if (map == null)
            {
                ClearReferenceTab(stateText, referenceText, currentText, distanceText, metricCards, grid, unavailableText);
                return;
            }

            if (stateText != null)
            {
                stateText.Text = map.ReferencePrice > 0m
                    ? map.ReferenceLabel + " | " + EmptyToDash(map.ReferenceSource) + " | venda acima / compra abaixo"
                    : unavailableText + " | " + EmptyToDash(map.ReferenceSource);
            }

            if (referenceText != null)
            {
                referenceText.Text = map.ReferencePrice > 0m ? map.ReferencePrice.ToString("N2", _ptBr) : "-";
            }

            if (currentText != null)
            {
                currentText.Text = currentPrice > 0m ? currentPrice.ToString("N2", _ptBr) : "-";
            }

            if (distanceText != null)
            {
                decimal? referenceDistance = currentPrice > 0m && map.ReferencePrice > 0m
                    ? (decimal?)(currentPrice - map.ReferencePrice)
                    : null;
                distanceText.Text = FormatPoints(referenceDistance);
                distanceText.Foreground = VariationBrush(referenceDistance);
            }

            if (metricCards != null)
            {
                metricCards.ItemsSource = BuildReferenceMetricCards(map);
            }

            if (grid != null)
            {
                grid.ItemsSource = BuildReferenceComparisonRows(map, currentPrice);
            }
        }

        private ReferenceMapResult FindReferenceMap(string referenceKey)
        {
            if (_result == null || _result.ReferenceMaps == null)
            {
                return null;
            }

            return _result.ReferenceMaps.FirstOrDefault(x => string.Equals(x.ReferenceKey, referenceKey, StringComparison.OrdinalIgnoreCase));
        }

        private List<ReferenceMetricCardRow> BuildReferenceMetricCards(ReferenceMapResult map)
        {
            List<ReferenceMetricCardRow> rows = new List<ReferenceMetricCardRow>();
            string basis = "Base " + EmptyToDash(map == null ? null : map.ReferenceLabel) + " | " + EmptyToDash(map == null ? null : map.ReferenceSource);
            rows.Add(BuildReferenceMetricCard(map == null ? null : map.GarmanSummary, basis));
            rows.Add(BuildReferenceMetricCard(map == null ? null : map.GaussSummary, basis));
            rows.Add(BuildReferenceMetricCard(map == null ? null : map.StdDevSummary, basis));
            rows.Add(BuildReferenceMetricCard(map == null ? null : map.GarchSummary, basis));
            return rows;
        }

        private ReferenceMetricCardRow BuildReferenceMetricCard(ReferenceMetricSummary summary, string basis)
        {
            ReferenceMetricCardRow row = new ReferenceMetricCardRow();
            row.MetricLabel = summary == null ? "-" : EmptyToDash(summary.MetricLabel);
            row.PointsText = MetricPointsText(summary == null ? 0m : summary.Points);
            row.BasisText = EmptyToDash(basis);
            row.NearestSellPriceText = summary == null || summary.NearestSell == null ? "-" : summary.NearestSell.Price.ToString("N2", _ptBr);
            row.NearestSellDistanceText = summary == null || summary.NearestSell == null ? "-" : FormatPoints(summary.NearestSell.DistanceCurrent);
            row.NearestBuyPriceText = summary == null || summary.NearestBuy == null ? "-" : summary.NearestBuy.Price.ToString("N2", _ptBr);
            row.NearestBuyDistanceText = summary == null || summary.NearestBuy == null ? "-" : FormatPoints(summary.NearestBuy.DistanceCurrent);
            return row;
        }

        private List<ReferenceComparisonRow> BuildReferenceComparisonRows(ReferenceMapResult map, decimal currentPrice)
        {
            List<ReferenceComparisonRow> rows = new List<ReferenceComparisonRow>();

            if (map == null || map.ReferencePrice <= 0m)
            {
                ReferenceComparisonRow empty = new ReferenceComparisonRow();
                empty.Direction = "Neutro";
                empty.Side = "Ref";
                empty.Level = "Referencia indisponivel";
                empty.GarmanPrice = "-";
                empty.GarmanDistance = "-";
                empty.GaussPrice = "-";
                empty.GaussDistance = "-";
                empty.StdDevPrice = "-";
                empty.StdDevDistance = "-";
                empty.GarchPrice = "-";
                empty.GarchDistance = "-";
                empty.GarchScore = "-";
                rows.Add(empty);
                return rows;
            }

            for (int multiplier = 4; multiplier >= 1; multiplier--)
            {
                rows.Add(BuildReferenceComparisonRow("Venda", "+" + multiplier.ToString(_ptBr), "Venda", map.ReferencePrice, currentPrice,
                    FindDeviationLevel(map.GarmanLevels, multiplier),
                    FindDeviationLevel(map.GaussLevels, multiplier),
                    FindDeviationLevel(map.StdDevLevels, multiplier),
                    FindDeviationLevel(map.GarchLevels, multiplier)));
            }

            rows.Add(BuildReferenceComparisonRow("Ref", "Referencia", "Neutro", map.ReferencePrice, currentPrice, null, null, null, null));

            for (int multiplier = 1; multiplier <= 4; multiplier++)
            {
                rows.Add(BuildReferenceComparisonRow("Compra", "-" + multiplier.ToString(_ptBr), "Compra", map.ReferencePrice, currentPrice,
                    FindDeviationLevel(map.GarmanLevels, -multiplier),
                    FindDeviationLevel(map.GaussLevels, -multiplier),
                    FindDeviationLevel(map.StdDevLevels, -multiplier),
                    FindDeviationLevel(map.GarchLevels, -multiplier)));
            }

            return rows;
        }

        private ReferenceComparisonRow BuildReferenceComparisonRow(
            string side,
            string level,
            string direction,
            decimal referencePrice,
            decimal currentPrice,
            DeviationLevel garmanLevel,
            DeviationLevel gaussLevel,
            DeviationLevel stdDevLevel,
            DeviationLevel garchLevel)
        {
            ReferenceComparisonRow row = new ReferenceComparisonRow();
            row.Side = side;
            row.Level = level;
            row.Direction = direction;

            if (string.Equals(direction, "Neutro", StringComparison.OrdinalIgnoreCase))
            {
                row.GarmanPrice = referencePrice.ToString("N2", _ptBr);
                row.GarmanDistance = FormatPoints(referencePrice - currentPrice);
                row.GaussPrice = referencePrice.ToString("N2", _ptBr);
                row.GaussDistance = FormatPoints(referencePrice - currentPrice);
                row.StdDevPrice = referencePrice.ToString("N2", _ptBr);
                row.StdDevDistance = FormatPoints(referencePrice - currentPrice);
                row.GarchPrice = referencePrice.ToString("N2", _ptBr);
                row.GarchDistance = FormatPoints(referencePrice - currentPrice);
                row.GarchScore = "-";
                return row;
            }

            row.GarmanPrice = garmanLevel == null ? "-" : garmanLevel.Price.ToString("N2", _ptBr);
            row.GarmanDistance = garmanLevel == null ? "-" : FormatPoints(garmanLevel.DistanceCurrent);
            row.GaussPrice = gaussLevel == null ? "-" : gaussLevel.Price.ToString("N2", _ptBr);
            row.GaussDistance = gaussLevel == null ? "-" : FormatPoints(gaussLevel.DistanceCurrent);
            row.StdDevPrice = stdDevLevel == null ? "-" : stdDevLevel.Price.ToString("N2", _ptBr);
            row.StdDevDistance = stdDevLevel == null ? "-" : FormatPoints(stdDevLevel.DistanceCurrent);
            row.GarchPrice = garchLevel == null ? "-" : garchLevel.Price.ToString("N2", _ptBr);
            row.GarchDistance = garchLevel == null ? "-" : FormatPoints(garchLevel.DistanceCurrent);
            row.GarchScore = garchLevel == null || garchLevel.Score <= 0d ? "-" : garchLevel.Score.ToString("N0", _ptBr);
            return row;
        }

        private DeviationLevel FindDeviationLevel(IEnumerable<DeviationLevel> levels, decimal sigma)
        {
            return (levels ?? new List<DeviationLevel>())
                .FirstOrDefault(x => x != null && x.Sigma == sigma);
        }

        private List<LevelsMapRow> BuildOpeningMapRows(MarketSnapshot snapshot)
        {
            List<LevelsMapRow> rows = new List<LevelsMapRow>();

            if (_result == null || _result.Intraday == null)
            {
                return rows;
            }

            decimal currentPrice = ResolveLevelsCurrentPrice(snapshot);
            decimal openPrice = _result.Intraday.Open;

            rows.Add(NewLevelsMapRow("Opening", "Abertura", "Abertura do dia", "Abertura (RTD)", openPrice, openPrice - currentPrice, 0m, "RTD", true));

            foreach (DeviationLevel level in (_result.OpeningLevels ?? new List<DeviationLevel>()).OrderByDescending(x => x.Price))
            {
                string zone = string.Equals(level.Side, "Venda", StringComparison.OrdinalIgnoreCase) ? "Sell" : "Buy";
                rows.Add(NewLevelsMapRow(zone, EmptyToDash(level.Side), EmptyToDash(level.Label), "Garman-Klass (HL/OC)", level.Price, level.Price - currentPrice, level.Price - openPrice, "Abertura", false));
            }

            return rows
                .OrderByDescending(x => x.SortPrice)
                .ThenBy(x => x.IsOpening ? 0 : 1)
                .ToList();
        }

        private List<LevelsMapRow> BuildDeviationRows(IEnumerable<DeviationLevel> levels, MarketSnapshot snapshot, string source)
        {
            List<LevelsMapRow> rows = new List<LevelsMapRow>();
            decimal currentPrice = ResolveLevelsCurrentPrice(snapshot);

            string metricLabel = LevelMetricLabel(source);

            foreach (DeviationLevel level in (levels ?? new List<DeviationLevel>()).OrderByDescending(x => x.Price))
            {
                string zone = string.Equals(level.Side, "Venda", StringComparison.OrdinalIgnoreCase)
                    ? "Sell"
                    : (string.Equals(level.Side, "Compra", StringComparison.OrdinalIgnoreCase) ? "Buy" : "Opening");
                rows.Add(NewLevelsMapRow(zone, EmptyToDash(level.Side), EmptyToDash(level.Label), metricLabel, level.Price, level.Price - currentPrice, level.DistanceReference, source, false));
            }

            return rows;
        }

        private LevelsMapRow NewLevelsMapRow(string zone, string side, string label, string metric, decimal price, decimal distanceCurrent, decimal distanceReference, string source, bool isOpening)
        {
            LevelsMapRow row = new LevelsMapRow();
            row.Zone = zone;
            row.Side = side;
            row.Label = label;
            row.Metric = EmptyToDash(metric);
            row.Price = price.ToString("N2", _ptBr);
            row.DistanceCurrent = FormatPoints(distanceCurrent);
            row.DistanceReference = FormatPoints(distanceReference);
            row.Source = source;
            row.IsOpening = isOpening;
            row.SortPrice = price;
            row.Direction = isOpening || !string.Equals(side, "Venda", StringComparison.OrdinalIgnoreCase) && !string.Equals(side, "Compra", StringComparison.OrdinalIgnoreCase)
                ? "Neutro"
                : (string.Equals(side, "Venda", StringComparison.OrdinalIgnoreCase) ? "Venda" : "Compra");
            return row;
        }

        private string LevelMetricLabel(string source)
        {
            if (string.Equals(source, "POC", StringComparison.OrdinalIgnoreCase))
            {
                return "Garman-Klass (base POC)";
            }

            if (string.Equals(source, "Desvio", StringComparison.OrdinalIgnoreCase))
            {
                return "Desvio padrao (STDEV.P H-L)";
            }

            if (string.Equals(source, "Gauss", StringComparison.OrdinalIgnoreCase))
            {
                return "Gauss robusto (YZ + MAD)";
            }

            if (string.Equals(source, "Abertura", StringComparison.OrdinalIgnoreCase))
            {
                return "Abertura (RTD)";
            }

            return EmptyToDash(source);
        }

        private List<NameValueRow> BuildLevelsSummaryRows(MarketSnapshot snapshot, List<LevelsMapRow> openingRows)
        {
            List<NameValueRow> rows = new List<NameValueRow>();
            decimal currentPrice = ResolveLevelsCurrentPrice(snapshot);
            decimal openPrice = _result == null || _result.Intraday == null ? 0m : _result.Intraday.Open;

            AddRow(rows, "Atual", currentPrice <= 0m ? "-" : currentPrice.ToString("N2", _ptBr), DescribeCurrentVsOpen(currentPrice, openPrice));
            AddRow(rows, "Abertura", openPrice <= 0m ? "-" : openPrice.ToString("N2", _ptBr), "linha central do mapa");

            AddMetricSummaryRows(rows, "Garman-Klass", _result == null ? null : _result.GarmanKlass, _result == null ? null : _result.OpeningLevels, currentPrice, "base abertura");
            AddMetricSummaryRows(rows, "Gauss", _result == null ? null : _result.Gauss, _result == null ? null : _result.GaussLevels, currentPrice, "base abertura");
            AddMetricSummaryRows(rows, "Desvio padrão", _result == null ? null : _result.StandardDeviation, _result == null ? null : _result.StandardDeviationLevels, currentPrice, "base abertura");

            AddRow(rows, "Faixa", OpeningBandText(openingRows), "amplitude entre a compra e a venda mais extremas");
            AddRow(rows, "Regime", EmptyToDash(_result.Regime), _result.Technicals == null ? "-" : EmptyToDash(_result.Technicals.TrendState));
            return rows;
        }

        private void UpdateLevelsHeader(MarketSnapshot snapshot, List<LevelsMapRow> openingRows)
        {
            decimal currentPrice = ResolveLevelsCurrentPrice(snapshot);
            decimal openPrice = _result == null || _result.Intraday == null ? 0m : _result.Intraday.Open;

            string header = EmptyToDash(FocusedAsset()) +
                            " | snapshot " + (snapshot == null ? "-" : AgeText(snapshot.LocalTimestamp)) +
                            " | abertura no centro | RTD " + EmptyToDash(_probeService.Status) +
                            " | " + (openingRows == null ? 0 : openingRows.Count(x => !x.IsOpening)).ToString(_ptBr) + " nivel(is)";
            if (_result != null)
            {
                header += " | GK " + MetricPointsText(_result.GarmanKlass == null ? 0m : _result.GarmanKlass.Points) +
                          " | Gauss " + MetricPointsText(_result.Gauss == null ? 0m : _result.Gauss.Points) +
                          " | Desvio " + MetricPointsText(_result.StandardDeviation == null ? 0m : _result.StandardDeviation.Points);
            }

            LevelsMapStateText.Text = header;
            LevelsCurrentPriceText.Text = currentPrice <= 0m ? "-" : currentPrice.ToString("N2", _ptBr);
            LevelsOpenPriceText.Text = openPrice <= 0m ? "-" : openPrice.ToString("N2", _ptBr);
            LevelsOpenDistanceText.Text = openPrice <= 0m ? "-" : FormatPoints(currentPrice - openPrice);
            LevelsGkPointsText.Text = _result == null || _result.GarmanKlass == null ? "-" : MetricPointsText(_result.GarmanKlass.Points);
            UpdateNearestMetricRow(LevelsGkSellText, LevelsGkBuyText, _result == null ? null : _result.OpeningLevels, currentPrice);
            LevelsGaussPointsText.Text = _result == null || _result.Gauss == null ? "-" : MetricPointsText(_result.Gauss.Points);
            UpdateNearestMetricRow(LevelsGaussSellText, LevelsGaussBuyText, _result == null ? null : _result.GaussLevels, currentPrice);
            LevelsStdDevPointsText.Text = _result == null || _result.StandardDeviation == null ? "-" : MetricPointsText(_result.StandardDeviation.Points);
            UpdateNearestMetricRow(LevelsStdDevSellText, LevelsStdDevBuyText, _result == null ? null : _result.StandardDeviationLevels, currentPrice);
            LevelsOpenDistanceText.Foreground = VariationBrush(currentPrice - openPrice);
        }

        private string MetricPointsText(decimal points)
        {
            decimal rounded = Math.Round(points, 2, MidpointRounding.AwayFromZero);
            return rounded.ToString("N2", _ptBr) + " pts";
        }

        private void AddMetricSummaryRows(List<NameValueRow> rows, string metricName, VolatilityMetric metric, IEnumerable<DeviationLevel> levels, decimal currentPrice, string basis)
        {
            string label = NormalizeMetricLabel(metricName);
            string standardLabel = string.Equals(label, "Desvio padrão", StringComparison.OrdinalIgnoreCase) ? label : label + " padrão";
            AddRow(rows, standardLabel, MetricPointsText(metric == null ? 0m : metric.Points), basis);

            DeviationLevel nearestSell = NearestDeviationLevel(levels, "Venda", currentPrice);
            DeviationLevel nearestBuy = NearestDeviationLevel(levels, "Compra", currentPrice);

            AddRow(rows, label + " venda", nearestSell == null ? "-" : nearestSell.Price.ToString("N2", _ptBr), nearestSell == null ? "-" : FormatPoints(nearestSell.DistanceCurrent) + " | " + EmptyToDash(nearestSell.Label));
            AddRow(rows, label + " compra", nearestBuy == null ? "-" : nearestBuy.Price.ToString("N2", _ptBr), nearestBuy == null ? "-" : FormatPoints(nearestBuy.DistanceCurrent) + " | " + EmptyToDash(nearestBuy.Label));
        }

        private string NormalizeMetricLabel(string metricName)
        {
            if (string.IsNullOrWhiteSpace(metricName))
            {
                return "-";
            }

            if (string.Equals(metricName, "Desvio padrÃ£o", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(metricName, "Desvio padrao", StringComparison.OrdinalIgnoreCase))
            {
                return "Desvio padrão";
            }

            return metricName;
        }

        private DeviationLevel NearestDeviationLevel(IEnumerable<DeviationLevel> levels, string side, decimal currentPrice)
        {
            List<DeviationLevel> filtered = (levels ?? new List<DeviationLevel>())
                .Where(x => string.Equals(x.Side, side, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(x.Direction, side, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (filtered.Count == 0)
            {
                return null;
            }

            if (currentPrice > 0m)
            {
                List<DeviationLevel> directional = string.Equals(side, "Venda", StringComparison.OrdinalIgnoreCase)
                    ? filtered.Where(x => x.Price >= currentPrice).OrderBy(x => x.Price).ToList()
                    : filtered.Where(x => x.Price <= currentPrice).OrderByDescending(x => x.Price).ToList();

                if (directional.Count > 0)
                {
                    return directional.First();
                }
            }

            return filtered.OrderBy(x => Math.Abs(x.Price - currentPrice)).FirstOrDefault();
        }

        private void UpdateNearestMetricRow(TextBlock sellText, TextBlock buyText, IEnumerable<DeviationLevel> levels, decimal currentPrice)
        {
            if (sellText != null)
            {
                sellText.Text = FormatNearestMetricLevel(NearestDeviationLevel(levels, "Venda", currentPrice));
            }

            if (buyText != null)
            {
                buyText.Text = FormatNearestMetricLevel(NearestDeviationLevel(levels, "Compra", currentPrice));
            }
        }

        private string FormatNearestMetricLevel(DeviationLevel level)
        {
            if (level == null)
            {
                return "-";
            }

            return level.Price.ToString("N2", _ptBr) + " | " + FormatPoints(level.DistanceCurrent);
        }

        private List<OpportunityLevelRow> BuildLevelRows(IEnumerable<KeyLevel> levels, MarketSnapshot snapshot)
        {
            List<OpportunityLevelRow> rows = new List<OpportunityLevelRow>();
            decimal currentPrice = ResolveLevelsCurrentPrice(snapshot);

            foreach (KeyLevel level in (levels ?? new List<KeyLevel>()).OrderBy(x => Math.Abs(x.Price - currentPrice)).ThenByDescending(x => x.Score))
            {
                OpportunityLevelRow row = new OpportunityLevelRow();
                row.Price = level.Price.ToString("N2", _ptBr);
                row.Label = EmptyToDash(level.Label);
                row.Source = EmptyToDash(level.Source);
                row.Score = level.Score.ToString("N0", _ptBr);
                row.Distance = FormatPoints(level.Price - currentPrice);
                row.Evidence = EmptyToDash(level.Evidence);
                row.Direction = EmptyToDash(level.Direction);
                rows.Add(row);
            }

            return rows;
        }

        private decimal ResolveLevelsCurrentPrice(MarketSnapshot snapshot)
        {
            if (snapshot != null && snapshot.Ultimo.HasValue && snapshot.Ultimo.Value > 0m)
            {
                return snapshot.Ultimo.Value;
            }

            if (_result != null && _result.Intraday != null && _result.Intraday.Price > 0m)
            {
                return _result.Intraday.Price;
            }

            return 0m;
        }

        private string DescribeCurrentVsOpen(decimal currentPrice, decimal openPrice)
        {
            if (currentPrice <= 0m || openPrice <= 0m)
            {
                return "-";
            }

            decimal distance = currentPrice - openPrice;

            if (Math.Abs(distance) <= _config.Rtd.TickSize)
            {
                return "preco em teste da abertura";
            }

            return distance > 0m ? "preco acima da abertura" : "preco abaixo da abertura";
        }

        private string OpeningBandText(List<LevelsMapRow> openingRows)
        {
            if (openingRows == null || openingRows.Count == 0)
            {
                return "-";
            }

            LevelsMapRow highest = openingRows.OrderByDescending(x => x.SortPrice).FirstOrDefault(x => !x.IsOpening);
            LevelsMapRow lowest = openingRows.OrderBy(x => x.SortPrice).FirstOrDefault(x => !x.IsOpening);

            if (highest == null || lowest == null)
            {
                return "-";
            }

            return FormatPoints(highest.SortPrice - lowest.SortPrice);
        }

        private void RenderRisk(MarketSnapshot snapshot)
        {
            if (RiskSummaryGrid == null || RiskChecklistGrid == null || RiskChannelsGrid == null || RiskSignalsGrid == null)
            {
                return;
            }

            string focused = FocusedAsset();
            RtdAssetConfig asset = _config.Rtd.FindAsset(focused);
            MarketSnapshot effective = snapshot ?? FocusedSnapshot();

            if (effective == null &&
                _lastSnapshot != null &&
                (string.IsNullOrWhiteSpace(focused) || string.Equals(_lastSnapshot.Asset, focused, StringComparison.OrdinalIgnoreCase)))
            {
                effective = _lastSnapshot;
            }

            FlowMetrics metrics = _flowProcessor.GetMetrics(focused);
            List<FlowSignal> signals = _flowProcessor.GetSignals(focused, 250) ?? new List<FlowSignal>();
            List<AlertRow> alerts = BuildOperationalAlerts(effective, metrics, signals);
            string severity = HighestRiskSeverity(alerts);

            if (RiskStatusText != null)
            {
                RiskStatusText.Text = "Risco " + severity;
                RiskStatusText.Foreground = SeverityBrush(severity);
            }

            if (RiskStatusBorder != null)
            {
                RiskStatusBorder.BorderBrush = SeverityBrush(severity);
            }

            if (RiskDetailText != null)
            {
                int critical = alerts.Count(x => string.Equals(x.Severity, "Critico", StringComparison.OrdinalIgnoreCase));
                int warnings = alerts.Count(x => string.Equals(x.Severity, "Aviso", StringComparison.OrdinalIgnoreCase));
                RiskDetailText.Text = EmptyToDash(focused) +
                                      " | RTD " + EmptyToDash(_probeService.Status) +
                                      " | Criticos " + critical.ToString(_ptBr) +
                                      " | Avisos " + warnings.ToString(_ptBr) +
                                      " | Snapshot " + (effective == null ? "-" : AgeText(effective.LocalTimestamp));
            }

            RiskSummaryGrid.ItemsSource = BuildRiskSummaryRows(effective, metrics, alerts, signals);
            RiskChecklistGrid.ItemsSource = BuildRiskChecklistRows(effective, metrics, signals);
            RiskChannelsGrid.ItemsSource = BuildDashboardChannelRows(asset, effective);
            RiskSignalsGrid.ItemsSource = signals
                .OrderByDescending(x => x.LocalTimestamp)
                .ThenByDescending(x => x.Score)
                .Take(120)
                .ToList();
        }

        private List<NameValueRow> BuildRiskSummaryRows(MarketSnapshot snapshot, FlowMetrics metrics, List<AlertRow> alerts, List<FlowSignal> signals)
        {
            List<NameValueRow> rows = new List<NameValueRow>();
            List<AlertRow> source = alerts ?? new List<AlertRow>();
            List<FlowSignal> flowSignals = signals ?? new List<FlowSignal>();
            string focused = FocusedAsset();
            RtdAssetConfig asset = _config.Rtd.FindAsset(focused);

            AddRow(rows, "Estado", HighestRiskSeverity(source), "prioridade operacional");
            AddRow(rows, "RTD", EmptyToDash(_probeService.Status), "updates " + _probeService.UpdatesReceived.ToString(_ptBr));
            AddRow(rows, "Ativo", EmptyToDash(focused), asset == null ? "nao cadastrado" : "cadastrado");
            AddRow(rows, "Snapshot", snapshot == null ? "-" : AgeText(snapshot.LocalTimestamp), snapshot == null ? "sem dados" : EmptyToDash(snapshot.HoraProfit));
            AddRow(rows, "Fluxo", metrics == null ? "-" : metrics.DataQuality.ToString(), metrics == null ? "-" : (metrics.Derived ? "derivado" : "real"));
            AddRow(rows, "CSV", FocusedAssetCsvText(asset), _dailyBars.Count.ToString(_ptBr) + " pregoes");
            AddRow(rows, "Fila drop", _flowProcessor.Dropped.ToString(_ptBr), "fila bounded");
            AddRow(rows, "Sinais fortes", flowSignals.Count(x => x.Score >= _config.Flow.StrongSetupScoreThreshold).ToString(_ptBr), "score >= " + _config.Flow.StrongSetupScoreThreshold.ToString(_ptBr));
            AddRow(rows, "Criticos", source.Count(x => string.Equals(x.Severity, "Critico", StringComparison.OrdinalIgnoreCase)).ToString(_ptBr), "alertas ativos");
            AddRow(rows, "Avisos", source.Count(x => string.Equals(x.Severity, "Aviso", StringComparison.OrdinalIgnoreCase)).ToString(_ptBr), "alertas ativos");

            return rows;
        }

        private List<RiskRow> BuildRiskChecklistRows(MarketSnapshot snapshot, FlowMetrics metrics, List<FlowSignal> signals)
        {
            List<RiskRow> rows = new List<RiskRow>();
            string focused = FocusedAsset();
            RtdAssetConfig asset = _config.Rtd.FindAsset(focused);
            List<string> enabledAssets = _config.Rtd.GetEnabledAssets();
            List<RtdSubscriptionSpec> subscriptions = _config.Rtd.GetSubscriptions();
            List<FlowSignal> flowSignals = signals ?? new List<FlowSignal>();

            AddRiskRow(rows,
                enabledAssets.Count == 0 ? "Critico" : "Normal",
                "RTD",
                "Ativos ligados",
                enabledAssets.Count.ToString(_ptBr),
                ">= 1",
                enabledAssets.Count == 0 ? "Ligar ativo em Ativos" : "OK");

            AddRiskRow(rows,
                subscriptions.Count == 0 ? "Critico" : "Normal",
                "RTD",
                "Assinaturas",
                subscriptions.Count.ToString(_ptBr),
                ">= 1 canal",
                subscriptions.Count == 0 ? "Ligar Cotacao, Book ou Times" : "OK");

            AddRiskRow(rows,
                _probeService.LastError == null ? "Normal" : "Critico",
                "RTD",
                "Ultimo erro",
                _probeService.LastError == null ? "-" : FormatError(_probeService.LastError),
                "-",
                _probeService.LastError == null ? "OK" : "Abrir Diagnostico");

            string snapshotSeverity = "Info";
            string snapshotValue = "-";
            string snapshotAction = "Aguardando dados";

            if (snapshot != null)
            {
                TimeSpan age = DateTimeOffset.Now - snapshot.LocalTimestamp;
                snapshotValue = AgeText(snapshot.LocalTimestamp);
                snapshotAction = "OK";

                if (age.TotalSeconds >= 15)
                {
                    snapshotSeverity = "Critico";
                    snapshotAction = "Reconectar RTD";
                }
                else if (age.TotalSeconds >= 5)
                {
                    snapshotSeverity = "Aviso";
                    snapshotAction = "Monitorar feed";
                }
                else
                {
                    snapshotSeverity = "Normal";
                }
            }
            else if (_probeService.IsRunning)
            {
                snapshotSeverity = "Aviso";
                snapshotAction = "Verificar ativo/canal";
            }

            AddRiskRow(rows, snapshotSeverity, "Mercado", "Idade snapshot", snapshotValue, "< 5 s", snapshotAction);

            AddRiskRow(rows,
                asset != null && ChannelEnabled(focused, "Cotacao") ? "Normal" : "Critico",
                "Canais",
                "Cotacao",
                ChannelTopicText(asset, "Cotacao"),
                "ligado",
                asset != null && ChannelEnabled(focused, "Cotacao") ? "OK" : "Ligar Cotacao");

            AddRiskRow(rows,
                asset != null && ChannelEnabled(focused, "Book") ? "Normal" : "Aviso",
                "Canais",
                "Book",
                ChannelTopicText(asset, "Book"),
                "ligado",
                asset != null && ChannelEnabled(focused, "Book") ? "OK" : "Ligar Book para DOM");

            AddRiskRow(rows,
                asset != null && ChannelEnabled(focused, "Times") ? "Normal" : "Aviso",
                "Canais",
                "Times",
                ChannelTopicText(asset, "Times"),
                "ligado",
                asset != null && ChannelEnabled(focused, "Times") ? "OK" : "Ligar Times para Tape real");

            string csvSeverity = "Normal";
            string csvAction = "OK";

            if (asset == null || string.IsNullOrWhiteSpace(asset.CsvPath))
            {
                csvSeverity = "Aviso";
                csvAction = "Selecionar CSV historico";
            }
            else if (!File.Exists(asset.CsvPath))
            {
                csvSeverity = "Aviso";
                csvAction = "Corrigir caminho CSV";
            }
            else if (_dailyBars.Count == 0)
            {
                csvSeverity = "Aviso";
                csvAction = "Carregar CSV do ativo";
            }

            AddRiskRow(rows, csvSeverity, "Historico", "CSV", FocusedAssetCsvText(asset), "carregado", csvAction);

            QuantSignal bestQuant = BestQuantSignalForAsset(focused, metrics);
            string edgeSeverity = "Info";
            string edgeAction = "Aguardando sinal quant";

            if (bestQuant != null)
            {
                if (QuantSignalHasPositiveEdge(bestQuant))
                {
                    edgeSeverity = "Normal";
                    edgeAction = "OK";
                }
                else if (QuantSignalHasUsableEdge(bestQuant))
                {
                    edgeSeverity = "Info";
                    edgeAction = "Usar como confluencia";
                }
                else
                {
                    edgeSeverity = "Aviso";
                    edgeAction = "Nao tratar como robusto";
                }
            }

            AddRiskRow(rows, edgeSeverity, "Quant", "Edge direcional", QuantEdgeValue(bestQuant), "exp > 0 e PF > 1,05", edgeAction);

            string qualitySeverity = "Info";
            string qualityValue = "-";
            string qualityAction = "Aguardando fluxo";

            if (metrics != null)
            {
                qualityValue = metrics.DataQuality.ToString();
                qualityAction = metrics.Derived ? "Confirmar Times/Book reais" : "OK";
                qualitySeverity = metrics.DataQuality == MarketDataQuality.TopOfBookOnly ? "Aviso" : "Normal";
            }

            AddRiskRow(rows, qualitySeverity, "Fluxo", "Qualidade", qualityValue, "Times/Book reais", qualityAction);

            AddRiskRow(rows,
                _flowProcessor.Dropped > 0 ? "Aviso" : "Normal",
                "Performance",
                "Fila drop",
                _flowProcessor.Dropped.ToString(_ptBr),
                "0",
                _flowProcessor.Dropped > 0 ? "Reduzir fontes ou revisar carga" : "OK");

            int strongSignals = flowSignals.Count(x => x.Score >= _config.Flow.StrongSetupScoreThreshold);
            AddRiskRow(rows,
                strongSignals > 0 ? "Aviso" : "Info",
                "Setups",
                "Sinais fortes",
                strongSignals.ToString(_ptBr),
                "operacional",
                strongSignals > 0 ? "Revisar Setups" : "Sem sinal forte");

            return rows
                .OrderBy(x => AlertSeverityRank(x.Severity))
                .ThenBy(x => x.Area)
                .ThenBy(x => x.Item)
                .ToList();
        }

        private void AddRiskRow(List<RiskRow> rows, string severity, string area, string item, string value, string limit, string action)
        {
            RiskRow row = new RiskRow();
            row.Severity = EmptyToDash(severity);
            row.Area = EmptyToDash(area);
            row.Item = EmptyToDash(item);
            row.Value = EmptyToDash(value);
            row.Limit = EmptyToDash(limit);
            row.Action = EmptyToDash(action);
            rows.Add(row);
        }

        private string HighestRiskSeverity(List<AlertRow> alerts)
        {
            List<AlertRow> source = alerts ?? new List<AlertRow>();

            if (source.Any(x => string.Equals(x.Severity, "Critico", StringComparison.OrdinalIgnoreCase)))
            {
                return "Critico";
            }

            if (source.Any(x => string.Equals(x.Severity, "Aviso", StringComparison.OrdinalIgnoreCase)))
            {
                return "Aviso";
            }

            return "Normal";
        }

        private void RenderAlerts(MarketSnapshot snapshot)
        {
            if (AlertsSummaryGrid == null || OperationalAlertsGrid == null || AlertSignalsGrid == null)
            {
                return;
            }

            string focused = FocusedAsset();
            FlowMetrics metrics = _flowProcessor.GetMetrics(focused);
            List<FlowSignal> signals = _flowProcessor.GetSignals(focused, 250) ?? new List<FlowSignal>();
            List<AlertRow> alerts = BuildOperationalAlerts(snapshot, metrics, signals);

            AlertsSummaryGrid.ItemsSource = BuildAlertsSummaryRows(snapshot, metrics, alerts, signals);
            OperationalAlertsGrid.ItemsSource = alerts;
            AlertSignalsGrid.ItemsSource = signals
                .OrderByDescending(x => x.LocalTimestamp)
                .ThenByDescending(x => x.Score)
                .Take(120)
                .ToList();
        }

        private List<NameValueRow> BuildAlertsSummaryRows(MarketSnapshot snapshot, FlowMetrics metrics, List<AlertRow> alerts, List<FlowSignal> signals)
        {
            List<NameValueRow> rows = new List<NameValueRow>();
            List<AlertRow> source = alerts ?? new List<AlertRow>();
            List<FlowSignal> flowSignals = signals ?? new List<FlowSignal>();

            AddRow(rows, "Criticos", source.Count(x => string.Equals(x.Severity, "Critico", StringComparison.OrdinalIgnoreCase)).ToString(_ptBr), "alertas ativos");
            AddRow(rows, "Avisos", source.Count(x => string.Equals(x.Severity, "Aviso", StringComparison.OrdinalIgnoreCase)).ToString(_ptBr), "alertas ativos");
            AddRow(rows, "Sinais fortes", flowSignals.Count(x => x.Score >= _config.Flow.StrongSetupScoreThreshold).ToString(_ptBr), "score >= " + _config.Flow.StrongSetupScoreThreshold.ToString(_ptBr));
            AddRow(rows, "RTD", EmptyToDash(_probeService.Status), "updates " + _probeService.UpdatesReceived.ToString(_ptBr));
            AddRow(rows, "Snapshot", snapshot == null ? "-" : AgeText(snapshot.LocalTimestamp), "ativo " + EmptyToDash(FocusedAsset()));
            AddRow(rows, "Qualidade", metrics == null ? "-" : metrics.DataQuality.ToString(), metrics == null ? "-" : metrics.Derived ? "derived" : "real");
            AddRow(rows, "CSV", FocusedAssetCsvText(_config.Rtd.FindAsset(FocusedAsset())), _dailyBars.Count.ToString(_ptBr) + " pregoes");
            AddRow(rows, "Fila drop", _flowProcessor.Dropped.ToString(_ptBr), "bounded queue");

            return rows;
        }

        private List<AlertRow> BuildOperationalAlerts(MarketSnapshot snapshot, FlowMetrics metrics, List<FlowSignal> signals)
        {
            List<AlertRow> rows = new List<AlertRow>();
            string focused = FocusedAsset();
            RtdAssetConfig asset = _config.Rtd.FindAsset(focused);
            List<string> enabledAssets = _config.Rtd.GetEnabledAssets();
            List<RtdSubscriptionSpec> subscriptions = _config.Rtd.GetSubscriptions();

            if (enabledAssets.Count == 0)
            {
                AddAlert(rows, "Critico", "RTD", focused, "Nenhum ativo ligado", "Ative um ativo em Ativos.", "-");
            }

            if (subscriptions.Count == 0)
            {
                AddAlert(rows, "Critico", "RTD", focused, "Nenhum canal ligado", "Ligue Cotacao, Book ou Times.", "-");
            }

            Exception lastError = _probeService.LastError;

            if (lastError != null)
            {
                AddAlert(rows, "Critico", "RTD", focused, "Erro RTD", FormatError(lastError), "-");
            }

            string status = _probeService.Status;

            if (string.Equals(status, "reconnecting", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "connecting", StringComparison.OrdinalIgnoreCase))
            {
                AddAlert(rows, "Aviso", "RTD", focused, "Conexao em andamento", EmptyToDash(status), "-");
            }

            if (_probeService.IsRunning && snapshot == null)
            {
                AddAlert(rows, "Aviso", "Mercado", focused, "Sem snapshot", "RTD ligado sem snapshot do ativo em foco.", "-");
            }

            if (snapshot != null)
            {
                TimeSpan age = DateTimeOffset.Now - snapshot.LocalTimestamp;

                if (age.TotalSeconds >= 15)
                {
                    AddAlert(rows, "Critico", "Mercado", focused, "Snapshot atrasado", AgeText(snapshot.LocalTimestamp), AgeText(snapshot.LocalTimestamp));
                }
                else if (age.TotalSeconds >= 5)
                {
                    AddAlert(rows, "Aviso", "Mercado", focused, "Snapshot lento", AgeText(snapshot.LocalTimestamp), AgeText(snapshot.LocalTimestamp));
                }
            }

            if (asset == null)
            {
                AddAlert(rows, "Critico", "Ativos", focused, "Ativo em foco ausente", "Cadastre ou selecione um ativo.", "-");
            }
            else if (string.IsNullOrWhiteSpace(asset.CsvPath))
            {
                AddAlert(rows, "Aviso", "CSV", asset.Asset, "CSV historico ausente", "Sem caminho salvo para o ativo.", "-");
            }
            else if (!File.Exists(asset.CsvPath))
            {
                AddAlert(rows, "Aviso", "CSV", asset.Asset, "CSV nao encontrado", asset.CsvPath, "-");
            }
            else if (_dailyBars.Count == 0)
            {
                AddAlert(rows, "Aviso", "CSV", asset.Asset, "CSV sem barras carregadas", Path.GetFileName(asset.CsvPath), "-");
            }

            if (_flowProcessor.Dropped > 0)
            {
                AddAlert(rows, "Aviso", "Fluxo", focused, "Eventos descartados", _flowProcessor.Dropped.ToString(_ptBr), "-");
            }

            if (metrics != null)
            {
                if (metrics.Derived)
                {
                    AddAlert(rows, "Info", "Fluxo", focused, "Tape derivado", metrics.DataQuality.ToString(), AgeText(metrics.LocalTimestamp));
                }

                if (metrics.DataQuality == MarketDataQuality.TopOfBookOnly)
                {
                    AddAlert(rows, "Aviso", "Fluxo", focused, "Dados limitados", "TopOfBookOnly", AgeText(metrics.LocalTimestamp));
                }
            }

            if (_result != null && _result.Warnings != null)
            {
                foreach (string warning in _result.Warnings.Where(x => !string.IsNullOrWhiteSpace(x)).Take(25))
                {
                    AddAlert(rows, "Aviso", "Calculo", focused, warning, "QuantEngine", "-");
                }
            }

            foreach (FlowSignal signal in (signals ?? new List<FlowSignal>()).OrderByDescending(x => x.LocalTimestamp).ThenByDescending(x => x.Score).Take(40))
            {
                string severity = signal.Score >= _config.Flow.StrongSetupScoreThreshold ? "Aviso" : "Info";
                AddAlert(rows, severity, "Setup", signal.Asset, signal.Setup + " " + signal.Direction, "Score " + signal.Score.ToString(_ptBr) + " | " + EmptyToDash(signal.Reasons), AgeText(signal.LocalTimestamp));
            }

            if (rows.Count == 0)
            {
                AddAlert(rows, "Info", "Sistema", focused, "Sem alertas operacionais", "RTD, fluxo e calculo sem alertas ativos.", "-");
            }

            return rows
                .OrderBy(x => AlertSeverityRank(x.Severity))
                .ThenBy(x => x.Source)
                .ThenBy(x => x.Message)
                .ToList();
        }

        private void AddAlert(List<AlertRow> rows, string severity, string source, string asset, string message, string detail, string ageText)
        {
            AlertRow row = new AlertRow();
            row.Severity = EmptyToDash(severity);
            row.Source = EmptyToDash(source);
            row.Asset = EmptyToDash(asset);
            row.Message = EmptyToDash(message);
            row.Detail = EmptyToDash(detail);
            row.AgeText = EmptyToDash(ageText);
            rows.Add(row);
        }

        private int AlertSeverityRank(string severity)
        {
            if (string.Equals(severity, "Critico", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (string.Equals(severity, "Aviso", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            return 2;
        }

        private string AgeText(DateTimeOffset timestamp)
        {
            TimeSpan age = DateTimeOffset.Now - timestamp;

            if (age.TotalSeconds < 1)
            {
                return Math.Max(0, (int)age.TotalMilliseconds).ToString(_ptBr) + " ms";
            }

            if (age.TotalMinutes < 1)
            {
                return Math.Max(0, age.TotalSeconds).ToString("N1", _ptBr) + " s";
            }

            return Math.Max(0, age.TotalMinutes).ToString("N1", _ptBr) + " min";
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

            if (result == null)
            {
                return lines;
            }

            int calculationDays = result.CalculationDays <= 0 ? SelectedCalculationDays() : result.CalculationDays;

            lines.Add("Janela calculo: " + calculationDays.ToString(_ptBr) + " dias");

            foreach (VolatilityMetric metric in result.Metrics)
            {
                lines.Add(MetricDisplayLabel(metric.Name) + " " + metric.Window + ": " + metric.Points.ToString("N1", _ptBr) + " pts (" + metric.Percent.ToString("N2", _ptBr) + "%)");
            }

            if (result.Profile != null && result.Profile.Poc != null)
            {
                lines.Add("POC proxy: " + result.Profile.Poc.Price.ToString("N2", _ptBr));
                lines.Add("VAH/VAL: " + result.Profile.Vah.ToString("N2", _ptBr) + " / " + result.Profile.Val.ToString("N2", _ptBr));
            }

            if (result.Technicals != null)
            {
                lines.Add("RSI14: " + FormatDecimal(result.Technicals.Rsi14, "N1") +
                          " | Z20: " + FormatDecimal(result.Technicals.ZScore20, "N2") +
                          " | ATR/VWAP: " + FormatDecimal(result.Technicals.AtrVwapDistance, "N2"));
                lines.Add("EMA9/21/50: " +
                          FormatDecimal(result.Technicals.Ema9, "N2") + " / " +
                          FormatDecimal(result.Technicals.Ema21, "N2") + " / " +
                          FormatDecimal(result.Technicals.Ema50, "N2"));
                lines.Add("Bollinger20: " +
                          FormatDecimal(result.Technicals.BollingerLower20, "N2") + " / " +
                          FormatDecimal(result.Technicals.BollingerMiddle20, "N2") + " / " +
                          FormatDecimal(result.Technicals.BollingerUpper20, "N2"));
                lines.Add("Tecnico: " + EmptyToDash(result.Technicals.TrendState) +
                          " | " + EmptyToDash(result.Technicals.ReversionState) +
                          " | amostra " + result.Technicals.SampleSize.ToString(_ptBr) +
                          " | janela " + calculationDays.ToString(_ptBr) + "d");
            }

            if (result.QuantSignals != null && result.QuantSignals.Count > 0)
            {
                foreach (QuantSignal signal in result.QuantSignals.OrderByDescending(x => x.Score).Take(3))
                {
                    lines.Add("Quant " + signal.Direction + " " + signal.Setup +
                              " score " + signal.Score.ToString(_ptBr) +
                              " | " + EmptyToDash(signal.LevelName) +
                              " | " + EmptyToDash(signal.StatisticalEdge));
                }
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

        private void RenderFlowMap(MarketSnapshot snapshot)
        {
            if (FlowMapGrid == null || FlowMapStateText == null)
            {
                return;
            }

            string focused = FocusedAsset();
            MarketSnapshot effective = snapshot ?? FocusedSnapshot();

            if (effective == null &&
                _lastSnapshot != null &&
                (string.IsNullOrWhiteSpace(focused) || string.Equals(_lastSnapshot.Asset, focused, StringComparison.OrdinalIgnoreCase)))
            {
                effective = _lastSnapshot;
            }

            FlowMetrics metrics = _flowProcessor.GetMetrics(focused);
            List<FlowSignal> signals = _flowProcessor.GetSignals(focused, 250) ?? new List<FlowSignal>();

            if (effective == null)
            {
                FlowMapGrid.ItemsSource = new List<FlowMapBookRow>();

                if (FlowMapSummaryGrid != null)
                {
                    FlowMapSummaryGrid.ItemsSource = BuildFlowMapSummaryRows(null, metrics, signals, 0, 0);
                }

                if (FlowMapLevelsGrid != null)
                {
                    FlowMapLevelsGrid.ItemsSource = new List<FlowMapLevelRow>();
                }

                if (FlowMapSignalsGrid != null)
                {
                    FlowMapSignalsGrid.ItemsSource = signals.OrderByDescending(x => x.LocalTimestamp).ThenByDescending(x => x.Score).Take(80).ToList();
                }

                UpdateFlowMapHeader(focused, null, metrics, 0, 0);
                return;
            }

            List<BookDepthRow> bookRows = BuildBookRows(effective);
            List<FlowMapBookRow> mapRows = BuildFlowMapRows(effective, metrics, bookRows);
            List<FlowMapLevelRow> levelRows = BuildFlowMapLevelRows(effective, metrics, signals);

            FlowMapGrid.ItemsSource = mapRows;

            if (FlowMapSummaryGrid != null)
            {
                FlowMapSummaryGrid.ItemsSource = BuildFlowMapSummaryRows(effective, metrics, signals, bookRows.Count, mapRows.Count);
            }

            if (FlowMapLevelsGrid != null)
            {
                FlowMapLevelsGrid.ItemsSource = levelRows;
            }

            if (FlowMapSignalsGrid != null)
            {
                FlowMapSignalsGrid.ItemsSource = signals.OrderByDescending(x => x.LocalTimestamp).ThenByDescending(x => x.Score).Take(80).ToList();
            }

            UpdateFlowMapHeader(focused, effective, metrics, bookRows.Count, mapRows.Count);
        }

        private void UpdateFlowMapHeader(string focused, MarketSnapshot snapshot, FlowMetrics metrics, int bookRows, int mapRows)
        {
            if (FlowMapAssetText != null)
            {
                FlowMapAssetText.Text = EmptyToDash(focused);
            }

            if (FlowMapDeltaText != null)
            {
                FlowMapDeltaText.Text = metrics == null ? "-" : metrics.CumulativeDelta.ToString("N0", _ptBr);
            }

            if (FlowMapBiasText != null)
            {
                FlowMapBiasText.Text = metrics == null
                    ? "-"
                    : "imb " + FormatDecimal(metrics.TopBookImbalance, "N3") + " | micro " + FormatDecimal(metrics.MicroBias, "N3");
            }

            string quality = metrics == null ? "-" : metrics.DataQuality + (metrics.Derived ? " derivado" : " real");
            string age = snapshot == null ? "sem snapshot" : "snapshot " + AgeText(snapshot.LocalTimestamp);

            FlowMapStateText.Text = EmptyToDash(focused) +
                                    " | " + age +
                                    " | book " + bookRows.ToString(_ptBr) +
                                    " | mapa " + mapRows.ToString(_ptBr) +
                                    " | qualidade " + quality +
                                    " | RTD " + EmptyToDash(_probeService.Status);
        }

        private List<NameValueRow> BuildFlowMapSummaryRows(MarketSnapshot snapshot, FlowMetrics metrics, List<FlowSignal> signals, int bookRows, int mapRows)
        {
            List<NameValueRow> rows = new List<NameValueRow>();
            string focused = FocusedAsset();
            RtdAssetConfig asset = _config.Rtd.FindAsset(focused);
            List<FlowSignal> flowSignals = signals ?? new List<FlowSignal>();

            AddRow(rows, "Ativo", EmptyToDash(focused), asset == null ? "nao cadastrado" : "cadastrado");
            AddRow(rows, "Snapshot", snapshot == null ? "-" : AgeText(snapshot.LocalTimestamp), snapshot == null ? "sem dados" : EmptyToDash(snapshot.HoraProfit));
            AddRow(rows, "Book", bookRows.ToString(_ptBr), ChannelEnabled(focused, "Book") ? "ligado" : "desligado");
            AddRow(rows, "Times", ChannelEnabled(focused, "Times") ? "ligado" : "desligado", "tape real quando disponivel");
            AddRow(rows, "Qualidade", metrics == null ? "-" : metrics.DataQuality.ToString(), metrics == null ? "-" : (metrics.Derived ? "derivado" : "real"));
            AddRow(rows, "Delta", metrics == null ? "-" : metrics.CumulativeDelta.ToString("N0", _ptBr), "sessao");
            AddRow(rows, "Imbalance", metrics == null ? "-" : FormatDecimal(metrics.TopBookImbalance, "N3"), "top book");
            AddRow(rows, "Microbias", metrics == null ? "-" : FormatDecimal(metrics.MicroBias, "N3"), "microprice - mid");
            AddRow(rows, "VWAP", metrics == null ? "-" : FormatDecimal(metrics.Vwap, "N2"), metrics == null ? "-" : "dist " + FormatDecimal(metrics.VwapDistance, "N2"));
            AddRow(rows, "Linhas mapa", mapRows.ToString(_ptBr), "janela perto do preco");
            AddRow(rows, "Sinais", flowSignals.Count.ToString(_ptBr), "ultimos sinais do ativo");

            return rows;
        }

        private List<FlowMapBookRow> BuildFlowMapRows(MarketSnapshot snapshot, FlowMetrics metrics, List<BookDepthRow> bookRows)
        {
            List<FlowMapBookRow> rows = new List<FlowMapBookRow>();

            if (snapshot == null)
            {
                return rows;
            }

            decimal tickSize = _config.Rtd.TickSize > 0m ? _config.Rtd.TickSize : 0.5m;
            decimal? centerValue = snapshot.Ultimo.HasValue ? snapshot.Ultimo : (metrics == null ? null : metrics.Price);

            if (!centerValue.HasValue && snapshot.OfertaCompra.HasValue && snapshot.OfertaVenda.HasValue)
            {
                centerValue = (snapshot.OfertaCompra.Value + snapshot.OfertaVenda.Value) / 2m;
            }

            if (!centerValue.HasValue)
            {
                return rows;
            }

            decimal center = DomLadderModel.RoundToTick(centerValue.Value, tickSize);
            Dictionary<decimal, decimal> bidByPrice = new Dictionary<decimal, decimal>();
            Dictionary<decimal, decimal> askByPrice = new Dictionary<decimal, decimal>();

            foreach (BookDepthRow book in bookRows ?? new List<BookDepthRow>())
            {
                AddFlowMapBookLevel(bidByPrice, book.Compra, book.QtdeCompra, tickSize);
                AddFlowMapBookLevel(askByPrice, book.Venda, book.QtdeVenda, tickSize);
            }

            if (bidByPrice.Count == 0 && snapshot.OfertaCompra.HasValue && snapshot.VolumeOfertaCompra.HasValue)
            {
                bidByPrice[DomLadderModel.RoundToTick(snapshot.OfertaCompra.Value, tickSize)] = snapshot.VolumeOfertaCompra.Value;
            }

            if (askByPrice.Count == 0 && snapshot.OfertaVenda.HasValue && snapshot.VolumeOfertaVenda.HasValue)
            {
                askByPrice[DomLadderModel.RoundToTick(snapshot.OfertaVenda.Value, tickSize)] = snapshot.VolumeOfertaVenda.Value;
            }

            List<KeyLevel> levels = BuildDashboardLevels(snapshot);
            Dictionary<decimal, List<KeyLevel>> levelsByPrice = new Dictionary<decimal, List<KeyLevel>>();
            decimal nearWindow = tickSize * 18m;

            foreach (KeyLevel level in levels)
            {
                decimal price = DomLadderModel.RoundToTick(level.Price, tickSize);
                decimal distance = Math.Abs(price - center);

                if (distance > nearWindow)
                {
                    continue;
                }

                if (!levelsByPrice.ContainsKey(price))
                {
                    levelsByPrice[price] = new List<KeyLevel>();
                }

                levelsByPrice[price].Add(level);
            }

            decimal maxVolume = 0m;

            foreach (decimal volume in bidByPrice.Values.Concat(askByPrice.Values))
            {
                if (volume > maxVolume)
                {
                    maxVolume = volume;
                }
            }

            if (maxVolume <= 0m)
            {
                maxVolume = 1m;
            }

            decimal min = center - nearWindow;
            decimal max = center + nearWindow;

            for (decimal price = max; price >= min; price -= tickSize)
            {
                decimal rounded = DomLadderModel.RoundToTick(price, tickSize);
                decimal bidVolume;
                decimal askVolume;
                List<KeyLevel> rowLevels;
                bidByPrice.TryGetValue(rounded, out bidVolume);
                askByPrice.TryGetValue(rounded, out askVolume);
                levelsByPrice.TryGetValue(rounded, out rowLevels);

                bool hasLevel = rowLevels != null && rowLevels.Count > 0;
                string levelsText = hasLevel ? string.Join(" | ", rowLevels.OrderByDescending(x => x.Score).Select(x => x.Label).Take(3).ToArray()) : string.Empty;
                string zone = FlowMapZone(rounded, center, hasLevel);

                FlowMapBookRow row = new FlowMapBookRow();
                row.Zone = zone;
                row.Price = rounded.ToString("N2", _ptBr);
                row.BidSize = bidVolume > 0m ? bidVolume.ToString("N0", _ptBr) : string.Empty;
                row.AskSize = askVolume > 0m ? askVolume.ToString("N0", _ptBr) : string.Empty;
                row.Imbalance = FlowMapImbalanceText(bidVolume, askVolume);
                row.BidBarWidth = FlowMapBarWidth(bidVolume, maxVolume);
                row.AskBarWidth = FlowMapBarWidth(askVolume, maxVolume);
                row.Levels = EmptyToDash(levelsText);
                row.Read = FlowMapRowRead(zone, bidVolume, askVolume, levelsText);
                rows.Add(row);
            }

            return rows;
        }

        private void AddFlowMapBookLevel(Dictionary<decimal, decimal> byPrice, string priceText, string volumeText, decimal tickSize)
        {
            decimal? price = ValueParser.ToDecimal(priceText);
            decimal? volume = ValueParser.ToDecimal(volumeText);

            if (!price.HasValue || !volume.HasValue || volume.Value <= 0m)
            {
                return;
            }

            decimal rounded = DomLadderModel.RoundToTick(price.Value, tickSize);

            if (!byPrice.ContainsKey(rounded))
            {
                byPrice[rounded] = 0m;
            }

            byPrice[rounded] += volume.Value;
        }

        private List<FlowMapLevelRow> BuildFlowMapLevelRows(MarketSnapshot snapshot, FlowMetrics metrics, List<FlowSignal> signals)
        {
            List<FlowMapLevelRow> rows = new List<FlowMapLevelRow>();

            if (snapshot == null)
            {
                return rows;
            }

            foreach (KeyLevel level in BuildDashboardLevels(snapshot).OrderBy(x => Math.Abs(x.Distance)).ThenByDescending(x => x.Score).Take(45))
            {
                FlowMapLevelRow row = new FlowMapLevelRow();
                row.Price = level.Price.ToString("N2", _ptBr);
                row.Label = EmptyToDash(level.Label);
                row.Distance = level.Distance.ToString("N2", _ptBr);
                row.Source = EmptyToDash(level.Source);
                row.Score = level.Score.ToString("N0", _ptBr);
                row.Read = FlowMapLevelRead(level);
                row.Direction = level.Direction;
                rows.Add(row);
            }

            return rows;
        }

        private void RenderHeatmap(MarketSnapshot snapshot)
        {
            string focused = FocusedAsset();
            MarketSnapshot effective = snapshot ?? FocusedSnapshot();

            if (effective == null &&
                _lastSnapshot != null &&
                (string.IsNullOrWhiteSpace(focused) || string.Equals(_lastSnapshot.Asset, focused, StringComparison.OrdinalIgnoreCase)))
            {
                effective = _lastSnapshot;
            }

            HeatmapSnapshot heatmap = _heatmapProcessor.GetSnapshot(focused, effective == null ? (decimal?)null : effective.Ultimo, 72);

            if (HeatmapChart != null)
            {
                HeatmapChart.SetData(heatmap);
            }

            if (HeatmapSummaryGrid != null)
            {
                HeatmapSummaryGrid.ItemsSource = BuildHeatmapSummaryRows(heatmap, effective);
            }

            if (HeatmapInterestGrid != null)
            {
                HeatmapInterestGrid.ItemsSource = BuildHeatmapInterestRows(heatmap).Take(80).ToList();
            }

            if (HeatmapStateText != null)
            {
                string age = effective == null ? "sem snapshot" : "snapshot " + AgeText(effective.LocalTimestamp);
                HeatmapStateText.Text = EmptyToDash(focused) +
                                        " | " + age +
                                        " | book " + heatmap.BookLevels.ToString(_ptBr) +
                                        " | trades " + heatmap.TradeCount.ToString(_ptBr) +
                                        " | " + heatmap.StorageStatus;
            }
        }

        private List<NameValueRow> BuildHeatmapSummaryRows(HeatmapSnapshot heatmap, MarketSnapshot snapshot)
        {
            List<NameValueRow> rows = new List<NameValueRow>();

            if (heatmap == null)
            {
                return rows;
            }

            AddRow(rows, "Ativo", EmptyToDash(heatmap.Asset), snapshot == null ? "sem snapshot" : AgeText(snapshot.LocalTimestamp));
            AddRow(rows, "Book compra", heatmap.TotalBidLiquidity.ToString("N0", _ptBr), "liquidez passiva bid");
            AddRow(rows, "Book venda", heatmap.TotalAskLiquidity.ToString("N0", _ptBr), "liquidez passiva ask");
            AddRow(rows, "Negocios compra", heatmap.TotalBuyVolume.ToString("N0", _ptBr), "agressoes compra");
            AddRow(rows, "Negocios venda", heatmap.TotalSellVolume.ToString("N0", _ptBr), "agressoes venda");
            AddRow(rows, "CVD", heatmap.CumulativeDelta.ToString("N0", _ptBr), "delta agregado da janela");
            AddRow(rows, "SQLite", heatmap.StorageStatus, _heatmapProcessor.DatabasePath);
            return rows;
        }

        private List<HeatmapInterestRow> BuildHeatmapInterestRows(HeatmapSnapshot heatmap)
        {
            List<HeatmapInterestRow> rows = new List<HeatmapInterestRow>();

            if (heatmap == null || heatmap.Cells == null)
            {
                return rows;
            }

            foreach (HeatmapCell cell in heatmap.Cells.OrderByDescending(x => x.InterestScore).ThenBy(x => Math.Abs(x.Price - (heatmap.CurrentPrice ?? x.Price))))
            {
                HeatmapInterestRow row = new HeatmapInterestRow();
                row.Price = cell.Price.ToString("N2", _ptBr);
                row.Direction = string.IsNullOrWhiteSpace(cell.Direction) ? "Neutro" : cell.Direction;
                row.Score = cell.InterestScore.ToString("N0", _ptBr);
                row.Book = "C " + cell.BidLiquidity.ToString("N0", _ptBr) + " / V " + cell.AskLiquidity.ToString("N0", _ptBr);
                row.Trades = "C " + cell.BuyVolume.ToString("N0", _ptBr) + " / V " + cell.SellVolume.ToString("N0", _ptBr);
                row.Delta = cell.Delta.ToString("N0", _ptBr);
                row.Read = EmptyToDash(cell.Read);
                rows.Add(row);
            }

            return rows;
        }

        private string FlowMapZone(decimal price, decimal center, bool hasLevel)
        {
            if (price == center)
            {
                return "ULT";
            }

            if (hasLevel)
            {
                return "PROFILE";
            }

            return price > center ? "ASK" : "BID";
        }

        private string FlowMapImbalanceText(decimal bidVolume, decimal askVolume)
        {
            decimal total = bidVolume + askVolume;

            if (total <= 0m)
            {
                return "-";
            }

            decimal imbalance = (bidVolume - askVolume) / total;
            return imbalance.ToString("N2", _ptBr);
        }

        private double FlowMapBarWidth(decimal volume, decimal maxVolume)
        {
            if (volume <= 0m || maxVolume <= 0m)
            {
                return 0d;
            }

            return Math.Max(2d, Math.Min(70d, (double)(volume / maxVolume) * 70d));
        }

        private string FlowMapRowRead(string zone, decimal bidVolume, decimal askVolume, string levelsText)
        {
            if (!string.IsNullOrWhiteSpace(levelsText))
            {
                return levelsText;
            }

            if (string.Equals(zone, "ULT", StringComparison.OrdinalIgnoreCase))
            {
                return "preco atual";
            }

            if (bidVolume <= 0m && askVolume <= 0m)
            {
                return "-";
            }

            if (bidVolume > askVolume * 2m)
            {
                return "liquidez compra dominante";
            }

            if (askVolume > bidVolume * 2m)
            {
                return "liquidez venda dominante";
            }

            if (bidVolume > 0m && askVolume > 0m)
            {
                return "liquidez equilibrada";
            }

            return bidVolume > 0m ? "liquidez compra" : "liquidez venda";
        }

        private string FlowMapLevelRead(KeyLevel level)
        {
            if (level == null)
            {
                return "-";
            }

            decimal distance = level.Distance;

            if (Math.Abs(distance) <= _config.Rtd.TickSize)
            {
                return "em teste";
            }

            if (distance > 0m)
            {
                return "preco acima";
            }

            return "preco abaixo";
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

        private string FormatPoints(decimal? value)
        {
            if (!value.HasValue)
            {
                return "-";
            }

            decimal rounded = Math.Round(value.Value, 2, MidpointRounding.AwayFromZero);
            string formatted = rounded.ToString("N2", _ptBr);

            if (rounded > 0m)
            {
                formatted = "+" + formatted;
            }

            return formatted + " pts";
        }

        private void RenderDom(MarketSnapshot snapshot)
        {
            if (DomGrid == null)
            {
                return;
            }

            List<KeyLevel> levels = (_result == null ? BasicLevels(snapshot) : _result.KeyLevels.Concat(_result.Confluence)).ToList();
            levels.AddRange(FlowProfileKeyLevels(snapshot));
            levels.AddRange(FlowSignalKeyLevels(snapshot));
            DomGrid.ItemsSource = DomLadderModel.Build(snapshot, levels, _config.Rtd.TickSize, _config.Ui.DomTicksEachSide);
            ScheduleDomCenter();
        }

        private void ScheduleDomCenter()
        {
            if (_domCenterQueued || DomGrid == null)
            {
                return;
            }

            _domCenterQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
            {
                _domCenterQueued = false;
                CenterDomGridOnCurrentPrice(DomGrid);
            }));
        }

        private void ScheduleDashboardDomCenter()
        {
            if (_dashboardDomCenterQueued || DashboardDomGrid == null)
            {
                return;
            }

            _dashboardDomCenterQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
            {
                _dashboardDomCenterQueued = false;
                CenterDomGridOnCurrentPrice(DashboardDomGrid);
            }));
        }

        private void ScheduleLevelsMapCenter()
        {
            if (_levelsMapCenterQueued || OpeningMapGrid == null)
            {
                return;
            }

            _levelsMapCenterQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
            {
                _levelsMapCenterQueued = false;
                CenterLevelsMapOnOpening();
            }));
        }

        private void CenterLevelsMapOnOpening()
        {
            if (OpeningMapGrid == null || OpeningMapGrid.Items == null || OpeningMapGrid.Items.Count == 0)
            {
                return;
            }

            int openingIndex = -1;

            for (int index = 0; index < OpeningMapGrid.Items.Count; index++)
            {
                LevelsMapRow row = OpeningMapGrid.Items[index] as LevelsMapRow;

                if (row != null && row.IsOpening)
                {
                    openingIndex = index;
                    break;
                }
            }

            if (openingIndex < 0)
            {
                openingIndex = OpeningMapGrid.Items.Count / 2;
            }

            CenterDataGridOnIndex(OpeningMapGrid, openingIndex);
        }

        private void CenterDomGridOnCurrentPrice(System.Windows.Controls.DataGrid grid)
        {
            if (grid == null || grid.Items == null || grid.Items.Count == 0)
            {
                return;
            }

            int currentIndex = -1;

            for (int index = 0; index < grid.Items.Count; index++)
            {
                DomRow row = grid.Items[index] as DomRow;

                if (row != null && row.IsCurrent)
                {
                    currentIndex = index;
                    break;
                }
            }

            if (currentIndex < 0)
            {
                currentIndex = grid.Items.Count / 2;
            }

            CenterDataGridOnIndex(grid, currentIndex);
        }

        private void CenterDataGridOnIndex(System.Windows.Controls.DataGrid grid, int currentIndex)
        {
            if (grid == null || grid.Items == null || grid.Items.Count == 0 || currentIndex < 0 || currentIndex >= grid.Items.Count)
            {
                return;
            }

            grid.ScrollIntoView(grid.Items[currentIndex]);
            grid.UpdateLayout();

            ScrollViewer scrollViewer = FindDataGridScrollViewer(grid);

            if (scrollViewer == null)
            {
                return;
            }

            double viewport = scrollViewer.ViewportHeight;

            if (viewport <= 0d)
            {
                return;
            }

            double targetOffset = currentIndex - Math.Max(0d, (viewport / 2d) - 0.5d);
            double maxOffset = Math.Max(0d, scrollViewer.ExtentHeight - viewport);

            if (targetOffset < 0d)
            {
                targetOffset = 0d;
            }

            if (targetOffset > maxOffset)
            {
                targetOffset = maxOffset;
            }

            scrollViewer.ScrollToVerticalOffset(targetOffset);
        }

        private ScrollViewer FindDataGridScrollViewer(System.Windows.Controls.DataGrid grid)
        {
            if (grid == null)
            {
                return null;
            }

            grid.ApplyTemplate();
            ScrollViewer templateScrollViewer = grid.Template == null ? null : grid.Template.FindName("DG_ScrollViewer", grid) as ScrollViewer;

            if (templateScrollViewer != null)
            {
                return templateScrollViewer;
            }

            return FindVisualChild<ScrollViewer>(grid);
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null)
            {
                return null;
            }

            int count = VisualTreeHelper.GetChildrenCount(parent);

            for (int index = 0; index < count; index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                T childOfType = child as T;

                if (childOfType != null)
                {
                    return childOfType;
                }

                T nestedChild = FindVisualChild<T>(child);

                if (nestedChild != null)
                {
                    return nestedChild;
                }
            }

            return null;
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
                level.Direction = signal.Direction;
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

        private string FormatPercent(decimal? value, string format)
        {
            return value.HasValue ? value.Value.ToString(format, _ptBr) + "%" : "-";
        }

        private string FormatSignedDecimal(decimal? value, string format)
        {
            if (!value.HasValue)
            {
                return "-";
            }

            string formatted = value.Value.ToString(format, _ptBr);
            return value.Value > 0m ? "+" + formatted : formatted;
        }

        private string EmptyToDash(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }

        private string MetricDisplayLabel(string metricName)
        {
            if (string.IsNullOrWhiteSpace(metricName))
            {
                return "-";
            }

            if (string.Equals(metricName, "Garman-Klass", StringComparison.OrdinalIgnoreCase))
            {
                return "Garman-Klass (HL/OC)";
            }

            if (string.Equals(metricName, "Parkinson", StringComparison.OrdinalIgnoreCase))
            {
                return "Parkinson (HL)";
            }

            if (string.Equals(metricName, "Rogers-Satchell", StringComparison.OrdinalIgnoreCase))
            {
                return "Rogers-Satchell (OHLC)";
            }

            if (string.Equals(metricName, "Yang-Zhang", StringComparison.OrdinalIgnoreCase))
            {
                return "Yang-Zhang (overnight + intraday)";
            }

            if (string.Equals(metricName, "Close-to-close", StringComparison.OrdinalIgnoreCase))
            {
                return "Close-to-close (log returns)";
            }

            if (string.Equals(metricName, "Desvio padrao", StringComparison.OrdinalIgnoreCase))
            {
                return "Desvio padrao (STDEV.P H-L)";
            }

            if (string.Equals(metricName, "Gauss", StringComparison.OrdinalIgnoreCase))
            {
                return "Gauss robusto (YZ + MAD)";
            }

            if (string.Equals(metricName, "ATR", StringComparison.OrdinalIgnoreCase))
            {
                return "ATR (True Range)";
            }

            return metricName;
        }

        private string FormatError(Exception error)
        {
            if (error == null)
            {
                return "-";
            }

            return error.GetType().Name + ": " + error.Message;
        }

        private Brush SeverityBrush(string severity)
        {
            if (string.Equals(severity, "Critico", StringComparison.OrdinalIgnoreCase))
            {
                return new SolidColorBrush(Color.FromRgb(255, 59, 48));
            }

            if (string.Equals(severity, "Aviso", StringComparison.OrdinalIgnoreCase))
            {
                return new SolidColorBrush(Color.FromRgb(255, 184, 0));
            }

            if (string.Equals(severity, "Normal", StringComparison.OrdinalIgnoreCase))
            {
                return new SolidColorBrush(Color.FromRgb(0, 230, 118));
            }

            return new SolidColorBrush(Color.FromRgb(169, 179, 191));
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

        private Brush VariationBrush(decimal? value)
        {
            if (!value.HasValue)
            {
                return FindResource("Muted") as Brush ?? Brushes.Gray;
            }

            if (value.Value > 0m)
            {
                return FindResource("Accent") as Brush ?? Brushes.LimeGreen;
            }

            if (value.Value < 0m)
            {
                return FindResource("Danger") as Brush ?? Brushes.OrangeRed;
            }

            return FindResource("Muted") as Brush ?? Brushes.Gray;
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

        private sealed class MonitorRow
        {
            public string Asset { get; set; }
            public string Name { get; set; }
            public string FocusText { get; set; }
            public string EnabledText { get; set; }
            public string ChannelsText { get; set; }
            public string LastText { get; set; }
            public string BidAskText { get; set; }
            public string VolumeText { get; set; }
            public string SnapshotAgeText { get; set; }
            public string FlowQuality { get; set; }
            public string DeltaText { get; set; }
            public string CsvText { get; set; }
            public string Status { get; set; }
        }

        private sealed class ShortcutRow
        {
            public string Shortcut { get; set; }
            public string Workspace { get; set; }
            public string Command { get; set; }
            public string Use { get; set; }
        }

        private sealed class WorkflowRow
        {
            public string Step { get; set; }
            public string Area { get; set; }
            public string Workspace { get; set; }
            public string Action { get; set; }
        }

        private sealed class OpportunityRow
        {
            public string Asset { get; set; }
            public string Setup { get; set; }
            public string Direction { get; set; }
            public string Price { get; set; }
            public string Score { get; set; }
            public string Robustness { get; set; }
            public string Level { get; set; }
            public string Quality { get; set; }
            public string Age { get; set; }
            public string Reasons { get; set; }
            public double SortScore { get; set; }
        }

        private sealed class OpportunityScore
        {
            public int Score { get; set; }
            public string Robustness { get; set; }
            public string Detail { get; set; }
        }

        private sealed class WorkflowOpportunity
        {
            public string Value { get; set; }
            public string State { get; set; }
            public Brush Brush { get; set; }
        }

        private sealed class OpportunityLevelRow
        {
            public string Price { get; set; }
            public string Label { get; set; }
            public string Source { get; set; }
            public string Score { get; set; }
            public string Distance { get; set; }
            public string Evidence { get; set; }
            public string Direction { get; set; }
        }

        private sealed class ReferenceMetricCardRow
        {
            public string MetricLabel { get; set; }
            public string PointsText { get; set; }
            public string BasisText { get; set; }
            public string NearestSellPriceText { get; set; }
            public string NearestSellDistanceText { get; set; }
            public string NearestBuyPriceText { get; set; }
            public string NearestBuyDistanceText { get; set; }
        }

        private sealed class ReferenceComparisonRow
        {
            public string Side { get; set; }
            public string Level { get; set; }
            public string GarmanPrice { get; set; }
            public string GarmanDistance { get; set; }
            public string GaussPrice { get; set; }
            public string GaussDistance { get; set; }
            public string StdDevPrice { get; set; }
            public string StdDevDistance { get; set; }
            public string GarchPrice { get; set; }
            public string GarchDistance { get; set; }
            public string GarchScore { get; set; }
            public string Direction { get; set; }
        }

        private sealed class PtaxHistoryViewRow
        {
            public string Data { get; set; }
            public string Ptax { get; set; }
            public string AtualizadoEm { get; set; }
        }

        private sealed class LevelsMapRow
        {
            public string Zone { get; set; }
            public string Side { get; set; }
            public string Label { get; set; }
            public string Metric { get; set; }
            public string Price { get; set; }
            public string DistanceCurrent { get; set; }
            public string DistanceReference { get; set; }
            public string Source { get; set; }
            public bool IsOpening { get; set; }
            public decimal SortPrice { get; set; }
            public string Direction { get; set; }
        }

        private sealed class ScannerRow
        {
            public string Rank { get; set; }
            public string Asset { get; set; }
            public string EnabledText { get; set; }
            public string FocusText { get; set; }
            public string Status { get; set; }
            public string Last { get; set; }
            public string BidAsk { get; set; }
            public string SnapshotAge { get; set; }
            public string Channels { get; set; }
            public string Quality { get; set; }
            public string BestSetup { get; set; }
            public string Direction { get; set; }
            public string Score { get; set; }
            public string Level { get; set; }
            public string Distance { get; set; }
            public string Delta { get; set; }
            public string VwapDistance { get; set; }
            public string Read { get; set; }
            public double SortScore { get; set; }
        }

        private sealed class FlowMapBookRow
        {
            public string Zone { get; set; }
            public string Price { get; set; }
            public string BidSize { get; set; }
            public string AskSize { get; set; }
            public string Imbalance { get; set; }
            public double BidBarWidth { get; set; }
            public double AskBarWidth { get; set; }
            public string Levels { get; set; }
            public string Read { get; set; }
        }

        private sealed class FlowMapLevelRow
        {
            public string Price { get; set; }
            public string Label { get; set; }
            public string Distance { get; set; }
            public string Source { get; set; }
            public string Score { get; set; }
            public string Read { get; set; }
            public string Direction { get; set; }
        }

        private sealed class HeatmapInterestRow
        {
            public string Price { get; set; }
            public string Direction { get; set; }
            public string Score { get; set; }
            public string Book { get; set; }
            public string Trades { get; set; }
            public string Delta { get; set; }
            public string Read { get; set; }
        }

        private sealed class DashboardWindowRow
        {
            public string Window { get; set; }
            public string TradeCount { get; set; }
            public string BuyVolume { get; set; }
            public string SellVolume { get; set; }
            public string Delta { get; set; }
            public string DeltaRatio { get; set; }
        }

        private sealed class DashboardTapeRow
        {
            public string Time { get; set; }
            public string Price { get; set; }
            public string Quantity { get; set; }
            public string Aggressor { get; set; }
            public string Quality { get; set; }
        }

        private sealed class HistoryRow
        {
            public string Time { get; set; }
            public string Asset { get; set; }
            public string Area { get; set; }
            public string Event { get; set; }
            public string Detail { get; set; }
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

        private sealed class RtdReadinessRow
        {
            public string Channel { get; set; }
            public string Topic { get; set; }
            public string SubscriptionCount { get; set; }
            public string Status { get; set; }
            public string Formula { get; set; }
        }

        private sealed class NameValueRow
        {
            public string Name { get; set; }
            public string Value { get; set; }
            public string Detail { get; set; }
        }

        private sealed class IndicatorAuditRow
        {
            public string Indicator { get; set; }
            public string Value { get; set; }
            public string State { get; set; }
            public string Source { get; set; }
        }

        private sealed class QuantSignalAuditRow
        {
            public string Setup { get; set; }
            public string Direction { get; set; }
            public string Price { get; set; }
            public string Score { get; set; }
            public string Level { get; set; }
            public string Edge { get; set; }
            public string Confidence { get; set; }
            public string RiskReward { get; set; }
            public string Gate { get; set; }
            public string TechnicalState { get; set; }
            public string Reasons { get; set; }
        }

        private sealed class GarchBandViewRow
        {
            public string Side { get; set; }
            public string Sigma { get; set; }
            public string Price { get; set; }
            public string DistanceCurrent { get; set; }
            public string Score { get; set; }
            public string Read { get; set; }
        }

        private sealed class GarchParameterRow
        {
            public string Parameter { get; set; }
            public string Value { get; set; }
        }

        private sealed class GarchAuditRow
        {
            public string Item { get; set; }
            public string Value { get; set; }
        }

        private sealed class GarchBacktestViewRow
        {
            public string Scope { get; set; }
            public string Direction { get; set; }
            public string Sigma { get; set; }
            public string Touches { get; set; }
            public string Reversals { get; set; }
            public string ReversalRateText { get; set; }
            public string EdgeText { get; set; }
        }

        private sealed class GarchForecastViewRow
        {
            public string Scope { get; set; }
            public string Horizon { get; set; }
            public string SigmaPercent { get; set; }
            public string Points { get; set; }
            public string Read { get; set; }
        }

        private sealed class MetricAuditRow
        {
            public string Metric { get; set; }
            public string Window { get; set; }
            public string Points { get; set; }
            public string Percent { get; set; }
            public string Percentile { get; set; }
        }

        private sealed class BacktestAuditRow
        {
            public string Direction { get; set; }
            public string Multiplier { get; set; }
            public string Samples { get; set; }
            public string Touches { get; set; }
            public string TouchRate { get; set; }
            public string ReversalRate { get; set; }
            public string ContinuationRate { get; set; }
            public string Expectancy { get; set; }
            public string ProfitFactor { get; set; }
            public string Confidence { get; set; }
            public string RiskReward { get; set; }
            public string EdgeScore { get; set; }
            public string EdgeQuality { get; set; }
        }

        private sealed class AlertRow
        {
            public string Severity { get; set; }
            public string Source { get; set; }
            public string Asset { get; set; }
            public string Message { get; set; }
            public string Detail { get; set; }
            public string AgeText { get; set; }
        }

        private sealed class RiskRow
        {
            public string Severity { get; set; }
            public string Area { get; set; }
            public string Item { get; set; }
            public string Value { get; set; }
            public string Limit { get; set; }
            public string Action { get; set; }
        }

        private sealed class RtdCompleteGroupRow
        {
            public string Group { get; set; }
            public string Detail { get; set; }
            public string CountText { get; set; }
        }

        private sealed class RtdCompleteFieldRow
        {
            public string Field { get; set; }
            public string Code { get; set; }
            public string Value { get; set; }
            public string Group { get; set; }
            public string Subgroup { get; set; }
            public string Status { get; set; }
            public string Updated { get; set; }
            public string Source { get; set; }
            public string Direction { get; set; }
            public int SortPriority { get; set; }
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

