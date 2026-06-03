using System;
using System.Collections.Generic;
using System.Linq;
using RtdDolarNative.Csv;
using RtdDolarNative.MarketData;

namespace RtdDolarNative.Quant
{
    public static class QuantEngine
    {
        private static readonly double[] PercentVariations = new[] { 3d, 2.5d, 2d, 1.5d, 1d, 0.5d, 0d, -0.5d, -1d, -1.5d, -2d, -2.5d, -3d };

        public static QuantResult Build(List<DailyBar> allBars, MarketSnapshot snapshot, decimal tickSize)
        {
            QuantResult result = new QuantResult();
            result.Bars = allBars == null ? new List<DailyBar>() : allBars.OrderBy(x => x.Date).ToList();

            if (result.Bars.Count == 0)
            {
                result.Warnings.Add("Carregue um CSV diario para calcular os niveis.");
                result.Intraday = BuildIntraday(snapshot, null);
                return result;
            }

            if (result.Bars.Count < 21)
            {
                result.Warnings.Add("CSV tem menos de 21 pregoes validos; calculos ficam incompletos.");
            }

            result.PreviousDay = result.Bars[result.Bars.Count - 1];
            result.Intraday = BuildIntraday(snapshot, result.PreviousDay);

            List<DailyBar> window = result.Bars.Skip(Math.Max(0, result.Bars.Count - 63)).ToList();
            List<DailyBar> w21 = result.Bars.Skip(Math.Max(0, result.Bars.Count - 21)).ToList();

            result.GarmanKlass = CalcGarmanKlass(w21, 21);
            result.Parkinson = CalcParkinson(w21, 21);
            result.RogersSatchell = CalcRogersSatchell(w21, 21);
            result.YangZhang = CalcYangZhang(w21, 21);
            result.CloseToClose = CalcCloseToClose(w21, 21);
            result.Atr = CalcAtr(w21, 21, result.Bars);
            result.Metrics.Add(result.GarmanKlass);
            result.Metrics.Add(result.Parkinson);
            result.Metrics.Add(result.RogersSatchell);
            result.Metrics.Add(result.YangZhang);
            result.Metrics.Add(result.CloseToClose);
            result.Metrics.Add(result.Atr);

            int[] windows = new[] { 21, 45, 63 };
            foreach (int days in windows)
            {
                List<DailyBar> bars = result.Bars.Skip(Math.Max(0, result.Bars.Count - days)).ToList();

                if (bars.Count >= 2)
                {
                    result.WindowMetrics.Add(CalcGarmanKlass(bars, days));
                    result.WindowMetrics.Add(CalcYangZhang(bars, days));
                    result.WindowMetrics.Add(CalcAtr(bars, days, result.Bars));
                }
            }

            result.Profile = VolumeProfileProxy(window.Count > 0 ? window : result.Bars);
            result.SupportResistance = SupportResistanceEngine(window, result.Intraday.Price, result.GarmanKlass.Points, result.Atr.Points);
            result.Avwaps = AnchoredVwaps(window);
            result.OpeningLevels = ReferenceDeviationLevels(result.Intraday.Open, result.GarmanKlass.Points, result.Intraday.Price);
            result.PocDeviationLevels = ReferenceDeviationLevels(result.Profile.Poc.Price, result.GarmanKlass.Points, result.Intraday.Price);
            result.PercentMaps = PercentVariationMaps(result.PreviousDay, result.Intraday, result.Profile);
            result.PercentTable = FlattenPercentMaps(result.PercentMaps, result.Intraday.Price);
            result.Backtest = BacktestProxy(result.Bars, 21);
            result.KeyLevels = BuildRawLevels(result);
            result.Confluence = MergeInterestLevels(result.KeyLevels, result.Intraday.Price, tickSize);
            result.Regime = DetectRegime(result.Atr, result.CloseToClose, result.Intraday, result.PreviousDay);

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
            VolatilityMetric metric = new VolatilityMetric();
            metric.Name = name;
            metric.Window = window;
            metric.Points = Dec(ratio * D(price));
            metric.Percent = ratio * 100d;
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
            List<DeviationLevel> levels = new List<DeviationLevel>();
            int[] multipliers = new[] { 1, 2, 3, 4 };

            foreach (int m in multipliers)
            {
                decimal sell = referencePrice + m * sigma;
                decimal buy = referencePrice - m * sigma;
                levels.Add(Deviation("Venda", "sell", m, sell, referencePrice, currentPrice, "Venda +" + m + " desvio"));
                levels.Add(Deviation("Compra", "buy", -m, buy, referencePrice, currentPrice, "Compra -" + m + " desvio"));
            }

            return levels;
        }

        private static DeviationLevel Deviation(string side, string dir, int sigma, decimal price, decimal reference, decimal current, string label)
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
            maps.Add(PercentMap("prevClose", "Fechamento anterior", "D-1 fechamento", "real/csv", previousDay.Close, intraday.Price));
            maps.Add(PercentMap("opening", "Abertura atual", "Abertura", "real/RTD", intraday.Open, intraday.Price));
            maps.Add(PercentMap("poc", "POC proxy", "POC", "proxy diario", profile.Poc.Price, intraday.Price));
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
                    KeyLevel row = Level(level.Price, map.ShortLabel + " " + PercentLabel(level.Percent), level.Percent >= 0 ? "Resistencia" : "Suporte", "Percent", PercentWeight(map.Key, level.Percent), map.Status);
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
                BacktestRow row = new BacktestRow();
                row.Multiplier = m;
                rows.Add(row);
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
                    bool touch = next.High >= up || next.Low <= down;

                    if (touch)
                    {
                        row.Touches++;
                        bool reversal = (next.High >= up && next.Close < up) || (next.Low <= down && next.Close > down);

                        if (reversal)
                        {
                            row.Reversals++;
                        }
                    }
                }
            }

            foreach (BacktestRow row in rows)
            {
                row.TouchRate = row.Samples == 0 ? 0d : (double)row.Touches / row.Samples * 100d;
                row.ReversalRate = row.Touches == 0 ? 0d : (double)row.Reversals / row.Touches * 100d;
            }

            return rows;
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

        private static string PercentLabel(double pct)
        {
            return pct.ToString("0.##") + "%";
        }

        private static double Clamp(double n, double min, double max)
        {
            return Math.Max(min, Math.Min(max, n));
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
