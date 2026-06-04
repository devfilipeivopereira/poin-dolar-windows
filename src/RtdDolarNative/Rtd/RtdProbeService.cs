using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using RtdDolarNative.Config;
using RtdDolarNative.Logging;
using RtdDolarNative.LowLatency;
using RtdDolarNative.MarketData;

namespace RtdDolarNative.Rtd
{
    public sealed class RtdProbeService : IDisposable
    {
        private readonly RtdConfig _config;
        private readonly DiagnosticsConfig _diagnostics;
        private readonly Logger _log;
        private readonly LatestSnapshotBuffer _snapshotBuffer;
        private readonly object _statusLock = new object();
        private readonly object _statesLock = new object();
        private readonly Dictionary<int, RtdTopic> _topics = new Dictionary<int, RtdTopic>();
        private readonly Dictionary<string, MarketState> _states = new Dictionary<string, MarketState>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, decimal?> _lastTradePrices = new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase);
        private Thread _thread;
        private RtdUpdateEvent _callback;
        private volatile bool _stopRequested;
        private string _status;
        private Exception _lastError;
        private long _updatesReceived;

        public RtdProbeService(RtdConfig config, DiagnosticsConfig diagnostics, Logger log, LatestSnapshotBuffer snapshotBuffer)
        {
            _config = config;
            _diagnostics = diagnostics;
            _log = log;
            _snapshotBuffer = snapshotBuffer;
            _status = "idle";
        }

        public event Action<string, Exception> StatusChanged;
        public event Action<TickEvent> TickReceived;
        public event Action<MarketSnapshot> SnapshotReceived;

        public bool IsRunning
        {
            get { return _thread != null && _thread.IsAlive && !_stopRequested; }
        }

        public long UpdatesReceived
        {
            get { return Interlocked.Read(ref _updatesReceived); }
        }

        public string Status
        {
            get
            {
                lock (_statusLock)
                {
                    return _status;
                }
            }
        }

        public Exception LastError
        {
            get
            {
                lock (_statusLock)
                {
                    return _lastError;
                }
            }
        }

        public void Start()
        {
            if (_thread != null && _thread.IsAlive)
            {
                return;
            }

            _stopRequested = false;
            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.Name = "Profit RTD Probe STA";
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public void Stop()
        {
            _stopRequested = true;

            if (_thread != null && _thread.IsAlive)
            {
                _thread.Join(TimeSpan.FromSeconds(5));
            }

            _thread = null;
            SetStatus("stopped", null);
        }

        public void Restart()
        {
            Stop();
            Start();
        }

        public void Dispose()
        {
            Stop();
        }

        private void Run()
        {
            _log.Info("Iniciando RTD probe em processo " + (Environment.Is64BitProcess ? "x64" : "x86") + ".");

            while (!_stopRequested)
            {
                try
                {
                    ConnectAndPump();
                }
                catch (Exception ex)
                {
                    SetStatus("reconnecting", ex);
                    _log.Error("Falha no loop RTD. Tentando reconectar.", ex);
                    SleepInterruptible(Math.Max(_config.ReconnectIntervalMs, 1000));
                }
            }
        }

        private void ConnectAndPump()
        {
            object rtdObject = null;
            IRtdServer server = null;

            try
            {
                SetStatus("connecting", null);
                _topics.Clear();
                _lastTradePrices.Clear();

                List<string> activeAssets = _config.GetEnabledAssets();
                List<RtdSubscriptionSpec> subscriptions = _config.GetSubscriptions();

                if (activeAssets.Count == 0 || subscriptions.Count == 0)
                {
                    _log.Info("Nenhum ativo/fonte RTD ligado. Loop em espera.");
                    SetStatus("idle", null);

                    while (!_stopRequested)
                    {
                        SleepInterruptible(250);
                    }

                    return;
                }

                Type rtdType = Type.GetTypeFromProgID(_config.ProgId);

                if (rtdType == null)
                {
                    throw new InvalidOperationException("Servidor RTD nao encontrado: " + _config.ProgId);
                }

                rtdObject = Activator.CreateInstance(rtdType);
                server = (IRtdServer)rtdObject;
                _callback = new RtdUpdateEvent();

                int startResult = server.ServerStart(_callback);
                _log.Info("ServerStart retornou " + startResult + ".");

                if (startResult <= 0)
                {
                    throw new InvalidOperationException("ServerStart falhou. Codigo: " + startResult);
                }

                SubscribeConfiguredFields(server, subscriptions);
                SetStatus("connected", null);

                DateTime nextHeartbeat = DateTime.UtcNow.AddSeconds(1);

                while (!_stopRequested)
                {
                    PumpRefreshData(server);

                    if (DateTime.UtcNow >= nextHeartbeat)
                    {
                        int heartbeat = server.Heartbeat();

                        if (heartbeat <= 0)
                        {
                            throw new InvalidOperationException("Heartbeat RTD falhou. Codigo: " + heartbeat);
                        }

                        nextHeartbeat = DateTime.UtcNow.AddSeconds(1);
                    }

                    SleepInterruptible(Math.Max(_config.GetEffectivePollIntervalMs(), 10));
                }
            }
            finally
            {
                Disconnect(server);
                _callback = null;

                if (rtdObject != null && Marshal.IsComObject(rtdObject))
                {
                    Marshal.ReleaseComObject(rtdObject);
                }
            }
        }

        private void SubscribeConfiguredFields(IRtdServer server, IEnumerable<RtdSubscriptionSpec> subscriptions)
        {
            int topicId = 1;
            List<RtdSubscriptionSpec> specs = subscriptions == null ? new List<RtdSubscriptionSpec>() : subscriptions.ToList();

            foreach (string asset in specs.Select(x => RtdConfig.NormalizeAsset(x.Asset)).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                EnsureState(asset);
            }

            foreach (RtdSubscriptionSpec spec in specs)
            {
                string asset = RtdConfig.NormalizeAsset(spec.Asset);
                string field = string.IsNullOrWhiteSpace(spec.Field) ? string.Empty : spec.Field.Trim().ToUpperInvariant();

                if (string.IsNullOrWhiteSpace(asset) || string.IsNullOrWhiteSpace(field))
                {
                    continue;
                }

                object[] arguments = spec.Arguments == null ? new object[] { asset, field } : spec.Arguments.ToArray();
                object initialValue = ConnectDataWithFallback(server, topicId, asset, field, arguments);

                RtdTopic topic = new RtdTopic();
                topic.TopicId = topicId;
                topic.Asset = asset;
                topic.Topic = string.IsNullOrWhiteSpace(spec.Topic) ? asset : spec.Topic;
                topic.Field = field;
                topic.RtdField = string.IsNullOrWhiteSpace(spec.RtdField) ? field : spec.RtdField;
                topic.Index = spec.Index;
                topic.InfoField = spec.InfoField;
                topic.SourceName = spec.SourceName;
                topic.Role = spec.Role;
                topic.Arguments = arguments;
                topic.LastValue = initialValue;

                _topics[topicId] = topic;
                _log.Info("Assinado RTD " + topic.Key + " args " + FormatArguments(arguments) + " fonte " + spec.SourceName + " papel " + spec.Role + ".");
                Publish(topic, initialValue);

                topicId++;
            }
        }

        private object ConnectDataWithFallback(IRtdServer server, int topicId, string asset, string field, object[] arguments)
        {
            Exception lastError = null;
            object[] safeArguments = arguments == null || arguments.Length == 0 ? new object[] { asset, field } : arguments;

            try
            {
                bool getNewValues = true;
                object[] topicArgs = safeArguments.ToArray();
                object value = server.ConnectData(topicId, ref topicArgs, ref getNewValues);
                _log.Info("ConnectData OK " + asset + ":" + field + " args " + FormatArguments(safeArguments) + " usando object[] zero-based.");
                return value;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _log.Warn("ConnectData falhou " + asset + ":" + field + " args " + FormatArguments(safeArguments) + " usando object[] zero-based | " + ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                bool getNewValues = true;
                object[] topicArgs = new object[safeArguments.Length + 1];
                topicArgs[0] = string.Empty;
                Array.Copy(safeArguments, 0, topicArgs, 1, safeArguments.Length);
                object value = server.ConnectData(topicId, ref topicArgs, ref getNewValues);
                _log.Info("ConnectData OK " + asset + ":" + field + " args " + FormatArguments(topicArgs) + " usando object[] com slot vazio.");
                return value;
            }
            catch (Exception ex)
            {
                lastError = ex;
                object[] topicArgs = new object[safeArguments.Length + 1];
                topicArgs[0] = string.Empty;
                Array.Copy(safeArguments, 0, topicArgs, 1, safeArguments.Length);
                _log.Warn("ConnectData falhou " + asset + ":" + field + " args " + FormatArguments(topicArgs) + " usando object[] com slot vazio | " + ex.GetType().Name + ": " + ex.Message);
            }

            throw new InvalidOperationException("Falha ao assinar RTD " + asset + ":" + field + " args " + FormatArguments(safeArguments) + ".", lastError);
        }

        private static string FormatArguments(IEnumerable<object> arguments)
        {
            if (arguments == null)
            {
                return "-";
            }

            return string.Join("|", arguments.Select(x => x == null ? string.Empty : x.ToString()).ToArray());
        }

        private void PumpRefreshData(IRtdServer server)
        {
            int topicCount = 0;
            object[,] data = server.RefreshData(ref topicCount);

            if (data == null || topicCount <= 0)
            {
                return;
            }

            int rowLower = data.GetLowerBound(0);
            int colLower = data.GetLowerBound(1);

            for (int i = 0; i < topicCount; i++)
            {
                object idObject = data.GetValue(rowLower, colLower + i);
                object value = data.GetValue(rowLower + 1, colLower + i);

                if (idObject == null)
                {
                    continue;
                }

                int topicId = Convert.ToInt32(idObject);
                RtdTopic topic;

                if (!_topics.TryGetValue(topicId, out topic))
                {
                    continue;
                }

                topic.LastValue = value;
                Publish(topic, value);
            }
        }

        private void Publish(RtdTopic topic, object value)
        {
            MarketState state = EnsureState(topic.Asset);
            MarketSnapshot snapshot = state.Update(topic.Asset, topic.Field, value, Status);
            _snapshotBuffer.Publish(snapshot);
            NotifySnapshot(snapshot);
            Interlocked.Increment(ref _updatesReceived);
            PublishTickIfNeeded(topic, snapshot);

            if (_diagnostics != null && _diagnostics.LogEveryTick)
            {
                _log.Debug("Update " + topic.Key + " = " + (value == null ? "<null>" : value.ToString()));
            }
        }

        private void PublishTickIfNeeded(RtdTopic topic, MarketSnapshot snapshot)
        {
            if (!string.Equals(topic.Field, "ULT", StringComparison.OrdinalIgnoreCase) || !snapshot.Ultimo.HasValue)
            {
                return;
            }

            decimal price = snapshot.Ultimo.Value;
            decimal threshold = Math.Max(0.0001m, _config.TickSize / 2m);
            decimal? lastTradePrice;
            _lastTradePrices.TryGetValue(topic.Asset, out lastTradePrice);

            if (lastTradePrice.HasValue && Math.Abs(price - lastTradePrice.Value) < threshold)
            {
                return;
            }

            decimal delta = lastTradePrice.HasValue ? price - lastTradePrice.Value : 0m;
            TickEvent tick = new TickEvent();
            tick.Asset = topic.Asset;
            tick.LocalTimestamp = snapshot.LocalTimestamp;
            tick.ProfitTime = snapshot.HoraProfit;
            tick.Price = price;
            tick.Quantity = snapshot.QuantidadeUltimoNegocio;
            tick.Volume = snapshot.Volume;
            tick.Delta = delta;
            tick.Side = !lastTradePrice.HasValue ? "Inicial" : delta > 0m ? "Subiu" : delta < 0m ? "Caiu" : "Neutro";
            tick.Bid = snapshot.OfertaCompra;
            tick.Ask = snapshot.OfertaVenda;
            _lastTradePrices[topic.Asset] = price;

            Action<TickEvent> handler = TickReceived;

            if (handler != null)
            {
                handler(tick);
            }
        }

        private void Disconnect(IRtdServer server)
        {
            if (server == null)
            {
                return;
            }

            foreach (RtdTopic topic in _topics.Values.ToList())
            {
                try
                {
                    server.DisconnectData(topic.TopicId);
                }
                catch (Exception ex)
                {
                    _log.Warn("Falha ao desconectar topico " + topic.Key + ": " + ex.Message);
                }
            }

            try
            {
                server.ServerTerminate();
            }
            catch (Exception ex)
            {
                _log.Warn("Falha ao finalizar servidor RTD: " + ex.Message);
            }
        }

        private void SetStatus(string status, Exception error)
        {
            bool changed;

            lock (_statusLock)
            {
                changed = !string.Equals(_status, status, StringComparison.OrdinalIgnoreCase) || !object.ReferenceEquals(_lastError, error);
                _status = status;
                _lastError = error;
            }

            PublishStatusSnapshots(status);

            if (changed)
            {
                _log.Info("Status RTD: " + status + ".");
                Action<string, Exception> handler = StatusChanged;

                if (handler != null)
                {
                    handler(status, error);
                }
            }
        }

        private MarketState EnsureState(string asset)
        {
            string key = RtdConfig.NormalizeAsset(asset);

            if (string.IsNullOrWhiteSpace(key))
            {
                key = RtdConfig.NormalizeAsset(_config.Asset);
            }

            lock (_statesLock)
            {
                MarketState state;

                if (!_states.TryGetValue(key, out state))
                {
                    state = new MarketState();
                    _states[key] = state;
                }

                return state;
            }
        }

        private void PublishStatusSnapshots(string status)
        {
            List<MarketState> states;

            lock (_statesLock)
            {
                if (_states.Count == 0)
                {
                    _states[RtdConfig.NormalizeAsset(_config.Asset)] = new MarketState();
                }

                states = _states.Values.ToList();
            }

            foreach (MarketState state in states)
            {
                MarketSnapshot snapshot = state.MarkStatus(status);
                _snapshotBuffer.Publish(snapshot);
                NotifySnapshot(snapshot);
            }
        }

        private void NotifySnapshot(MarketSnapshot snapshot)
        {
            Action<MarketSnapshot> handler = SnapshotReceived;

            if (handler != null)
            {
                handler(snapshot);
            }
        }

        private void SleepInterruptible(int milliseconds)
        {
            int remaining = milliseconds;

            while (!_stopRequested && remaining > 0)
            {
                int chunk = Math.Min(remaining, 100);
                Thread.Sleep(chunk);
                remaining -= chunk;
            }
        }
    }
}
