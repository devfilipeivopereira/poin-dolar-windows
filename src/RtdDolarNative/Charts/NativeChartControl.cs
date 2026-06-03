using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using RtdDolarNative.Csv;
using RtdDolarNative.MarketData;
using RtdDolarNative.Quant;

namespace RtdDolarNative.Charts
{
    public sealed class NativeChartControl : FrameworkElement
    {
        private List<DailyBar> _bars = new List<DailyBar>();
        private MarketSnapshot _snapshot;
        private QuantResult _result;
        private readonly Typeface _mono = new Typeface("Consolas");

        public void SetData(List<DailyBar> bars, MarketSnapshot snapshot, QuantResult result)
        {
            _bars = bars == null ? new List<DailyBar>() : bars.ToList();
            _snapshot = snapshot;
            _result = result;
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
            Pen gridPen = new Pen(new SolidColorBrush(Color.FromRgb(48, 56, 68)), 1);
            Pen axisPen = new Pen(new SolidColorBrush(Color.FromRgb(84, 94, 108)), 1);
            Brush textBrush = new SolidColorBrush(Color.FromRgb(169, 179, 191));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(11, 12, 10)), axisPen, plot);

            List<DailyBar> visible = _bars.Skip(Math.Max(0, _bars.Count - 90)).ToList();

            if (_snapshot != null && _snapshot.Ultimo.HasValue)
            {
                DailyBar current = new DailyBar();
                current.Date = DateTime.Today;
                current.Open = _snapshot.Abertura ?? _snapshot.Ultimo.Value;
                current.High = _snapshot.Maxima ?? _snapshot.Ultimo.Value;
                current.Low = _snapshot.Minima ?? _snapshot.Ultimo.Value;
                current.Close = _snapshot.Ultimo.Value;
                current.Volume = _snapshot.Volume;
                visible.Add(current);
            }

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

            for (int i = 0; i <= 5; i++)
            {
                double y = plot.Top + plot.Height * i / 5d;
                dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                decimal price = max - (max - min) * i / 5m;
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
