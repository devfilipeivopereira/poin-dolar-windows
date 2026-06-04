using System;
using System.Collections.Generic;
using System.Linq;
using RtdDolarNative.Config;

namespace RtdDolarNative.Flow
{
    public sealed class SetupDetector
    {
        private readonly FlowConfig _config;
        private readonly decimal _tickSize;
        private readonly Dictionary<string, DateTimeOffset> _lastSignals = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        public SetupDetector(decimal tickSize, FlowConfig config)
        {
            _tickSize = tickSize <= 0m ? 0.5m : tickSize;
            _config = config ?? new FlowConfig();
            _config.Normalize();
        }

        public List<FlowSignal> Detect(FlowMetrics metrics, TradePrint trade)
        {
            List<FlowSignal> signals = new List<FlowSignal>();

            if (metrics == null || !metrics.Price.HasValue)
            {
                return signals;
            }

            decimal price = metrics.Price.Value;
            ProfileLevel nearest = FindNearestProfileLevel(metrics.Profile, price, 6m);
            FlowWindowMetrics w1 = FindWindow(metrics, 1);
            FlowWindowMetrics w5 = FindWindow(metrics, 5);
            FlowWindowMetrics w15 = FindWindow(metrics, 15);
            decimal delta1 = w1 == null ? 0m : w1.Delta;
            decimal delta5 = w5 == null ? 0m : w5.Delta;
            decimal delta15 = w15 == null ? 0m : w15.Delta;
            decimal imbalance = metrics.TopBookImbalance.HasValue ? metrics.TopBookImbalance.Value : 0m;
            decimal microBias = metrics.MicroBias.HasValue ? metrics.MicroBias.Value : 0m;

            if (trade != null)
            {
                DetectAbsorption(signals, metrics, trade, nearest, delta5, imbalance);
                DetectBreakout(signals, metrics, trade, delta15);
                DetectLvnRejection(signals, metrics, trade, nearest, delta1, microBias);
                DetectPocDefenseOrLoss(signals, metrics, trade, nearest, delta5);
            }

            DetectVwapSetups(signals, metrics, delta1, delta5);
            return signals;
        }

        private void DetectAbsorption(List<FlowSignal> signals, FlowMetrics metrics, TradePrint trade, ProfileLevel nearest, decimal delta5, decimal imbalance)
        {
            if (nearest == null || !IsReferenceLevel(nearest.Type))
            {
                return;
            }

            if (trade.Aggressor == "Sell" && delta5 < 0m && imbalance > 0.15m)
            {
                AddSignal(signals, metrics, "Absorcao compradora", "Buy", 62 + LevelBonus(nearest) + BiasBonus(imbalance), nearest, "venda agredindo nivel, book comprador defendendo, delta 5s negativo");
            }
            else if (trade.Aggressor == "Buy" && delta5 > 0m && imbalance < -0.15m)
            {
                AddSignal(signals, metrics, "Absorcao vendedora", "Sell", 62 + LevelBonus(nearest) + BiasBonus(imbalance), nearest, "compra agredindo nivel, book vendedor defendendo, delta 5s positivo");
            }
        }

        private void DetectBreakout(List<FlowSignal> signals, FlowMetrics metrics, TradePrint trade, decimal delta15)
        {
            if (metrics.Profile == null || !metrics.Profile.Vah.HasValue || !metrics.Profile.Val.HasValue)
            {
                return;
            }

            decimal price = metrics.Price.Value;

            if (price > metrics.Profile.Vah.Value + _tickSize && trade.Aggressor == "Buy" && delta15 > Math.Max(3m, trade.Quantity))
            {
                ProfileLevel level = new ProfileLevel { Type = "vah", Label = "VAH", Price = metrics.Profile.Vah.Value, Score = 88d, Source = "Volume Profile" };
                AddSignal(signals, metrics, "Rompimento com fluxo", "Buy", 72, level, "preco aceitando acima da VAH com delta 15s comprador");
            }
            else if (price < metrics.Profile.Val.Value - _tickSize && trade.Aggressor == "Sell" && delta15 < -Math.Max(3m, trade.Quantity))
            {
                ProfileLevel level = new ProfileLevel { Type = "val", Label = "VAL", Price = metrics.Profile.Val.Value, Score = 88d, Source = "Volume Profile" };
                AddSignal(signals, metrics, "Rompimento com fluxo", "Sell", 72, level, "preco aceitando abaixo da VAL com delta 15s vendedor");
            }
        }

        private void DetectLvnRejection(List<FlowSignal> signals, FlowMetrics metrics, TradePrint trade, ProfileLevel nearest, decimal delta1, decimal microBias)
        {
            if (nearest == null || !string.Equals(nearest.Type, "lvn", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (trade.Aggressor == "Sell" && delta1 < 0m && microBias > 0m)
            {
                AddSignal(signals, metrics, "Rejeicao em LVN", "Buy", 69 + LevelBonus(nearest), nearest, "LVN rejeitado, venda perdeu resposta no microprice");
            }
            else if (trade.Aggressor == "Buy" && delta1 > 0m && microBias < 0m)
            {
                AddSignal(signals, metrics, "Rejeicao em LVN", "Sell", 69 + LevelBonus(nearest), nearest, "LVN rejeitado, compra perdeu resposta no microprice");
            }
        }

        private void DetectPocDefenseOrLoss(List<FlowSignal> signals, FlowMetrics metrics, TradePrint trade, ProfileLevel nearest, decimal delta5)
        {
            if (nearest == null || !string.Equals(nearest.Type, "poc", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (trade.Aggressor == "Sell" && delta5 < 0m && metrics.Price.Value >= nearest.Price - _tickSize)
            {
                AddSignal(signals, metrics, "Defesa de POC", "Buy", 74, nearest, "POC segurando agressao vendedora");
            }
            else if (trade.Aggressor == "Buy" && delta5 > 0m && metrics.Price.Value <= nearest.Price + _tickSize)
            {
                AddSignal(signals, metrics, "Defesa de POC", "Sell", 74, nearest, "POC segurando agressao compradora");
            }
            else if (metrics.Price.Value > nearest.Price + (_tickSize * 3m) && delta5 > 0m)
            {
                AddSignal(signals, metrics, "Perda de POC", "Buy", 68, nearest, "POC perdido para cima com delta comprador");
            }
            else if (metrics.Price.Value < nearest.Price - (_tickSize * 3m) && delta5 < 0m)
            {
                AddSignal(signals, metrics, "Perda de POC", "Sell", 68, nearest, "POC perdido para baixo com delta vendedor");
            }
        }

        private void DetectVwapSetups(List<FlowSignal> signals, FlowMetrics metrics, decimal delta1, decimal delta5)
        {
            if (!metrics.Vwap.HasValue || !metrics.Price.HasValue)
            {
                return;
            }

            decimal distanceTicks = (metrics.Price.Value - metrics.Vwap.Value) / _tickSize;

            if (distanceTicks >= 8m && delta1 < 0m)
            {
                ProfileLevel vwap = new ProfileLevel { Type = "vwap", Label = "VWAP", Price = metrics.Vwap.Value, Score = 80d, Source = "Order Flow" };
                AddSignal(signals, metrics, "VWAP reversion", "Sell", 64 + Math.Min(10, (int)Math.Abs(distanceTicks / 4m)), vwap, "preco esticado acima da VWAP e delta curto virou vendedor");
            }
            else if (distanceTicks <= -8m && delta1 > 0m)
            {
                ProfileLevel vwap = new ProfileLevel { Type = "vwap", Label = "VWAP", Price = metrics.Vwap.Value, Score = 80d, Source = "Order Flow" };
                AddSignal(signals, metrics, "VWAP reversion", "Buy", 64 + Math.Min(10, (int)Math.Abs(distanceTicks / 4m)), vwap, "preco esticado abaixo da VWAP e delta curto virou comprador");
            }
            else if (Math.Abs(distanceTicks) <= 4m && delta5 > 0m)
            {
                ProfileLevel vwap = new ProfileLevel { Type = "vwap", Label = "VWAP", Price = metrics.Vwap.Value, Score = 80d, Source = "Order Flow" };
                AddSignal(signals, metrics, "VWAP continuation", "Buy", 61, vwap, "rotacao perto da VWAP com delta 5s comprador");
            }
            else if (Math.Abs(distanceTicks) <= 4m && delta5 < 0m)
            {
                ProfileLevel vwap = new ProfileLevel { Type = "vwap", Label = "VWAP", Price = metrics.Vwap.Value, Score = 80d, Source = "Order Flow" };
                AddSignal(signals, metrics, "VWAP continuation", "Sell", 61, vwap, "rotacao perto da VWAP com delta 5s vendedor");
            }
        }

        private void AddSignal(List<FlowSignal> signals, FlowMetrics metrics, string setup, string direction, int baseScore, ProfileLevel level, string reason)
        {
            int score = CapScore(baseScore, metrics.DataQuality);

            if (score < _config.SetupScoreThreshold)
            {
                return;
            }

            string key = metrics.Asset + "|" + setup + "|" + direction + "|" + (level == null ? string.Empty : level.Label);
            DateTimeOffset now = metrics.LocalTimestamp;
            DateTimeOffset last;

            if (_lastSignals.TryGetValue(key, out last) && (now - last).TotalMilliseconds < _config.SignalCooldownMs)
            {
                return;
            }

            _lastSignals[key] = now;

            FlowSignal signal = new FlowSignal();
            signal.Asset = metrics.Asset;
            signal.LocalTimestamp = now;
            signal.Setup = setup;
            signal.Direction = direction;
            signal.Price = metrics.Price.Value;
            signal.Score = score;
            signal.LevelName = level == null ? "-" : level.Label + " (" + level.Type + ")";
            signal.LevelPrice = level == null ? (decimal?)null : level.Price;
            signal.Reasons = reason + "; qualidade " + metrics.DataQuality + (metrics.Derived ? "; derived" : string.Empty);
            signal.Derived = metrics.Derived;
            signal.DataQuality = metrics.DataQuality;
            signal.CooldownKey = key;
            signals.Add(signal);
        }

        private ProfileLevel FindNearestProfileLevel(VolumeProfileMetrics profile, decimal price, decimal maxTicks)
        {
            if (profile == null || profile.Levels == null || profile.Levels.Count == 0)
            {
                return null;
            }

            decimal maxDistance = maxTicks * _tickSize;
            return profile.Levels
                .Where(x => Math.Abs(x.Price - price) <= maxDistance)
                .OrderBy(x => Math.Abs(x.Price - price))
                .ThenByDescending(x => x.Score)
                .FirstOrDefault();
        }

        private FlowWindowMetrics FindWindow(FlowMetrics metrics, int seconds)
        {
            if (metrics.Windows == null)
            {
                return null;
            }

            return metrics.Windows.FirstOrDefault(x => x.Seconds == seconds);
        }

        private bool IsReferenceLevel(string type)
        {
            return string.Equals(type, "poc", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "vah", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "val", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "hvn", StringComparison.OrdinalIgnoreCase);
        }

        private int LevelBonus(ProfileLevel level)
        {
            if (level == null)
            {
                return 0;
            }

            if (string.Equals(level.Type, "poc", StringComparison.OrdinalIgnoreCase))
            {
                return 9;
            }

            if (string.Equals(level.Type, "vah", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(level.Type, "val", StringComparison.OrdinalIgnoreCase))
            {
                return 7;
            }

            if (string.Equals(level.Type, "hvn", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(level.Type, "lvn", StringComparison.OrdinalIgnoreCase))
            {
                return 5;
            }

            return 0;
        }

        private int BiasBonus(decimal value)
        {
            return (int)Math.Min(8m, Math.Abs(value) * 20m);
        }

        private int CapScore(int score, MarketDataQuality quality)
        {
            int cap = 100;

            if (quality == MarketDataQuality.TopOfBookOnly)
            {
                cap = _config.TopOfBookOnlyScoreCap;
            }
            else if (quality == MarketDataQuality.DerivedTape)
            {
                cap = _config.DerivedTapeScoreCap;
            }

            return Math.Max(0, Math.Min(cap, score));
        }
    }
}
