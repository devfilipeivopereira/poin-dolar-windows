using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RtdDolarNative.Charts;
using RtdDolarNative.Csv;
using RtdDolarNative.MarketData;
using RtdDolarNative.Quant;

namespace QuantEngineTests
{
    internal static class Program
    {
        [STAThread]
        private static int Main()
        {
            List<Action> tests = new List<Action>
            {
                GaussUsesDataDrivenRobustScale,
                GaussLevelsAreCenteredOnCurrentOpen,
                GaussLevelsFeedConfluenceMap,
                ReferenceMapsResolvePrimarySources,
                ReferenceMapsFallbackWhenSnapshotFieldsAreMissing,
                ReferenceMapClosingUsesCsvWhenRtdCloseMatchesOpening,
                ReferenceMapsBuildDirectionalLadders,
                PtaxHistoryStoreUpsertsAndLoads,
                ChartReferenceLineModeFiltersOpeningAndClosingMaps,
                ChartMetricLinesUseOnlySelectedReferenceMode,
                ChartMetricBuySellLinesUseDirectionalColors,
                ChartClosingLinesUseCsvD1CloseEvenWhenRtdFecExists,
                ChartCommandsAdjustViewportPredictably,
                ChartResetPreservesDisplaySettings,
                GarchEngineFitsParametersAndCalculatesBands
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

        private static void ReferenceMapsResolvePrimarySources()
        {
            QuantResult result = BuildResult();
            ReferenceMapResult opening = result.ReferenceMaps.FirstOrDefault(x => x.ReferenceKey == "opening");
            ReferenceMapResult closing = result.ReferenceMaps.FirstOrDefault(x => x.ReferenceKey == "closing");
            ReferenceMapResult poc = result.ReferenceMaps.FirstOrDefault(x => x.ReferenceKey == "poc");
            ReferenceMapResult adjustment = result.ReferenceMaps.FirstOrDefault(x => x.ReferenceKey == "adjustment");
            ReferenceMapResult ptax = result.ReferenceMaps.FirstOrDefault(x => x.ReferenceKey == "ptax");

            Assert(opening != null, "Opening reference map should exist.");
            Assert(closing != null, "Closing reference map should exist.");
            Assert(poc != null, "POC reference map should exist.");
            Assert(adjustment != null, "Adjustment reference map should exist.");
            Assert(ptax != null, "PTAX reference map should exist.");
            AssertEqual(5180m, opening.ReferencePrice, "Opening reference should use RTD abertura.");
            AssertEqual(result.PreviousDay.Close, closing.ReferencePrice, "Closing reference should use CSV D-1 close when available.");
            AssertEqual("CSV D-1", closing.ReferenceSource, "Closing reference should identify the CSV D-1 source.");
            AssertEqual(5192m, adjustment.ReferencePrice, "Adjustment reference should use AJU when available.");
            AssertEqual(5.41m, ptax.ReferencePrice, "PTAX reference should use the applied manual SQL value.");
            Assert(poc.ReferencePrice > 0m, "POC reference should come from the proxy profile.");
        }

        private static void ReferenceMapsFallbackWhenSnapshotFieldsAreMissing()
        {
            MarketSnapshot snapshot = new MarketSnapshot();
            snapshot.Rtd["ULT"] = 5223.5m;
            snapshot.Rtd["MAX"] = 5232.5m;
            snapshot.Rtd["MIN"] = 5162m;
            snapshot.Rtd["MED"] = 5204m;
            snapshot.Rtd["VOL"] = 100000m;
            snapshot.Rtd["AJA"] = 5188m;

            QuantResult result = QuantEngine.Build(BuildBars(), snapshot, 0.5m, 45);
            ReferenceMapResult opening = result.ReferenceMaps.FirstOrDefault(x => x.ReferenceKey == "opening");
            ReferenceMapResult closing = result.ReferenceMaps.FirstOrDefault(x => x.ReferenceKey == "closing");
            ReferenceMapResult adjustment = result.ReferenceMaps.FirstOrDefault(x => x.ReferenceKey == "adjustment");
            ReferenceMapResult ptax = result.ReferenceMaps.FirstOrDefault(x => x.ReferenceKey == "ptax");

            Assert(opening != null, "Opening fallback map should exist.");
            Assert(closing != null, "Closing fallback map should exist.");
            Assert(adjustment != null, "Adjustment fallback map should exist.");
            Assert(ptax != null, "PTAX fallback map should exist.");
            AssertEqual(result.Intraday.Open, opening.ReferencePrice, "Opening fallback should use the intraday open proxy.");
            AssertEqual(result.PreviousDay.Close, closing.ReferencePrice, "Closing fallback should use D-1 close from CSV.");
            AssertEqual(5188m, adjustment.ReferencePrice, "Adjustment fallback should use AJA when AJU is unavailable.");
            AssertEqual(0m, ptax.ReferencePrice, "PTAX should stay unavailable when there is no saved value.");
            Assert(ptax.GarmanLevels.Count == 0 && ptax.GaussLevels.Count == 0 && ptax.StdDevLevels.Count == 0, "PTAX map should not fabricate levels without a valid reference.");
        }

        private static void ReferenceMapClosingUsesCsvWhenRtdCloseMatchesOpening()
        {
            MarketSnapshot snapshot = new MarketSnapshot();
            snapshot.Rtd["ULT"] = 5223.5m;
            snapshot.Rtd["ABE"] = 5180m;
            snapshot.Rtd["FEC"] = 5180m;
            snapshot.Rtd["MAX"] = 5232.5m;
            snapshot.Rtd["MIN"] = 5162m;
            snapshot.Rtd["AJU"] = 5192m;
            snapshot.Rtd["MED"] = 5204m;
            snapshot.Rtd["VOL"] = 100000m;

            QuantResult result = QuantEngine.Build(BuildBars(), snapshot, 0.5m, 45);
            ReferenceMapResult opening = result.ReferenceMaps.FirstOrDefault(x => x.ReferenceKey == "opening");
            ReferenceMapResult closing = result.ReferenceMaps.FirstOrDefault(x => x.ReferenceKey == "closing");

            Assert(opening != null, "Opening reference map should exist.");
            Assert(closing != null, "Closing reference map should exist.");
            AssertEqual(5180m, opening.ReferencePrice, "Opening reference should keep RTD abertura.");
            AssertEqual(result.PreviousDay.Close, closing.ReferencePrice, "Closing reference should fall back to CSV D-1 when RTD FEC equals abertura.");
            Assert(opening.GarmanLevels.First(x => x.Side == "Venda" && x.Sigma == 1m).Price != closing.GarmanLevels.First(x => x.Side == "Venda" && x.Sigma == 1m).Price,
                "Opening and closing chart levels should move when their references differ.");
        }

        private static void ReferenceMapsBuildDirectionalLadders()
        {
            QuantResult result = BuildResult();
            ReferenceMapResult opening = result.ReferenceMaps.FirstOrDefault(x => x.ReferenceKey == "opening");

            Assert(opening != null, "Opening reference map should exist.");
            AssertEqual(8, opening.GarmanLevels.Count, "Garman-Klass opening map should build 8 directional levels.");
            AssertEqual(8, opening.GaussLevels.Count, "Gauss opening map should build 8 directional levels.");
            AssertEqual(8, opening.StdDevLevels.Count, "StdDev opening map should build 8 directional levels.");
            Assert(opening.GarmanLevels.Count(x => x.Side == "Venda" && x.Price > opening.ReferencePrice) == 4, "Sell Garman levels should stay above the reference.");
            Assert(opening.GarmanLevels.Count(x => x.Side == "Compra" && x.Price < opening.ReferencePrice) == 4, "Buy Garman levels should stay below the reference.");
        }

        private static void PtaxHistoryStoreUpsertsAndLoads()
        {
            string folder = Path.Combine(Path.GetTempPath(), "ptax-history-tests", Guid.NewGuid().ToString("N"));
            string path = Path.Combine(folder, "history.sqlite");
            PtaxHistorySqliteStore store = new PtaxHistorySqliteStore(path);

            try
            {
                store.Upsert(new DateTime(2026, 6, 7), 5.31m);
                store.Upsert(new DateTime(2026, 6, 8), 5.44m);
                store.Upsert(new DateTime(2026, 6, 7), 5.36m);

                PtaxHistoryEntry loaded = store.Load(new DateTime(2026, 6, 7));
                List<PtaxHistoryEntry> all = store.LoadAll();

                Assert(loaded != null, "PTAX load by date should return the saved row.");
                AssertEqual(5.36m, loaded.Value, "PTAX upsert should overwrite the previous value for the same date.");
                AssertEqual(2, all.Count, "PTAX history should deduplicate repeated dates.");
                AssertEqual(new DateTime(2026, 6, 8), all[0].TradeDate, "PTAX history should be sorted from newest to oldest.");
                AssertEqual(new DateTime(2026, 6, 7), all[1].TradeDate, "PTAX history should keep the previous date after the newest row.");
            }
            finally
            {
                TryDelete(folder);
            }
        }

        private static void ChartReferenceLineModeFiltersOpeningAndClosingMaps()
        {
            QuantResult result = BuildResult();
            NativeChartControl chart = new NativeChartControl();

            chart.ChartReferenceLineMode = ChartReferenceLineMode.Opening;
            List<KeyLevel> openingLevels = chart.ReferenceMetricLevelsForDiagnostics(result);
            Assert(openingLevels.Count > 0, "Opening reference mode should produce indicator levels.");
            Assert(openingLevels.All(x => string.Equals(x.Tags, "opening", StringComparison.OrdinalIgnoreCase)), "Opening reference mode should keep only opening levels.");

            chart.ChartReferenceLineMode = ChartReferenceLineMode.Closing;
            List<KeyLevel> closingLevels = chart.ReferenceMetricLevelsForDiagnostics(result);
            Assert(closingLevels.Count > 0, "D-1 close reference mode should produce indicator levels.");
            Assert(closingLevels.All(x => string.Equals(x.Tags, "closing", StringComparison.OrdinalIgnoreCase)), "D-1 close reference mode should keep only closing levels.");

            chart.ChartReferenceLineMode = ChartReferenceLineMode.OpeningAndClosing;
            List<KeyLevel> bothLevels = chart.ReferenceMetricLevelsForDiagnostics(result);
            Assert(bothLevels.Any(x => string.Equals(x.Tags, "opening", StringComparison.OrdinalIgnoreCase)), "Both reference mode should include opening levels.");
            Assert(bothLevels.Any(x => string.Equals(x.Tags, "closing", StringComparison.OrdinalIgnoreCase)), "Both reference mode should include D-1 close levels.");
            Assert(bothLevels.All(x =>
                string.Equals(x.Tags, "opening", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.Tags, "closing", StringComparison.OrdinalIgnoreCase)), "Both reference mode should not include adjustment, PTAX, POC or other reference maps.");
            Assert(!bothLevels.Any(x => string.Equals(x.Tags, "poc", StringComparison.OrdinalIgnoreCase)), "Reference levels sent to the chart should keep POC hidden.");
        }

        private static void ChartMetricLinesUseOnlySelectedReferenceMode()
        {
            QuantResult result = BuildResult();
            result.Garch.DailyBands.Add(new GarchBandLevel
            {
                Price = 5333m,
                Label = "GARCH diario legado",
                Source = "GARCH-Diario",
                Side = "Venda",
                ScoreHint = 93
            });

            Assert(result.KeyLevels.Any(x => string.Equals(x.Source, "Gauss", StringComparison.OrdinalIgnoreCase)), "Test setup should include legacy Gauss lines in base key levels.");

            NativeChartControl chart = new NativeChartControl();
            chart.ShowConfluenceLevels = false;
            chart.ChartReferenceLineMode = ChartReferenceLineMode.Opening;

            List<KeyLevel> chartLevels = chart.ChartLevelsForDiagnostics(result);
            List<KeyLevel> indicatorLevels = chartLevels
                .Where(x => IsChartIndicatorMetricSource(x.Source))
                .ToList();

            Assert(indicatorLevels.Count > 0, "Chart should still show metric indicator lines.");
            Assert(indicatorLevels.All(x => string.Equals(x.Tags, "opening", StringComparison.OrdinalIgnoreCase)), "Metric indicator lines should come only from the selected reference mode.");
            Assert(!indicatorLevels.Any(x => string.IsNullOrWhiteSpace(x.Tags)), "Metric indicator lines should not include legacy base or standalone bands without a reference tag.");
            Assert(!indicatorLevels.Any(x => !string.IsNullOrWhiteSpace(x.Source) && x.Source.IndexOf("GARCH-", StringComparison.OrdinalIgnoreCase) >= 0), "Standalone GARCH bands should not be added on top of reference metric lines.");
        }

        private static void ChartMetricBuySellLinesUseDirectionalColors()
        {
            QuantResult result = BuildResult();
            NativeChartControl chart = new NativeChartControl();
            chart.ChartReferenceLineMode = ChartReferenceLineMode.Opening;

            List<KeyLevel> levels = chart.ReferenceMetricLevelsForDiagnostics(result);
            KeyLevel sell = levels.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Label) && x.Label.IndexOf("Venda", StringComparison.OrdinalIgnoreCase) >= 0);
            KeyLevel buy = levels.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Label) && x.Label.IndexOf("Compra", StringComparison.OrdinalIgnoreCase) >= 0);

            Assert(sell != null, "Reference metrics should include sell lines.");
            Assert(buy != null, "Reference metrics should include buy lines.");
            AssertEqual("#FFFF5252", chart.ChartLevelColorForDiagnostics(sell), "Sell lines should be red.");
            AssertEqual("#FF12B886", chart.ChartLevelColorForDiagnostics(buy), "Buy lines should be green.");
        }

        private static void ChartClosingLinesUseCsvD1CloseEvenWhenRtdFecExists()
        {
            QuantResult result = BuildResult();
            NativeChartControl chart = new NativeChartControl();
            chart.ChartReferenceLineMode = ChartReferenceLineMode.Closing;

            KeyLevel gaussSell = chart.ReferenceMetricLevelsForDiagnostics(result)
                .FirstOrDefault(x => string.Equals(x.Source, "Gauss", StringComparison.OrdinalIgnoreCase) &&
                                     string.Equals(x.Type, "Venda", StringComparison.OrdinalIgnoreCase) &&
                                     x.Label != null &&
                                     x.Label.IndexOf("+1", StringComparison.OrdinalIgnoreCase) >= 0);

            Assert(gaussSell != null, "Closing chart mode should produce a Gauss sell +1 line.");
            AssertEqual(result.PreviousDay.Close + result.Gauss.Points, gaussSell.Price, "Closing chart lines should be anchored on CSV D-1 close, not RTD FEC.");
            Assert(gaussSell.Label.IndexOf("Fechamento", StringComparison.OrdinalIgnoreCase) >= 0, "Closing chart line label should still identify fechamento.");
        }

        private static void ChartCommandsAdjustViewportPredictably()
        {
            NativeChartControl chart = new NativeChartControl();
            chart.SetData(BuildLongBars(), null, null);

            chart.PanHorizontalCandles(18);
            AssertEqual(18, chart.ViewOffsetFromEndForDiagnostics, "Horizontal pan should move the viewport by candle count.");

            chart.PanHorizontalCandles(-1000);
            AssertEqual(-40, chart.ViewOffsetFromEndForDiagnostics, "Horizontal pan should clamp to the future limit.");

            int beforeZoom = chart.VisibleCandlesForDiagnostics;
            chart.ZoomHorizontalSteps(1);
            Assert(chart.VisibleCandlesForDiagnostics < beforeZoom, "Horizontal zoom in should reduce visible candles.");

            chart.ZoomHorizontalSteps(-1);
            AssertEqual(beforeZoom, chart.VisibleCandlesForDiagnostics, "Horizontal zoom out should restore visible candle count.");

            chart.PanVerticalFraction(0.25d);
            Assert(chart.PricePanOffsetForDiagnostics > 0m, "Vertical pan down should move the price window down with a positive offset.");

            double beforePriceZoom = chart.PriceScaleForDiagnostics;
            chart.ZoomVerticalSteps(1);
            Assert(chart.PriceScaleForDiagnostics < beforePriceZoom, "Vertical zoom in should reduce price scale.");
        }

        private static void ChartResetPreservesDisplaySettings()
        {
            NativeChartControl chart = new NativeChartControl();
            chart.SetData(BuildLongBars(), null, null);
            chart.PriceGridTickInterval = 50;
            chart.CandleSpacingPercent = 150;
            chart.PanHorizontalCandles(24);
            chart.PanVerticalFraction(0.2d);
            chart.ZoomHorizontalSteps(2);
            chart.ZoomVerticalSteps(2);

            chart.ResetViewport();

            AssertEqual(0, chart.ViewOffsetFromEndForDiagnostics, "Reset should return horizontal offset to live view.");
            AssertEqual(90, chart.VisibleCandlesForDiagnostics, "Reset should restore default visible candles.");
            AssertEqual(1d, chart.PriceScaleForDiagnostics, "Reset should restore vertical scale.");
            AssertEqual(0m, chart.PricePanOffsetForDiagnostics, "Reset should clear vertical pan.");
            AssertEqual(50, chart.PriceGridTickInterval, "Reset should not overwrite selected price grid spacing.");
            AssertEqual(150, chart.CandleSpacingPercent, "Reset should not overwrite selected candle spacing.");
        }

        private static QuantResult BuildResult()
        {
            MarketSnapshot snapshot = new MarketSnapshot();
            snapshot.Rtd["ULT"] = 5223.5m;
            snapshot.Rtd["ABE"] = 5180m;
            snapshot.Rtd["MAX"] = 5232.5m;
            snapshot.Rtd["MIN"] = 5162m;
            snapshot.Rtd["FEC"] = 5200m;
            snapshot.Rtd["AJU"] = 5192m;
            snapshot.Rtd["MED"] = 5204m;
            snapshot.Rtd["VOL"] = 100000m;
            snapshot.Rtd["PTAX"] = 5.41m;
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

        private static List<DailyBar> BuildLongBars()
        {
            List<DailyBar> bars = new List<DailyBar>();
            DateTime start = new DateTime(2025, 1, 2);

            for (int i = 0; i < 320; i++)
            {
                decimal open = 5000m + i * 0.7m + (i % 11);
                decimal close = open + ((i % 5) - 2) * 1.5m;

                bars.Add(new DailyBar
                {
                    Asset = "WDOFUT_F_0",
                    Date = start.AddDays(i),
                    Open = open,
                    High = Math.Max(open, close) + 12m + (i % 4),
                    Low = Math.Min(open, close) - 10m - (i % 3),
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

        private static void AssertEqual(int expected, int actual, string message)
        {
            if (expected != actual)
            {
                throw new InvalidOperationException(message + " Expected " + expected + ", got " + actual + ".");
            }
        }

        private static void AssertEqual(DateTime expected, DateTime actual, string message)
        {
            if (expected != actual)
            {
                throw new InvalidOperationException(message + " Expected " + expected.ToString("yyyy-MM-dd") + ", got " + actual.ToString("yyyy-MM-dd") + ".");
            }
        }

        private static void AssertEqual(double expected, double actual, string message)
        {
            if (Math.Abs(expected - actual) > 0.000001d)
            {
                throw new InvalidOperationException(message + " Expected " + expected + ", got " + actual + ".");
            }
        }

        private static void AssertEqual(string expected, string actual, string message)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(message + " Expected " + expected + ", got " + actual + ".");
            }
        }

        private static bool IsChartIndicatorMetricSource(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return false;
            }

            return source.IndexOf("Garman", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   source.IndexOf("GK", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   source.IndexOf("Gauss", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   source.IndexOf("Desvio", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   source.IndexOf("StdDev", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   source.IndexOf("StandardDeviation", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   source.IndexOf("GARCH", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void GarchEngineFitsParametersAndCalculatesBands()
        {
            List<DailyBar> bars = new List<DailyBar>();
            DateTime start = new DateTime(2025, 1, 2);
            double dailyVolatility = 0.015d;
            double currentPrice = 5000.0d;
            Random rand = new Random(42);

            for (int i = 0; i < 200; i++)
            {
                double u1 = 1.0 - rand.NextDouble();
                double u2 = 1.0 - rand.NextDouble();
                double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
                double dailyReturn = randStdNormal * dailyVolatility;
                double nextPrice = currentPrice * Math.Exp(dailyReturn);

                bars.Add(new DailyBar
                {
                    Asset = "WDOFUT_F_0",
                    Date = start.AddDays(i),
                    Open = (decimal)currentPrice,
                    High = (decimal)(Math.Max(currentPrice, nextPrice) + 10.0),
                    Low = (decimal)(Math.Min(currentPrice, nextPrice) - 10.0),
                    Close = (decimal)nextPrice,
                    Volume = 50000m
                });

                currentPrice = nextPrice;
            }

            MarketSnapshot snapshot = new MarketSnapshot();
            snapshot.Asset = "WDOFUT_F_0";
            snapshot.Rtd["ULT"] = (decimal)currentPrice;
            snapshot.Rtd["AJA"] = (decimal)currentPrice;

            GarchConfig config = new GarchConfig
            {
                Enabled = true,
                DailyWindowDays = 252,
                DailyMinSamples = 126,
                IntradayTimeframeSeconds = 60,
                IntradayMinBars = 90,
                MaxIntradayBars = 1200,
                StationarityCap = 0.995,
                MaxIterations = 100,
                Tolerance = 1e-6,
                BandMultipliers = new double[] { 0.5, 1.0, 1.5, 2.0, 2.5 }
            };

            IntradayContext intraday = new IntradayContext { Open = (decimal)currentPrice, Price = (decimal)currentPrice };
            GarchSnapshot garch = GarchEngine.Build(bars, intraday, snapshot, new List<TickEvent>(), 0.5m, config);

            Assert(garch.DailyFit != null, "Garch daily fit result should be generated.");
            Assert(garch.DailyFit.Success, "Garch daily fit should succeed with sufficient data: " + garch.DailyFit.Status);
            Assert(garch.DailyFit.Persistence < 1.0, "Estimated Garch persistence should be stationary (< 1.0).");
            Assert(garch.DailyBands != null && garch.DailyBands.Count > 0, "Garch daily bands should be generated.");

            foreach (var band in garch.DailyBands)
            {
                decimal remainder = band.Price % 0.5m;
                Assert(remainder == 0m, "Band price " + band.Price + " should be rounded to the tick size (0.5). Remainder: " + remainder);
            }
        }

        private static void TryDelete(string folder)
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                }
            }
            catch
            {
            }
        }
    }
}
