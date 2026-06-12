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
                CorridorIsUnavailableWithoutBothSides,
                CorridorClassifiesCompressionNearResistance,
                CorridorClassifiesWideMiddleRange,
                SqlMemoryFramesHistoricalSupportAndResistanceAfterRestart,
                SqlMemoryBiasesTowardStrongerHistoricalSupport,
                OperationalPlanPromotesAlignedSupportDefense,
                OperationalPlanWaitsWhenLiveAndSqlConflict,
                OperationalPlanCalculatesBuyDefenseRiskReward,
                OperationalPlanCalculatesSellDefenseRiskReward,
                HeatmapDoesNotCrashWhenPriceDistanceOverflowsIntTicks,
                HeatmapAutoViewportTracksCurrentPrice,
                HeatmapManualViewportCanNavigateAwayFromCurrentPrice
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

        private static void CorridorClassifiesCompressionNearResistance()
        {
            HeatmapProcessor processor = BuildProcessor();

            try
            {
                MarketSnapshot snapshot = BuildSnapshot(5000.5m);
                AddBid(snapshot, 0, 4999.5m, 3200m);
                AddAsk(snapshot, 0, 5001m, 3400m);
                processor.PostSnapshot(snapshot);

                HeatmapSnapshot heatmap = processor.GetSnapshot("WDOFUT_F_0", 5000.5m, 40);

                Assert(heatmap.Corridor.IsAvailable, "Compressed corridor should be available.");
                AssertEqual("Comprimido", heatmap.Corridor.Phase, "A narrow framed corridor should be marked as compressed.");
                AssertEqual("Perto resistencia", heatmap.Corridor.Location, "Price in the upper corridor third should be near resistance.");
                Assert(heatmap.Corridor.Read.IndexOf("comprimido", StringComparison.OrdinalIgnoreCase) >= 0, "Read should include corridor phase.");
            }
            finally
            {
                processor.Dispose();
            }
        }

        private static void CorridorClassifiesWideMiddleRange()
        {
            HeatmapProcessor processor = BuildProcessor();

            try
            {
                MarketSnapshot snapshot = BuildSnapshot(5000m);
                AddBid(snapshot, 0, 4996m, 3200m);
                AddAsk(snapshot, 0, 5004m, 3400m);
                processor.PostSnapshot(snapshot);

                HeatmapSnapshot heatmap = processor.GetSnapshot("WDOFUT_F_0", 5000m, 40);

                Assert(heatmap.Corridor.IsAvailable, "Wide corridor should be available.");
                AssertEqual("Amplo", heatmap.Corridor.Phase, "A wide framed corridor should be marked as wide.");
                AssertEqual("Meio", heatmap.Corridor.Location, "Price near the middle should be marked as middle corridor.");
                Assert(heatmap.Corridor.Read.IndexOf("meio", StringComparison.OrdinalIgnoreCase) >= 0, "Read should include corridor location.");
            }
            finally
            {
                processor.Dispose();
            }
        }

        private static void SqlMemoryFramesHistoricalSupportAndResistanceAfterRestart()
        {
            string path = BuildDatabasePath();
            HeatmapProcessor writer = BuildProcessor(path, true);

            try
            {
                MarketSnapshot historical = BuildSnapshot(5000m);
                historical.LocalTimestamp = DateTimeOffset.Now.AddMinutes(-5);
                AddBid(historical, 0, 4998.5m, 5200m);
                AddAsk(historical, 0, 5002m, 4700m);

                writer.Start();
                writer.PostSnapshot(historical);
                Assert(WaitUntil(() => writer.StorageStatus.IndexOf("book 2", StringComparison.OrdinalIgnoreCase) >= 0, 3000), "Writer should persist historical book levels into SQLite.");
            }
            finally
            {
                writer.Dispose();
            }

            HeatmapProcessor reader = BuildProcessor(path, true);

            try
            {
                HeatmapSnapshot heatmap = reader.GetSnapshot("WDOFUT_F_0", 5000m, 40);

                Assert(heatmap.SqlMemory != null, "Heatmap should expose an SQL memory summary.");
                Assert(heatmap.SqlMemory.IsAvailable, "SQL memory should be available when historical levels frame the current price.");
                AssertEqual(4998.5m, heatmap.SqlMemory.SupportPrice.Value, "SQL memory should expose nearest historical support below price.");
                AssertEqual(5002m, heatmap.SqlMemory.ResistancePrice.Value, "SQL memory should expose nearest historical resistance above price.");
                Assert(heatmap.SqlMemory.Read.IndexOf("SQL", StringComparison.OrdinalIgnoreCase) >= 0, "SQL memory read should make the database source explicit.");
            }
            finally
            {
                reader.Dispose();
            }
        }

        private static void SqlMemoryBiasesTowardStrongerHistoricalSupport()
        {
            string path = BuildDatabasePath();
            HeatmapProcessor writer = BuildProcessor(path, true);

            try
            {
                MarketSnapshot historical = BuildSnapshot(5000m);
                historical.LocalTimestamp = DateTimeOffset.Now.AddMinutes(-4);
                AddBid(historical, 0, 4998.5m, 8000m);
                AddAsk(historical, 0, 5002m, 4200m);

                writer.Start();
                writer.PostSnapshot(historical);
                Assert(WaitUntil(() => writer.StorageStatus.IndexOf("book 2", StringComparison.OrdinalIgnoreCase) >= 0, 3000), "Writer should persist historical support and resistance into SQLite.");
            }
            finally
            {
                writer.Dispose();
            }

            HeatmapProcessor reader = BuildProcessor(path, true);

            try
            {
                HeatmapSnapshot heatmap = reader.GetSnapshot("WDOFUT_F_0", 5000m, 40);

                Assert(heatmap.SqlMemory != null && heatmap.SqlMemory.IsAvailable, "SQL memory should summarize persisted historical context.");
                AssertEqual("Compra", heatmap.SqlMemory.Direction, "SQL memory should bias toward the stronger historical support side.");
                Assert(heatmap.SqlMemory.PressureScore > 10m, "SQL memory pressure should be positive when historical support dominates.");
                Assert(heatmap.SqlMemory.Read.IndexOf("sup", StringComparison.OrdinalIgnoreCase) >= 0, "SQL memory read should name the support anchor.");
            }
            finally
            {
                reader.Dispose();
            }
        }

        private static void OperationalPlanPromotesAlignedSupportDefense()
        {
            string path = BuildDatabasePath();
            HeatmapProcessor writer = BuildProcessor(path, true);

            try
            {
                MarketSnapshot historical = BuildSnapshot(5000m);
                historical.LocalTimestamp = DateTimeOffset.Now.AddMinutes(-3);
                AddBid(historical, 0, 4999.5m, 8200m);
                AddAsk(historical, 0, 5002m, 3600m);

                writer.Start();
                writer.PostSnapshot(historical);
                Assert(WaitUntil(() => writer.StorageStatus.IndexOf("book 2", StringComparison.OrdinalIgnoreCase) >= 0, 3000), "Writer should persist aligned SQL support.");
            }
            finally
            {
                writer.Dispose();
            }

            HeatmapProcessor reader = BuildProcessor(path, true);

            try
            {
                MarketSnapshot live = BuildSnapshot(5000m);
                AddBid(live, 0, 4999.5m, 6200m);
                AddAsk(live, 0, 5002m, 3500m);
                reader.PostSnapshot(live);

                HeatmapSnapshot heatmap = reader.GetSnapshot("WDOFUT_F_0", 5000m, 40);

                Assert(heatmap.Plan != null, "Heatmap should expose an operational plan.");
                AssertEqual("Compra defesa", heatmap.Plan.State, "Aligned nearby support should become the primary operational plan.");
                AssertEqual("Compra", heatmap.Plan.Direction, "Aligned support plan should point to the buy side.");
                Assert(heatmap.Plan.ConfidenceScore >= 70m, "Aligned support plan should carry high confidence.");
                Assert(heatmap.Plan.Trigger.IndexOf("4999,50", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       heatmap.Plan.Trigger.IndexOf("4999.50", StringComparison.OrdinalIgnoreCase) >= 0,
                    "Plan trigger should expose the support price.");
            }
            finally
            {
                reader.Dispose();
            }
        }

        private static void OperationalPlanWaitsWhenLiveAndSqlConflict()
        {
            string path = BuildDatabasePath();
            HeatmapProcessor writer = BuildProcessor(path, true);

            try
            {
                MarketSnapshot historical = BuildSnapshot(5000m);
                historical.LocalTimestamp = DateTimeOffset.Now.AddMinutes(-3);
                AddAsk(historical, 0, 4999.5m, 8600m);

                writer.Start();
                writer.PostSnapshot(historical);
                Assert(WaitUntil(() => writer.StorageStatus.IndexOf("book 1", StringComparison.OrdinalIgnoreCase) >= 0, 3000), "Writer should persist conflicting SQL resistance.");
            }
            finally
            {
                writer.Dispose();
            }

            HeatmapProcessor reader = BuildProcessor(path, true);

            try
            {
                MarketSnapshot live = BuildSnapshot(5000m);
                AddBid(live, 0, 4999.5m, 6200m);
                AddAsk(live, 0, 5002m, 2800m);
                reader.PostSnapshot(live);

                HeatmapSnapshot heatmap = reader.GetSnapshot("WDOFUT_F_0", 5000m, 40);

                Assert(heatmap.Plan != null, "Heatmap should expose an operational plan even under conflict.");
                AssertEqual("Aguardar conflito", heatmap.Plan.State, "Conflicting live/SQL context should block directional action.");
                AssertEqual("Neutro", heatmap.Plan.Direction, "Conflict plan should be neutral.");
                Assert(heatmap.Plan.Read.IndexOf("conflito", StringComparison.OrdinalIgnoreCase) >= 0, "Conflict plan should explain why it is waiting.");
            }
            finally
            {
                reader.Dispose();
            }
        }

        private static void OperationalPlanCalculatesBuyDefenseRiskReward()
        {
            HeatmapProcessor processor = BuildProcessor();

            try
            {
                MarketSnapshot snapshot = BuildSnapshot(5000m);
                AddBid(snapshot, 0, 4999.5m, 6200m);
                AddAsk(snapshot, 0, 5002m, 3600m);
                processor.PostSnapshot(snapshot);

                HeatmapSnapshot heatmap = processor.GetSnapshot("WDOFUT_F_0", 5000m, 40);

                AssertEqual("Compra defesa", heatmap.Plan.State, "Buy support should create a defensive plan.");
                Assert(heatmap.Plan.TargetPrice.HasValue, "Buy defense should expose a target price.");
                Assert(heatmap.Plan.StopPrice.HasValue, "Buy defense should expose a stop price.");
                AssertEqual(5002m, heatmap.Plan.TargetPrice.Value, "Buy defense target should use corridor resistance.");
                AssertEqual(4999m, heatmap.Plan.StopPrice.Value, "Buy defense stop should sit one tick below support.");
                AssertEqual(1, heatmap.Plan.RiskTicks, "Buy defense should expose risk in ticks.");
                AssertEqual(5, heatmap.Plan.RewardTicks, "Buy defense should expose reward in ticks.");
                Assert(heatmap.Plan.RiskReward >= 4.9m, "Buy defense should expose a favorable risk/reward.");
                Assert(heatmap.Plan.Envelope.IndexOf("R/R", StringComparison.OrdinalIgnoreCase) >= 0, "Envelope should summarize risk/reward.");
            }
            finally
            {
                processor.Dispose();
            }
        }

        private static void OperationalPlanCalculatesSellDefenseRiskReward()
        {
            HeatmapProcessor processor = BuildProcessor();

            try
            {
                MarketSnapshot snapshot = BuildSnapshot(5000m);
                AddAsk(snapshot, 0, 5000.5m, 6200m);
                AddBid(snapshot, 0, 4998m, 3600m);
                processor.PostSnapshot(snapshot);

                HeatmapSnapshot heatmap = processor.GetSnapshot("WDOFUT_F_0", 5000m, 40);

                AssertEqual("Venda defesa", heatmap.Plan.State, "Sell resistance should create a defensive plan.");
                Assert(heatmap.Plan.TargetPrice.HasValue, "Sell defense should expose a target price.");
                Assert(heatmap.Plan.StopPrice.HasValue, "Sell defense should expose a stop price.");
                AssertEqual(4998m, heatmap.Plan.TargetPrice.Value, "Sell defense target should use corridor support.");
                AssertEqual(5001m, heatmap.Plan.StopPrice.Value, "Sell defense stop should sit one tick above resistance.");
                AssertEqual(1, heatmap.Plan.RiskTicks, "Sell defense should expose risk in ticks.");
                AssertEqual(5, heatmap.Plan.RewardTicks, "Sell defense should expose reward in ticks.");
                Assert(heatmap.Plan.RiskReward >= 4.9m, "Sell defense should expose a favorable risk/reward.");
            }
            finally
            {
                processor.Dispose();
            }
        }

        private static void HeatmapDoesNotCrashWhenPriceDistanceOverflowsIntTicks()
        {
            HeatmapProcessor processor = BuildProcessor();

            try
            {
                MarketSnapshot snapshot = BuildSnapshot(2000000000m);
                AddBid(snapshot, 0, 1m, 5000m);
                AddAsk(snapshot, 0, 2m, 5000m);
                processor.PostSnapshot(snapshot);

                HeatmapSnapshot heatmap = processor.GetSnapshot("WDOFUT_F_0", 2000000000m, 40);

                Assert(heatmap.InterestCells.Count > 0, "Extreme but valid price distances should still produce interest rows.");
                Assert(!heatmap.InterestCells.Any(x => x.DistanceTicks == int.MinValue), "Distance ticks should be clamped away from int.MinValue.");
            }
            finally
            {
                processor.Dispose();
            }
        }

        private static void HeatmapAutoViewportTracksCurrentPrice()
        {
            HeatmapProcessor processor = BuildProcessor();

            try
            {
                MarketSnapshot snapshot = BuildSnapshot(5000m);
                AddDenseBook(snapshot, 4980m, 0.5m, 60);
                processor.PostSnapshot(snapshot);

                HeatmapSnapshot heatmap = processor.GetSnapshot("WDOFUT_F_0", 5000m, 12, null);

                AssertEqual("Auto", heatmap.ViewportMode, "Default heatmap viewport should follow current price.");
                Assert(heatmap.TotalPriceLevels > heatmap.Cells.Count, "Snapshot should expose the total available price levels.");
                Assert(heatmap.Cells.Any(x => x.Price == 5000m), "Auto viewport should include the current price region.");
                Assert(!heatmap.Cells.Any(x => x.Price == 4980m), "Auto viewport should not be stuck at the oldest visible level.");
            }
            finally
            {
                processor.Dispose();
            }
        }

        private static void HeatmapManualViewportCanNavigateAwayFromCurrentPrice()
        {
            HeatmapProcessor processor = BuildProcessor();

            try
            {
                MarketSnapshot snapshot = BuildSnapshot(5000m);
                AddDenseBook(snapshot, 4980m, 0.5m, 60);
                processor.PostSnapshot(snapshot);

                HeatmapSnapshot heatmap = processor.GetSnapshot("WDOFUT_F_0", 5000m, 12, 4985m);

                AssertEqual("Manual", heatmap.ViewportMode, "Manual heatmap viewport should be explicit.");
                AssertEqual(4985m, heatmap.ViewportAnchorPrice.Value, "Manual viewport should remember the requested anchor.");
                Assert(heatmap.Cells.Any(x => x.Price == 4985m), "Manual viewport should include the requested price region.");
                Assert(!heatmap.Cells.Any(x => x.Price == 5000m), "Manual viewport should be able to leave the current price region.");
                Assert(heatmap.VisibleTopPrice.HasValue && heatmap.VisibleBottomPrice.HasValue, "Manual viewport should expose the visible price range.");
            }
            finally
            {
                processor.Dispose();
            }
        }

        private static HeatmapProcessor BuildProcessor()
        {
            HeatmapProcessor processor = BuildProcessor(BuildDatabasePath(), false);
            return processor;
        }

        private static HeatmapProcessor BuildProcessor(string path, bool useHistoricalContext)
        {
            HeatmapProcessor processor = new HeatmapProcessor(0.5m, path, new Logger(null));
            processor.UseHistoricalContext = useHistoricalContext;
            return processor;
        }

        private static string BuildDatabasePath()
        {
            string folder = Path.Combine(Path.GetTempPath(), "heatmap-feature-tests", Guid.NewGuid().ToString("N"));
            return Path.Combine(folder, "heatmap.sqlite");
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

        private static void AddDenseBook(MarketSnapshot snapshot, decimal firstPrice, decimal step, int levels)
        {
            for (int i = 0; i < levels && i < 50; i++)
            {
                decimal price = firstPrice + i * step;

                if (price <= 5000m)
                {
                    AddBid(snapshot, i, price, 1000m + i * 10m);
                }
                else
                {
                    AddAsk(snapshot, i, price, 1000m + i * 10m);
                }
            }
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

        private static void AssertEqual(string expected, string actual, string message)
        {
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(message + " Expected " + expected + ", got " + actual + ".");
            }
        }

        private static bool WaitUntil(Func<bool> predicate, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow <= deadline)
            {
                if (predicate())
                {
                    return true;
                }

                System.Threading.Thread.Sleep(25);
            }

            return predicate();
        }
    }
}
