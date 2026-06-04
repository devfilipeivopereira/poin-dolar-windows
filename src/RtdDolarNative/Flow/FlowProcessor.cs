using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RtdDolarNative.Config;
using RtdDolarNative.Logging;
using RtdDolarNative.MarketData;

namespace RtdDolarNative.Flow
{
    public sealed class FlowProcessor : IDisposable
    {
        private readonly FlowConfig _config;
        private readonly decimal _tickSize;
        private readonly Logger _log;
        private readonly object _queueLock = new object();
        private readonly object _stateLock = new object();
        private readonly Queue<MarketSnapshot> _queue = new Queue<MarketSnapshot>();
        private readonly Dictionary<string, FlowEngine> _engines = new Dictionary<string, FlowEngine>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FlowMetrics> _metrics = new Dictionary<string, FlowMetrics>(StringComparer.OrdinalIgnoreCase);
        private readonly List<TradePrint> _trades = new List<TradePrint>();
        private readonly List<FlowSignal> _signals = new List<FlowSignal>();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private Thread _thread;
        private volatile bool _stopRequested;
        private long _processed;
        private long _dropped;

        public FlowProcessor(decimal tickSize, FlowConfig config, Logger log)
        {
            _tickSize = tickSize <= 0m ? 0.5m : tickSize;
            _config = config ?? new FlowConfig();
            _config.Normalize();
            _log = log;
        }

        public long Processed
        {
            get { return Interlocked.Read(ref _processed); }
        }

        public long Dropped
        {
            get { return Interlocked.Read(ref _dropped); }
        }

        public void Start()
        {
            if (!_config.Enabled)
            {
                return;
            }

            if (_thread != null && _thread.IsAlive)
            {
                return;
            }

            _stopRequested = false;
            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.Name = "FlowProcessor";
            _thread.Start();
        }

        public void Stop()
        {
            _stopRequested = true;
            _wake.Set();

            if (_thread != null && !_thread.Join(3000))
            {
                try
                {
                    _thread.Abort();
                }
                catch
                {
                }
            }

            _thread = null;
        }

        public void Post(MarketSnapshot snapshot)
        {
            if (!_config.Enabled || snapshot == null || string.IsNullOrWhiteSpace(snapshot.Asset))
            {
                return;
            }

            lock (_queueLock)
            {
                while (_queue.Count >= _config.MaxQueueSize)
                {
                    _queue.Dequeue();
                    Interlocked.Increment(ref _dropped);
                }

                _queue.Enqueue(snapshot.Clone());
            }

            _wake.Set();
        }

        public void PostTrade(TradePrint trade, MarketSnapshot snapshot)
        {
            if (!_config.Enabled || trade == null || string.IsNullOrWhiteSpace(trade.Asset))
            {
                return;
            }

            try
            {
                FlowEngine engine;

                lock (_stateLock)
                {
                    if (!_engines.TryGetValue(trade.Asset, out engine))
                    {
                        engine = new FlowEngine(_tickSize, _config);
                        _engines[trade.Asset] = engine;
                    }
                }

                FlowUpdate update;

                lock (engine)
                {
                    update = engine.ProcessTrade(trade, snapshot);
                }

                ApplyUpdate(trade.Asset, update);
                Interlocked.Increment(ref _processed);
            }
            catch (Exception ex)
            {
                if (_log != null)
                {
                    _log.Error("Falha ao processar TimesAndTrades real.", ex);
                }
            }
        }

        public FlowMetrics GetMetrics(string asset)
        {
            lock (_stateLock)
            {
                FlowMetrics metrics;

                if (_metrics.TryGetValue(asset ?? string.Empty, out metrics))
                {
                    return metrics;
                }
            }

            return null;
        }

        public List<TradePrint> GetTrades(string asset, int max)
        {
            lock (_stateLock)
            {
                IEnumerable<TradePrint> source = _trades;

                if (!string.IsNullOrWhiteSpace(asset))
                {
                    source = source.Where(x => string.Equals(x.Asset, asset, StringComparison.OrdinalIgnoreCase));
                }

                return source.Take(Math.Max(1, max)).ToList();
            }
        }

        public List<FlowSignal> GetSignals(string asset, int max)
        {
            lock (_stateLock)
            {
                IEnumerable<FlowSignal> source = _signals;

                if (!string.IsNullOrWhiteSpace(asset))
                {
                    source = source.Where(x => string.Equals(x.Asset, asset, StringComparison.OrdinalIgnoreCase));
                }

                return source.Take(Math.Max(1, max)).ToList();
            }
        }

        public void Dispose()
        {
            Stop();
            _wake.Dispose();
        }

        private void Run()
        {
            while (!_stopRequested)
            {
                try
                {
                    _wake.WaitOne(_config.BroadcastIntervalMs);
                    List<MarketSnapshot> snapshots = TakeLatestSnapshots();

                    if (snapshots.Count == 0)
                    {
                        continue;
                    }

                    SleepCoalescingWindow();
                    List<MarketSnapshot> newer = TakeLatestSnapshots();

                    foreach (MarketSnapshot snapshot in newer)
                    {
                        int existing = snapshots.FindIndex(x => string.Equals(x.Asset, snapshot.Asset, StringComparison.OrdinalIgnoreCase));

                        if (existing >= 0)
                        {
                            snapshots[existing] = snapshot;
                        }
                        else
                        {
                            snapshots.Add(snapshot);
                        }
                    }

                    foreach (MarketSnapshot snapshot in snapshots)
                    {
                        ProcessSnapshot(snapshot);
                    }
                }
                catch (ThreadAbortException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (_log != null)
                    {
                        _log.Error("Falha no FlowProcessor.", ex);
                    }
                }
            }
        }

        private void SleepCoalescingWindow()
        {
            int waited = 0;

            while (!_stopRequested && waited < _config.CoalescingMs)
            {
                Thread.Sleep(Math.Min(25, _config.CoalescingMs - waited));
                waited += 25;
            }
        }

        private List<MarketSnapshot> TakeLatestSnapshots()
        {
            lock (_queueLock)
            {
                List<MarketSnapshot> snapshots = new List<MarketSnapshot>();

                if (_queue.Count == 0)
                {
                    return snapshots;
                }

                Dictionary<string, MarketSnapshot> byAsset = new Dictionary<string, MarketSnapshot>(StringComparer.OrdinalIgnoreCase);

                while (_queue.Count > 0)
                {
                    MarketSnapshot snapshot = _queue.Dequeue();

                    if (snapshot != null && !string.IsNullOrWhiteSpace(snapshot.Asset))
                    {
                        byAsset[snapshot.Asset] = snapshot;
                    }
                }

                snapshots.AddRange(byAsset.Values);
                return snapshots;
            }
        }

        private void ProcessSnapshot(MarketSnapshot snapshot)
        {
            FlowEngine engine;

            lock (_stateLock)
            {
                if (!_engines.TryGetValue(snapshot.Asset, out engine))
                {
                    engine = new FlowEngine(_tickSize, _config);
                    _engines[snapshot.Asset] = engine;
                }
            }

            FlowUpdate update;

            lock (engine)
            {
                update = engine.Process(snapshot);
            }

            ApplyUpdate(snapshot.Asset, update);
            Interlocked.Increment(ref _processed);
        }

        private void ApplyUpdate(string asset, FlowUpdate update)
        {
            if (update == null)
            {
                return;
            }

            lock (_stateLock)
            {
                if (update.Metrics != null)
                {
                    _metrics[asset] = update.Metrics;
                }

                if (update.Trade != null)
                {
                    _trades.Insert(0, update.Trade);
                }

                if (update.Signals != null)
                {
                    foreach (FlowSignal signal in update.Signals)
                    {
                        _signals.Insert(0, signal);
                    }
                }

                TrimNewestFirst(_trades, _config.MaxTradeBuffer);
                TrimNewestFirst(_signals, 1000);
            }
        }

        private void TrimNewestFirst<T>(List<T> items, int max)
        {
            if (items.Count <= max)
            {
                return;
            }

            items.RemoveRange(max, items.Count - max);
        }
    }
}
