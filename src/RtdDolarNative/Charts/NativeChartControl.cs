using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RtdDolarNative.Csv;
using RtdDolarNative.MarketData;
using RtdDolarNative.Quant;

namespace RtdDolarNative.Charts
{
    public sealed class NativeChartControl : FrameworkElement
    {
        private const int MinVisibleCandles = 20;
        private const int MaxVisibleCandles = 240;
        private const int FuturePanLimit = 40;
        private const int DefaultPriceGridTickInterval = 10;
        private static readonly int[] AllowedPriceGridTickIntervals = new[] { 5, 10, 50, 100 };
        private const int DefaultCandleSpacingPercent = 100;
        private static readonly int[] AllowedCandleSpacingPercents = new[] { 75, 100, 125, 150 };
        private static readonly CultureInfo PtBrCulture = CultureInfo.GetCultureInfo("pt-BR");

        private List<DailyBar> _bars = new List<DailyBar>();
        private MarketSnapshot _snapshot;
        private QuantResult _result;
        private readonly Typeface _mono = new Typeface("Consolas");
        private Rect _lastPlot = Rect.Empty;
        private Point _dragStart;
        private int _dragStartOffset;
        private int _viewOffsetFromEnd;
        private int _visibleCandles = 90;
        private double _priceScale = 1d;
        private decimal _pricePanOffset;
        private decimal _dragStartPriceOffset;
        private decimal _lastVisiblePriceRange = 1m;
        private bool _isDragging;
        private ChartTimeframe _timeframe = ChartTimeframe.Daily;
        private decimal _tickSize = 0.5m;
        private int _priceGridTickInterval = DefaultPriceGridTickInterval;
        private int _candleSpacingPercent = DefaultCandleSpacingPercent;
        private bool _showCandles = true;
        private bool _showPriceGrid = true;
        private bool _showCurrentPriceLine = true;
        private bool _showKeyLevels = true;
        private bool _showConfluenceLevels = true;
        private bool _showRtdLevels = true;
        private bool _showProfileLevels = true;
        private bool _showTechnicalLevels = true;
        private bool _showMarketLevels = true;
        private bool _showPercentLevels = true;
        private bool _showGarchLevels = true;
        private bool _showGarmanLevels = true;
        private bool _showGaussLevels = true;
        private bool _showStdDevLevels = true;

        public NativeChartControl()
        {
            Focusable = true;
            ClipToBounds = true;
            Cursor = Cursors.SizeAll;
        }

        protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
        {
            Point point = hitTestParameters.HitPoint;

            if (point.X < 0d || point.Y < 0d || point.X > ActualWidth || point.Y > ActualHeight)
            {
                return null;
            }

            return new PointHitTestResult(this, point);
        }

        public ChartTimeframe Timeframe
        {
            get { return _timeframe; }
            set
            {
                ChartTimeframe normalized = NormalizeTimeframe(value);

                if (_timeframe == normalized)
                {
                    return;
                }

                _timeframe = normalized;
                InvalidateVisual();
            }
        }

        public decimal TickSize
        {
            get { return _tickSize; }
            set
            {
                decimal normalized = value > 0m ? value : 0.5m;

                if (_tickSize == normalized)
                {
                    return;
                }

                _tickSize = normalized;
                InvalidateVisual();
            }
        }

        public int PriceGridTickInterval
        {
            get { return _priceGridTickInterval; }
            set
            {
                int normalized = NormalizePriceGridTickInterval(value);

                if (_priceGridTickInterval == normalized)
                {
                    return;
                }

                _priceGridTickInterval = normalized;
                InvalidateVisual();
            }
        }

        public int CandleSpacingPercent
        {
            get { return _candleSpacingPercent; }
            set
            {
                int normalized = NormalizeCandleSpacingPercent(value);

                if (_candleSpacingPercent == normalized)
                {
                    return;
                }

                _candleSpacingPercent = normalized;
                InvalidateVisual();
            }
        }

        public bool ShowCandles
        {
            get { return _showCandles; }
            set
            {
                if (_showCandles == value)
                {
                    return;
                }

                _showCandles = value;
                InvalidateVisual();
            }
        }

        public bool ShowPriceGrid
        {
            get { return _showPriceGrid; }
            set
            {
                if (_showPriceGrid == value)
                {
                    return;
                }

                _showPriceGrid = value;
                InvalidateVisual();
            }
        }

        public bool ShowCurrentPriceLine
        {
            get { return _showCurrentPriceLine; }
            set
            {
                if (_showCurrentPriceLine == value)
                {
                    return;
                }

                _showCurrentPriceLine = value;
                InvalidateVisual();
            }
        }

        public bool ShowKeyLevels
        {
            get { return _showKeyLevels; }
            set
            {
                if (_showKeyLevels == value)
                {
                    return;
                }

                _showKeyLevels = value;
                InvalidateVisual();
            }
        }

        public bool ShowConfluenceLevels
        {
            get { return _showConfluenceLevels; }
            set
            {
                if (_showConfluenceLevels == value)
                {
                    return;
                }

                _showConfluenceLevels = value;
                InvalidateVisual();
            }
        }

        public bool ShowRtdLevels
        {
            get { return _showRtdLevels; }
            set
            {
                if (_showRtdLevels == value)
                {
                    return;
                }

                _showRtdLevels = value;
                InvalidateVisual();
            }
        }

        public bool ShowProfileLevels
        {
            get { return _showProfileLevels; }
            set
            {
                if (_showProfileLevels == value)
                {
                    return;
                }

                _showProfileLevels = value;
                InvalidateVisual();
            }
        }

        public bool ShowTechnicalLevels
        {
            get { return _showTechnicalLevels; }
            set
            {
                if (_showTechnicalLevels == value)
                {
                    return;
                }

                _showTechnicalLevels = value;
                InvalidateVisual();
            }
        }

        public bool ShowMarketLevels
        {
            get { return _showMarketLevels; }
            set
            {
                if (_showMarketLevels == value)
                {
                    return;
                }

                _showMarketLevels = value;
                InvalidateVisual();
            }
        }

        public bool ShowPercentLevels
        {
            get { return _showPercentLevels; }
            set
            {
                if (_showPercentLevels == value)
                {
                    return;
                }

                _showPercentLevels = value;
                InvalidateVisual();
            }
        }

        public bool ShowGarchLevels
        {
            get { return _showGarchLevels; }
            set
            {
                if (_showGarchLevels == value)
                {
                    return;
                }

                _showGarchLevels = value;
                InvalidateVisual();
            }
        }

        public bool ShowGarmanLevels
        {
            get { return _showGarmanLevels; }
            set
            {
                if (_showGarmanLevels == value)
                {
                    return;
                }

                _showGarmanLevels = value;
                InvalidateVisual();
            }
        }

        public bool ShowGaussLevels
        {
            get { return _showGaussLevels; }
            set
            {
                if (_showGaussLevels == value)
                {
                    return;
                }

                _showGaussLevels = value;
                InvalidateVisual();
            }
        }

        public bool ShowStdDevLevels
        {
            get { return _showStdDevLevels; }
            set
            {
                if (_showStdDevLevels == value)
                {
                    return;
                }

                _showStdDevLevels = value;
                InvalidateVisual();
            }
        }

        public int ViewOffsetFromEndForDiagnostics
        {
            get { return _viewOffsetFromEnd; }
        }

        public int VisibleCandlesForDiagnostics
        {
            get { return _visibleCandles; }
        }

        public double PriceScaleForDiagnostics
        {
            get { return _priceScale; }
        }

        public decimal PricePanOffsetForDiagnostics
        {
            get { return _pricePanOffset; }
        }

        public void PanHorizontalCandles(int candles)
        {
            _viewOffsetFromEnd += candles;
            ClampViewportOffset();
            InvalidateVisual();
        }

        public void ZoomHorizontalSteps(int steps)
        {
            if (steps == 0)
            {
                return;
            }

            Point anchor = CenterPoint(InteractionPlot());
            int count = Math.Abs(steps);
            int direction = steps > 0 ? 1 : -1;

            for (int i = 0; i < count; i++)
            {
                ZoomCandles(direction, anchor);
            }

            InvalidateVisual();
        }

        public void PanVerticalFraction(double fraction)
        {
            if (double.IsNaN(fraction) || double.IsInfinity(fraction))
            {
                return;
            }

            decimal range = _lastVisiblePriceRange <= 0m ? 1m : _lastVisiblePriceRange;
            _pricePanOffset += Convert.ToDecimal(fraction) * range;
            InvalidateVisual();
        }

        public void ZoomVerticalSteps(int steps)
        {
            if (steps == 0)
            {
                return;
            }

            int count = Math.Abs(steps);
            double factor = steps > 0 ? 0.9d : 1.1d;

            for (int i = 0; i < count; i++)
            {
                _priceScale = Clamp(_priceScale * factor, 0.35d, 3.0d);
            }

            InvalidateVisual();
        }

        public void ResetViewport()
        {
            _viewOffsetFromEnd = 0;
            _visibleCandles = 90;
            _priceScale = 1d;
            _pricePanOffset = 0m;
            ClampViewportOffset();
            InvalidateVisual();
        }

        public void SetData(List<DailyBar> bars, MarketSnapshot snapshot, QuantResult result)
        {
            int previousCount = _bars.Count;
            _bars = bars == null
                ? new List<DailyBar>()
                : bars.ToList();
            _snapshot = snapshot;
            _result = result;

            if (_bars.Count < previousCount)
            {
                _viewOffsetFromEnd = 0;
            }

            ClampViewportOffset();
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            Rect bounds = new Rect(0, 0, ActualWidth, ActualHeight);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0, 0, 0)), null, bounds);

            if (ActualWidth < 80 || ActualHeight < 80)
            {
                return;
            }

            Rect plot = new Rect(54, 18, Math.Max(20, ActualWidth - 120), Math.Max(20, ActualHeight - 48));
            _lastPlot = plot;
            Pen gridPen = new Pen(new SolidColorBrush(Color.FromRgb(48, 56, 68)), 1);
            Pen axisPen = new Pen(new SolidColorBrush(Color.FromRgb(84, 94, 108)), 1);
            Brush textBrush = new SolidColorBrush(Color.FromRgb(169, 179, 191));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(11, 12, 10)), axisPen, plot);

            List<DailyBar> series = SeriesForChart();
            ChartViewport viewport = BuildViewport(series);

            if (viewport.VisibleBars.Count == 0)
            {
                DrawText(dc, "Carregue CSV para visualizar candles e niveis.", 16, 16, textBrush, 13);
                return;
            }

            decimal min = viewport.VisibleBars.Min(x => x.Low);
            decimal max = viewport.VisibleBars.Max(x => x.High);
            List<KeyLevel> levels = new List<KeyLevel>();

            if (_result != null)
            {
                if (_showKeyLevels)
                {
                    levels.AddRange(_result.KeyLevels
                        .Where(x => !IsPercentLevel(x))
                        .Where(IsLevelCategoryEnabled));
                }

                if (_showPercentLevels && _result.PercentTable != null)
                {
                    levels.AddRange(_result.PercentTable.Where(x => x != null && x.Price > 0m));
                }

                levels.AddRange(BuildReferenceMetricLevels(_result));

                if (_showConfluenceLevels)
                {
                    levels.AddRange(_result.Confluence.Take(12));
                }

                if (_showGarchLevels)
                {
                    levels.AddRange(BuildGarchLevels(_result.Garch));
                }
            }

            foreach (KeyLevel level in levels)
            {
                if (level.Price > 0m)
                {
                    min = Math.Min(min, level.Price);
                    max = Math.Max(max, level.Price);
                }
            }

            decimal pad = Math.Max(1m, (max - min) * 0.08m);
            min -= pad;
            max += pad;
            ApplyPriceScale(ref min, ref max);
            _lastVisiblePriceRange = Math.Max(1m, max - min);

            if (_showPriceGrid)
            {
                DrawPriceGrid(dc, plot, min, max, gridPen, textBrush);
            }
            double candleSlot = EffectiveCandleSlot(plot, viewport.SlotCount);
            double candleWidth = Math.Max(3d, Math.Min(12d, candleSlot * EffectiveCandleBodyFactor()));

            if (_showCandles)
            {
                dc.PushClip(new RectangleGeometry(plot));

                try
                {
                    for (int slot = 0; slot < viewport.SlotCount; slot++)
                    {
                        int index = viewport.StartIndex + slot;

                        if (index < 0 || index >= series.Count)
                        {
                            continue;
                        }

                        DailyBar bar = series[index];
                        double x = plot.Left + candleSlot * slot + candleSlot / 2d;
                        double yHigh = Y(bar.High, min, max, plot);
                        double yLow = Y(bar.Low, min, max, plot);
                        double yOpen = Y(bar.Open, min, max, plot);
                        double yClose = Y(bar.Close, min, max, plot);
                        bool up = bar.Close >= bar.Open;
                        Brush fill = new SolidColorBrush(up ? Color.FromRgb(18, 184, 134) : Color.FromRgb(250, 82, 82));
                        Pen wick = new Pen(fill, 1);
                        dc.DrawLine(wick, new Point(x, yHigh), new Point(x, yLow));
                        Rect body = new Rect(x - candleWidth / 2d, Math.Min(yOpen, yClose), candleWidth, Math.Max(1d, Math.Abs(yClose - yOpen)));
                        dc.DrawRectangle(fill, null, body);
                    }
                }
                finally
                {
                    dc.Pop();
                }
            }

            DrawText(dc, TimeframeText(_timeframe), plot.Left + 6, plot.Top + 6, textBrush, 11);

            foreach (KeyLevel level in SelectVisibleChartLevels(levels))
            {
                if (level.Price <= 0m)
                {
                    continue;
                }

                double y = Y(level.Price, min, max, plot);
                Pen pen = new Pen(LevelBrush(level), level.Source == "POC" || level.Type == "Atual" ? 2 : 1);
                dc.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));
                DrawText(dc, level.Label, plot.Left + 6, y - 14, LevelBrush(level), 10);
            }

            if (_showCurrentPriceLine)
            {
                DrawCurrentPriceMarker(dc, plot, min, max);
            }
        }

        private bool IsLevelCategoryEnabled(KeyLevel level)
        {
            if (level == null)
            {
                return false;
            }

            List<string> tokens = LevelSourceTokens(level.Source);
            bool hasGarman = tokens.Any(IsGarmanSource);
            bool hasGauss = tokens.Any(IsGaussSource);
            bool hasStdDev = tokens.Any(IsStdDevSource);
            bool hasPercent = tokens.Any(IsPercentSource);
            bool hasTechnical = tokens.Any(IsTechnicalSource);
            bool hasProfile = tokens.Any(IsProfileSource);
            bool hasMarket = tokens.Any(IsMarketSource);
            bool hasRtd = tokens.Any(IsRtdSource);
            bool hasGarch = tokens.Any(IsGarchSource);

            if (hasGarman || hasGauss || hasStdDev || hasGarch)
            {
                return (hasGarman && _showGarmanLevels) ||
                       (hasGauss && _showGaussLevels) ||
                       (hasStdDev && _showStdDevLevels) ||
                       (hasGarch && _showGarchLevels);
            }

            bool visibleByCategory =
                (hasPercent && _showPercentLevels) ||
                (hasTechnical && _showTechnicalLevels) ||
                (hasProfile && _showProfileLevels) ||
                (hasMarket && _showMarketLevels) ||
                (hasRtd && _showRtdLevels) ||
                (hasGarch && _showGarchLevels);

            if (hasPercent || hasTechnical || hasProfile || hasMarket || hasRtd || hasGarch)
            {
                return visibleByCategory;
            }

            if (tokens.Count == 0)
            {
                return _showMarketLevels || _showRtdLevels || _showGarchLevels || _showGarmanLevels || _showGaussLevels || _showStdDevLevels;
            }

            return _showPercentLevels || _showTechnicalLevels || _showProfileLevels || _showMarketLevels || _showRtdLevels || _showGarchLevels || _showGarmanLevels || _showGaussLevels || _showStdDevLevels;
        }

        private static bool IsPercentLevel(KeyLevel level)
        {
            if (level == null)
            {
                return false;
            }

            return LevelSourceTokens(level.Source).Any(IsPercentSource);
        }

        private static List<KeyLevel> SelectVisibleChartLevels(IEnumerable<KeyLevel> levels)
        {
            List<KeyLevel> valid = (levels ?? Enumerable.Empty<KeyLevel>())
                .Where(x => x != null && x.Price > 0m)
                .GroupBy(x => LevelIdentity(x), StringComparer.OrdinalIgnoreCase)
                .Select(x => x.OrderByDescending(y => y.Score).First())
                .ToList();

            List<KeyLevel> nonPercent = valid
                .Where(x => !IsPercentLevel(x))
                .OrderByDescending(x => x.Score)
                .Take(40)
                .ToList();

            List<KeyLevel> percent = valid
                .Where(IsPercentLevel)
                .OrderByDescending(x => x.Price)
                .ToList();

            nonPercent.AddRange(percent);
            return nonPercent;
        }

        private static readonly char[] LevelSourceSeparators = new[] { ',', ';', '+', '/', '|' };

        private static List<string> LevelSourceTokens(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return new List<string>();
            }

            List<string> tokens = source
                .Split(LevelSourceSeparators, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Where(x => !string.Equals(x, " ", StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (tokens.Count == 0)
            {
                return tokens;
            }

            tokens.AddRange(source.Split('-')
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Replace("(", string.Empty))
                .Select(x => x.Replace(")", string.Empty))
                .Where(x => !string.IsNullOrWhiteSpace(x)));

            return tokens.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool IsPercentSource(string source)
        {
            return string.Equals(source, "Percent", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsGarmanSource(string source)
        {
            return !string.IsNullOrWhiteSpace(source) &&
                   (source.IndexOf("Garman", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    source.IndexOf("GK", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsGaussSource(string source)
        {
            return !string.IsNullOrWhiteSpace(source) &&
                   source.IndexOf("Gauss", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsStdDevSource(string source)
        {
            return !string.IsNullOrWhiteSpace(source) &&
                   (source.IndexOf("Desvio", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    source.IndexOf("StdDev", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    source.IndexOf("StandardDeviation", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsTechnicalSource(string source)
        {
            return string.Equals(source, "Tecnico", StringComparison.OrdinalIgnoreCase) ||
                source.IndexOf("Tecnico", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsProfileSource(string source)
        {
            return string.Equals(source, "VAH", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "VAL", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "POC", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "HVN", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "LVN", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "AVWAP", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "Vah", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "Val", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "profile", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsMarketSource(string source)
        {
            return string.Equals(source, "D1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "csv", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "Round", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "SR", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "grade", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "market", StringComparison.OrdinalIgnoreCase) ||
                source.IndexOf("d-1", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsRtdSource(string source)
        {
            return string.Equals(source, "RTD", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "Open", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "Abertura", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "VWAP", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "Sigma", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "Atual", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "MED", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "Maxima", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "Minima", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsGarchSource(string source)
        {
            return !string.IsNullOrWhiteSpace(source) &&
                   source.IndexOf("GARCH", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private List<KeyLevel> BuildReferenceMetricLevels(QuantResult result)
        {
            List<KeyLevel> levels = new List<KeyLevel>();

            if (result == null || result.ReferenceMaps == null)
            {
                return levels;
            }

            foreach (ReferenceMapResult map in result.ReferenceMaps)
            {
                if (map == null || map.ReferencePrice <= 0m)
                {
                    continue;
                }

                if (_showGarmanLevels)
                {
                    AddReferenceMetricLevels(levels, map, map.GarmanLevels, "GK", "Garman-Klass", 74d);
                }

                if (_showGaussLevels)
                {
                    AddReferenceMetricLevels(levels, map, map.GaussLevels, "Gauss", "Gauss", 72d);
                }

                if (_showStdDevLevels)
                {
                    AddReferenceMetricLevels(levels, map, map.StdDevLevels, "Desvio", "Desvio padrao", 70d);
                }

                if (_showGarchLevels)
                {
                    AddReferenceMetricLevels(levels, map, map.GarchLevels, "GARCH", "GARCH", 76d);
                }
            }

            return levels
                .Where(x => x != null && x.Price > 0m)
                .GroupBy(x => LevelIdentity(x), StringComparer.OrdinalIgnoreCase)
                .Select(x => x.OrderByDescending(y => y.Score).First())
                .ToList();
        }

        private static void AddReferenceMetricLevels(List<KeyLevel> target, ReferenceMapResult map, IEnumerable<DeviationLevel> source, string shortMetric, string sourceName, double baseScore)
        {
            if (target == null || map == null || source == null)
            {
                return;
            }

            foreach (DeviationLevel level in source)
            {
                KeyLevel keyLevel = ToReferenceMetricKeyLevel(map, level, shortMetric, sourceName, baseScore);
                if (keyLevel != null)
                {
                    target.Add(keyLevel);
                }
            }
        }

        private static KeyLevel ToReferenceMetricKeyLevel(ReferenceMapResult map, DeviationLevel level, string shortMetric, string sourceName, double baseScore)
        {
            if (map == null || level == null || level.Price <= 0m)
            {
                return null;
            }

            decimal sigma = Math.Abs(level.Sigma);
            double score = baseScore - Math.Min(16d, Convert.ToDouble(sigma) * 3d);
            string side = string.Equals(level.Side, "Venda", StringComparison.OrdinalIgnoreCase) ? "Resistencia" : "Suporte";
            string label = EmptyChartText(map.ReferenceLabel) + " " + shortMetric + " " + EmptyChartText(level.Side) + " " + SigmaLabel(level.Sigma);

            return new KeyLevel
            {
                Price = level.Price,
                Label = label,
                Type = side,
                Source = sourceName,
                Score = score,
                Distance = level.DistanceCurrent,
                Evidence = EmptyChartText(map.ReferenceSource) + " | " + EmptyChartText(level.Label),
                Layer = "Niveis-" + sourceName,
                Tags = EmptyChartText(map.ReferenceKey)
            };
        }

        private static string LevelIdentity(KeyLevel level)
        {
            if (level == null)
            {
                return string.Empty;
            }

            return level.Source + "|" + level.Price.ToString("0.##", CultureInfo.InvariantCulture) + "|" + level.Type;
        }

        private static string EmptyChartText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
        }

        private static string SigmaLabel(decimal sigma)
        {
            if (sigma == 0m)
            {
                return "ref";
            }

            return sigma.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture);
        }

        private static List<KeyLevel> BuildGarchLevels(GarchSnapshot garch)
        {
            List<KeyLevel> levels = new List<KeyLevel>();

            if (garch == null)
            {
                return levels;
            }

            foreach (GarchBandLevel band in garch.DailyBands ?? Enumerable.Empty<GarchBandLevel>())
            {
                levels.Add(ToKeyLevel(band, "GARCH-Diario"));
            }

            foreach (GarchBandLevel band in garch.IntradayBands ?? Enumerable.Empty<GarchBandLevel>())
            {
                levels.Add(ToKeyLevel(band, "GARCH-Intraday"));
            }

            return levels
                .Where(x => x != null && x.Price > 0m)
                .OrderBy(x => x.Score)
                .ToList();
        }

        private static KeyLevel ToKeyLevel(GarchBandLevel band, string sourcePrefix)
        {
            if (band == null)
            {
                return null;
            }

            return new KeyLevel
            {
                Price = band.Price,
                Label = band.Label,
                Type = band.Side,
                Source = string.IsNullOrWhiteSpace(band.Source) ? sourcePrefix : band.Source,
                Score = band.ScoreHint,
                Distance = band.DistanceCurrent,
                Evidence = band.Read,
                Layer = sourcePrefix
            };
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus();
            _isDragging = true;
            _dragStart = e.GetPosition(this);
            _dragStartOffset = _viewOffsetFromEnd;
            _dragStartPriceOffset = _pricePanOffset;
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (!_isDragging || !IsMouseCaptured)
            {
                return;
            }

            Point current = e.GetPosition(this);
            Rect plot = InteractionPlot();
            double slot = EffectiveCandleSlot(plot, _visibleCandles);
            int deltaCandles = (int)Math.Round((current.X - _dragStart.X) / Math.Max(1d, slot));
            _viewOffsetFromEnd = _dragStartOffset + deltaCandles;

            if (plot.Height > 0d)
            {
                decimal deltaPrice = Convert.ToDecimal((current.Y - _dragStart.Y) / plot.Height) * _lastVisiblePriceRange;
                _pricePanOffset = _dragStartPriceOffset + deltaPrice;
            }

            ClampViewportOffset();
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            _isDragging = false;
            ReleaseMouseCapture();
            e.Handled = true;
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            Focus();

            Point point = e.GetPosition(this);
            int direction = e.Delta > 0 ? 1 : -1;

            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                ZoomCandles(direction, point);
            }
            else
            {
                ZoomVerticalSteps(direction);
            }

            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonDown(e);
            ResetViewport();
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            bool handled = true;
            int horizontalStep = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift ? 20 : 5;
            double verticalStep = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift ? 0.22d : 0.10d;

            switch (e.Key)
            {
                case Key.Left:
                    PanHorizontalCandles(horizontalStep);
                    break;
                case Key.Right:
                    PanHorizontalCandles(-horizontalStep);
                    break;
                case Key.Up:
                    if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                    {
                        ZoomVerticalSteps(1);
                    }
                    else
                    {
                        PanVerticalFraction(-verticalStep);
                    }

                    break;
                case Key.Down:
                    if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                    {
                        ZoomVerticalSteps(-1);
                    }
                    else
                    {
                        PanVerticalFraction(verticalStep);
                    }

                    break;
                case Key.Add:
                case Key.OemPlus:
                    ZoomHorizontalSteps(1);
                    break;
                case Key.Subtract:
                case Key.OemMinus:
                    ZoomHorizontalSteps(-1);
                    break;
                case Key.Home:
                    ResetViewport();
                    break;
                default:
                    handled = false;
                    break;
            }

            e.Handled = handled;
        }

        private List<DailyBar> SeriesForChart()
        {
            List<DailyBar> series = _bars == null
                ? new List<DailyBar>()
                : _bars.OrderBy(x => x.Date).ToList();

            if (_snapshot != null && _snapshot.Ultimo.HasValue)
            {
                DailyBar current = new DailyBar
                {
                    Date = DateTime.Today,
                    Open = _snapshot.Abertura ?? _snapshot.Ultimo.Value,
                    High = _snapshot.Maxima ?? _snapshot.Ultimo.Value,
                    Low = _snapshot.Minima ?? _snapshot.Ultimo.Value,
                    Close = _snapshot.Ultimo.Value,
                    Volume = _snapshot.Volume
                };

                if (series.Count > 0 && series[series.Count - 1].Date.Date == current.Date.Date)
                {
                    series[series.Count - 1] = current;
                }
                else
                {
                    series.Add(current);
                }
            }

            switch (NormalizeTimeframe(_timeframe))
            {
                case ChartTimeframe.Weekly:
                    return AggregateSeries(series, true);
                case ChartTimeframe.Monthly:
                    return AggregateSeries(series, false);
                default:
                    return series;
            }
        }

        private ChartViewport BuildViewport(List<DailyBar> series)
        {
            int slotCount = Clamp(_visibleCandles, MinVisibleCandles, MaxVisibleCandles);

            if (series == null || series.Count == 0)
            {
                _viewOffsetFromEnd = 0;
                return new ChartViewport(0, slotCount, new List<DailyBar>());
            }

            int maxHistoricalOffset = Math.Max(0, series.Count - slotCount);
            _viewOffsetFromEnd = Clamp(_viewOffsetFromEnd, -FuturePanLimit, maxHistoricalOffset);
            int startIndex = series.Count - slotCount - _viewOffsetFromEnd;
            int endIndex = startIndex + slotCount - 1;
            int firstActual = Math.Max(0, startIndex);
            int lastActual = Math.Min(series.Count - 1, endIndex);
            List<DailyBar> visible = new List<DailyBar>();

            for (int index = firstActual; index <= lastActual; index++)
            {
                visible.Add(series[index]);
            }

            return new ChartViewport(startIndex, slotCount, visible);
        }

        private List<DailyBar> AggregateSeries(List<DailyBar> source, bool weekly)
        {
            if (source == null || source.Count == 0)
            {
                return new List<DailyBar>();
            }

            List<DailyBar> ordered = source.OrderBy(x => x.Date).ToList();
            List<DailyBar> aggregated = new List<DailyBar>();
            DailyBar current = null;
            DateTime currentKey = DateTime.MinValue;

            foreach (DailyBar bar in ordered)
            {
                DateTime key = weekly ? StartOfWeek(bar.Date) : new DateTime(bar.Date.Year, bar.Date.Month, 1);

                if (current == null || key != currentKey)
                {
                    if (current != null)
                    {
                        aggregated.Add(current);
                    }

                    current = CloneForAggregate(bar, key);
                    currentKey = key;
                    continue;
                }

                current.High = Math.Max(current.High, bar.High);
                current.Low = Math.Min(current.Low, bar.Low);
                current.Close = bar.Close;
                current.Volume = SumNullable(current.Volume, bar.Volume);
                current.Quantity = SumNullable(current.Quantity, bar.Quantity);
                current.Date = bar.Date;
            }

            if (current != null)
            {
                aggregated.Add(current);
            }

            return aggregated;
        }

        private DailyBar CloneForAggregate(DailyBar bar, DateTime periodStart)
        {
            return new DailyBar
            {
                Asset = bar.Asset,
                Date = periodStart,
                Open = bar.Open,
                High = bar.High,
                Low = bar.Low,
                Close = bar.Close,
                Volume = bar.Volume,
                Quantity = bar.Quantity
            };
        }

        private static decimal? SumNullable(decimal? left, decimal? right)
        {
            if (!left.HasValue && !right.HasValue)
            {
                return null;
            }

            return (left ?? 0m) + (right ?? 0m);
        }

        private void ApplyPriceScale(ref decimal min, ref decimal max)
        {
            decimal range = max - min;

            if (range <= 0m)
            {
                range = 1m;
            }

            decimal center = (min + max) / 2m + _pricePanOffset;
            decimal half = range / 2m * Convert.ToDecimal(Clamp(_priceScale, 0.35d, 3.0d));
            min = center - half;
            max = center + half;
        }

        private void DrawPriceGrid(DrawingContext dc, Rect plot, decimal min, decimal max, Pen gridPen, Brush textBrush)
        {
            decimal step = EffectivePriceGridStep();

            if (step <= 0m)
            {
                return;
            }

            decimal top = RoundUpToStep(max, step);
            decimal bottom = RoundDownToStep(min, step);
            decimal range = max - min;
            double stepPixels = range <= 0m
                ? plot.Height
                : plot.Height * Convert.ToDouble(step / range);
            int labelStride = stepPixels >= 18d
                ? 1
                : Math.Max(1, (int)Math.Ceiling(18d / Math.Max(1d, stepPixels)));
            int index = 0;

            for (decimal price = top; price >= bottom; price -= step)
            {
                double y = Y(price, min, max, plot);
                dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));

                if (index % labelStride == 0 || price == top || price == bottom)
                {
                    DrawText(dc, FormatPrice(price), plot.Right + 6, y - 8, textBrush, 11);
                }

                index++;

                if (index > 250)
                {
                    break;
                }
            }
        }

        private void DrawCurrentPriceMarker(DrawingContext dc, Rect plot, decimal min, decimal max)
        {
            if (_snapshot == null || !_snapshot.Ultimo.HasValue)
            {
                return;
            }

            decimal currentPrice = _snapshot.Ultimo.Value;
            double y = Y(currentPrice, min, max, plot);

            if (double.IsNaN(y) || double.IsInfinity(y))
            {
                return;
            }

            y = Clamp(y, plot.Top + 2d, plot.Bottom - 2d);

            Color accent = CurrentPriceColor(_snapshot);
            Brush lineBrush = new SolidColorBrush(Color.FromArgb(210, accent.R, accent.G, accent.B));
            Pen linePen = new Pen(lineBrush, 2d);
            dc.DrawLine(linePen, new Point(plot.Left, y), new Point(plot.Right, y));

            string label = FormatPrice(currentPrice);
            FormattedText formatted = new FormattedText(
                label,
                PtBrCulture,
                FlowDirection.LeftToRight,
                _mono,
                10,
                Brushes.White);

            double paddingX = 6d;
            double paddingY = 2d;
            double labelWidth = Math.Ceiling(formatted.WidthIncludingTrailingWhitespace + (paddingX * 2d));
            double labelHeight = Math.Ceiling(Math.Max(18d, formatted.Height + (paddingY * 2d)));
            double labelLeft = Math.Max(plot.Right + 2d, ActualWidth - labelWidth - 6d);
            double labelTop = Clamp(y - (labelHeight / 2d), plot.Top + 2d, plot.Bottom - labelHeight - 2d);
            Rect labelRect = new Rect(labelLeft, labelTop, labelWidth, labelHeight);

            Brush labelFill = new SolidColorBrush(Color.FromArgb(238, accent.R, accent.G, accent.B));
            Brush labelBorder = new SolidColorBrush(Color.FromArgb(255, (byte)Math.Max(0, accent.R - 35), (byte)Math.Max(0, accent.G - 35), (byte)Math.Max(0, accent.B - 35)));
            dc.DrawRoundedRectangle(labelFill, new Pen(labelBorder, 1d), labelRect, 3d, 3d);
            DrawText(dc, label, labelRect.Left + paddingX, labelRect.Top + paddingY, Brushes.White, 10);
        }

        private void ZoomCandles(int direction, Point point)
        {
            List<DailyBar> series = SeriesForChart();
            int seriesCount = series.Count;
            int currentVisible = Clamp(_visibleCandles, MinVisibleCandles, MaxVisibleCandles);

            if (seriesCount == 0)
            {
                _visibleCandles = Clamp(_visibleCandles - direction * 10, MinVisibleCandles, MaxVisibleCandles);
                return;
            }

            int currentStart = CurrentStartIndex(seriesCount, currentVisible, _viewOffsetFromEnd);
            Rect plot = InteractionPlot();
            double slotWidth = EffectiveCandleSlot(plot, currentVisible);
            int anchorSlot = Clamp((int)Math.Round((point.X - plot.Left) / Math.Max(1d, slotWidth)), 0, Math.Max(0, currentVisible - 1));
            int anchorSeriesIndex = currentStart + anchorSlot;

            _visibleCandles = Clamp(_visibleCandles - direction * 10, MinVisibleCandles, MaxVisibleCandles);
            int newVisible = Clamp(_visibleCandles, MinVisibleCandles, MaxVisibleCandles);
            int newMaxHistoricalOffset = Math.Max(0, seriesCount - newVisible);
            int desiredStart = anchorSeriesIndex - anchorSlot;
            int desiredOffset = seriesCount - newVisible - desiredStart;
            _viewOffsetFromEnd = Clamp(desiredOffset, -FuturePanLimit, newMaxHistoricalOffset);
        }

        private int CurrentStartIndex(int seriesCount, int visibleCount, int offset)
        {
            int maxHistoricalOffset = Math.Max(0, seriesCount - visibleCount);
            int clampedOffset = Clamp(offset, -FuturePanLimit, maxHistoricalOffset);
            return seriesCount - visibleCount - clampedOffset;
        }

        private void ClampViewportOffset()
        {
            int seriesCount = SeriesForChart().Count;
            int visibleCount = Clamp(_visibleCandles, MinVisibleCandles, MaxVisibleCandles);
            int maxOffset = Math.Max(0, seriesCount - visibleCount);
            _viewOffsetFromEnd = Clamp(_viewOffsetFromEnd, -FuturePanLimit, maxOffset);
        }

        private static ChartTimeframe NormalizeTimeframe(ChartTimeframe timeframe)
        {
            switch (timeframe)
            {
                case ChartTimeframe.Weekly:
                case ChartTimeframe.Monthly:
                    return timeframe;
                default:
                    return ChartTimeframe.Daily;
            }
        }

        private static DateTime StartOfWeek(DateTime date)
        {
            int delta = (7 + ((int)date.DayOfWeek - (int)DayOfWeek.Monday)) % 7;
            return date.Date.AddDays(-delta);
        }

        private string TimeframeText(ChartTimeframe timeframe)
        {
            switch (NormalizeTimeframe(timeframe))
            {
                case ChartTimeframe.Weekly:
                    return "1W";
                case ChartTimeframe.Monthly:
                    return "1M";
                default:
                    return "1D";
            }
        }

        private sealed class ChartViewport
        {
            public ChartViewport(int startIndex, int slotCount, List<DailyBar> visibleBars)
            {
                StartIndex = startIndex;
                SlotCount = slotCount;
                VisibleBars = visibleBars ?? new List<DailyBar>();
            }

            public int StartIndex { get; private set; }
            public int SlotCount { get; private set; }
            public List<DailyBar> VisibleBars { get; private set; }
        }

        private decimal EffectivePriceGridStep()
        {
            decimal tickSize = _tickSize > 0m ? _tickSize : 0.5m;
            int tickInterval = NormalizePriceGridTickInterval(_priceGridTickInterval);
            decimal zoomInfluence = Convert.ToDecimal(Math.Sqrt(Clamp(_priceScale, 0.35d, 3.0d)));
            decimal adjustedTicks = Math.Max(1m, Convert.ToDecimal(tickInterval) * zoomInfluence);
            decimal roundedTicks = Math.Max(1m, Math.Round(adjustedTicks, 0, MidpointRounding.AwayFromZero));
            return tickSize * roundedTicks;
        }

        private double EffectiveCandleSpacingFactor()
        {
            int normalized = NormalizeCandleSpacingPercent(_candleSpacingPercent);
            return Math.Max(0.75d, normalized / 100d);
        }

        private double EffectiveCandleBodyFactor()
        {
            double spacing = EffectiveCandleSpacingFactor();
            return Clamp(0.85d - (spacing - 0.75d) * 0.44d, 0.42d, 0.85d);
        }

        private double EffectiveCandleSlot(Rect plot, int slotCount)
        {
            double baseSlot = plot.Width / Math.Max(1, slotCount);
            return Math.Max(1d, baseSlot);
        }

        private Rect InteractionPlot()
        {
            if (!_lastPlot.IsEmpty && _lastPlot.Width > 0d && _lastPlot.Height > 0d)
            {
                return _lastPlot;
            }

            double width = ActualWidth > 0d ? ActualWidth : 900d;
            double height = ActualHeight > 0d ? ActualHeight : 420d;
            return new Rect(54, 18, Math.Max(20d, width - 120d), Math.Max(20d, height - 48d));
        }

        private static Point CenterPoint(Rect rect)
        {
            return new Point(rect.Left + rect.Width / 2d, rect.Top + rect.Height / 2d);
        }

        private static decimal RoundUpToStep(decimal value, decimal step)
        {
            if (step <= 0m)
            {
                return value;
            }

            decimal quotient = value / step;
            decimal rounded = Convert.ToDecimal(Math.Ceiling(Convert.ToDouble(quotient)));
            return rounded * step;
        }

        private static decimal RoundDownToStep(decimal value, decimal step)
        {
            if (step <= 0m)
            {
                return value;
            }

            decimal quotient = value / step;
            decimal rounded = Convert.ToDecimal(Math.Floor(Convert.ToDouble(quotient)));
            return rounded * step;
        }

        private static int NormalizePriceGridTickInterval(int tickInterval)
        {
            if (AllowedPriceGridTickIntervals.Contains(tickInterval))
            {
                return tickInterval;
            }

            return DefaultPriceGridTickInterval;
        }

        private static int NormalizeCandleSpacingPercent(int spacingPercent)
        {
            if (AllowedCandleSpacingPercents.Contains(spacingPercent))
            {
                return spacingPercent;
            }

            return DefaultCandleSpacingPercent;
        }

        private static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private static double Clamp(double value, double min, double max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private static double Y(decimal price, decimal min, decimal max, Rect plot)
        {
            if (max == min)
            {
                return plot.Top + plot.Height / 2d;
            }

            return plot.Bottom - (Convert.ToDouble(price - min) / Convert.ToDouble(max - min)) * plot.Height;
        }

        private Brush LevelBrush(KeyLevel level)
        {
            if (!string.IsNullOrWhiteSpace(level.Source) &&
                IsGarmanSource(level.Source))
            {
                return new SolidColorBrush(Color.FromRgb(255, 209, 102));
            }

            if (!string.IsNullOrWhiteSpace(level.Source) &&
                IsGaussSource(level.Source))
            {
                return new SolidColorBrush(Color.FromRgb(108, 182, 255));
            }

            if (!string.IsNullOrWhiteSpace(level.Source) &&
                IsStdDevSource(level.Source))
            {
                return new SolidColorBrush(Color.FromRgb(248, 248, 242));
            }

            if (!string.IsNullOrWhiteSpace(level.Source) &&
                level.Source.IndexOf("GARCH", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return string.Equals(level.Type, "Venda", StringComparison.OrdinalIgnoreCase)
                    ? new SolidColorBrush(Color.FromRgb(255, 82, 82))
                    : new SolidColorBrush(Color.FromRgb(18, 184, 134));
            }

            if (level.Type == "Suporte")
            {
                return new SolidColorBrush(Color.FromRgb(59, 201, 219));
            }

            if (level.Type == "Resistencia")
            {
                return new SolidColorBrush(Color.FromRgb(250, 82, 82));
            }

            if (level.Source == "POC")
            {
                return new SolidColorBrush(Color.FromRgb(250, 176, 5));
            }

            if (level.Source == "AVWAP")
            {
                return new SolidColorBrush(Color.FromRgb(151, 117, 250));
            }

            return new SolidColorBrush(Color.FromRgb(18, 184, 134));
        }

        private void DrawText(DrawingContext dc, string text, double x, double y, Brush brush, double size)
        {
            FormattedText ft = new FormattedText(
                text ?? string.Empty,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                _mono,
                size,
                brush);
            dc.DrawText(ft, new Point(x, y));
        }

        private string FormatPrice(decimal price)
        {
            return price.ToString("N2", PtBrCulture);
        }

        private static Color CurrentPriceColor(MarketSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.Ultimo.HasValue)
            {
                return Color.FromRgb(34, 139, 230);
            }

            decimal current = snapshot.Ultimo.Value;
            decimal reference = snapshot.FechamentoAnterior ?? snapshot.Abertura ?? current;

            if (current > reference)
            {
                return Color.FromRgb(18, 184, 134);
            }

            if (current < reference)
            {
                return Color.FromRgb(250, 82, 82);
            }

            return Color.FromRgb(34, 139, 230);
        }
    }
}
