using System;
using System.Collections.Generic;
using System.Linq;

namespace RtdDolarNative.MarketData
{
    public sealed class IntradayBarAggregator
    {
        private readonly object _lock = new object();
        private readonly int _maxBars;
        private readonly int[] _timeframes;
        private class SnapshotState
        {
            public decimal Price { get; set; }
            public decimal Volume { get; set; }
        }

        private readonly Dictionary<string, List<IntradayBar>> _bars = new Dictionary<string, List<IntradayBar>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SnapshotState> _lastSnapshots = new Dictionary<string, SnapshotState>(StringComparer.OrdinalIgnoreCase);

        public IntradayBarAggregator(int maxBars, params int[] timeframesSeconds)
        {
            _maxBars = maxBars > 0 ? maxBars : 1200;
            _timeframes = timeframesSeconds != null && timeframesSeconds.Length > 0 ? timeframesSeconds : new[] { 60, 300 };
        }

        public void Add(TickEvent tick)
        {
            if (tick == null || string.IsNullOrWhiteSpace(tick.Asset) || tick.Price <= 0m)
                return;

            lock (_lock)
            {
                foreach (int seconds in _timeframes)
                {
                    string key = tick.Asset + "|" + seconds;
                    List<IntradayBar> list;
                    if (!_bars.TryGetValue(key, out list))
                    {
                        list = new List<IntradayBar>();
                        _bars[key] = list;
                    }

                    long frameTicks = seconds * TimeSpan.TicksPerSecond;
                    DateTimeOffset barStart = TruncateToFrame(tick.LocalTimestamp, frameTicks);

                    IntradayBar currentBar = list.Count > 0 ? list[list.Count - 1] : null;

                    if (currentBar == null || currentBar.Start != barStart)
                    {
                        currentBar = new IntradayBar
                        {
                            Asset = tick.Asset,
                            Start = barStart,
                            Seconds = seconds,
                            Open = tick.Price,
                            High = tick.Price,
                            Low = tick.Price,
                            Close = tick.Price,
                            Volume = tick.Volume ?? 0m,
                            Quantity = tick.Quantity ?? 0m,
                            TradeCount = 1,
                            Delta = tick.Delta
                        };
                        list.Add(currentBar);

                        if (list.Count > _maxBars)
                        {
                            list.RemoveAt(0);
                        }
                    }
                    else
                    {
                        currentBar.High = Math.Max(currentBar.High, tick.Price);
                        currentBar.Low = Math.Min(currentBar.Low, tick.Price);
                        currentBar.Close = tick.Price;
                        currentBar.Volume += tick.Volume ?? 0m;
                        currentBar.Quantity += tick.Quantity ?? 0m;
                        currentBar.TradeCount++;
                        currentBar.Delta += tick.Delta;
                    }
                }
            }
        }

        public void AddFromSnapshot(MarketSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Asset) || !snapshot.Ultimo.HasValue || snapshot.Ultimo.Value <= 0m)
                return;

            string asset = snapshot.Asset;
            decimal price = snapshot.Ultimo.Value;
            decimal volume = snapshot.Volume ?? 0m;
            decimal quantity = snapshot.Quantidade ?? 0m;

            lock (_lock)
            {
                SnapshotState last;
                _lastSnapshots.TryGetValue(asset, out last);
                if (last != null && last.Price == price && last.Volume == volume)
                {
                    return; // Nothing changed
                }

                decimal diffVolume = last != null && last.Volume > 0 && volume > last.Volume ? volume - last.Volume : 0m;
                decimal diffQuantity = last != null && last.Volume > 0 && quantity > last.Volume ? quantity - last.Volume : 0m;

                _lastSnapshots[asset] = new SnapshotState { Price = price, Volume = volume };

                TickEvent tick = new TickEvent
                {
                    Asset = asset,
                    Price = price,
                    Volume = diffVolume > 0m ? diffVolume : (diffQuantity > 0m ? diffQuantity : 1m),
                    Quantity = diffQuantity > 0m ? diffQuantity : 1m,
                    LocalTimestamp = DateTimeOffset.Now,
                    Delta = 0m // neutral since we can't infer side from snapshot alone
                };
                Add(tick);
            }
        }

        public List<IntradayBar> GetBars(string asset, int seconds)
        {
            if (string.IsNullOrWhiteSpace(asset))
                return new List<IntradayBar>();

            string key = asset + "|" + seconds;
            lock (_lock)
            {
                List<IntradayBar> list;
                if (_bars.TryGetValue(key, out list))
                {
                    return list.ToList(); // return copy
                }
            }
            return new List<IntradayBar>();
        }

        public void ResetAsset(string asset)
        {
            if (string.IsNullOrWhiteSpace(asset))
                return;

            lock (_lock)
            {
                foreach (int seconds in _timeframes)
                {
                    string key = asset + "|" + seconds;
                    _bars.Remove(key);
                }
                _lastSnapshots.Remove(asset);
            }
        }

        private static DateTimeOffset TruncateToFrame(DateTimeOffset timestamp, long frameTicks)
        {
            long ticks = timestamp.Ticks / frameTicks * frameTicks;
            return new DateTimeOffset(ticks, timestamp.Offset);
        }
    }
}
