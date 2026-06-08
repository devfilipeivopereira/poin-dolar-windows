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
        private const int MinPriceGridLines = 4;
        private const int MaxPriceGridLines = 14;
        private const int FuturePanLimit = 40;

        private List<DailyBar> _bars = new List<DailyBar>();
        private MarketSnapshot _snapshot;
        private QuantResult _result;
        private readonly Typeface _mono = new Typeface("Consolas");
        private Rect _lastPlot = Rect.Empty;
        private Point _dragStart;
        private int _dragStartOffset;
        private int _viewOffsetFromEnd;
        private int _visibleCandles = 90;
        private int _priceGridLines = 6;
        private double _priceScale = 1d;
        private bool _isDragging;
        private ChartTimeframe _timeframe = ChartTimeframe.Daily;

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
                levels.AddRange(_result.KeyLevels);
                levels.AddRange(_result.Confluence.Take(12));
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

            int priceLines = EffectivePriceGridLines();
            for (int i = 0; i < priceLines; i++)
            {
                double ratio = priceLines == 1 ? 0d : i / (double)(priceLines - 1);
                double y = plot.Top + plot.Height * ratio;
                dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                decimal price = max - (max - min) * i / Math.Max(1m, priceLines - 1m);
                DrawText(dc, price.ToString("N1", new CultureInfo("pt-BR")), plot.Right + 6, y - 8, textBrush, 11);
            }

            double candleSlot = plot.Width / Math.Max(1, viewport.SlotCount);
            double candleWidth = Math.Max(3d, Math.Min(12d, candleSlot * 0.58d));

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

            DrawText(dc, TimeframeText(_timeframe), plot.Left + 6, plot.Top + 6, textBrush, 11);

            foreach (KeyLevel level in levels.OrderByDescending(x => x.Score).Take(40))
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
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus();
            _isDragging = true;
            _dragStart = e.GetPosition(this);
            _dragStartOffset = _viewOffsetFromEnd;
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
            double slot = _lastPlot.Width <= 0d ? 8d : _lastPlot.Width / Math.Max(1, _visibleCandles);
            int deltaCandles = (int)Math.Round((current.X - _dragStart.X) / Math.Max(1d, slot));
            _viewOffsetFromEnd = _dragStartOffset + deltaCandles;
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
            else if (IsInPriceAxis(point) || (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                _priceGridLines = Clamp(_priceGridLines + direction, MinPriceGridLines, MaxPriceGridLines);
            }
            else
            {
                _priceScale = Clamp(_priceScale * (direction > 0 ? 0.9d : 1.1d), 0.35d, 3.0d);
            }

            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonDown(e);
            _viewOffsetFromEnd = 0;
            _visibleCandles = 90;
            _priceGridLines = 6;
            _priceScale = 1d;
            InvalidateVisual();
            e.Handled = true;
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

            decimal center = (min + max) / 2m;
            decimal half = range / 2m * Convert.ToDecimal(Clamp(_priceScale, 0.35d, 3.0d));
            min = center - half;
            max = center + half;
        }

        private int EffectivePriceGridLines()
        {
            int baseLines = Clamp(_priceGridLines, MinPriceGridLines, MaxPriceGridLines);
            double zoom = Clamp(_priceScale, 0.35d, 3.0d);
            int scaledLines = (int)Math.Round(baseLines * Math.Sqrt(1d / zoom));
            return Clamp(scaledLines, MinPriceGridLines, MaxPriceGridLines);
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
            double slotWidth = _lastPlot.Width <= 0d ? 8d : _lastPlot.Width / Math.Max(1, currentVisible);
            int anchorSlot = Clamp((int)Math.Round((point.X - _lastPlot.Left) / Math.Max(1d, slotWidth)), 0, Math.Max(0, currentVisible - 1));
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
            int seriesCount = _bars.Count + (_snapshot != null && _snapshot.Ultimo.HasValue ? 1 : 0);
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

        private bool IsInPriceAxis(Point point)
        {
            return !_lastPlot.IsEmpty && point.X >= _lastPlot.Right && point.Y >= _lastPlot.Top && point.Y <= _lastPlot.Bottom;
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

            return plot.Bottom - (decimal.ToDouble(price - min) / decimal.ToDouble(max - min)) * plot.Height;
        }

        private Brush LevelBrush(KeyLevel level)
        {
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
    }
}
