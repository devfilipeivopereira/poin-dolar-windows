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
        private readonly MarketState _state = new MarketState();
        private readonly object _statusLock = new object();
        private readonly Dictionary<int, RtdTopic> _topics = new Dictionary<int, RtdTopic>();
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
            _status = "starting";
        }

        public event Action<string, Exception> StatusChanged;

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

                SubscribeProbeFields(server);
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

                    SleepInterruptible(Math.Max(_config.PollIntervalMs, 10));
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

        private void SubscribeProbeFields(IRtdServer server)
        {
            int topicId = 1;
            IEnumerable<string> fields;

            if (_config.ProbeFields == null || _config.ProbeFields.Count == 0)
            {
                fields = new[] { "HOR", "ULT", "VOL" };
            }
            else
            {
                fields = _config.ProbeFields;
            }

            foreach (string field in fields.Select(x => x.Trim().ToUpperInvariant()).Distinct())
            {
                object initialValue = ConnectDataWithFallback(server, topicId, _config.Asset, field);

                RtdTopic topic = new RtdTopic();
                topic.TopicId = topicId;
                topic.Asset = _config.Asset;
                topic.Field = field;
                topic.LastValue = initialValue;

                _topics[topicId] = topic;
                _log.Info("Assinado RTD " + topic.Key + ".");
                Publish(topic, initialValue);

                topicId++;
            }
        }

        private object ConnectDataWithFallback(IRtdServer server, int topicId, string asset, string field)
        {
            Exception lastError = null;

            try
            {
                bool getNewValues = true;
                object[] topicArgs = new object[] { asset, field };
                object value = server.ConnectData(topicId, ref topicArgs, ref getNewValues);
                _log.Info("ConnectData OK " + asset + ":" + field + " usando object[] zero-based.");
                return value;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _log.Warn("ConnectData falhou " + asset + ":" + field + " usando object[] zero-based | " + ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                bool getNewValues = true;
                object[] topicArgs = new object[] { string.Empty, asset, field };
                object value = server.ConnectData(topicId, ref topicArgs, ref getNewValues);
                _log.Info("ConnectData OK " + asset + ":" + field + " usando object[] com slot vazio.");
                return value;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _log.Warn("ConnectData falhou " + asset + ":" + field + " usando object[] com slot vazio | " + ex.GetType().Name + ": " + ex.Message);
            }

            throw new InvalidOperationException("Falha ao assinar RTD " + asset + ":" + field + ".", lastError);
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
            MarketSnapshot snapshot = _state.Update(topic.Asset, topic.Field, value, Status);
            _snapshotBuffer.Publish(snapshot);
            Interlocked.Increment(ref _updatesReceived);

            if (_diagnostics != null && _diagnostics.LogEveryTick)
            {
                _log.Debug("Update " + topic.Key + " = " + (value == null ? "<null>" : value.ToString()));
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

            MarketSnapshot snapshot = _state.MarkStatus(status);
            _snapshotBuffer.Publish(snapshot);

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
