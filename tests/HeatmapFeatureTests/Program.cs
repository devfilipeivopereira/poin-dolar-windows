using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RtdDolarNative.Heatmap;
using RtdDolarNative.Logging;
using RtdDolarNative.MarketData;

namespace HeatmapFeatureTests
{
    internal static class Program
    {
        private static int Main()
        {
            List<Action> tests = new List<Action>
            {
                CorridorTracksNearestSupportResistanceAroundPrice,
                CorridorIsUnavailableWithoutBothSides
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

        private static void CorridorTracksNearestSupportResistanceAroundPrice()
        {
            HeatmapProcessor processor = BuildProcessor();

            try
            {
                MarketSnapshot snapshot = BuildSnapshot(5000m);
                AddBid(snapshot, 0, 4999m, 3200m);
                AddBid(snapshot, 1, 4998.5m, 1100m);
                AddAsk(snapshot, 0, 5002m, 3300m);
                AddAsk(snapshot, 1, 5002.5m, 1200m);
                processor.PostSnapshot(snapshot);

                HeatmapSnapshot heatmap = processor.GetSnapshot("WDOFUT_F_0", 5000m, 40);

                Assert(heatmap.Corridor != null, "Heatmap should expose a corridor model.");
                Assert(heatmap.Corridor.IsAvailable, "Corridor should be available when buy support and sell resistance frame current price.");
                AssertEqual(4999m, heatmap.Corridor.SupportPrice, "Corridor support should use the nearest buy-zone center below price.");
                AssertEqual(5002m, heatmap.Corridor.ResistancePrice, "Corridor resistance should use the nearest sell-zone center above price.");
                AssertEqual(6, heatmap.Corridor.WidthTicks, "Corridor width should be measured in ticks.");
                Assert(heatmap.Corridor.CurrentPositionPct > 30m && heatmap.Corridor.CurrentPositionPct < 35m, "Current price should expose its relative corridor position.");
                Assert(heatmap.Corridor.Read.IndexOf("corredor", StringComparison.OrdinalIgnoreCase) >= 0, "Corridor read should be human-readable.");
            }
            finally
            {
                processor.Dispose();
            }
        }

        private static void CorridorIsUnavailableWithoutBothSides()
        {
            HeatmapProcessor processor = BuildProcessor();

            try
            {
                MarketSnapshot snapshot = BuildSnapshot(5000m);
                AddBid(snapshot, 0, 4999m, 3000m);
                AddBid(snapshot, 1, 4998.5m, 1200m);
                processor.PostSnapshot(snapshot);

                HeatmapSnapshot heatmap = processor.GetSnapshot("WDOFUT_F_0", 5000m, 40);

                Assert(heatmap.Corridor != null, "Heatmap should always expose a corridor model.");
                Assert(!heatmap.Corridor.IsAvailable, "Corridor should be unavailable without both support and resistance.");
                Assert(heatmap.Corridor.Read.IndexOf("sem corredor", StringComparison.OrdinalIgnoreCase) >= 0, "Unavailable corridor should explain the missing frame.");
            }
            finally
            {
                processor.Dispose();
            }
        }

        private static HeatmapProcessor BuildProcessor()
        {
            string folder = Path.Combine(Path.GetTempPath(), "heatmap-feature-tests", Guid.NewGuid().ToString("N"));
            string path = Path.Combine(folder, "heatmap.sqlite");
            HeatmapProcessor processor = new HeatmapProcessor(0.5m, path, new Logger(null));
            processor.UseHistoricalContext = false;
            return processor;
        }

        private static MarketSnapshot BuildSnapshot(decimal currentPrice)
        {
            MarketSnapshot snapshot = new MarketSnapshot();
            snapshot.Asset = "WDOFUT_F_0";
            snapshot.LocalTimestamp = DateTimeOffset.Now;
            snapshot.Rtd["ULT"] = currentPrice;
            return snapshot;
        }

        private static void AddBid(MarketSnapshot snapshot, int index, decimal price, decimal quantity)
        {
            snapshot.Rtd["BOOK_OCP_" + index.ToString(CultureInfo.InvariantCulture)] = price;
            snapshot.Rtd["BOOK_VOC_" + index.ToString(CultureInfo.InvariantCulture)] = quantity;
        }

        private static void AddAsk(MarketSnapshot snapshot, int index, decimal price, decimal quantity)
        {
            snapshot.Rtd["BOOK_OVD_" + index.ToString(CultureInfo.InvariantCulture)] = price;
            snapshot.Rtd["BOOK_VOV_" + index.ToString(CultureInfo.InvariantCulture)] = quantity;
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
                throw new InvalidOperationException(message + " Expected " + expected.ToString(CultureInfo.InvariantCulture) + ", got " + actual.ToString(CultureInfo.InvariantCulture) + ".");
            }
        }

        private static void AssertEqual(int expected, int actual, string message)
        {
            if (expected != actual)
            {
                throw new InvalidOperationException(message + " Expected " + expected.ToString(CultureInfo.InvariantCulture) + ", got " + actual.ToString(CultureInfo.InvariantCulture) + ".");
            }
        }
    }
}
