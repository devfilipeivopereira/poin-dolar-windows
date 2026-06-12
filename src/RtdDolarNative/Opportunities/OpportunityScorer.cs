using System;
using System.Collections.Generic;
using System.Linq;
using RtdDolarNative.Flow;
using RtdDolarNative.MarketData;
using RtdDolarNative.Quant;

namespace RtdDolarNative.Opportunities
{
    public static class OpportunityScorer
    {
        public static OpportunityScoreResult Score(
            OpportunityAssetState asset,
            MarketSnapshot snapshot,
            FlowMetrics metrics,
            FlowSignal flowSignal,
            QuantSignal quantSignal,
            KeyLevel nearestLevel,
            OpportunityScoringContext context)
        {
            OpportunityScoringContext cfg = context ?? new OpportunityScoringContext();
            List<string> evidence = new List<string>();
            int score = Math.Max(flowSignal == null ? 0 : flowSignal.Score, quantSignal == null ? 0 : QuantFlowAdjustedScore(quantSignal, metrics));
            int cap = OpportunityScoreCap(asset, snapshot, metrics, quantSignal != null, cfg, evidence);
            string direction = OpportunityDirection(flowSignal, quantSignal);
            int confirmations = 0;

            if (flowSignal != null)
            {
                confirmations++;
                evidence.Add("setup fluxo " + flowSignal.Score);
            }

            if (quantSignal != null)
            {
                confirmations++;
                evidence.Add("setup quant " + QuantFlowAdjustedScore(quantSignal, metrics));
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
                    cap = Math.Min(cap, 80);
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

            int sampleSize = Math.Max(0, cfg.HistoricalSampleSize);

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

            BacktestRow bestBacktest = BestBacktestRow(direction, cfg.Backtest);

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

            if (nearestLevel != null && Math.Abs(nearestLevel.Distance) <= cfg.TickSize * 10m)
            {
                score += 3;
                evidence.Add("preco perto de nivel");
            }

            if (cfg.DroppedEvents > 0)
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

            OpportunityScoreResult result = new OpportunityScoreResult();
            result.Score = score;
            result.Cap = cap;
            result.Confirmations = confirmations;
            result.FlowConfirms = flowConfirms;
            result.Robustness = OpportunityRobustness(score, cap, confirmations, quantSignal, metrics, flowConfirms, cfg);
            result.Detail = string.Join("; ", evidence.Where(x => !string.IsNullOrWhiteSpace(x)).Take(12).ToArray());
            return result;
        }

        public static int QuantFlowAdjustedScore(QuantSignal signal, FlowMetrics metrics)
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

        public static bool QuantSignalHasPositiveEdge(QuantSignal signal)
        {
            return signal != null &&
                   signal.ExpectancyPoints.HasValue &&
                   signal.ExpectancyPoints.Value > 0m &&
                   signal.ReversalRate >= 58d &&
                   signal.ProfitFactor >= 1.25d &&
                   signal.Confidence >= 45d &&
                   signal.RiskReward >= 1m;
        }

        public static bool QuantSignalHasUsableEdge(QuantSignal signal)
        {
            return signal != null &&
                   signal.ExpectancyPoints.HasValue &&
                   signal.ExpectancyPoints.Value > 0m &&
                   signal.ReversalRate >= 52d &&
                   signal.ProfitFactor >= 1.05d &&
                   signal.Confidence >= 30d &&
                   signal.RiskReward >= 0.85m;
        }

        private static int OpportunityScoreCap(OpportunityAssetState asset, MarketSnapshot snapshot, FlowMetrics metrics, bool requiresHistory, OpportunityScoringContext cfg, List<string> evidence)
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
                cap = Math.Min(cap, Math.Min(cfg.TopOfBookOnlyScoreCap, 62));
                evidence.Add("cap top-of-book");
            }
            else if (metrics.DataQuality == MarketDataQuality.DerivedTape)
            {
                cap = Math.Min(cap, Math.Min(cfg.DerivedTapeScoreCap, 70));
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

            if (requiresHistory && cfg.HistoricalSampleSize < 21)
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

        private static string OpportunityRobustness(int score, int cap, int confirmations, QuantSignal quantSignal, FlowMetrics metrics, bool flowConfirms, OpportunityScoringContext cfg)
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

            if (score >= cfg.StrongSetupScoreThreshold && confirmations >= 3 && (flowConfirms || quantEdgeUsable))
            {
                return "Acionavel";
            }

            if (score >= cfg.SetupScoreThreshold)
            {
                return "Monitorar";
            }

            return "Fraco";
        }

        private static BacktestRow BestBacktestRow(string direction, List<BacktestRow> backtest)
        {
            if (backtest == null || backtest.Count == 0)
            {
                return null;
            }

            return backtest
                .Where(x => string.IsNullOrWhiteSpace(direction) || string.Equals(x.Direction, direction, StringComparison.OrdinalIgnoreCase))
                .Where(x => x.Touches > 0)
                .OrderByDescending(x => x.ExpectancyPoints)
                .ThenByDescending(x => x.ReversalRate)
                .FirstOrDefault();
        }

        private static bool SameDirection(string left, string right)
        {
            return !string.IsNullOrWhiteSpace(left) &&
                   !string.IsNullOrWhiteSpace(right) &&
                   string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static string OpportunityDirection(FlowSignal flowSignal, QuantSignal quantSignal)
        {
            if (flowSignal != null && !string.IsNullOrWhiteSpace(flowSignal.Direction))
            {
                return flowSignal.Direction;
            }

            return quantSignal == null ? string.Empty : quantSignal.Direction;
        }

        private static string FlowDirectionAlignment(string direction, FlowMetrics metrics)
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

        private static bool HasProfileReference(FlowSignal flowSignal, QuantSignal quantSignal, KeyLevel nearestLevel)
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
    }
}
