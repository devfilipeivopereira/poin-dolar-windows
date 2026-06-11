using System;
using System.Collections.Generic;
using System.Linq;
using RtdDolarNative.Config;
using RtdDolarNative.Csv;
using RtdDolarNative.MarketData;

namespace RtdDolarNative.Quant
{
    public static class QuantEngine
    {
        private static readonly double[] PercentVariations = new[] { 1d, 2d, 3d, -1d, -2d, -3d };
        private const double GaussMadScale = 1.4826d;
        private const double GaussWinsorMad = 3d;
        private const double GaussRsCapPercentile = 0.90d;

        public static QuantResult Build(List<DailyBar> allBars, MarketSnapshot snapshot, decimal tickSize, int calculationDays)
        {
            return Build(allBars, snapshot, tickSize, calculationDays, null);
        }

        public static QuantResult Build(List<DailyBar> allBars, MarketSnapshot snapshot, decimal tickSize, int calculationDays, IEnumerable<TickEvent> ticks)
        {
            return Build(allBars, snapshot, tickSize, calculationDays, ticks, null, null);
        }

        public static QuantResult Build(
            List<DailyBar> allBars,
            MarketSnapshot snapshot,
            decimal tickSize,
            int calculationDays,
            IEnumerable<TickEvent> ticks,
            List<IntradayBar> intradayBars,
            GarchConfig garchConfig)
        {
            QuantResult result = new QuantResult();
            result.Bars = allBars == null ? new List<DailyBar>() : allBars.OrderBy(x => x.Date).ToList();
            int windowDays = UiConfig.NormalizeCalculationDays(calculationDays);
            result.CalculationDays = windowDays;

            if (result.Bars.Count == 0)
            {
                result.Warnings.Add("Carregue um CSV diario para calcular os niveis.");
                result.Intraday = BuildIntraday(snapshot, null);
                result.Technicals = BuildTechnicalIndicators(result.Bars, result.Intraday, null, windowDays);
                return result;
            }

            if (result.Bars.Count < windowDays)
            {
                result.Warnings.Add("CSV tem menos de " + windowDays + " pregoes validos; calculos ficam incompletos.");
            }

            result.PreviousDay = result.Bars[result.Bars.Count - 1];
            result.Intraday = BuildIntraday(snapshot, result.PreviousDay);

            if (snapshot == null || !snapshot.Ultimo.HasValue)
            {
                result.Warnings.Add("RTD ULT ausente; sinais quantitativos usam o ultimo fechamento do CSV como fallback.");
            }

            if (snapshot == null || !snapshot.Media.HasValue)
            {
                result.Warnings.Add("RTD MED/VWAP ausente; distancia de VWAP usa proxy ate o feed informar o campo.");
            }

            if (snapshot == null || !snapshot.Volume.HasValue)
            {
                result.Warnings.Add("RTD VOL ausente; leitura de participacao por volume fica limitada.");
            }

            List<DailyBar> window = result.Bars.Skip(Math.Max(0, result.Bars.Count - windowDays)).ToList();
            List<DailyBar> selectedWindow = window.Count > 0 ? window : result.Bars;

            result.GarmanKlass = CalcGarmanKlass(selectedWindow, windowDays);
            result.Parkinson = CalcParkinson(selectedWindow, windowDays);
            result.RogersSatchell = CalcRogersSatchell(selectedWindow, windowDays);
            result.YangZhang = CalcYangZhang(selectedWindow, windowDays);
            result.CloseToClose = CalcCloseToClose(selectedWindow, windowDays);
            result.StandardDeviation = CalcStandardDeviation(selectedWindow, windowDays, tickSize);
            result.Gauss = CalcGauss(selectedWindow, result.Intraday, result.YangZhang, windowDays, tickSize);
            result.Atr = CalcAtr(selectedWindow, windowDays, result.Bars);
            result.Metrics.Add(result.GarmanKlass);
            result.Metrics.Add(result.Parkinson);
            result.Metrics.Add(result.RogersSatchell);
            result.Metrics.Add(result.YangZhang);
            result.Metrics.Add(result.CloseToClose);
            result.Metrics.Add(result.StandardDeviation);
            result.Metrics.Add(result.Gauss);
            result.Metrics.Add(result.Atr);

            if (selectedWindow.Count >= 2)
            {
                result.WindowMetrics.Add(CalcGarmanKlass(selectedWindow, windowDays));
                result.WindowMetrics.Add(CalcYangZhang(selectedWindow, windowDays));
                result.WindowMetrics.Add(CalcAtr(selectedWindow, windowDays, result.Bars));
            }

            result.Profile = VolumeProfileProxy(selectedWindow);
            result.SupportResistance = SupportResistanceEngine(selectedWindow, result.Intraday.Price, result.GarmanKlass.Points, result.Atr.Points);
            result.Avwaps = AnchoredVwaps(selectedWindow);
            result.Garch = BuildGarch(result.Bars, result.Intraday, snapshot, ticks, intradayBars, tickSize, garchConfig);
            result.OpeningLevels = ReferenceDeviationLevels("Abertura", result.Intraday.Open, result.GarmanKlass.Points, result.Intraday.Price);
            result.PocDeviationLevels = ReferenceDeviationLevels("POC", result.Profile.Poc.Price, result.GarmanKlass.Points, result.Intraday.Price);
            result.StandardDeviationLevels = MetricLevels("Desvio padrao", result.Intraday.Open, result.StandardDeviation, result.Intraday.Price);
            result.GaussLevels = MetricLevels("Gauss robusto", result.Intraday.Open, result.Gauss, result.Intraday.Price);
            result.ReferenceMaps = BuildReferenceMaps(result, snapshot, tickSize);
            result.PercentMaps = PercentVariationMaps(result.PreviousDay, result.Intraday, result.Profile);
            result.PercentTable = FlattenPercentMaps(result.PercentMaps, result.Intraday.Price);
            result.Backtest = BacktestProxy(result.Bars, windowDays);
            result.Technicals = BuildTechnicalIndicators(result.Bars, result.Intraday, result.Atr, windowDays);
            result.KeyLevels = BuildRawLevels(result);
            result.Confluence = MergeInterestLevels(result.KeyLevels, result.Intraday.Price, tickSize);
            result.Regime = DetectRegime(result.Atr, result.CloseToClose, result.Intraday, result.PreviousDay);
            result.QuantSignals = BuildQuantSignals(result, tickSize);

            if (result.Intraday.VwapIsProxy)
            {
                result.Warnings.Add("VWAP/MED nao informado pelo RTD; usando preco tipico parcial como proxy.");
            }

            if (result.Atr.Percentile > 75)
            {
                result.Warnings.Add("ATR em percentil alto; niveis exigem confirmacao adicional.");
            }

            return result;
        }

        private static GarchSnapshot BuildGarch(List<DailyBar> bars, IntradayContext intraday, MarketSnapshot snapshot, IEnumerable<TickEvent> ticks, List<IntradayBar> intradayBars, decimal tickSize, GarchConfig garchConfig)
        {
            try
            {
                if (intradayBars != null && intradayBars.Count > 0)
                {
                    return GarchEngine.Build(bars, intraday, snapshot, intradayBars, tickSize, garchConfig);
                }
                return GarchEngine.Build(bars, intraday, snapshot, ticks, tickSize, garchConfig);
            }
            catch (Exception ex)
            {
                GarchSnapshot error = new GarchSnapshot();
                error.Warnings.Add("GARCH indisponivel no momento: " + ex.GetType().Name + " - " + ex.Message);
                return error;
            }
        }

        private static IntradayContext BuildIntraday(MarketSnapshot snapshot, DailyBar previousDay)
        {
            decimal price = First(snapshot == null ? null : snapshot.Ultimo, previousDay == null ? (decimal?)null : previousDay.Close, 0m);
            decimal open = First(snapshot == null ? null : snapshot.Abertura, previousDay == null ? (decimal?)null : previousDay.Close, price);
            decimal high = First(snapshot == null ? null : snapshot.Maxima, Math.Max(open, price));
            decimal low = First(snapshot == null ? null : snapshot.Minima, Math.Min(open, price));
            decimal vwap = First(snapshot == null ? null : snapshot.Media, (open + high + low + price) / 4m);

            IntradayContext ctx = new IntradayContext();
            ctx.Open = open;
            ctx.High = high;
            ctx.Low = low;
            ctx.Price = price;
            ctx.Vwap = vwap;
            ctx.Volume = First(snapshot == null ? null : snapshot.Volume, 0m);
            ctx.VwapIsProxy = snapshot == null || !snapshot.Media.HasValue;
            return ctx;
        }

        private static decimal First(decimal? a, decimal b)
        {
            return a.HasValue && a.Value != 0m ? a.Value : b;
        }

        private static decimal First(decimal? a, decimal? b, decimal c)
        {
            if (a.HasValue && a.Value != 0m)
            {
                return a.Value;
            }

            if (b.HasValue && b.Value != 0m)
            {
                return b.Value;
            }

            return c;
        }

        private static VolatilityMetric CalcGarmanKlass(List<DailyBar> bars, int window)
        {
            List<double> terms = new List<double>();

            foreach (DailyBar b in bars)
            {
                double highLow = Math.Log(D(b.High) / D(b.Low));
                double closeOpen = Math.Log(D(b.Close) / D(b.Open));
                terms.Add(0.5d * highLow * highLow - (2d * Math.Log(2d) - 1d) * closeOpen * closeOpen);
            }

            return VolMetric("Garman-Klass", window, bars, Math.Sqrt(Math.Max(0d, Average(terms))));
        }

        private static VolatilityMetric CalcParkinson(List<DailyBar> bars, int window)
        {
            List<double> terms = bars.Select(b => Math.Pow(Math.Log(D(b.High) / D(b.Low)), 2d)).ToList();
            double ratio = Math.Sqrt(Average(terms) / (4d * Math.Log(2d)));
            return VolMetric("Parkinson", window, bars, ratio);
        }

        private static VolatilityMetric CalcRogersSatchell(List<DailyBar> bars, int window)
        {
            List<double> terms = new List<double>();

            foreach (DailyBar b in bars)
            {
                double h = D(b.High);
                double l = D(b.Low);
                double o = D(b.Open);
                double c = D(b.Close);
                terms.Add(Math.Log(h / c) * Math.Log(h / o) + Math.Log(l / c) * Math.Log(l / o));
            }

            return VolMetric("Rogers-Satchell", window, bars, Math.Sqrt(Math.Max(0d, Average(terms))));
        }

        private static VolatilityMetric CalcYangZhang(List<DailyBar> bars, int window)
        {
            if (bars.Count < 2)
            {
                return VolMetric("Yang-Zhang", window, bars, 0d);
            }

            List<double> overnight = new List<double>();
            List<double> openClose = new List<double>();
            List<double> rs = new List<double>();

            for (int i = 1; i < bars.Count; i++)
            {
                DailyBar prev = bars[i - 1];
                DailyBar b = bars[i];
                overnight.Add(Math.Log(D(b.Open) / D(prev.Close)));
                openClose.Add(Math.Log(D(b.Close) / D(b.Open)));
                rs.Add(Math.Log(D(b.High) / D(b.Close)) * Math.Log(D(b.High) / D(b.Open)) + Math.Log(D(b.Low) / D(b.Close)) * Math.Log(D(b.Low) / D(b.Open)));
            }

            double n = Math.Max(2d, bars.Count);
            double k = 0.34d / (1.34d + (n + 1d) / (n - 1d));
            double variance = Variance(overnight) + k * Variance(openClose) + (1d - k) * Average(rs);
            return VolMetric("Yang-Zhang", window, bars, Math.Sqrt(Math.Max(0d, variance)));
        }

        private static VolatilityMetric CalcCloseToClose(List<DailyBar> bars, int window)
        {
            if (bars.Count < 2)
            {
                return VolMetric("Close-to-close", window, bars, 0d);
            }

            List<double> returns = new List<double>();

            for (int i = 1; i < bars.Count; i++)
            {
                returns.Add(Math.Log(D(bars[i].Close) / D(bars[i - 1].Close)));
            }

            return VolMetric("Close-to-close", window, bars, Stdev(returns, true));
        }

        private static VolatilityMetric CalcStandardDeviation(List<DailyBar> bars, int window, decimal tickSize)
        {
            if (bars == null || bars.Count == 0)
            {
                return PointMetric("Desvio padrao", window, 0m, 0m);
            }

            List<decimal> ranges = bars.Select(x => x.High - x.Low).Where(x => x > 0m).ToList();

            if (ranges.Count < 2)
            {
                return PointMetric("Desvio padrao", window, 0m, bars.Average(x => x.Close));
            }

            decimal price = bars.Average(x => x.Close);
            decimal stdev = RoundToStep(PopulationStdevDecimal(ranges), tickSize);
            return PointMetric("Desvio padrao", window, stdev, price);
        }

        private static VolatilityMetric CalcGauss(List<DailyBar> bars, IntradayContext intraday, VolatilityMetric yangZhang, int window, decimal tickSize)
        {
            decimal reference = intraday != null && intraday.Open > 0m
                ? intraday.Open
                : bars != null && bars.Count > 0 ? bars[bars.Count - 1].Close : 0m;

            if (bars == null || bars.Count < 2 || reference <= 0m)
            {
                return PointMetric("Gauss robusto", window, 0m, reference);
            }

            decimal robustYangZhang = CalcRobustYangZhangPoints(bars, reference);
            decimal openCloseMad = CalcOpenCloseMadPoints(bars, reference);
            decimal yangZhangPoints = yangZhang == null ? 0m : yangZhang.Points;
            decimal sigma = robustYangZhang > 0m ? robustYangZhang : yangZhangPoints;

            if (openCloseMad > 0m)
            {
                sigma = Math.Max(sigma, openCloseMad);

                decimal upper = openCloseMad * 2.25m;
                if (upper > 0m && sigma > upper)
                {
                    sigma = upper;
                }
            }

            if (sigma <= 0m)
            {
                List<decimal> ranges = bars.Select(x => x.High - x.Low).Where(x => x > 0m).ToList();
                sigma = ranges.Count == 0 ? 0m : ranges.Average() / 2m;
            }

            sigma = RoundToStep(sigma, tickSize);
            return PointMetric("Gauss robusto", window, sigma, reference);
        }

        private static decimal CalcRobustYangZhangPoints(List<DailyBar> bars, decimal reference)
        {
            if (bars == null || bars.Count < 2 || reference <= 0m)
            {
                return 0m;
            }

            List<double> overnight = new List<double>();
            List<double> openClose = new List<double>();
            List<double> rs = new List<double>();

            for (int i = 1; i < bars.Count; i++)
            {
                DailyBar prev = bars[i - 1];
                DailyBar b = bars[i];

                if (!IsValidPrice(prev.Close) || !IsValidPrice(b.Open) || !IsValidPrice(b.High) || !IsValidPrice(b.Low) || !IsValidPrice(b.Close))
                {
                    continue;
                }

                overnight.Add(Math.Log(D(b.Open) / D(prev.Close)));
                openClose.Add(Math.Log(D(b.Close) / D(b.Open)));
                double rsTerm = Math.Log(D(b.High) / D(b.Close)) * Math.Log(D(b.High) / D(b.Open)) +
                                Math.Log(D(b.Low) / D(b.Close)) * Math.Log(D(b.Low) / D(b.Open));
                rs.Add(Math.Max(0d, rsTerm));
            }

            if (overnight.Count < 2 || openClose.Count < 2 || rs.Count == 0)
            {
                return 0m;
            }

            List<double> robustOvernight = WinsorizeByMad(overnight, GaussWinsorMad);
            List<double> robustOpenClose = WinsorizeByMad(openClose, GaussWinsorMad);
            List<double> robustRs = WinsorizeUpper(rs, GaussRsCapPercentile);
            double n = Math.Max(2d, bars.Count);
            double k = 0.34d / (1.34d + (n + 1d) / (n - 1d));
            double variance = Variance(robustOvernight) + k * Variance(robustOpenClose) + (1d - k) * Average(robustRs);
            return Dec(Math.Sqrt(Math.Max(0d, variance)) * D(reference));
        }

        private static decimal CalcOpenCloseMadPoints(List<DailyBar> bars, decimal reference)
        {
            if (bars == null || bars.Count < 2 || reference <= 0m)
            {
                return 0m;
            }

            List<double> moves = new List<double>();

            foreach (DailyBar bar in bars)
            {
                if (!IsValidPrice(bar.Open) || !IsValidPrice(bar.Close))
                {
                    continue;
                }

                moves.Add(Math.Log(D(bar.Close) / D(bar.Open)) * D(reference));
            }

            return Dec(RobustMadScale(moves));
        }

        private static bool IsValidPrice(decimal price)
        {
            return price > 0m;
        }

        private static VolatilityMetric CalcAtr(List<DailyBar> bars, int window, List<DailyBar> allBars)
        {
            List<decimal> trs = TrueRanges(bars);
            decimal atr = trs.Count == 0 ? 0m : trs.Average();
            List<decimal> allTrs = TrueRanges(allBars);
            double percentile = allTrs.Count == 0 ? 0d : PercentileRank(allTrs.Select(x => D(x)).ToList(), D(atr));

            VolatilityMetric metric = new VolatilityMetric();
            metric.Name = "ATR";
            metric.Window = window;
            metric.Points = atr;
            metric.Percent = bars.Count == 0 ? 0d : D(atr) / D(bars.Average(x => x.Close)) * 100d;
            metric.Percentile = percentile;
            return metric;
        }

        private static VolatilityMetric VolMetric(string name, int window, List<DailyBar> bars, double ratio)
        {
            decimal price = bars.Count == 0 ? 0m : bars.Average(x => x.Close);
            return PointMetric(name, window, Dec(ratio * D(price)), price);
        }

        private static VolatilityMetric PointMetric(string name, int window, decimal points, decimal referencePrice)
        {
            VolatilityMetric metric = new VolatilityMetric();
            metric.Name = name;
            metric.Window = window;
            metric.Points = points;
            metric.Percent = referencePrice <= 0m ? 0d : D(points) / D(referencePrice) * 100d;
            metric.Percentile = 0d;
            return metric;
        }

        private static List<decimal> TrueRanges(List<DailyBar> bars)
        {
            List<decimal> trs = new List<decimal>();

            for (int i = 0; i < bars.Count; i++)
            {
                DailyBar b = bars[i];
                decimal prevClose = i == 0 ? b.Close : bars[i - 1].Close;
                decimal tr = Math.Max(b.High - b.Low, Math.Max(Math.Abs(b.High - prevClose), Math.Abs(b.Low - prevClose)));
                trs.Add(tr);
            }

            return trs;
        }

        private static VolumeProfileResult VolumeProfileProxy(List<DailyBar> bars)
        {
            VolumeProfileResult result = new VolumeProfileResult();

            if (bars == null || bars.Count == 0)
            {
                ProfileBin empty = new ProfileBin();
                result.Poc = empty;
                return result;
            }

            decimal min = bars.Min(x => x.Low);
            decimal max = bars.Max(x => x.High);
            decimal range = Math.Max(1m, max - min);
            decimal width = ChooseBinWidth(range);
            int count = Math.Max(8, Math.Min(80, (int)Math.Ceiling(range / width)));

            for (int i = 0; i < count; i++)
            {
                ProfileBin bin = new ProfileBin();
                bin.Low = min + width * i;
                bin.High = i == count - 1 ? max : min + width * (i + 1);
                bin.Price = (bin.Low + bin.High) / 2m;
                result.Bins.Add(bin);
            }

            foreach (DailyBar bar in bars)
            {
                double vol = D(BarVolume(bar));
                List<ProfileBin> touched = result.Bins.Where(x => x.High >= bar.Low && x.Low <= bar.High).ToList();
                double share = touched.Count == 0 ? 0d : vol / touched.Count;

                foreach (ProfileBin bin in touched)
                {
                    bin.Volume += share;
                }
            }

            result.Poc = result.Bins.OrderByDescending(x => x.Volume).First();
            double total = result.Bins.Sum(x => x.Volume);
            double target = total * 0.7d;
            List<ProfileBin> selected = new List<ProfileBin>();
            selected.Add(result.Poc);
            double running = result.Poc.Volume;
            int pocIndex = result.Bins.IndexOf(result.Poc);
            int left = pocIndex - 1;
            int right = pocIndex + 1;

            while (running < target && (left >= 0 || right < result.Bins.Count))
            {
                double lv = left >= 0 ? result.Bins[left].Volume : -1d;
                double rv = right < result.Bins.Count ? result.Bins[right].Volume : -1d;

                if (rv >= lv)
                {
                    selected.Add(result.Bins[right]);
                    running += result.Bins[right].Volume;
                    right++;
                }
                else
                {
                    selected.Add(result.Bins[left]);
                    running += result.Bins[left].Volume;
                    left--;
                }
            }

            foreach (ProfileBin bin in selected)
            {
                bin.InValue = true;
            }

            result.Vah = selected.Max(x => x.High);
            result.Val = selected.Min(x => x.Low);

            for (int i = 0; i < result.Bins.Count; i++)
            {
                ProfileBin bin = result.Bins[i];
                double prev = i > 0 ? result.Bins[i - 1].Volume : -1d;
                double next = i + 1 < result.Bins.Count ? result.Bins[i + 1].Volume : -1d;
                bin.IsHvn = bin.Volume >= prev && bin.Volume >= next && bin.Volume > 0d;
                bin.IsLvn = bin.Volume <= prev && bin.Volume <= next && bin.Volume > 0d;
                bin.Rank = total <= 0d ? 0d : bin.Volume / total;
            }

            result.Hvn = result.Bins.Where(x => x.IsHvn).OrderByDescending(x => x.Volume).Take(5).ToList();
            result.Lvn = result.Bins.Where(x => x.IsLvn).OrderBy(x => x.Volume).Take(5).ToList();
            return result;
        }

        private static decimal ChooseBinWidth(decimal range)
        {
            decimal rough = range / 40m;

            if (rough <= 0.5m)
            {
                return 0.5m;
            }

            decimal[] steps = new[] { 0.5m, 1m, 2.5m, 5m, 10m, 25m, 50m, 100m };

            foreach (decimal step in steps)
            {
                if (rough <= step)
                {
                    return step;
                }
            }

            return Math.Ceiling(rough / 100m) * 100m;
        }

        private static List<KeyLevel> SupportResistanceEngine(List<DailyBar> bars, decimal price, decimal sigma, decimal atr)
        {
            List<KeyLevel> raw = new List<KeyLevel>();

            for (int i = 2; i < bars.Count - 2; i++)
            {
                DailyBar b = bars[i];

                if (b.High >= bars[i - 1].High && b.High >= bars[i - 2].High && b.High >= bars[i + 1].High && b.High >= bars[i + 2].High)
                {
                    raw.Add(Level(b.High, "Pivo alta", "Resistencia", "SR", 58d + i * 20d / Math.Max(1, bars.Count), "pivo"));
                }

                if (b.Low <= bars[i - 1].Low && b.Low <= bars[i - 2].Low && b.Low <= bars[i + 1].Low && b.Low <= bars[i + 2].Low)
                {
                    raw.Add(Level(b.Low, "Pivo baixa", "Suporte", "SR", 58d + i * 20d / Math.Max(1, bars.Count), "pivo"));
                }
            }

            if (bars.Count > 0)
            {
                decimal min = bars.Min(x => x.Low);
                decimal max = bars.Max(x => x.High);
                decimal step = Math.Max(5m, Math.Round(Math.Max(atr, sigma) / 5m) * 5m);
                decimal start = Math.Floor(min / step) * step;

                for (decimal p = start; p <= max; p += step)
                {
                    raw.Add(Level(p, "Numero redondo", p >= price ? "Resistencia" : "Suporte", "Round", 36d, "grade"));
                }
            }

            return MergeInterestLevels(raw, price, Math.Max(0.5m, atr / 5m)).Take(18).ToList();
        }

        private static List<AnchoredVwap> AnchoredVwaps(List<DailyBar> bars)
        {
            List<AnchoredVwap> output = new List<AnchoredVwap>();

            if (bars.Count == 0)
            {
                return output;
            }

            List<int> anchors = new List<int>();
            int maxVol = 0;

            for (int i = 1; i < bars.Count; i++)
            {
                if (BarVolume(bars[i]) > BarVolume(bars[maxVol]))
                {
                    maxVol = i;
                }
            }

            anchors.Add(Math.Max(0, bars.Count - 21));
            anchors.Add(maxVol);
            anchors.Add(Math.Max(0, bars.Count - 5));
            anchors = anchors.Distinct().OrderBy(x => x).ToList();

            foreach (int index in anchors)
            {
                decimal pv = 0m;
                decimal vol = 0m;

                for (int i = index; i < bars.Count; i++)
                {
                    decimal v = BarVolume(bars[i]);
                    pv += TypicalPrice(bars[i]) * v;
                    vol += v;
                }

                if (vol > 0m)
                {
                    AnchoredVwap item = new AnchoredVwap();
                    item.Label = bars[index].Date.ToString("dd/MM");
                    item.AnchorDate = bars[index].Date;
                    item.Price = pv / vol;
                    output.Add(item);
                }
            }

            return output;
        }

        private static List<DeviationLevel> ReferenceDeviationLevels(decimal referencePrice, decimal sigma, decimal currentPrice)
        {
            return ReferenceDeviationLevels(string.Empty, referencePrice, sigma, currentPrice);
        }

        private static List<DeviationLevel> ReferenceDeviationLevels(string referenceName, decimal referencePrice, decimal sigma, decimal currentPrice)
        {
            List<DeviationLevel> levels = new List<DeviationLevel>();

            if (referencePrice <= 0m || sigma <= 0m)
            {
                return levels;
            }

            int[] multipliers = new[] { 1, 2, 3, 4 };
            string prefix = string.IsNullOrWhiteSpace(referenceName) ? string.Empty : referenceName.Trim() + " ";

            foreach (int m in multipliers)
            {
                decimal sell = referencePrice + m * sigma;
                decimal buy = referencePrice - m * sigma;
                levels.Add(Deviation("Venda", "sell", m, sell, referencePrice, currentPrice, prefix + "+" + m + " desvio"));
                levels.Add(Deviation("Compra", "buy", -m, buy, referencePrice, currentPrice, prefix + "-" + m + " desvio"));
            }

            return levels;
        }

        private static List<ReferenceMapResult> BuildReferenceMaps(QuantResult result, MarketSnapshot snapshot, decimal tickSize)
        {
            List<ReferenceMapResult> maps = new List<ReferenceMapResult>();

            if (result == null || result.Intraday == null)
            {
                return maps;
            }

            decimal openingReference = snapshot != null && snapshot.Abertura.HasValue && snapshot.Abertura.Value > 0m
                ? snapshot.Abertura.Value
                : result.Intraday.Open;
            maps.Add(BuildReferenceMap(result, "opening", "Abertura", snapshot != null && snapshot.Abertura.HasValue ? "RTD" : "Intraday", openingReference, tickSize));

            decimal csvPreviousClose = result.PreviousDay == null ? 0m : result.PreviousDay.Close;
            bool hasRtdClose = snapshot != null && snapshot.FechamentoAnterior.HasValue && snapshot.FechamentoAnterior.Value > 0m;
            decimal closingReference = hasRtdClose ? snapshot.FechamentoAnterior.Value : csvPreviousClose;
            string closingSource = hasRtdClose ? "RTD" : "CSV D-1";

            if (hasRtdClose &&
                csvPreviousClose > 0m &&
                csvPreviousClose != closingReference &&
                openingReference > 0m &&
                closingReference == openingReference)
            {
                closingReference = csvPreviousClose;
                closingSource = "CSV D-1";
            }

            maps.Add(BuildReferenceMap(result, "closing", "Fechamento", closingSource, closingReference, tickSize));

            decimal pocReference = result.Profile == null || result.Profile.Poc == null ? 0m : result.Profile.Poc.Price;
            maps.Add(BuildReferenceMap(result, "poc", "POC", "Profile CSV", pocReference, tickSize));

            decimal adjustmentReference = snapshot != null && snapshot.Ajuste.HasValue && snapshot.Ajuste.Value > 0m
                ? snapshot.Ajuste.Value
                : (snapshot != null && snapshot.AjusteAnterior.HasValue ? snapshot.AjusteAnterior.Value : 0m);
            decimal? ajusteField = snapshot == null
                ? (decimal?)null
                : (snapshot.Rtd != null && snapshot.Rtd.ContainsKey("AJU") ? ValueParser.ToDecimal(snapshot.Rtd["AJU"]) : (decimal?)null);
            string adjustmentSource = ajusteField.HasValue && ajusteField.Value != 0m
                ? "RTD AJU"
                : (snapshot != null && snapshot.AjusteAnterior.HasValue ? "RTD AJA" : "RTD");
            maps.Add(BuildReferenceMap(result, "adjustment", "Ajuste", adjustmentSource, adjustmentReference, tickSize));

            decimal ptaxReference = snapshot != null && snapshot.Ptax.HasValue && snapshot.Ptax.Value > 0m ? snapshot.Ptax.Value : 0m;
            maps.Add(BuildReferenceMap(result, "ptax", "PTAX", ptaxReference > 0m ? "SQL manual" : "SQL manual sem valor", ptaxReference, tickSize));

            return maps;
        }

        private static ReferenceMapResult BuildReferenceMap(QuantResult result, string referenceKey, string referenceLabel, string source, decimal referencePrice, decimal tickSize)
        {
            ReferenceMapResult map = new ReferenceMapResult();
            map.ReferenceKey = referenceKey;
            map.ReferenceLabel = referenceLabel;
            map.ReferenceSource = source;
            map.ReferencePrice = referencePrice;
            decimal currentPrice = result == null || result.Intraday == null ? 0m : result.Intraday.Price;

            if (referencePrice > 0m)
            {
                map.GarmanLevels = ReferenceDeviationLevels(referenceLabel, referencePrice, result == null || result.GarmanKlass == null ? 0m : result.GarmanKlass.Points, currentPrice);
                map.GaussLevels = MetricLevels("Gauss robusto", referencePrice, result == null ? null : result.Gauss, currentPrice);
                map.StdDevLevels = MetricLevels("Desvio padrao", referencePrice, result == null ? null : result.StandardDeviation, currentPrice);
                map.GarchLevels = GarchReferenceLevels(referenceLabel, referencePrice, currentPrice, result == null ? null : result.Garch, tickSize);
            }

            map.GarmanSummary = BuildReferenceMetricSummary("garman", "Garman-Klass", result == null ? null : result.GarmanKlass, map.GarmanLevels, currentPrice);
            map.GaussSummary = BuildReferenceMetricSummary("gauss", "Gauss", result == null ? null : result.Gauss, map.GaussLevels, currentPrice);
            map.StdDevSummary = BuildReferenceMetricSummary("stddev", "Desvio padrao", result == null ? null : result.StandardDeviation, map.StdDevLevels, currentPrice);
            map.GarchSummary = BuildGarchReferenceMetricSummary(result == null ? null : result.Garch, map.GarchLevels, referencePrice, currentPrice, tickSize);
            return map;
        }

        private static List<DeviationLevel> GarchReferenceLevels(string referenceLabel, decimal referencePrice, decimal currentPrice, GarchSnapshot garch, decimal tickSize)
        {
            List<DeviationLevel> levels = new List<DeviationLevel>();
            GarchFitResult fit = ResolveReferenceGarchFit(garch);

            if (fit == null || !fit.Success || referencePrice <= 0m)
            {
                return levels;
            }

            double[] multipliers = new[] { 1d, 2d, 3d, 4d };
            string scope = string.IsNullOrWhiteSpace(referenceLabel) ? "Referencia" : referenceLabel.Trim();
            List<GarchBandLevel> bands = GarchEngine.BuildReferenceBands(scope, referencePrice, currentPrice, fit, multipliers, tickSize);

            foreach (GarchBandLevel band in bands)
            {
                if (band == null || band.Price <= 0m)
                {
                    continue;
                }

                levels.Add(new DeviationLevel
                {
                    Side = band.Side,
                    Direction = string.Equals(band.Side, "Venda", StringComparison.OrdinalIgnoreCase) ? "sell" : "buy",
                    Sigma = Dec(band.Sigma),
                    Price = band.Price,
                    DistanceReference = band.DistanceReference,
                    DistanceCurrent = band.DistanceCurrent,
                    Label = band.Label,
                    Score = band.ScoreHint
                });
            }

            return levels;
        }

        private static ReferenceMetricSummary BuildGarchReferenceMetricSummary(GarchSnapshot garch, IEnumerable<DeviationLevel> levels, decimal referencePrice, decimal currentPrice, decimal tickSize)
        {
            ReferenceMetricSummary summary = new ReferenceMetricSummary();
            summary.MetricKey = "garch";
            summary.MetricLabel = "GARCH";
            summary.Points = GarchSigmaPoints(garch, referencePrice, tickSize);
            summary.NearestSell = NearestDeviationLevel(levels, "Venda", currentPrice);
            summary.NearestBuy = NearestDeviationLevel(levels, "Compra", currentPrice);
            return summary;
        }

        private static GarchFitResult ResolveReferenceGarchFit(GarchSnapshot garch)
        {
            if (garch == null)
            {
                return null;
            }

            if (garch.DailyFit != null && garch.DailyFit.Success)
            {
                return garch.DailyFit;
            }

            if (garch.IntradayFit != null && garch.IntradayFit.Success)
            {
                return garch.IntradayFit;
            }

            return null;
        }

        private static decimal GarchSigmaPoints(GarchSnapshot garch, decimal referencePrice, decimal tickSize)
        {
            GarchFitResult fit = ResolveReferenceGarchFit(garch);

            if (fit == null || !fit.Success || referencePrice <= 0m)
            {
                return 0m;
            }

            decimal points = Dec(Math.Abs(fit.NextSigma * D(referencePrice)));
            return RoundToTick(points, tickSize);
        }

        private static ReferenceMetricSummary BuildReferenceMetricSummary(string metricKey, string metricLabel, VolatilityMetric metric, IEnumerable<DeviationLevel> levels, decimal currentPrice)
        {
            ReferenceMetricSummary summary = new ReferenceMetricSummary();
            summary.MetricKey = metricKey;
            summary.MetricLabel = metricLabel;
            summary.Points = metric == null ? 0m : metric.Points;
            summary.NearestSell = NearestDeviationLevel(levels, "Venda", currentPrice);
            summary.NearestBuy = NearestDeviationLevel(levels, "Compra", currentPrice);
            return summary;
        }

        private static List<DeviationLevel> MetricLevels(string metricLabel, decimal referencePrice, VolatilityMetric metric, decimal currentPrice)
        {
            List<DeviationLevel> levels = new List<DeviationLevel>();

            if (metric == null || metric.Points <= 0m || referencePrice <= 0m)
            {
                return levels;
            }

            decimal sigma = metric.Points;

            for (int m = 1; m <= 4; m++)
            {
                decimal sell = referencePrice + m * sigma;
                decimal buy = referencePrice - m * sigma;
                levels.Add(Deviation("Venda", "sell", m, sell, referencePrice, currentPrice, "Ponto " + m + " | " + metricLabel + " +" + m + " desvio"));
                levels.Add(Deviation("Compra", "buy", -m, buy, referencePrice, currentPrice, "Ponto " + (m + 4) + " | " + metricLabel + " -" + m + " desvio"));
            }

            return levels;
        }

        private static DeviationLevel NearestDeviationLevel(IEnumerable<DeviationLevel> levels, string side, decimal currentPrice)
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

        private static DeviationLevel Deviation(string side, string dir, decimal sigma, decimal price, decimal reference, decimal current, string label)
        {
            DeviationLevel level = new DeviationLevel();
            level.Side = side;
            level.Direction = dir;
            level.Sigma = sigma;
            level.Price = price;
            level.DistanceReference = price - reference;
            level.DistanceCurrent = price - current;
            level.Label = label;
            return level;
        }

        private static List<PercentMap> PercentVariationMaps(DailyBar previousDay, IntradayContext intraday, VolumeProfileResult profile)
        {
            List<PercentMap> maps = new List<PercentMap>();
            maps.Add(PercentMap("prevClose", "Fechamento D-1", "D-1", "fechamento D-1", previousDay.Close, intraday.Price));
            return maps;
        }

        private static PercentMap PercentMap(string key, string label, string shortLabel, string status, decimal reference, decimal current)
        {
            PercentMap map = new PercentMap();
            map.Key = key;
            map.Label = label;
            map.ShortLabel = shortLabel;
            map.Status = status;
            map.Price = reference;

            if (reference <= 0m)
            {
                return map;
            }

            foreach (double pct in PercentVariations)
            {
                PercentLevel level = new PercentLevel();
                level.Percent = pct;
                level.Price = reference * (1m + Dec(pct / 100d));
                level.DistanceReference = level.Price - reference;
                level.DistanceCurrent = level.Price - current;
                level.Direction = pct > 0d ? "up" : pct < 0d ? "down" : "base";
                map.Levels.Add(level);
            }

            return map;
        }

        private static List<KeyLevel> FlattenPercentMaps(List<PercentMap> maps, decimal current)
        {
            List<KeyLevel> rows = new List<KeyLevel>();

            foreach (PercentMap map in maps)
            {
                foreach (PercentLevel level in map.Levels)
                {
                    KeyLevel row = Level(level.Price, PercentLabel(level.Percent) + " D-1", level.Percent >= 0 ? "Resistencia" : "Suporte", "Percent", PercentWeight(map.Key, level.Percent), map.Status);
                    row.Distance = level.Price - current;
                    row.Layer = map.Key;
                    rows.Add(row);
                }
            }

            return rows;
        }

        private static double PercentWeight(string key, double pct)
        {
            double baseWeight = key == "poc" ? 15d : key == "opening" ? 13d : 12d;
            double whole = Math.Abs(pct % 1d) < 0.0001d ? 4d : 2d;
            return baseWeight + whole + (Math.Abs(pct) < 0.0001d ? 5d : 0d);
        }

        private static List<BacktestRow> BacktestProxy(List<DailyBar> allBars, int window)
        {
            List<BacktestRow> rows = new List<BacktestRow>();
            decimal[] multipliers = new[] { 1m, 1.5m, 2m };

            foreach (decimal m in multipliers)
            {
                rows.Add(NewBacktestRow("Buy", m));
                rows.Add(NewBacktestRow("Sell", m));
            }

            for (int i = window; i < allBars.Count - 1; i++)
            {
                List<DailyBar> hist = allBars.Skip(i - window).Take(window).ToList();
                decimal sigma = CalcGarmanKlass(hist, window).Points;
                decimal anchor = allBars[i - 1].Close;
                DailyBar next = allBars[i];

                foreach (BacktestRow row in rows)
                {
                    row.Samples++;
                    decimal up = anchor + row.Multiplier * sigma;
                    decimal down = anchor - row.Multiplier * sigma;

                    if (string.Equals(row.Direction, "Buy", StringComparison.OrdinalIgnoreCase))
                    {
                        if (next.Low <= down)
                        {
                            AddBacktestTouch(row, next.Close > down, Math.Max(0m, next.Close - down), Math.Max(0m, down - next.Close));
                        }
                    }
                    else if (string.Equals(row.Direction, "Sell", StringComparison.OrdinalIgnoreCase))
                    {
                        if (next.High >= up)
                        {
                            AddBacktestTouch(row, next.Close < up, Math.Max(0m, up - next.Close), Math.Max(0m, next.Close - up));
                        }
                    }
                }
            }

            foreach (BacktestRow row in rows)
            {
                row.TouchRate = row.Samples == 0 ? 0d : (double)row.Touches / row.Samples * 100d;
                row.ReversalRate = row.Touches == 0 ? 0d : (double)row.Reversals / row.Touches * 100d;
                row.ContinuationRate = row.Touches == 0 ? 0d : (double)row.Continuations / row.Touches * 100d;
                row.Confidence = WilsonLowerBoundPct(row.Reversals, row.Touches);
                row.RiskReward = row.AverageAdversePoints <= 0m
                    ? (row.AverageReversalPoints > 0m ? 99m : 0m)
                    : row.AverageReversalPoints / row.AverageAdversePoints;
                row.EdgeScore = BacktestEdgeScore(row);
            }

            return rows;
        }

        private static BacktestRow NewBacktestRow(string direction, decimal multiplier)
        {
            BacktestRow row = new BacktestRow();
            row.Direction = direction;
            row.Multiplier = multiplier;
            return row;
        }

        private static void AddBacktestTouch(BacktestRow row, bool reversal, decimal favorablePoints, decimal adversePoints)
        {
            if (row == null)
            {
                return;
            }

            row.Touches++;

            if (reversal)
            {
                row.Reversals++;
            }
            else
            {
                row.Continuations++;
            }

            decimal favorableTotal = row.AverageReversalPoints * Math.Max(0, row.Touches - 1) + favorablePoints;
            decimal adverseTotal = row.AverageAdversePoints * Math.Max(0, row.Touches - 1) + adversePoints;
            row.AverageReversalPoints = favorableTotal / row.Touches;
            row.AverageAdversePoints = adverseTotal / row.Touches;
            row.ExpectancyPoints = row.AverageReversalPoints - row.AverageAdversePoints;
            row.ProfitFactor = row.AverageAdversePoints <= 0m
                ? (row.AverageReversalPoints > 0m ? 99d : 0d)
                : D(row.AverageReversalPoints / row.AverageAdversePoints);
        }

        private static TechnicalIndicatorSnapshot BuildTechnicalIndicators(List<DailyBar> bars, IntradayContext intraday, VolatilityMetric atr, int windowDays)
        {
            TechnicalIndicatorSnapshot snapshot = new TechnicalIndicatorSnapshot();
            snapshot.Source = "CSV diario + RTD atual";
            snapshot.SampleSize = bars == null ? 0 : bars.Count;

            List<decimal> closes = bars == null ? new List<decimal>() : bars.Select(x => x.Close).Where(x => x > 0m).ToList();

            if (intraday != null && intraday.Price > 0m)
            {
                closes.Add(intraday.Price);
            }

            snapshot.Sma20 = Sma(closes, 20);
            snapshot.Sma50 = Sma(closes, 50);
            snapshot.Ema9 = Ema(closes, 9);
            snapshot.Ema21 = Ema(closes, 21);
            snapshot.Ema50 = Ema(closes, 50);
            snapshot.Rsi14 = Rsi(closes, 14);

            List<decimal> last20 = closes.Skip(Math.Max(0, closes.Count - 20)).ToList();

            if (last20.Count >= 10)
            {
                decimal mean = last20.Average();
                decimal stdev = StdevDecimal(last20);
                snapshot.BollingerMiddle20 = mean;
                snapshot.BollingerUpper20 = mean + 2m * stdev;
                snapshot.BollingerLower20 = mean - 2m * stdev;
                snapshot.ZScore20 = stdev <= 0m || intraday == null ? (decimal?)null : (intraday.Price - mean) / stdev;
            }

            List<decimal> macdLine = MacdSeries(closes);

            if (macdLine.Count > 0)
            {
                snapshot.Macd = macdLine[macdLine.Count - 1];
                snapshot.MacdSignal = Ema(macdLine, 9);
                snapshot.MacdHistogram = snapshot.Macd.HasValue && snapshot.MacdSignal.HasValue ? snapshot.Macd.Value - snapshot.MacdSignal.Value : (decimal?)null;
            }

            if (intraday != null && atr != null && atr.Points > 0m)
            {
                snapshot.AtrVwapDistance = (intraday.Price - intraday.Vwap) / atr.Points;
            }

            ApplyReturnRiskMetrics(snapshot, closes, windowDays);
            snapshot.TrendState = TechnicalTrendState(snapshot, intraday);
            snapshot.ReversionState = TechnicalReversionState(snapshot);
            return snapshot;
        }

        private static void ApplyReturnRiskMetrics(TechnicalIndicatorSnapshot snapshot, List<decimal> closes, int window)
        {
            if (snapshot == null || closes == null || closes.Count < window + 1)
            {
                return;
            }

            List<decimal> returns = new List<decimal>();

            for (int i = 1; i < closes.Count; i++)
            {
                if (closes[i - 1] > 0m && closes[i] > 0m)
                {
                    returns.Add(((closes[i] / closes[i - 1]) - 1m) * 100m);
                }
            }

            List<decimal> recent = returns.Skip(Math.Max(0, returns.Count - window)).ToList();

            if (recent.Count < Math.Min(10, window))
            {
                return;
            }

            decimal var95 = Percentile(recent, 0.05d);
            List<decimal> tail = recent.Where(x => x <= var95).ToList();
            decimal stdev = StdevDecimal(recent);
            decimal downside = DownsideDeviation(recent);
            decimal mean = recent.Average();
            snapshot.ReturnMean21Pct = recent.Average();
            snapshot.ReturnStd21Pct = stdev;
            snapshot.DownsideStd21Pct = downside;
            snapshot.PositiveReturnRate21Pct = recent.Count == 0 ? 0m : recent.Count(x => x > 0m) * 100m / recent.Count;
            snapshot.Sharpe21 = stdev <= 0m ? (decimal?)null : mean / stdev * Dec(Math.Sqrt(window));
            snapshot.Sortino21 = downside <= 0m ? (decimal?)null : mean / downside * Dec(Math.Sqrt(window));
            snapshot.ValueAtRisk95Pct = var95;
            snapshot.ExpectedShortfall95Pct = tail.Count == 0 ? var95 : tail.Average();

            if (closes.Count >= 11 && closes[closes.Count - 11] > 0m)
            {
                snapshot.Momentum10Pct = ((closes[closes.Count - 1] / closes[closes.Count - 11]) - 1m) * 100m;
            }
        }

        private static List<QuantSignal> BuildQuantSignals(QuantResult result, decimal tickSize)
        {
            List<QuantSignal> signals = new List<QuantSignal>();

            if (result == null || result.Intraday == null || result.Technicals == null)
            {
                return signals;
            }

            decimal price = result.Intraday.Price;
            decimal atr = result.Atr == null || result.Atr.Points <= 0m ? Math.Max(tickSize * 12m, 1m) : result.Atr.Points;
            decimal tolerance = Math.Max(tickSize * 8m, atr * 0.35m);
            KeyLevel support = NearestQuantLevel(result.Confluence, price, "Suporte", tolerance);
            KeyLevel resistance = NearestQuantLevel(result.Confluence, price, "Resistencia", tolerance);
            int cap = QuantScoreCap(result.Bars == null ? 0 : result.Bars.Count);

            decimal rsi = result.Technicals.Rsi14.HasValue ? result.Technicals.Rsi14.Value : 50m;
            decimal z = result.Technicals.ZScore20.HasValue ? result.Technicals.ZScore20.Value : 0m;
            decimal atrVwap = result.Technicals.AtrVwapDistance.HasValue ? result.Technicals.AtrVwapDistance.Value : 0m;

            if (support != null && (rsi <= 42m || z <= -1.15m || atrVwap <= -0.75m))
            {
                int score = 56 + ExtremeBonus(50m - rsi, 18m) + ExtremeBonus(Math.Abs(z), 2.5m) + LevelScoreBonus(support);
                AddQuantSignal(signals, result, "Reversao estatistica", "Buy", Math.Min(cap, score), support, "preco em suporte estatistico com RSI/z-score favorecendo retorno a media");
            }

            if (resistance != null && (rsi >= 58m || z >= 1.15m || atrVwap >= 0.75m))
            {
                int score = 56 + ExtremeBonus(rsi - 50m, 18m) + ExtremeBonus(Math.Abs(z), 2.5m) + LevelScoreBonus(resistance);
                AddQuantSignal(signals, result, "Reversao estatistica", "Sell", Math.Min(cap, score), resistance, "preco em resistencia estatistica com RSI/z-score favorecendo retorno a media");
            }

            if (result.Technicals.BollingerLower20.HasValue && price <= result.Technicals.BollingerLower20.Value && rsi <= 45m)
            {
                KeyLevel level = new KeyLevel { Price = result.Technicals.BollingerLower20.Value, Label = "Bollinger inferior 20", Type = "Suporte", Source = "Tecnico", Score = 70d, Evidence = "CSV+RTD" };
                AddQuantSignal(signals, result, "Bollinger mean reversion", "Buy", Math.Min(cap, 66 + ExtremeBonus(50m - rsi, 18m)), level, "preco abaixo da banda inferior com RSI enfraquecido");
            }

            if (result.Technicals.BollingerUpper20.HasValue && price >= result.Technicals.BollingerUpper20.Value && rsi >= 55m)
            {
                KeyLevel level = new KeyLevel { Price = result.Technicals.BollingerUpper20.Value, Label = "Bollinger superior 20", Type = "Resistencia", Source = "Tecnico", Score = 70d, Evidence = "CSV+RTD" };
                AddQuantSignal(signals, result, "Bollinger mean reversion", "Sell", Math.Min(cap, 66 + ExtremeBonus(rsi - 50m, 18m)), level, "preco acima da banda superior com RSI esticado");
            }

            if (IsBullTrend(result.Technicals) && result.Technicals.Ema21.HasValue && Math.Abs(price - result.Technicals.Ema21.Value) <= tolerance && rsi >= 45m && rsi <= 68m)
            {
                KeyLevel level = new KeyLevel { Price = result.Technicals.Ema21.Value, Label = "EMA21 pullback", Type = "Suporte", Source = "Tecnico", Score = 68d, Evidence = "EMA9>EMA21>EMA50" };
                AddQuantSignal(signals, result, "Pullback quantitativo", "Buy", Math.Min(cap, 63 + TrendBonus(result.Technicals)), level, "tendencia compradora com pullback para EMA21");
            }

            if (IsBearTrend(result.Technicals) && result.Technicals.Ema21.HasValue && Math.Abs(price - result.Technicals.Ema21.Value) <= tolerance && rsi >= 32m && rsi <= 55m)
            {
                KeyLevel level = new KeyLevel { Price = result.Technicals.Ema21.Value, Label = "EMA21 pullback", Type = "Resistencia", Source = "Tecnico", Score = 68d, Evidence = "EMA9<EMA21<EMA50" };
                AddQuantSignal(signals, result, "Pullback quantitativo", "Sell", Math.Min(cap, 63 + TrendBonus(result.Technicals)), level, "tendencia vendedora com pullback para EMA21");
            }

            if (IsBullTrend(result.Technicals) &&
                result.Technicals.MacdHistogram.HasValue &&
                result.Technicals.MacdHistogram.Value > 0m &&
                result.Technicals.Momentum10Pct.HasValue &&
                result.Technicals.Momentum10Pct.Value > 0m &&
                price >= result.Intraday.Vwap)
            {
                KeyLevel level = new KeyLevel { Price = result.Intraday.Vwap, Label = result.Intraday.VwapIsProxy ? "VWAP proxy" : "VWAP/MED", Type = "Valor", Source = "RTD+Tecnico", Score = 72d, Evidence = "EMA/MACD/momentum" };
                AddQuantSignal(signals, result, "Momentum continuation", "Buy", Math.Min(cap, 65 + TrendBonus(result.Technicals)), level, "tendencia, MACD e momentum confirmam acima da VWAP");
            }

            if (IsBearTrend(result.Technicals) &&
                result.Technicals.MacdHistogram.HasValue &&
                result.Technicals.MacdHistogram.Value < 0m &&
                result.Technicals.Momentum10Pct.HasValue &&
                result.Technicals.Momentum10Pct.Value < 0m &&
                price <= result.Intraday.Vwap)
            {
                KeyLevel level = new KeyLevel { Price = result.Intraday.Vwap, Label = result.Intraday.VwapIsProxy ? "VWAP proxy" : "VWAP/MED", Type = "Valor", Source = "RTD+Tecnico", Score = 72d, Evidence = "EMA/MACD/momentum" };
                AddQuantSignal(signals, result, "Momentum continuation", "Sell", Math.Min(cap, 65 + TrendBonus(result.Technicals)), level, "tendencia, MACD e momentum confirmam abaixo da VWAP");
            }

            return signals
                .OrderByDescending(x => x.Score)
                .ThenBy(x => Math.Abs(x.LevelPrice.HasValue ? x.LevelPrice.Value - price : 0m))
                .Take(12)
                .ToList();
        }

        private static List<KeyLevel> BuildRawLevels(QuantResult r)
        {
            List<KeyLevel> levels = new List<KeyLevel>();
            decimal p = r.Intraday.Price;
            decimal sigma = r.GarmanKlass.Points;

            levels.Add(Level(p, "Preco atual", "Atual", "RTD", 90d, "ULT"));
            levels.Add(Level(r.Intraday.Open, "Abertura atual", "Valor", "Open", 58d, "ABE"));
            levels.Add(Level(r.Intraday.High, "Maxima atual", "Resistencia", "RTD", 50d, "MAX"));
            levels.Add(Level(r.Intraday.Low, "Minima atual", "Suporte", "RTD", 50d, "MIN"));
            levels.Add(Level(r.Intraday.Vwap, r.Intraday.VwapIsProxy ? "VWAP proxy" : "VWAP/MED", "Valor", "VWAP", 76d, "MED"));
            levels.Add(Level(r.Profile.Poc.Price, "POC proxy", "Valor", "POC", 82d, "profile"));
            levels.Add(Level(r.Profile.Vah, "VAH 70%", "Resistencia", "VAH", 66d, "profile"));
            levels.Add(Level(r.Profile.Val, "VAL 70%", "Suporte", "VAL", 66d, "profile"));
            levels.Add(Level(r.PreviousDay.Open, "D-1 Abertura", "Valor", "D1", 45d, "csv"));
            levels.Add(Level(r.PreviousDay.High, "D-1 Maxima", "Resistencia", "D1", 52d, "csv"));
            levels.Add(Level(r.PreviousDay.Low, "D-1 Minima", "Suporte", "D1", 52d, "csv"));
            levels.Add(Level(r.PreviousDay.Close, "D-1 Fechamento", "Valor", "D1", 55d, "csv"));

            decimal[] sigmaMultipliers = new[] { 1m, 1.5m, 2m, 2.5m };

            foreach (decimal m in sigmaMultipliers)
            {
                levels.Add(Level(r.Intraday.Vwap + m * sigma, "+" + m + " sigma", "Resistencia", "Sigma", 48d + D(m) * 3d, "VWAP"));
                levels.Add(Level(r.Intraday.Vwap - m * sigma, "-" + m + " sigma", "Suporte", "Sigma", 48d + D(m) * 3d, "VWAP"));
            }

            foreach (DeviationLevel gauss in r.GaussLevels)
            {
                string type = string.Equals(gauss.Side, "Venda", StringComparison.OrdinalIgnoreCase) ? "Resistencia" : "Suporte";
                double sigmaScore = 64d + Math.Min(12d, Math.Abs(D(gauss.Sigma)) * 2d);
                levels.Add(Level(gauss.Price, gauss.Label, type, "Gauss", sigmaScore, "Yang-Zhang winsorizado + MAD"));
            }

            foreach (ProfileBin bin in r.Profile.Hvn)
            {
                levels.Add(Level(bin.Price, "HVN", "Valor", "HVN", 44d + bin.Rank * 100d, "profile"));
            }

            foreach (ProfileBin bin in r.Profile.Lvn)
            {
                levels.Add(Level(bin.Price, "LVN", bin.Price > p ? "Resistencia" : "Suporte", "LVN", 42d, "profile"));
            }

            foreach (KeyLevel sr in r.SupportResistance)
            {
                levels.Add(sr);
            }

            foreach (AnchoredVwap avwap in r.Avwaps)
            {
                levels.Add(Level(avwap.Price, "AVWAP " + avwap.Label, "Valor", "AVWAP", 56d, avwap.AnchorDate.ToString("dd/MM/yyyy")));
            }

            if (r.Technicals != null)
            {
                AddTechnicalLevel(levels, r.Technicals.Ema9, "EMA9", p, 42d);
                AddTechnicalLevel(levels, r.Technicals.Ema21, "EMA21", p, 50d);
                AddTechnicalLevel(levels, r.Technicals.Ema50, "EMA50", p, 48d);
                AddTechnicalLevel(levels, r.Technicals.Sma20, "SMA20", p, 44d);
                AddTechnicalLevel(levels, r.Technicals.Sma50, "SMA50", p, 43d);
                AddTechnicalLevel(levels, r.Technicals.BollingerUpper20, "Bollinger superior", p, 54d);
                AddTechnicalLevel(levels, r.Technicals.BollingerLower20, "Bollinger inferior", p, 54d);
            }

            levels.AddRange(r.PercentTable);

            foreach (KeyLevel level in levels)
            {
                level.Distance = level.Price - p;
            }

            return levels;
        }

        private static List<KeyLevel> MergeInterestLevels(List<KeyLevel> raw, decimal currentPrice, decimal tolerance)
        {
            List<KeyLevel> sorted = raw.Where(x => x != null && x.Price > 0m).OrderBy(x => x.Price).ToList();
            List<List<KeyLevel>> clusters = new List<List<KeyLevel>>();

            foreach (KeyLevel item in sorted)
            {
                if (clusters.Count == 0 || Math.Abs(item.Price - clusters[clusters.Count - 1].Average(x => x.Price)) > tolerance)
                {
                    clusters.Add(new List<KeyLevel>());
                }

                clusters[clusters.Count - 1].Add(item);
            }

            List<KeyLevel> merged = new List<KeyLevel>();

            foreach (List<KeyLevel> cluster in clusters)
            {
                decimal price = cluster.Average(x => x.Price);
                string type = ResolveType(cluster, price, currentPrice);
                double baseScore = cluster.Sum(x => x.Score);
                double proximity = 18d / (1d + Math.Abs(D(price - currentPrice)) / Math.Max(1d, D(tolerance)));
                double diversity = cluster.Select(x => x.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count() * 4d;
                double score = Clamp(baseScore / Math.Max(1d, cluster.Count) + proximity + diversity, 0d, 100d);
                KeyLevel level = new KeyLevel();
                level.Price = price;
                level.Type = type;
                level.Source = string.Join(", ", cluster.Select(x => x.Source).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray());
                level.Label = string.Join(" | ", cluster.Select(x => x.Label).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Take(5).ToArray());
                level.Score = score;
                level.Distance = price - currentPrice;
                level.Evidence = cluster.Count + " marcações; " + level.Source;
                level.Tags = string.Join(", ", cluster.Select(x => x.Label).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray());
                merged.Add(level);
            }

            return merged.OrderByDescending(x => x.Score).ThenBy(x => Math.Abs(x.Distance)).ToList();
        }

        private static string ResolveType(List<KeyLevel> cluster, decimal price, decimal current)
        {
            if (cluster.Any(x => string.Equals(x.Type, "Atual", StringComparison.OrdinalIgnoreCase)))
            {
                return "Atual";
            }

            int supports = cluster.Count(x => string.Equals(x.Type, "Suporte", StringComparison.OrdinalIgnoreCase));
            int resistances = cluster.Count(x => string.Equals(x.Type, "Resistencia", StringComparison.OrdinalIgnoreCase));

            if (supports == resistances)
            {
                return price >= current ? "Resistencia" : "Suporte";
            }

            return supports > resistances ? "Suporte" : "Resistencia";
        }

        private static string DetectRegime(VolatilityMetric atr, VolatilityMetric closeToClose, IntradayContext intraday, DailyBar previous)
        {
            decimal move = intraday.Price - previous.Close;
            decimal range = Math.Max(1m, intraday.High - intraday.Low);

            if (atr.Percentile > 75d && Math.Abs(move) > atr.Points)
            {
                return "Tendencial / volatilidade alta";
            }

            if (Math.Abs(move) < range * 0.25m)
            {
                return "Rotacional / media reversao";
            }

            return move >= 0m ? "Pressao compradora" : "Pressao vendedora";
        }

        private static decimal? Sma(List<decimal> values, int period)
        {
            if (values == null || values.Count < period)
            {
                return null;
            }

            return values.Skip(values.Count - period).Average();
        }

        private static decimal? Ema(List<decimal> values, int period)
        {
            if (values == null || values.Count < period)
            {
                return null;
            }

            decimal k = 2m / (period + 1m);
            decimal ema = values.Take(period).Average();

            for (int i = period; i < values.Count; i++)
            {
                ema = values[i] * k + ema * (1m - k);
            }

            return ema;
        }

        private static decimal? Rsi(List<decimal> values, int period)
        {
            if (values == null || values.Count <= period)
            {
                return null;
            }

            decimal gain = 0m;
            decimal loss = 0m;

            for (int i = values.Count - period; i < values.Count; i++)
            {
                decimal change = values[i] - values[i - 1];

                if (change >= 0m)
                {
                    gain += change;
                }
                else
                {
                    loss += Math.Abs(change);
                }
            }

            if (loss <= 0m)
            {
                return 100m;
            }

            decimal rs = gain / loss;
            return 100m - (100m / (1m + rs));
        }

        private static List<decimal> MacdSeries(List<decimal> closes)
        {
            List<decimal> result = new List<decimal>();

            if (closes == null || closes.Count < 26)
            {
                return result;
            }

            for (int i = 25; i < closes.Count; i++)
            {
                List<decimal> window = closes.Take(i + 1).ToList();
                decimal? ema12 = Ema(window, 12);
                decimal? ema26 = Ema(window, 26);

                if (ema12.HasValue && ema26.HasValue)
                {
                    result.Add(ema12.Value - ema26.Value);
                }
            }

            return result;
        }

        private static decimal StdevDecimal(List<decimal> values)
        {
            if (values == null || values.Count < 2)
            {
                return 0m;
            }

            decimal avg = values.Average();
            double variance = values.Sum(x => Math.Pow(D(x - avg), 2d)) / Math.Max(1, values.Count - 1);
            return Dec(Math.Sqrt(variance));
        }

        private static decimal PopulationStdevDecimal(List<decimal> values)
        {
            if (values == null || values.Count == 0)
            {
                return 0m;
            }

            decimal avg = values.Average();
            double variance = values.Sum(x => Math.Pow(D(x - avg), 2d)) / Math.Max(1, values.Count);
            return Dec(Math.Sqrt(variance));
        }

        private static decimal RoundToStep(decimal value, decimal step)
        {
            if (step <= 0m)
            {
                return value;
            }

            return Math.Round(value / step, 0, MidpointRounding.AwayFromZero) * step;
        }

        private static decimal DownsideDeviation(List<decimal> values)
        {
            if (values == null || values.Count == 0)
            {
                return 0m;
            }

            List<decimal> downside = values.Select(x => Math.Min(0m, x)).ToList();
            double variance = downside.Sum(x => Math.Pow(D(x), 2d)) / Math.Max(1, downside.Count);
            return Dec(Math.Sqrt(variance));
        }

        private static decimal Percentile(List<decimal> values, double percentile)
        {
            if (values == null || values.Count == 0)
            {
                return 0m;
            }

            List<decimal> sorted = values.OrderBy(x => x).ToList();
            double clamped = Math.Max(0d, Math.Min(1d, percentile));
            double position = (sorted.Count - 1) * clamped;
            int lower = (int)Math.Floor(position);
            int upper = (int)Math.Ceiling(position);

            if (lower == upper)
            {
                return sorted[lower];
            }

            decimal weight = Dec(position - lower);
            return sorted[lower] + (sorted[upper] - sorted[lower]) * weight;
        }

        private static string TechnicalTrendState(TechnicalIndicatorSnapshot t, IntradayContext intraday)
        {
            if (t == null || intraday == null)
            {
                return "-";
            }

            if (IsBullTrend(t) && intraday.Price >= (t.Ema21.HasValue ? t.Ema21.Value : intraday.Price))
            {
                return "tendencia compradora";
            }

            if (IsBearTrend(t) && intraday.Price <= (t.Ema21.HasValue ? t.Ema21.Value : intraday.Price))
            {
                return "tendencia vendedora";
            }

            return "rotacional";
        }

        private static string TechnicalReversionState(TechnicalIndicatorSnapshot t)
        {
            if (t == null)
            {
                return "-";
            }

            decimal rsi = t.Rsi14.HasValue ? t.Rsi14.Value : 50m;
            decimal z = t.ZScore20.HasValue ? t.ZScore20.Value : 0m;

            if (rsi <= 35m || z <= -1.5m)
            {
                return "sobrevenda";
            }

            if (rsi >= 65m || z >= 1.5m)
            {
                return "sobrecompra";
            }

            return "neutro";
        }

        private static bool IsBullTrend(TechnicalIndicatorSnapshot t)
        {
            return t != null &&
                   t.Ema9.HasValue &&
                   t.Ema21.HasValue &&
                   t.Ema50.HasValue &&
                   t.Ema9.Value > t.Ema21.Value &&
                   t.Ema21.Value > t.Ema50.Value;
        }

        private static bool IsBearTrend(TechnicalIndicatorSnapshot t)
        {
            return t != null &&
                   t.Ema9.HasValue &&
                   t.Ema21.HasValue &&
                   t.Ema50.HasValue &&
                   t.Ema9.Value < t.Ema21.Value &&
                   t.Ema21.Value < t.Ema50.Value;
        }

        private static KeyLevel NearestQuantLevel(List<KeyLevel> levels, decimal price, string type, decimal tolerance)
        {
            if (levels == null)
            {
                return null;
            }

            return levels
                .Where(x => string.Equals(x.Type, type, StringComparison.OrdinalIgnoreCase))
                .Where(x => Math.Abs(x.Price - price) <= tolerance)
                .OrderBy(x => Math.Abs(x.Price - price))
                .ThenByDescending(x => x.Score)
                .FirstOrDefault();
        }

        private static int QuantScoreCap(int samples)
        {
            if (samples >= 126)
            {
                return 90;
            }

            if (samples >= 63)
            {
                return 84;
            }

            if (samples >= 21)
            {
                return 72;
            }

            return 60;
        }

        private static int ExtremeBonus(decimal value, decimal scale)
        {
            if (value <= 0m || scale <= 0m)
            {
                return 0;
            }

            return (int)Math.Min(12m, value / scale * 12m);
        }

        private static int LevelScoreBonus(KeyLevel level)
        {
            if (level == null)
            {
                return 0;
            }

            return (int)Math.Min(14d, Math.Max(0d, level.Score - 55d) / 3d);
        }

        private static int TrendBonus(TechnicalIndicatorSnapshot t)
        {
            int bonus = 6;

            if (t != null && t.MacdHistogram.HasValue)
            {
                bonus += t.MacdHistogram.Value > 0m ? 3 : 0;
            }

            return bonus;
        }

        private static void AddQuantSignal(List<QuantSignal> signals, QuantResult result, string setup, string direction, int score, KeyLevel level, string reasons)
        {
            if (result == null || result.Intraday == null)
            {
                return;
            }

            BacktestRow edge = BestDirectionalBacktestRow(result, direction);
            int adjustedScore = score + DirectionalEdgeAdjustment(edge) + ConfidenceScoreAdjustment(edge);
            adjustedScore = Math.Min(adjustedScore, DirectionalEdgeCap(edge));

            if (adjustedScore < 60)
            {
                return;
            }

            decimal fallbackRisk = result.Atr == null || result.Atr.Points <= 0m ? 0m : result.Atr.Points * 0.35m;
            decimal targetPoints = edge != null && edge.AverageReversalPoints > 0m ? edge.AverageReversalPoints : fallbackRisk;
            decimal stopPoints = edge != null && edge.AverageAdversePoints > 0m ? edge.AverageAdversePoints : fallbackRisk;
            decimal riskReward = stopPoints <= 0m ? 0m : targetPoints / stopPoints;

            QuantSignal signal = new QuantSignal();
            signal.Setup = setup;
            signal.Direction = direction;
            signal.Price = result.Intraday.Price;
            signal.Score = Math.Max(0, Math.Min(95, adjustedScore));
            signal.LevelName = level == null ? "-" : level.Label;
            signal.LevelPrice = level == null ? (decimal?)null : level.Price;
            signal.Reasons = reasons + "; " + DirectionalEdgeReason(edge) + "; regime " + result.Regime;
            signal.DataSource = "CSV+RTD";
            signal.SampleSize = result.Bars == null ? 0 : result.Bars.Count;
            signal.TechnicalState = result.Technicals == null ? "-" : result.Technicals.TrendState + " / " + result.Technicals.ReversionState;
            signal.StatisticalEdge = StatisticalEdgeText(result, direction, edge);
            signal.ReversalRate = edge == null ? 0d : edge.ReversalRate;
            signal.ProfitFactor = edge == null ? 0d : edge.ProfitFactor;
            signal.ExpectancyPoints = edge == null ? (decimal?)null : edge.ExpectancyPoints;
            signal.EdgeQuality = DirectionalEdgeQuality(edge);
            signal.Confidence = edge == null ? 0d : edge.Confidence;
            signal.ExpectedWinRate = edge == null ? 0d : edge.ReversalRate;
            signal.RiskReward = riskReward;
            signal.TargetPoints = targetPoints <= 0m ? (decimal?)null : targetPoints;
            signal.StopPoints = stopPoints <= 0m ? (decimal?)null : stopPoints;
            signal.RiskModel = RiskModelText(edge, targetPoints, stopPoints);
            signal.RobustnessGate = RobustnessGateText(result, edge);
            signals.Add(signal);
        }

        private static BacktestRow BestDirectionalBacktestRow(QuantResult result, string direction)
        {
            if (result == null || result.Backtest == null || result.Backtest.Count == 0 || string.IsNullOrWhiteSpace(direction))
            {
                return null;
            }

            return result.Backtest
                .Where(x => string.Equals(x.Direction, direction, StringComparison.OrdinalIgnoreCase))
                .Where(x => x.Touches > 0)
                .OrderByDescending(x => x.ExpectancyPoints)
                .ThenByDescending(x => x.ReversalRate)
                .FirstOrDefault();
        }

        private static int DirectionalEdgeAdjustment(BacktestRow edge)
        {
            if (edge == null || edge.Touches < 5)
            {
                return -6;
            }

            if (edge.ExpectancyPoints > 0m && edge.ReversalRate >= 58d && edge.ProfitFactor >= 1.25d && edge.Confidence >= 45d)
            {
                return 9;
            }

            if (edge.ExpectancyPoints > 0m && edge.ReversalRate >= 52d && edge.Confidence >= 30d)
            {
                return 5;
            }

            if (edge.ExpectancyPoints < 0m || edge.ReversalRate < 45d)
            {
                return -12;
            }

            return 0;
        }

        private static int ConfidenceScoreAdjustment(BacktestRow edge)
        {
            if (edge == null || edge.Touches == 0)
            {
                return -4;
            }

            if (edge.Confidence >= 55d && edge.RiskReward >= 1.15m)
            {
                return 5;
            }

            if (edge.Confidence >= 40d && edge.RiskReward >= 1m)
            {
                return 2;
            }

            if (edge.Confidence < 25d || edge.RiskReward < 0.85m)
            {
                return -8;
            }

            return 0;
        }

        private static int DirectionalEdgeCap(BacktestRow edge)
        {
            if (edge == null || edge.Touches < 5)
            {
                return 72;
            }

            if (edge.ExpectancyPoints < 0m || edge.ReversalRate < 45d)
            {
                return 68;
            }

            if (edge.Confidence < 25d || edge.RiskReward < 0.85m)
            {
                return 72;
            }

            if (edge.Confidence < 40d || edge.RiskReward < 1m)
            {
                return 84;
            }

            if (edge.ExpectancyPoints <= 0m || edge.ProfitFactor < 1.05d)
            {
                return 78;
            }

            return 95;
        }

        private static string DirectionalEdgeQuality(BacktestRow edge)
        {
            if (edge == null || edge.Touches == 0)
            {
                return "sem edge";
            }

            if (edge.Touches < 5)
            {
                return "amostra baixa";
            }

            if (edge.ExpectancyPoints > 0m && edge.ReversalRate >= 58d && edge.ProfitFactor >= 1.25d && edge.Confidence >= 45d)
            {
                return "positivo";
            }

            if (edge.ExpectancyPoints > 0m && edge.ProfitFactor >= 1.05d && edge.Confidence >= 30d)
            {
                return "moderado";
            }

            return "fragil";
        }

        private static string DirectionalEdgeReason(BacktestRow edge)
        {
            if (edge == null || edge.Touches == 0)
            {
                return "edge direcional sem toques";
            }

            if (edge.Touches < 5)
            {
                return "edge direcional com poucos toques";
            }

            return "edge " + DirectionalEdgeQuality(edge) +
                   " exp " + edge.ExpectancyPoints.ToString("N1") +
                   " pts PF " + edge.ProfitFactor.ToString("N2") +
                   " conf " + edge.Confidence.ToString("N1") + "%";
        }

        private static string StatisticalEdgeText(QuantResult result, string direction, BacktestRow edge)
        {
            if (result == null || result.Backtest == null || result.Backtest.Count == 0)
            {
                return "sem backtest proxy";
            }

            if (edge == null || edge.Touches == 0)
            {
                return "sem toques suficientes";
            }

            return direction + " rev " + edge.ReversalRate.ToString("N1") +
                   "% | exp " + edge.ExpectancyPoints.ToString("N1") +
                   " pts | PF " + edge.ProfitFactor.ToString("N2") +
                   " | conf " + edge.Confidence.ToString("N1") +
                   "% | R/R " + edge.RiskReward.ToString("N2") +
                   " | " + edge.Multiplier.ToString("N1") + " sigma";
        }

        private static string RiskModelText(BacktestRow edge, decimal targetPoints, decimal stopPoints)
        {
            if (edge == null || edge.Touches == 0)
            {
                return "risco proxy por ATR; sem toques historicos";
            }

            return "alvo medio " + targetPoints.ToString("N1") +
                   " pts | risco medio " + stopPoints.ToString("N1") +
                   " pts | R/R " + (stopPoints <= 0m ? 0m : targetPoints / stopPoints).ToString("N2");
        }

        private static string RobustnessGateText(QuantResult result, BacktestRow edge)
        {
            int samples = result == null || result.Bars == null ? 0 : result.Bars.Count;

            if (edge == null || edge.Touches == 0)
            {
                return "bloqueado: sem toques no backtest proxy";
            }

            if (samples < 63)
            {
                return "limitado: amostra historica <63";
            }

            if (edge.Touches < 8)
            {
                return "limitado: poucos toques historicos";
            }

            if (edge.ExpectancyPoints <= 0m || edge.ProfitFactor < 1.05d)
            {
                return "limitado: expectancy/PF insuficiente";
            }

            if (edge.Confidence < 45d)
            {
                return "limitado: confianca estatistica baixa";
            }

            if (edge.RiskReward < 1m)
            {
                return "limitado: risco/retorno desfavoravel";
            }

            return "aprovado: exige confirmacao RTD de fluxo";
        }

        private static void AddTechnicalLevel(List<KeyLevel> levels, decimal? price, string label, decimal current, double score)
        {
            if (!price.HasValue || price.Value <= 0m)
            {
                return;
            }

            levels.Add(Level(price.Value, label, price.Value >= current ? "Resistencia" : "Suporte", "Tecnico", score, "CSV+RTD"));
        }

        private static KeyLevel Level(decimal price, string label, string type, string source, double score, string evidence)
        {
            KeyLevel level = new KeyLevel();
            level.Price = price;
            level.Label = label;
            level.Type = type;
            level.Source = source;
            level.Score = score;
            level.Evidence = evidence;
            level.Layer = source;
            return level;
        }

        private static decimal TypicalPrice(DailyBar b)
        {
            return (b.High + b.Low + b.Close) / 3m;
        }

        private static decimal BarVolume(DailyBar b)
        {
            if (b.Volume.HasValue && b.Volume.Value > 0m)
            {
                return b.Volume.Value;
            }

            if (b.Quantity.HasValue && b.Quantity.Value > 0m)
            {
                return b.Quantity.Value;
            }

            return 1m;
        }

        private static List<double> WinsorizeByMad(List<double> values, double madMultiplier)
        {
            if (values == null || values.Count == 0)
            {
                return new List<double>();
            }

            double median = Median(values);
            double robustScale = RobustMadScale(values);

            if (robustScale <= 0d)
            {
                return values.ToList();
            }

            double lower = median - madMultiplier * robustScale;
            double upper = median + madMultiplier * robustScale;
            return values.Select(x => Clamp(x, lower, upper)).ToList();
        }

        private static List<double> WinsorizeUpper(List<double> values, double upperPercentile)
        {
            if (values == null || values.Count == 0)
            {
                return new List<double>();
            }

            double upper = Percentile(values, upperPercentile);
            return values.Select(x => Math.Min(x, upper)).ToList();
        }

        private static double RobustMadScale(List<double> values)
        {
            if (values == null || values.Count == 0)
            {
                return 0d;
            }

            double median = Median(values);
            List<double> deviations = values.Select(x => Math.Abs(x - median)).ToList();
            return Median(deviations) * GaussMadScale;
        }

        private static double Median(List<double> values)
        {
            return Percentile(values, 0.5d);
        }

        private static double Percentile(List<double> values, double percentile)
        {
            if (values == null || values.Count == 0)
            {
                return 0d;
            }

            List<double> sorted = values.OrderBy(x => x).ToList();
            double clamped = Clamp(percentile, 0d, 1d);
            double position = (sorted.Count - 1) * clamped;
            int lower = (int)Math.Floor(position);
            int upper = (int)Math.Ceiling(position);

            if (lower == upper)
            {
                return sorted[lower];
            }

            double weight = position - lower;
            return sorted[lower] + (sorted[upper] - sorted[lower]) * weight;
        }

        private static double Average(List<double> values)
        {
            return values == null || values.Count == 0 ? 0d : values.Average();
        }

        private static double Variance(List<double> values)
        {
            if (values == null || values.Count < 2)
            {
                return 0d;
            }

            double avg = values.Average();
            return values.Sum(x => (x - avg) * (x - avg)) / (values.Count - 1);
        }

        private static double Stdev(List<double> values, bool sample)
        {
            if (values == null || values.Count < 2)
            {
                return 0d;
            }

            double avg = values.Average();
            double divisor = sample ? values.Count - 1 : values.Count;
            return Math.Sqrt(values.Sum(x => (x - avg) * (x - avg)) / Math.Max(1d, divisor));
        }

        private static double PercentileRank(List<double> values, double x)
        {
            if (values == null || values.Count == 0)
            {
                return 0d;
            }

            int below = values.Count(v => v <= x);
            return below * 100d / values.Count;
        }

        private static double WilsonLowerBoundPct(int successes, int total)
        {
            if (total <= 0 || successes <= 0)
            {
                return 0d;
            }

            double z = 1.96d;
            double n = total;
            double p = successes / n;
            double denominator = 1d + z * z / n;
            double centre = p + z * z / (2d * n);
            double margin = z * Math.Sqrt((p * (1d - p) + z * z / (4d * n)) / n);
            return Math.Max(0d, (centre - margin) / denominator * 100d);
        }

        private static double BacktestEdgeScore(BacktestRow row)
        {
            if (row == null || row.Touches == 0)
            {
                return 0d;
            }

            double expectancy = row.ExpectancyPoints <= 0m ? 0d : Math.Min(20d, D(row.ExpectancyPoints));
            double pf = Math.Min(25d, Math.Max(0d, row.ProfitFactor - 1d) * 25d);
            double rr = row.RiskReward <= 0m ? 0d : Math.Min(15d, D(row.RiskReward) * 7.5d);
            double sample = Math.Min(15d, row.Touches * 1.5d);
            return Clamp(row.Confidence * 0.25d + expectancy + pf + rr + sample, 0d, 100d);
        }

        private static string PercentLabel(double pct)
        {
            return pct.ToString("0.##") + "%";
        }

        private static double Clamp(double n, double min, double max)
        {
            return Math.Max(min, Math.Min(max, n));
        }

        private static decimal RoundToTick(decimal value, decimal tickSize)
        {
            decimal normalizedTick = tickSize <= 0m ? 0.5m : tickSize;
            return Math.Round(value / normalizedTick, 0, MidpointRounding.AwayFromZero) * normalizedTick;
        }

        private static double D(decimal value)
        {
            return decimal.ToDouble(value);
        }

        private static decimal Dec(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return 0m;
            }

            return Convert.ToDecimal(value);
        }
    }
}
