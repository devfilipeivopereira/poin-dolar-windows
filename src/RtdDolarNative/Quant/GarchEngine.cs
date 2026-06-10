using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RtdDolarNative.Csv;
using RtdDolarNative.MarketData;

namespace RtdDolarNative.Quant
{
    public static class GarchEngine
    {
        private const double MinimumVariance = 1e-12d;

        public static GarchSnapshot Build(
            List<DailyBar> dailyBars,
            IntradayContext intraday,
            MarketSnapshot snapshot,
            IEnumerable<TickEvent> ticks,
            decimal tickSize,
            GarchConfig config = null)
        {
            if (config == null)
            {
                config = new GarchConfig();
            }
            config.Normalize();

            List<IntradayBar> intradayBars = new List<IntradayBar>();
            if (ticks != null)
            {
                string source;
                List<DailyBar> tempBars = BuildIntradayBarsFromTicks(ticks, intraday, config, out source);
                foreach (var b in tempBars)
                {
                    intradayBars.Add(new IntradayBar
                    {
                        Asset = snapshot != null ? snapshot.Asset : "",
                        Start = new DateTimeOffset(b.Date),
                        Seconds = config.IntradayTimeframeSeconds,
                        Open = b.Open,
                        High = b.High,
                        Low = b.Low,
                        Close = b.Close,
                        Volume = b.Volume ?? 0m
                    });
                }
            }

            return Build(dailyBars, intraday, snapshot, intradayBars, tickSize, config);
        }

        public static GarchSnapshot Build(
            List<DailyBar> dailyBars,
            IntradayContext intraday,
            MarketSnapshot snapshot,
            List<IntradayBar> intradayBars,
            decimal tickSize,
            GarchConfig config)
        {
            if (config == null)
            {
                config = new GarchConfig();
            }
            config.Normalize();

            GarchSnapshot result = new GarchSnapshot();

            if (dailyBars == null)
            {
                dailyBars = new List<DailyBar>();
            }

            result.CurrentPrice = ResolveCurrentPrice(dailyBars, intraday, snapshot, result);

            List<double> dailyReturns = BuildDailyReturns(dailyBars);
            decimal dailyReference;
            string dailySource;
            dailyReference = ResolveDailyReference(dailyBars, snapshot, out dailySource);
            result.DailyReference = dailyReference;
            result.DailyReferenceName = dailySource;
            result.DailyFit = EstimateScope("Diario", dailyReturns, dailyReference, result.CurrentPrice, config.DailyMinSamples, config);
            if (!result.DailyFit.Success)
            {
                result.Warnings.Add("GARCH diario: " + result.DailyFit.Status);
            }
            else
            {
                result.DailyBands = BuildBands("Diario", dailyReference, result.CurrentPrice, result.DailyFit, config.BandMultipliers, tickSize);
                result.DailySigmaPoints = Dec(Math.Abs(result.DailyFit.NextSigma * Double(dailyReference)));
                result.DailySigmaPoints = RoundToTick(Abs(result.DailySigmaPoints), tickSize);
                result.ZDaily = ComputeZ(result.CurrentPrice, dailyReference, result.DailySigmaPoints);
            }

            string intradaySource;
            decimal intradayReference = ResolveIntradayReference(snapshot, intraday, dailyBars, out intradaySource);
            result.IntradayReference = intradayReference;
            result.IntradayReferenceName = intradaySource;

            List<DailyBar> convertedBars = ConvertIntradayToDailyBars(intradayBars);
            if (convertedBars.Count == 0)
            {
                result.Warnings.Add("GARCH intraday aguardando ticks.");
            }
            else
            {
                result.Warnings.Add("GARCH intraday origem: Tick " + config.IntradayTimeframeSeconds.ToString() + "s");
            }

            List<double> intradayReturns = BuildDailyReturns(convertedBars);
            result.IntradayFit = EstimateScope("Intraday", intradayReturns, intradayReference, result.CurrentPrice, config.IntradayMinBars, config);

            if (!result.IntradayFit.Success)
            {
                result.Warnings.Add("GARCH intraday: " + result.IntradayFit.Status);
            }
            else
            {
                result.IntradayBands = BuildBands("Intraday", intradayReference, result.CurrentPrice, result.IntradayFit, config.BandMultipliers, tickSize);
                result.IntradaySigmaPoints = Dec(Math.Abs(result.IntradayFit.NextSigma * Double(intradayReference)));
                result.IntradaySigmaPoints = RoundToTick(Abs(result.IntradaySigmaPoints), tickSize);
                result.ZIntraday = ComputeZ(result.CurrentPrice, intradayReference, result.IntradaySigmaPoints);
            }

            if (result.DailyFit.Success || result.IntradayFit.Success)
            {
                result.CombinedRead = BuildCombinedRead(
                    dailyReference,
                    intradayReference,
                    result.ZDaily,
                    result.ZIntraday,
                    result.DailyFit,
                    result.IntradayFit);
            }
            else
            {
                result.CombinedRead = "Aguardando ajuste estatistico.";
            }

            // Build Signals and Backtests
            result.Signals = BuildSignals(result, config, tickSize);
            if (result.DailyFit.Success)
            {
                result.Backtest.AddRange(BuildBacktest(dailyBars, result.DailyFit, config, tickSize));
            }
            if (result.IntradayFit.Success && convertedBars.Count > 0)
            {
                result.Backtest.AddRange(BuildBacktest(convertedBars, result.IntradayFit, config, tickSize));
            }

            return result;
        }

        private static decimal ResolveCurrentPrice(List<DailyBar> dailyBars, IntradayContext intraday, MarketSnapshot snapshot, GarchSnapshot result)
        {
            if (intraday != null && intraday.Price > 0m)
            {
                return intraday.Price;
            }

            if (snapshot != null && snapshot.Ultimo.HasValue && snapshot.Ultimo.Value > 0m)
            {
                return snapshot.Ultimo.Value;
            }

            if (dailyBars != null && dailyBars.Count > 0)
            {
                decimal close = dailyBars[dailyBars.Count - 1].Close;
                if (close > 0m)
                {
                    return close;
                }
            }

            return 0.5m;
        }

        private static GarchFitResult EstimateScope(
            string scope,
            List<double> returns,
            decimal reference,
            decimal currentPrice,
            int minSamples,
            GarchConfig config)
        {
            GarchFitResult fit = new GarchFitResult
            {
                Scope = scope,
                CalculatedAt = DateTimeOffset.UtcNow
            };

            if (reference <= 0m)
            {
                fit.Success = false;
                fit.Status = "Referencia invalida para " + scope;
                fit.Warning = "Referencia invalida.";
                return fit;
            }

            if (returns == null || returns.Count == 0)
            {
                fit.Success = false;
                fit.Status = "Sem retornos para " + scope;
                fit.Warning = fit.Status;
                fit.Samples = 0;
                return fit;
            }

            if (returns.Count < minSamples)
            {
                fit.Success = false;
                fit.Samples = returns.Count;
                fit.Status = scope + " sem amostra suficiente (" + returns.Count.ToString(CultureInfo.InvariantCulture) + ")";
                fit.Warning = scope + " com baixa amostra. minimo " + minSamples.ToString(CultureInfo.InvariantCulture);
                fit.Iterations = 0;
                return fit;
            }

            double variance = Variance(returns);
            if (variance < MinimumVariance)
            {
                fit.Success = false;
                fit.Samples = returns.Count;
                fit.Status = "Variancia abaixo do limiar para " + scope;
                fit.Warning = "Serie estatisticamente quase constante";
                return fit;
            }

            List<double> centered = returns.Select(x => x - returns.Average()).ToList();

            double mean = returns.Average();
            fit.Mu = mean;
            fit.Samples = centered.Count;

            FitScope(centered, config, fit);
            if (!fit.Success)
            {
                return fit;
            }

            fit.HalfLifePeriods = ComputeHalfLife(fit.Persistence);
            fit.LongRunVariance = fit.Omega / Math.Max(MinimumVariance, 1d - fit.Persistence);
            fit.LongRunSigma = Math.Sqrt(fit.LongRunVariance);



            if (fit.NextSigma > 0d)
            {
                double sigmaPoints = Math.Abs(fit.NextSigma * Double(reference));
                double z = (Double(currentPrice) - Double(reference)) / Math.Max(MinimumVariance, sigmaPoints);

                if (Math.Abs(z) >= (scope.Equals("Intraday", StringComparison.OrdinalIgnoreCase) ? config.ExtremeAbsZIntraday : config.ReversionMinAbsZDaily))
                {
                    fit.Warning = scope + " com desvio relevante em " + scope.ToLowerInvariant() + ".";
                }
                else
                {
                    fit.Warning = string.Empty;
                }
            }

            fit.Status = "Convergido";
            return fit;
        }

        private static void FitScope(List<double> centered, GarchConfig config, GarchFitResult fit)
        {
            double sampleVariance = Variance(centered);
            if (sampleVariance <= MinimumVariance)
            {
                fit.Success = false;
                fit.Status = "Variancia invalida para ajuste";
                return;
            }

            double bestLL = double.NegativeInfinity;
            double bestAlpha = 0.08d;
            double bestBeta = 0.88d;
            double bestLastVariance = sampleVariance;
            double bestLastError = 0d;

            for (double alpha = 0.01d; alpha <= 0.20d; alpha += 0.01d)
            {
                for (double beta = 0.45d; beta <= Math.Min(config.StationarityCap - 1e-4d, 0.98d); beta += 0.01d)
                {
                    double alphaBeta = alpha + beta;
                    if (alphaBeta >= config.StationarityCap || alpha < 0d || beta < 0d)
                    {
                        continue;
                    }

                    double omega = sampleVariance * Math.Max(0.0005d, 1d - alphaBeta);
                    if (omega <= 0d)
                    {
                        continue;
                    }

                    GarchFitResult candidate = new GarchFitResult
                    {
                        Omega = omega,
                        Alpha = alpha,
                        Beta = beta,
                        Mu = centered.Average()
                    };

                    double lastVariance;
                    double lastError;
                    int used;
                    double ll = EvaluateLogLikelihood(centered, candidate, out lastVariance, out lastError, out used);
                    if (double.IsNaN(ll) || double.IsInfinity(ll))
                    {
                        continue;
                    }

                    if (ll > bestLL)
                    {
                        bestLL = ll;
                        bestAlpha = alpha;
                        bestBeta = beta;
                        bestLastVariance = lastVariance;
                        bestLastError = lastError;
                    }
                }
            }

            if (double.IsNegativeInfinity(bestLL))
            {
                fit.Success = false;
                fit.Status = "Falha no ajuste de parametros";
                fit.Warning = "Nao foi possivel convergir GARCH.";
                return;
            }

            fit.Alpha = bestAlpha;
            fit.Beta = bestBeta;
            fit.Omega = sampleVariance * Math.Max(0.0001d, 1d - bestAlpha - bestBeta);
            fit.Persistence = Math.Min(config.StationarityCap - 1e-6d, fit.Alpha + fit.Beta);
            fit.NegativeLogLikelihood = -bestLL;
            fit.Success = true;
            fit.Warning = string.Empty;
            fit.Iterations = Math.Max(1, 1);
            fit.LastVariance = Math.Max(MinimumVariance, bestLastVariance);
            fit.NextVariance = Math.Max(MinimumVariance, fit.Omega + fit.Alpha * bestLastError * bestLastError + fit.Beta * bestLastVariance);
            fit.NextSigma = Math.Sqrt(fit.NextVariance);
        }

        private static double EvaluateLogLikelihood(List<double> returns, GarchFitResult candidate, out double lastVariance, out double lastError, out int usedSamples)
        {
            lastVariance = 1d;
            lastError = 0d;
            usedSamples = 0;

            if (returns == null || returns.Count < 2)
            {
                return double.NegativeInfinity;
            }

            double avg = returns.Average();
            double[] eps = returns.Select(x => x - avg).ToArray();
            double sigma2 = Math.Max(MinimumVariance, Variance(eps.ToList()));
            double prev = eps[0];
            double logLikelihood = 0d;
            usedSamples = 0;

            for (int i = 1; i < eps.Length; i++)
            {
                sigma2 = candidate.Omega + candidate.Alpha * prev * prev + candidate.Beta * sigma2;
                sigma2 = Math.Max(MinimumVariance, sigma2);

                if (double.IsNaN(sigma2) || double.IsInfinity(sigma2))
                {
                    return double.NegativeInfinity;
                }

                logLikelihood += -0.5d * (Math.Log(sigma2) + (eps[i] * eps[i]) / sigma2);
                prev = eps[i];
                usedSamples++;
            }

            lastVariance = sigma2;
            lastError = prev;
            return logLikelihood;
        }

        private static List<GarchBandLevel> BuildBands(string scope, decimal reference, decimal currentPrice, GarchFitResult fit, double[] multipliers, decimal tickSize)
        {
            List<GarchBandLevel> bands = new List<GarchBandLevel>();

            if (fit == null || !fit.Success || multipliers == null || multipliers.Length == 0 || tickSize <= 0m || reference <= 0m)
            {
                return bands;
            }

            foreach (double sigma in multipliers.OrderBy(x => x))
            {
                double positiveSigma = Math.Abs(sigma);
                decimal upperDistanceRef = RoundToTick(reference * Dec(Math.Exp(positiveSigma * fit.NextSigma)), tickSize) - reference;
                decimal lowerDistanceRef = RoundToTick(reference / Dec(Math.Exp(positiveSigma * fit.NextSigma)), tickSize) - reference;
                decimal upper = reference + upperDistanceRef;
                decimal lower = reference + lowerDistanceRef;

                bands.Add(NewGarchBand(scope, reference, currentPrice, fit, upper, "Venda", positiveSigma, upperDistanceRef, positiveSigma, "upper", tickSize));
                bands.Add(NewGarchBand(scope, reference, currentPrice, fit, lower, "Compra", -positiveSigma, lowerDistanceRef, positiveSigma, "lower", tickSize));
            }

            return bands
                .Where(x => x.Price > 0m)
                .OrderBy(x => Math.Abs(x.DistanceCurrent))
                .ToList();
        }

        private static GarchBandLevel NewGarchBand(
            string scope,
            decimal reference,
            decimal currentPrice,
            GarchFitResult fit,
            decimal price,
            string side,
            double sigma,
            decimal distanceReference,
            double zHint,
            string labelSuffix,
            decimal tickSize)
        {
            GarchBandLevel band = new GarchBandLevel();
            band.Scope = scope;
            band.ReferenceName = scope + " " + reference.ToString("N2", CultureInfo.InvariantCulture);
            band.ReferencePrice = reference;
            band.Sigma = sigma;
            band.Price = RoundToTick(price, tickSize);
            band.DistanceReference = distanceReference;
            band.DistanceCurrent = band.Price - currentPrice;
            band.Side = side;
            band.Source = "GARCH-" + scope;
            band.Read = scope + " " + zHint.ToString("N2", CultureInfo.InvariantCulture) + "σ";
            band.ScoreHint = ResolveBandScore(fit, sigma, zHint, side);
            band.Label = "GARCH " + scope + " " + (string.Equals(side, "Compra", StringComparison.OrdinalIgnoreCase) ? "compra" : "venda") + " " + labelSuffix + " " + sigma.ToString("0.##", CultureInfo.InvariantCulture) + "σ";
            return band;
        }

        private static int ResolveBandScore(GarchFitResult fit, double sigma, double zHint, string side)
        {
            int baseScore = 50 + (int)Math.Round(Math.Min(30d, Math.Abs(zHint) * 7d)) + (int)Math.Round(Math.Abs(sigma) * 4d);
            if (fit != null && fit.Success)
            {
                baseScore += fit.Persistence > 0.97d ? 10 : 0;
                baseScore += fit.LongRunSigma > 0d ? 5 : 0;
            }

            if (string.Equals(side, "Venda", StringComparison.OrdinalIgnoreCase))
            {
                baseScore += 2;
            }

            return Math.Max(25, Math.Min(99, baseScore));
        }

        private static double ComputeHalfLife(double persistence)
        {
            if (persistence <= 0d || persistence >= 0.9999d)
            {
                return 0d;
            }

            return Math.Log(0.5d) / Math.Log(persistence);
        }

        private static double ComputeZ(decimal current, decimal reference, decimal sigmaPoints)
        {
            if (sigmaPoints <= 0m)
            {
                return 0d;
            }

            return Double(current - reference) / Double(Math.Max(1m, Math.Abs(sigmaPoints)));
        }

        private static string BuildCombinedRead(decimal dailyReference, decimal intradayReference, double zDaily, double zIntraday, GarchFitResult dailyFit, GarchFitResult intradayFit)
        {
            List<string> parts = new List<string>();

            if (dailyFit != null && dailyFit.Success)
            {
                parts.Add("Diario: " + DescribeFit(dailyFit, zDaily, dailyReference));
            }

            if (intradayFit != null && intradayFit.Success)
            {
                parts.Add("Intraday: " + DescribeFit(intradayFit, zIntraday, intradayReference));
            }

            return parts.Count == 0 ? "Sem leitura estatistica completa." : string.Join(" | ", parts);
        }

        private static string DescribeFit(GarchFitResult fit, double z, decimal reference)
        {
            if (fit == null || !fit.Success)
            {
                return "sem ajuste";
            }

            string regime = Math.Abs(z) >= 2d
                ? "extenso"
                : Math.Abs(z) >= 1d
                    ? "tenso"
                    : "normal";

            return regime +
                   " | z " + z.ToString("0.##", CultureInfo.InvariantCulture) +
                   " ref " + reference.ToString("N2", CultureInfo.InvariantCulture) +
                   " persist " + fit.Persistence.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private static decimal ResolveDailyReference(List<DailyBar> dailyBars, MarketSnapshot snapshot, out string source)
        {
            source = "CSV";

            if (snapshot != null)
            {
                if (snapshot.AjusteAnterior.HasValue && snapshot.AjusteAnterior.Value > 0m)
                {
                    source = "AJA";
                    return snapshot.AjusteAnterior.Value;
                }

                if (snapshot.FechamentoAnterior.HasValue && snapshot.FechamentoAnterior.Value > 0m)
                {
                    source = "FEC";
                    return snapshot.FechamentoAnterior.Value;
                }

                if (snapshot.Ajuste.HasValue && snapshot.Ajuste.Value > 0m)
                {
                    source = "AJU";
                    return snapshot.Ajuste.Value;
                }
            }

            if (snapshot != null && snapshot.Abertura.HasValue && snapshot.Abertura.Value > 0m)
            {
                source = "ABE";
                return snapshot.Abertura.Value;
            }

            if (dailyBars != null && dailyBars.Count >= 2)
            {
                source = "CSV D-1";
                return dailyBars[dailyBars.Count - 2].Close;
            }

            if (dailyBars != null && dailyBars.Count >= 1)
            {
                source = "CSV D0";
                return dailyBars[dailyBars.Count - 1].Close;
            }

            return 0m;
        }

        private static decimal ResolveIntradayReference(MarketSnapshot snapshot, IntradayContext intraday, List<DailyBar> dailyBars, out string source)
        {
            source = "Price";

            if (snapshot != null && snapshot.Media.HasValue && snapshot.Media.Value > 0m)
            {
                source = "MED/VWAP";
                return snapshot.Media.Value;
            }

            if (intraday != null && intraday.Vwap > 0m)
            {
                source = "VWAP";
                return intraday.Vwap;
            }

            if (snapshot != null && snapshot.Abertura.HasValue && snapshot.Abertura.Value > 0m)
            {
                source = "Abertura";
                return snapshot.Abertura.Value;
            }

            if (dailyBars != null && dailyBars.Count >= 1 && dailyBars[dailyBars.Count - 1].Close > 0m)
            {
                source = "Fechamento anterior";
                return dailyBars[dailyBars.Count - 1].Close;
            }

            return 0m;
        }

        private static List<DailyBar> BuildIntradayBarsFromTicks(IEnumerable<TickEvent> ticks, IntradayContext intraday, GarchConfig config, out string source)
        {
            source = "Sem ticks intraday";

            if (ticks == null)
            {
                return new List<DailyBar>();
            }

            List<TickEvent> ordered = ticks
                .Where(t => t != null && t.Price > 0m)
                .OrderBy(t => t.LocalTimestamp)
                .ToList();

            if (ordered.Count < 2)
            {
                return new List<DailyBar>();
            }

            int frame = Math.Max(1, config.IntradayTimeframeSeconds);
            long frameTicks = frame * TimeSpan.TicksPerSecond;
            List<DailyBar> bars = new List<DailyBar>();
            DateTimeOffset current = TruncateToFrame(ordered[0].LocalTimestamp, frameTicks);
            decimal open = ordered[0].Price;
            decimal high = ordered[0].Price;
            decimal low = ordered[0].Price;
            decimal close = ordered[0].Price;
            decimal volume = FirstPositive(ordered[0].Volume, ordered[0].Quantity);

            for (int i = 1; i < ordered.Count; i++)
            {
                TickEvent tick = ordered[i];
                DateTimeOffset bucket = TruncateToFrame(tick.LocalTimestamp, frameTicks);

                if (bucket != current)
                {
                    FlushIntradayBar(bars, current, open, high, low, close, volume);
                    if (bars.Count >= config.MaxIntradayBars)
                    {
                        bars.RemoveAt(0);
                    }

                    current = bucket;
                    open = tick.Price;
                    high = tick.Price;
                    low = tick.Price;
                    close = tick.Price;
                    volume = FirstPositive(tick.Volume, tick.Quantity);
                    continue;
                }

                high = Math.Max(high, tick.Price);
                low = Math.Min(low, tick.Price);
                close = tick.Price;
                volume += FirstPositive(tick.Volume, tick.Quantity);
            }

            FlushIntradayBar(bars, current, open, high, low, close, volume);
            source = intraday != null ? "Tick " + frame.ToString(CultureInfo.InvariantCulture) + "s" : "Tick " + frame.ToString(CultureInfo.InvariantCulture) + "s";
            return bars;
        }

        private static void FlushIntradayBar(List<DailyBar> bars, DateTimeOffset start, decimal open, decimal high, decimal low, decimal close, decimal volume)
        {
            if (bars == null)
            {
                return;
            }

            bars.Add(new DailyBar
            {
                Date = start.DateTime,
                Open = open,
                High = Math.Max(high, Math.Max(open, close)),
                Low = Math.Min(low, Math.Min(open, close)),
                Close = close,
                Volume = Math.Max(0m, volume)
            });
        }

        private static List<double> BuildDailyReturns(List<DailyBar> bars)
        {
            List<double> returns = new List<double>();

            if (bars == null || bars.Count < 2)
            {
                return returns;
            }

            for (int i = 1; i < bars.Count; i++)
            {
                if (bars[i - 1].Close <= 0m || bars[i].Close <= 0m)
                {
                    continue;
                }

                double r = Math.Log(Double(bars[i].Close) / Double(bars[i - 1].Close));
                if (!double.IsNaN(r) && !double.IsInfinity(r))
                {
                    returns.Add(r);
                }
            }

            return returns;
        }

        private static double Variance(List<double> values)
        {
            if (values == null || values.Count < 2)
            {
                return 0d;
            }

            double mean = values.Average();
            double sum = 0d;

            for (int i = 0; i < values.Count; i++)
            {
                double d = values[i] - mean;
                sum += d * d;
            }

            return Math.Max(0d, sum / Math.Max(1, values.Count - 1));
        }

        private static double ReturnsPower(List<double> values, int power)
        {
            if (values == null || values.Count == 0)
            {
                return 0d;
            }

            double sum = 0d;
            for (int i = 0; i < values.Count; i++)
            {
                double v = Math.Abs(values[i]);
                sum += Math.Pow(v, power);
            }

            return sum / values.Count;
        }

        private static decimal RoundToTick(decimal value, decimal tickSize)
        {
            decimal normalizedTick = tickSize <= 0m ? 0.5m : tickSize;
            return Math.Round(value / normalizedTick, 0, MidpointRounding.AwayFromZero) * normalizedTick;
        }

        private static DateTimeOffset TruncateToFrame(DateTimeOffset timestamp, long frameTicks)
        {
            long ticks = timestamp.Ticks / frameTicks * frameTicks;
            return new DateTimeOffset(ticks, timestamp.Offset);
        }

        private static decimal Abs(decimal value)
        {
            return value >= 0m ? value : -value;
        }

        private static decimal FirstPositive(decimal? a, decimal? b)
        {
            if (a.HasValue && a.Value > 0m)
            {
                return a.Value;
            }

            if (b.HasValue && b.Value > 0m)
            {
                return b.Value;
            }

            return 0m;
        }

        private static decimal Dec(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return 0m;
            }
            try
            {
                return Convert.ToDecimal(value);
            }
            catch
            {
                return 0m;
            }
        }

        private static double Double(decimal value)
        {
            return decimal.ToDouble(value);
        }

        private static double Double(double value)
        {
            return value;
        }

        private static List<DailyBar> ConvertIntradayToDailyBars(List<IntradayBar> source)
        {
            List<DailyBar> list = new List<DailyBar>();
            if (source == null) return list;
            foreach (var b in source)
            {
                list.Add(new DailyBar
                {
                    Date = b.Start.DateTime,
                    Open = b.Open,
                    High = b.High,
                    Low = b.Low,
                    Close = b.Close,
                    Volume = b.Volume
                });
            }
            return list;
        }

        private static List<GarchSignal> BuildSignals(GarchSnapshot garch, GarchConfig config, decimal tickSize)
        {
            List<GarchSignal> signals = new List<GarchSignal>();
            if (garch == null || config == null || !config.Enabled)
                return signals;

            decimal price = garch.CurrentPrice;
            if (price <= 0m)
                return signals;

            foreach (var band in garch.DailyBands)
            {
                decimal distTicks = Math.Abs(price - band.Price) / tickSize;
                if (distTicks <= config.MaxEntryDistanceTicks)
                {
                    var signal = CreateSignal("Daily", band, price, garch.ZDaily, garch.ZIntraday, tickSize, config);
                    if (signal != null)
                        signals.Add(signal);
                }
            }

            foreach (var band in garch.IntradayBands)
            {
                decimal distTicks = Math.Abs(price - band.Price) / tickSize;
                if (distTicks <= config.MaxEntryDistanceTicks)
                {
                    var signal = CreateSignal("Intraday", band, price, garch.ZDaily, garch.ZIntraday, tickSize, config);
                    if (signal != null)
                        signals.Add(signal);
                }
            }

            return signals;
        }

        private static GarchSignal CreateSignal(string scope, GarchBandLevel band, decimal currentPrice, double zDaily, double zIntraday, decimal tickSize, GarchConfig config)
        {
            if (band == null) return null;

            string side = band.Side;
            if (string.IsNullOrEmpty(side)) return null;

            GarchSignal signal = new GarchSignal();
            signal.Scope = scope;
            signal.LevelName = band.Label;
            signal.LevelPrice = band.Price;
            signal.Price = currentPrice;
            signal.ZDaily = zDaily;
            signal.ZIntraday = zIntraday;

            if (side == "Compra")
            {
                signal.Direction = "Buy";
                signal.Setup = "GARCH Reversao Compra";
                
                decimal stopDistance = Math.Max(2m * tickSize, 0.25m * Math.Abs(band.DistanceReference));
                signal.StopPrice = RoundToTick(band.Price - stopDistance, tickSize);
                
                signal.Target1 = RoundToTick(band.ReferencePrice, tickSize);
                signal.Target2 = RoundToTick(band.ReferencePrice + Math.Abs(band.DistanceReference) * 0.5m, tickSize);
            }
            else
            {
                signal.Direction = "Sell";
                signal.Setup = "GARCH Reversao Venda";

                decimal stopDistance = Math.Max(2m * tickSize, 0.25m * Math.Abs(band.DistanceReference));
                signal.StopPrice = RoundToTick(band.Price + stopDistance, tickSize);

                signal.Target1 = RoundToTick(band.ReferencePrice, tickSize);
                signal.Target2 = RoundToTick(band.ReferencePrice - Math.Abs(band.DistanceReference) * 0.5m, tickSize);
            }

            if (signal.StopPrice.HasValue && signal.Target1.HasValue)
            {
                signal.RiskPoints = Math.Abs(currentPrice - signal.StopPrice.Value);
                signal.RewardPoints = Math.Abs(signal.Target1.Value - currentPrice);
                if (signal.RiskPoints.Value > 0m)
                {
                    signal.RiskReward = Math.Round(signal.RewardPoints.Value / signal.RiskPoints.Value, 2);
                }
            }

            int score = 45;
            List<string> reasons = new List<string>();

            double zDailyAbs = Math.Abs(zDaily);
            double zIntradayAbs = Math.Abs(zIntraday);

            if (zDailyAbs >= config.ReversionMinAbsZDaily)
            {
                score += 6;
                reasons.Add("zD-1 relevante (" + zDaily.ToString("F2", CultureInfo.InvariantCulture) + ")");
            }
            if (zDailyAbs >= 1.0)
            {
                score += 8;
                reasons.Add("zD-1 extremo");
            }
            if (zIntradayAbs >= config.ReversionMinAbsZIntraday)
            {
                score += 10;
                reasons.Add("zIntraday relevante (" + zIntraday.ToString("F2", CultureInfo.InvariantCulture) + ")");
            }
            if (zIntradayAbs >= config.ExtremeAbsZIntraday)
            {
                score += 10;
                reasons.Add("zIntraday extremo");
            }

            decimal distTicks = Math.Abs(currentPrice - band.Price) / tickSize;
            if (distTicks <= 2m)
            {
                score += 6;
                reasons.Add("Preço colado na banda (" + distTicks.ToString("F1", CultureInfo.InvariantCulture) + " ticks)");
            }

            signal.Score = Math.Min(95, score);
            signal.Reasons = string.Join("; ", reasons);
            signal.Robustness = signal.Score >= 85 ? "Robusto" : (signal.Score >= 75 ? "Acionavel" : "Monitorar");
            signal.Confirmation = "Rejeição na banda e fluxo virando";
            signal.Gate = "Aguardando gatilho de rejeição";

            return signal;
        }

        private static List<GarchBacktestRow> BuildBacktest(List<DailyBar> bars, GarchFitResult fit, GarchConfig config, decimal tickSize)
        {
            List<GarchBacktestRow> rows = new List<GarchBacktestRow>();
            if (bars == null || bars.Count < 60 || fit == null || !fit.Success)
                return rows;

            double[] multipliers = new[] { 1.0, 1.5, 2.0 };
            string scope = fit.Scope;

            foreach (double mult in multipliers)
            {
                foreach (string direction in new[] { "Buy", "Sell" })
                {
                    GarchBacktestRow row = new GarchBacktestRow
                    {
                        Scope = scope,
                        Direction = direction,
                        Sigma = direction == "Buy" ? -mult : mult,
                        Samples = bars.Count
                    };

                    int touches = 0;
                    int reversals = 0;
                    int continuations = 0;
                    double sumMfe = 0;
                    double sumMae = 0;

                    double[] returns = BuildDailyReturns(bars).ToArray();
                    double mu = fit.Mu;
                    double omega = fit.Omega;
                    double alpha = fit.Alpha;
                    double beta = fit.Beta;
                    double sigma2 = Variance(returns.ToList());

                    for (int i = 0; i < returns.Length; i++)
                    {
                        double eps = returns[i] - mu;
                        sigma2 = omega + alpha * eps * eps + beta * sigma2;
                        sigma2 = Math.Max(MinimumVariance, sigma2);

                        if (i + 1 >= bars.Count) break;
                        DailyBar targetBar = bars[i + 1];
                        decimal reference = bars[i].Close;

                        if (reference <= 0m || targetBar.Low <= 0m) continue;

                        double nextSigma = Math.Sqrt(sigma2);
                        decimal upperBand = reference * Dec(Math.Exp(mult * nextSigma));
                        decimal lowerBand = reference / Dec(Math.Exp(mult * nextSigma));

                        upperBand = RoundToTick(upperBand, tickSize);
                        lowerBand = RoundToTick(lowerBand, tickSize);

                        if (direction == "Buy")
                        {
                            if (targetBar.Low <= lowerBand)
                            {
                                touches++;
                                double mfe = Double(targetBar.High - lowerBand);
                                double mae = Double(lowerBand - targetBar.Low);
                                sumMfe += mfe;
                                sumMae += mae;

                                if (targetBar.Close > lowerBand)
                                    reversals++;
                                else
                                    continuations++;
                            }
                        }
                        else
                        {
                            if (targetBar.High >= upperBand)
                            {
                                touches++;
                                double mfe = Double(upperBand - targetBar.Low);
                                double mae = Double(targetBar.High - upperBand);
                                sumMfe += mfe;
                                sumMae += mae;

                                if (targetBar.Close < upperBand)
                                    reversals++;
                                else
                                    continuations++;
                            }
                        }
                    }

                    row.Touches = touches;
                    row.Reversals = reversals;
                    row.Continuations = continuations;
                    row.ReversalRate = touches > 0 ? (double)reversals / touches : 0d;
                    row.AverageMfePoints = touches > 0 ? Dec(sumMfe / touches) : 0m;
                    row.AverageMaePoints = touches > 0 ? Dec(sumMae / touches) : 0m;
                    row.ExpectancyPoints = touches > 0 ? Dec((sumMfe - sumMae) / touches) : 0m;
                    row.ProfitFactor = sumMae > 0 ? sumMfe / sumMae : (sumMfe > 0 ? 9.9d : 0d);
                    row.Confidence = touches > 10 ? 0.8d : (touches > 0 ? 0.5d : 0d);
                    row.RiskReward = row.AverageMaePoints > 0 ? row.AverageMfePoints / row.AverageMaePoints : 0m;
                    row.EdgeScore = row.ReversalRate * Double(row.ExpectancyPoints);
                    row.Read = touches > 0 ? string.Format("{0} toques | {1} rev", touches, reversals) : "Sem amostras";

                    rows.Add(row);
                }
            }

            return rows;
        }
    }
}
