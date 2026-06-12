using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RtdDolarNative.Charts;
using RtdDolarNative.Csv;
using RtdDolarNative.Dom;
using RtdDolarNative.Flow;
using RtdDolarNative.Heatmap;
using RtdDolarNative.Logging;
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
                VolumeProfileUsesRequestedCsvHistoryWindow,
                PtaxHistoryStoreUpsertsAndLoads,
                MarketBiasFavoursBuyWhenTrendAndMomentumAlign,
                MarketBiasStaysNeutralWhenCsvIsMissing,
                ChartReferenceLineModeFiltersOpeningAndClosingMaps,
                ChartMetricLinesUseOnlySelectedReferenceMode,
                ChartMetricBuySellLinesUseDirectionalColors,
                ChartClosingLinesUseCsvD1CloseEvenWhenRtdFecExists,
                ChartLineLabelsIncludeFormattedPrice,
                ChartCommandsAdjustViewportPredictably,
                ChartResetPreservesDisplaySettings,
                DomAnnotationFilterKeepsAllCategoriesVisibleByDefault,
                DomAnnotationFilterHidesUncheckedCategoriesFromDomMarkings,
                DomAnnotationFilterKeepsProfilePercentLabelsWhenPercentIsUnchecked,
                HeatmapKeepsDistantLiquidityWallsInInterestList,
                HeatmapScoresAbsorptionAndStackingAtBid,
                HeatmapGroupsAdjacentInterestIntoOperationalZones,
                HeatmapFlagsPulledLiquidityAsSpoofRisk,
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

        private static void VolumeProfileUsesRequestedCsvHistoryWindow()
        {
            MarketSnapshot snapshot = new MarketSnapshot();
            snapshot.Rtd["ULT"] = 6120m;
            snapshot.Rtd["ABE"] = 6110m;
            snapshot.Rtd["FEC"] = 6100m;

            QuantResult sevenDays = QuantEngine.Build(BuildVolumeProfileWindowBars(), snapshot, 0.5m, 45, 7, null, null, null);
            QuantResult fortyTwoDays = QuantEngine.Build(BuildVolumeProfileWindowBars(), snapshot, 0.5m, 45, 42, null, null, null);

            Assert(sevenDays.Profile != null && sevenDays.Profile.Poc != null, "7-day CSV profile should produce a POC.");
            Assert(fortyTwoDays.Profile != null && fortyTwoDays.Profile.Poc != null, "42-day CSV profile should produce a POC.");
            AssertEqual(7, sevenDays.Profile.WindowDays, "CSV volume profile should remember the requested 7-day window.");
            AssertEqual(7, sevenDays.Profile.SampleSize, "7-day CSV profile should use only the last 7 daily bars.");
            AssertEqual(42, fortyTwoDays.Profile.WindowDays, "CSV volume profile should remember the requested 42-day window.");
            AssertEqual(42, fortyTwoDays.Profile.SampleSize, "42-day CSV profile should use the last 42 daily bars.");
            Assert(sevenDays.Profile.Poc.Price > 6000m, "7-day POC should be anchored in the high-volume area from the latest 7 pregões.");
            Assert(fortyTwoDays.Profile.Poc.Price < 5300m, "42-day POC should be anchored in the older high-volume CSV area.");
            Assert(sevenDays.Profile.Poc.Price != fortyTwoDays.Profile.Poc.Price, "Changing the CSV volume window should move the POC price, not just the label.");
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

        private static void MarketBiasFavoursBuyWhenTrendAndMomentumAlign()
        {
            MarketSnapshot snapshot = new MarketSnapshot();
            snapshot.Rtd["ULT"] = 5408m;
            snapshot.Rtd["ABE"] = 5372m;
            snapshot.Rtd["MAX"] = 5412m;
            snapshot.Rtd["MIN"] = 5368m;
            snapshot.Rtd["FEC"] = 5386m;
            snapshot.Rtd["MED"] = 5392m;
            snapshot.Rtd["VOL"] = 150000m;

            QuantResult result = QuantEngine.Build(BuildTrendingBars(true), snapshot, 0.5m, 45);

            Assert(result.MarketBias != null, "Market bias should be calculated.");
            Assert(result.MarketBias.Score > 0.15d, "Aligned trend and momentum should produce a positive market-bias score.");
            AssertEqual("Compra", result.MarketBias.Direction, "Positive market-bias score should be labelled as Compra.");
            Assert(result.MarketBias.ConfidencePct > 25d, "Market bias should expose a usable confidence percentage.");
            Assert(result.MarketBias.CoveragePct > 40d, "Market bias should report factor coverage.");
            Assert(result.MarketBias.TopFactors.Any(x => x.Name.IndexOf("EMA", StringComparison.OrdinalIgnoreCase) >= 0), "EMA alignment should be one of the top explanatory factors.");
        }

        private static void MarketBiasStaysNeutralWhenCsvIsMissing()
        {
            MarketSnapshot snapshot = new MarketSnapshot();
            snapshot.Rtd["ULT"] = 5200m;

            QuantResult result = QuantEngine.Build(new List<DailyBar>(), snapshot, 0.5m, 45);

            Assert(result.MarketBias != null, "Market bias should exist even when data is missing.");
            AssertEqual("Neutro", result.MarketBias.Direction, "Missing CSV should not fabricate a directional market bias.");
            AssertEqual(0d, result.MarketBias.CoveragePct, "Missing CSV should produce zero factor coverage.");
            AssertEqual(0, result.MarketBias.Factors.Count, "Missing CSV should produce no active market-bias factors.");
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

        private static void ChartLineLabelsIncludeFormattedPrice()
        {
            QuantResult result = BuildResult();
            NativeChartControl chart = new NativeChartControl();
            chart.ChartReferenceLineMode = ChartReferenceLineMode.Opening;

            KeyLevel line = chart.ReferenceMetricLevelsForDiagnostics(result)
                .FirstOrDefault(x => string.Equals(x.Source, "Gauss", StringComparison.OrdinalIgnoreCase) &&
                                     string.Equals(x.Type, "Venda", StringComparison.OrdinalIgnoreCase));

            Assert(line != null, "Reference metrics should include a sample line.");
            string label = chart.ChartLevelLabelForDiagnostics(line);
            string expectedPrice = line.Price.ToString("N2", CultureInfo.GetCultureInfo("pt-BR"));

            Assert(label.IndexOf(line.Label, StringComparison.OrdinalIgnoreCase) >= 0, "Chart line label should keep the level name.");
            Assert(label.IndexOf(expectedPrice, StringComparison.OrdinalIgnoreCase) >= 0, "Chart line label should include the formatted price.");
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

        private static List<DailyBar> BuildVolumeProfileWindowBars()
        {
            List<DailyBar> bars = new List<DailyBar>();
            DateTime start = new DateTime(2026, 4, 1);

            for (int i = 0; i < 35; i++)
            {
                bars.Add(new DailyBar
                {
                    Asset = "WDOFUT_F_0",
                    Date = start.AddDays(i),
                    Open = 5010m,
                    High = 5030m,
                    Low = 4990m,
                    Close = 5015m,
                    Volume = 10000m
                });
            }

            for (int i = 35; i < 42; i++)
            {
                bars.Add(new DailyBar
                {
                    Asset = "WDOFUT_F_0",
                    Date = start.AddDays(i),
                    Open = 6100m,
                    High = 6125m,
                    Low = 6080m,
                    Close = 6110m,
                    Volume = 1000m
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

        private static List<DailyBar> BuildTrendingBars(bool bullish)
        {
            List<DailyBar> bars = new List<DailyBar>();
            DateTime start = new DateTime(2025, 10, 1);
            decimal price = bullish ? 5000m : 5400m;
            decimal step = bullish ? 3.6m : -3.6m;

            for (int i = 0; i < 95; i++)
            {
                decimal open = price;
                decimal close = open + step + (i % 3) * (bullish ? 0.5m : -0.5m);
                decimal high = Math.Max(open, close) + 8m + (i % 4);
                decimal low = Math.Min(open, close) - 7m - (i % 3);

                bars.Add(new DailyBar
                {
                    Asset = "WDOFUT_F_0",
                    Date = start.AddDays(i),
                    Open = open,
                    High = high,
                    Low = low,
                    Close = close,
                    Volume = 1500m + i * 4m
                });

                price = close;
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

        private static void DomAnnotationFilterKeepsAllCategoriesVisibleByDefault()
        {
            List<KeyLevel> levels = BuildDomAnnotationSampleLevels();

            List<KeyLevel> visible = DomAnnotationFilter.Apply(levels, new DomAnnotationOptions()).ToList();

            AssertEqual(levels.Count, visible.Count, "All DOM annotation categories should be visible by default.");
        }

        private static void DomAnnotationFilterHidesUncheckedCategoriesFromDomMarkings()
        {
            List<KeyLevel> levels = BuildDomAnnotationSampleLevels();
            DomAnnotationOptions options = new DomAnnotationOptions();
            options.ShowGauss = false;
            options.ShowProfile = false;
            options.ShowFlow = false;
            options.ShowPercent = false;
            options.ShowMaxMin7 = false;

            List<string> labels = DomAnnotationFilter.Apply(levels, options)
                .Select(x => x.Label)
                .ToList();

            Assert(labels.Contains("Atual"), "RTD/base DOM annotation should stay visible.");
            Assert(labels.Contains("GARCH"), "Enabled GARCH DOM annotation should stay visible.");
            Assert(!labels.Contains("Gauss"), "Unchecked Gauss DOM annotation should be hidden.");
            Assert(!labels.Contains("POC"), "Unchecked profile DOM annotation should be hidden.");
            Assert(!labels.Contains("Absorcao"), "Unchecked flow DOM annotation should be hidden.");
            Assert(!labels.Contains("1% D-1"), "Unchecked percent DOM annotation should be hidden.");
            Assert(!labels.Contains("MaxMin7 Max"), "Unchecked MaxMin7 DOM annotation should be hidden.");
            Assert(!labels.Contains("RTD + Gauss"), "Mixed DOM annotation should be hidden when one detected category is unchecked.");
        }

        private static void DomAnnotationFilterKeepsProfilePercentLabelsWhenPercentIsUnchecked()
        {
            List<KeyLevel> levels = new List<KeyLevel>
            {
                DomLevel("VAH 70%", "VAH", "Volume Profile", "profile")
            };

            DomAnnotationOptions options = new DomAnnotationOptions();
            options.ShowPercent = false;

            List<KeyLevel> visible = DomAnnotationFilter.Apply(levels, options).ToList();

            AssertEqual(1, visible.Count, "Profile labels with percentages, such as VAH 70%, should not be hidden by the percent toggle.");
        }

        private static List<KeyLevel> BuildDomAnnotationSampleLevels()
        {
            return new List<KeyLevel>
            {
                DomLevel("Atual", "RTD", null, null),
                DomLevel("Gauss", "Gauss", null, null),
                DomLevel("GARCH", "GARCH", null, null),
                DomLevel("POC", "POC", "Volume Profile", "profile"),
                DomLevel("Absorcao", "Setups", "Order Flow", "absorcao"),
                DomLevel("1% D-1", "Percent", null, null),
                DomLevel("MaxMin7 Max", "MaxMin7", "MaxMin7", null),
                DomLevel("RTD + Gauss", "RTD, Gauss", null, null)
            };
        }

        private static KeyLevel DomLevel(string label, string source, string layer, string tags)
        {
            return new KeyLevel
            {
                Price = 5000m,
                Label = label,
                Source = source,
                Layer = layer,
                Tags = tags,
                Score = 50d
            };
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

        private static void HeatmapKeepsDistantLiquidityWallsInInterestList()
        {
            HeatmapProcessor processor = BuildHeatmapProcessor();

            try
            {
                MarketSnapshot snapshot = BuildHeatmapSnapshot(5000m);

                for (int i = 0; i < 50; i++)
                {
                    AddBookAsk(snapshot, i, 5000.5m + i * 0.5m, i == 49 ? 120000m : 120m);
                }

                processor.PostSnapshot(snapshot);

                HeatmapSnapshot heatmap = processor.GetSnapshot("WDOFUT_F_0", 5000m, 20);
                HeatmapCell farWall = heatmap.InterestCells.FirstOrDefault(x => x.Price == 5025m);

                Assert(heatmap.Cells.Count <= 20, "Chart heatmap rows should stay bounded and centered for readability.");
                Assert(farWall != null, "A distant high-liquidity CSV/book wall should still appear in the interest list.");
                AssertEqual("Venda", farWall.Direction, "Large ask wall above price should be classified as sell-side liquidity.");
                Assert(farWall.WallScore >= 95m, "Distant wall should carry a strong wall score.");
                Assert(farWall.InterestScore > 60m && farWall.InterestScore <= 100m, "Interest score should be normalized and high for a dominant wall.");
            }
            finally
            {
                processor.Dispose();
            }
        }

        private static void HeatmapScoresAbsorptionAndStackingAtBid()
        {
            HeatmapProcessor processor = BuildHeatmapProcessor();

            try
            {
                MarketSnapshot first = BuildHeatmapSnapshot(5000m);
                AddBookBid(first, 0, 4999m, 100m);
                AddBookAsk(first, 0, 5001m, 100m);
                processor.PostSnapshot(first);

                MarketSnapshot second = BuildHeatmapSnapshot(5000m);
                AddBookBid(second, 0, 4999m, 1000m);
                AddBookAsk(second, 0, 5001m, 100m);
                processor.PostSnapshot(second);

                processor.PostTrade(new TradePrint
                {
                    Asset = "WDOFUT_F_0",
                    LocalTimestamp = DateTimeOffset.Now,
                    Price = 4999m,
                    Quantity = 300m,
                    Delta = -300m,
                    Aggressor = "Sell"
                });

                HeatmapSnapshot heatmap = processor.GetSnapshot("WDOFUT_F_0", 5000m, 40);
                HeatmapCell bid = heatmap.InterestCells.FirstOrDefault(x => x.Price == 4999m);

                Assert(bid != null, "Bid absorption level should appear in the interest list.");
                AssertEqual("Compra", bid.Direction, "Sell aggression absorbed by a stacked bid should be classified as buy support.");
                Assert(bid.BidChange >= 900m, "Heatmap should track stacking change at the bid.");
                Assert(bid.StackingScore >= 70m, "Stacking score should be high after a large bid increase.");
                Assert(bid.AbsorptionScore >= 70m, "Absorption score should be high when sell aggression trades into a strong bid.");
                Assert(bid.Read.IndexOf("absorcao", StringComparison.OrdinalIgnoreCase) >= 0, "Read should name absorption explicitly.");
            }
            finally
            {
                processor.Dispose();
            }
        }

        private static void HeatmapGroupsAdjacentInterestIntoOperationalZones()
        {
            HeatmapProcessor processor = BuildHeatmapProcessor();

            try
            {
                MarketSnapshot snapshot = BuildHeatmapSnapshot(5000m);
                AddBookBid(snapshot, 0, 4998.5m, 1500m);
                AddBookBid(snapshot, 1, 4999.0m, 1800m);
                AddBookBid(snapshot, 2, 4999.5m, 1600m);
                AddBookAsk(snapshot, 0, 5002.0m, 900m);
                processor.PostSnapshot(snapshot);

                processor.PostTrade(new TradePrint
                {
                    Asset = "WDOFUT_F_0",
                    LocalTimestamp = DateTimeOffset.Now,
                    Price = 4999m,
                    Quantity = 350m,
                    Delta = -350m,
                    Aggressor = "Sell"
                });

                HeatmapSnapshot heatmap = processor.GetSnapshot("WDOFUT_F_0", 5000m, 40);
                HeatmapZone support = heatmap.Zones.FirstOrDefault(x => x.Direction == "Compra");

                Assert(support != null, "Adjacent buy-side heatmap interest should be grouped into a support zone.");
                AssertEqual(4998.5m, support.LowPrice, "Support zone low should keep the first adjacent bid level.");
                AssertEqual(4999.5m, support.HighPrice, "Support zone high should keep the last adjacent bid level.");
                AssertEqual(4999.0m, support.CenterPrice, "Support zone center should be rounded to the weighted central tick.");
                AssertEqual(3, support.CellCount, "Support zone should include the three adjacent bid cells.");
                Assert(support.Score >= 70m, "Support zone score should stay high when multiple adjacent cells concentrate liquidity.");
                Assert(support.DistanceTicks < 0, "Support zone below current price should carry a negative distance.");
                Assert(support.Read.IndexOf("zona", StringComparison.OrdinalIgnoreCase) >= 0, "Zone read should identify it as a zone, not an isolated line.");
            }
            finally
            {
                processor.Dispose();
            }
        }

        private static void HeatmapFlagsPulledLiquidityAsSpoofRisk()
        {
            HeatmapProcessor processor = BuildHeatmapProcessor();

            try
            {
                MarketSnapshot first = BuildHeatmapSnapshot(5000m);
                AddBookBid(first, 0, 4998.5m, 8000m);
                AddBookAsk(first, 0, 5001m, 500m);
                processor.PostSnapshot(first);

                MarketSnapshot second = BuildHeatmapSnapshot(5000m);
                AddBookAsk(second, 0, 5001m, 500m);
                processor.PostSnapshot(second);

                HeatmapSnapshot heatmap = processor.GetSnapshot("WDOFUT_F_0", 5000m, 40);
                HeatmapCell pulledBid = heatmap.InterestCells.FirstOrDefault(x => x.Price == 4998.5m);

                Assert(pulledBid != null, "Pulled liquidity should stay visible as an operational heatmap event.");
                AssertEqual("Venda", pulledBid.Direction, "Large pulled bid below price should be a sell-side warning.");
                Assert(pulledBid.PullingScore >= 95m, "Pulled bid should carry a high pulling score.");
                Assert(pulledBid.SpoofRiskScore >= 80m, "Fast removal without trade should carry spoof-risk score.");
                Assert(pulledBid.InterestScore >= 70m, "Pulled liquidity should rank high enough to remain in the interest list.");
                Assert(pulledBid.Read.IndexOf("retirada", StringComparison.OrdinalIgnoreCase) >= 0, "Read should state that liquidity was removed.");
            }
            finally
            {
                processor.Dispose();
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

        private static HeatmapProcessor BuildHeatmapProcessor()
        {
            string folder = Path.Combine(Path.GetTempPath(), "heatmap-tests", Guid.NewGuid().ToString("N"));
            string path = Path.Combine(folder, "heatmap.sqlite");
            return new HeatmapProcessor(0.5m, path, new Logger(null));
        }

        private static MarketSnapshot BuildHeatmapSnapshot(decimal currentPrice)
        {
            MarketSnapshot snapshot = new MarketSnapshot();
            snapshot.Asset = "WDOFUT_F_0";
            snapshot.LocalTimestamp = DateTimeOffset.Now;
            snapshot.Rtd["ULT"] = currentPrice;
            return snapshot;
        }

        private static void AddBookBid(MarketSnapshot snapshot, int index, decimal price, decimal quantity)
        {
            snapshot.Rtd["BOOK_OCP_" + index.ToString(CultureInfo.InvariantCulture)] = price;
            snapshot.Rtd["BOOK_VOC_" + index.ToString(CultureInfo.InvariantCulture)] = quantity;
        }

        private static void AddBookAsk(MarketSnapshot snapshot, int index, decimal price, decimal quantity)
        {
            snapshot.Rtd["BOOK_OVD_" + index.ToString(CultureInfo.InvariantCulture)] = price;
            snapshot.Rtd["BOOK_VOV_" + index.ToString(CultureInfo.InvariantCulture)] = quantity;
        }
    }
}
