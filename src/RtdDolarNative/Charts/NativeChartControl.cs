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

        public NativeChartControl()
        {
            Focusable = true;
            ClipToBounds = true;
            Cursor = Cursors.SizeAll;
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

            List<DailyBar> visible = VisibleBars(SeriesWithCurrentSnapshot());

            if (visible.Count == 0)
            {
                DrawText(dc, "Carregue CSV para visualizar candles e niveis.", 16, 16, textBrush, 13);
                return;
            }

            decimal min = visible.Min(x => x.Low);
            decimal max = visible.Max(x => x.High);
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

            int priceLines = Clamp(_priceGridLines, MinPriceGridLines, MaxPriceGridLines);
            for (int i = 0; i < priceLines; i++)
            {
                double ratio = priceLines == 1 ? 0d : i / (double)(priceLines - 1);
                double y = plot.Top + plot.Height * ratio;
                dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                decimal price = max - (max - min) * i / Math.Max(1m, priceLines - 1m);
                DrawText(dc, price.ToString("N1", new CultureInfo("pt-BR")), plot.Right + 6, y - 8, textBrush, 11);
            }

            double candleSlot = plot.Width / Math.Max(1, visible.Count);
            double candleWidth = Math.Max(3d, Math.Min(12d, candleSlot * 0.58d));

            for (int i = 0; i < visible.Count; i++)
            {
                DailyBar bar = visible[i];
                double x = plot.Left + candleSlot * i + candleSlot / 2d;
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
                _visibleCandles = Clamp(_visibleCandles - direction * 10, MinVisibleCandles, MaxVisibleCandles);
                ClampViewportOffset();
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

        private List<DailyBar> SeriesWithCurrentSnapshot()
        {
            List<DailyBar> series = _bars.ToList();

            if (_snapshot != null && _snapshot.Ultimo.HasValue)
            {
                DailyBar current = new DailyBar();
                current.Date = DateTime.Today;
                current.Open = _snapshot.Abertura ?? _snapshot.Ultimo.Value;
                current.High = _snapshot.Maxima ?? _snapshot.Ultimo.Value;
                current.Low = _snapshot.Minima ?? _snapshot.Ultimo.Value;
                current.Close = _snapshot.Ultimo.Value;
                current.Volume = _snapshot.Volume;
                series.Add(current);
            }

            return series;
        }

        private List<DailyBar> VisibleBars(List<DailyBar> series)
        {
            if (series == null || series.Count == 0)
            {
                return new List<DailyBar>();
            }

            int visibleCount = Math.Min(series.Count, Clamp(_visibleCandles, MinVisibleCandles, MaxVisibleCandles));
            int maxOffset = Math.Max(0, series.Count - visibleCount);
            _viewOffsetFromEnd = Clamp(_viewOffsetFromEnd, 0, maxOffset);
            int start = Math.Max(0, series.Count - visibleCount - _viewOffsetFromEnd);
            return series.Skip(start).Take(visibleCount).ToList();
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

        private void ClampViewportOffset()
        {
            int seriesCount = _bars.Count + (_snapshot != null && _snapshot.Ultimo.HasValue ? 1 : 0);
            int visibleCount = Math.Min(seriesCount, Clamp(_visibleCandles, MinVisibleCandles, MaxVisibleCandles));
            int maxOffset = Math.Max(0, seriesCount - visibleCount);
            _viewOffsetFromEnd = Clamp(_viewOffsetFromEnd, 0, maxOffset);
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
