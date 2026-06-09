using System;
using System.Collections.Generic;
using System.Linq;
using RtdDolarNative.Csv;
using RtdDolarNative.MarketData;
using RtdDolarNative.Quant;

namespace QuantEngineTests
{
    internal static class Program
    {
        private static int Main()
        {
            List<Action> tests = new List<Action>
            {
                GaussUsesDataDrivenRobustScale,
                GaussLevelsAreCenteredOnCurrentOpen,
                GaussLevelsFeedConfluenceMap
            };

            int failed = 0;

            foreach (Action test in tests)
            {
                try
                {
                    test();
                    Console.WriteLine("PASS " + test.Method.Name);
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine("FAIL " + test.Method.Name + ": " + ex.Message);
                }
            }

            return failed == 0 ? 0 : 1;
        }

        private static void GaussUsesDataDrivenRobustScale()
        {
            QuantResult result = BuildResult();

            Assert(result.Gauss != null, "Gauss metric should be available.");
            Assert(result.Gauss.Points > 0m, "Gauss points should be positive.");
            Assert(result.Gauss.Points != 52m, "Gauss points should be calculated from the loaded OHLC window, not fixed at 52.");
            Assert(string.Equals(result.Gauss.Name, "Gauss robusto", StringComparison.OrdinalIgnoreCase), "Gauss metric name should identify the robust model.");
        }

        private static void GaussLevelsAreCenteredOnCurrentOpen()
        {
            QuantResult result = BuildResult();
            decimal open = result.Intraday.Open;
            decimal sigma = result.Gauss.Points;
            DeviationLevel sell = result.GaussLevels.FirstOrDefault(x => x.Side == "Venda" && x.Sigma == 1m);
            DeviationLevel buy = result.GaussLevels.FirstOrDefault(x => x.Side == "Compra" && x.Sigma == -1m);

            Assert(sell != null, "First sell Gauss level should exist.");
            Assert(buy != null, "First buy Gauss level should exist.");
            AssertEqual(open + sigma, sell.Price, "First sell Gauss level should be abertura + sigma.");
            AssertEqual(open - sigma, buy.Price, "First buy Gauss level should be abertura - sigma.");
            AssertEqual(open, sell.Price - sell.DistanceReference, "Sell distance reference should point back to abertura.");
            AssertEqual(open, buy.Price - buy.DistanceReference, "Buy distance reference should point back to abertura.");
        }

        private static void GaussLevelsFeedConfluenceMap()
        {
            QuantResult result = BuildResult();

            Assert(result.KeyLevels.Any(x => string.Equals(x.Source, "Gauss", StringComparison.OrdinalIgnoreCase)), "Gauss levels should be part of the raw/confluence level map.");
        }

        private static QuantResult BuildResult()
        {
            MarketSnapshot snapshot = new MarketSnapshot();
            snapshot.Rtd["ULT"] = 5223.5m;
            snapshot.Rtd["ABE"] = 5180m;
            snapshot.Rtd["MAX"] = 5232.5m;
            snapshot.Rtd["MIN"] = 5162m;
            snapshot.Rtd["MED"] = 5204m;
            snapshot.Rtd["VOL"] = 100000m;
            return QuantEngine.Build(BuildBars(), snapshot, 0.5m, 45);
        }

        private static List<DailyBar> BuildBars()
        {
            List<DailyBar> bars = new List<DailyBar>();
            DateTime start = new DateTime(2026, 4, 1);

            for (int i = 0; i < 48; i++)
            {
                decimal drift = (i % 9) - 4;
                decimal open = 5100m + i * 1.75m + drift;
                decimal high = open + 19m + (i % 6) * 2.5m;
                decimal low = open - 17m - (i % 5) * 1.5m;
                decimal close = open + ((i % 7) - 3) * 4.25m;

                if (i == 12)
                {
                    high += 120m;
                    low -= 95m;
                    close += 80m;
                }

                bars.Add(new DailyBar
                {
                    Asset = "WDOFUT_F_0",
                    Date = start.AddDays(i),
                    Open = open,
                    High = Math.Max(high, Math.Max(open, close)),
                    Low = Math.Min(low, Math.Min(open, close)),
                    Close = close,
                    Volume = 1000m + i
                });
            }

            return bars;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void AssertEqual(decimal expected, decimal actual, string message)
        {
            if (expected != actual)
            {
                throw new InvalidOperationException(message + " Expected " + expected + ", got " + actual + ".");
            }
        }
    }
}
