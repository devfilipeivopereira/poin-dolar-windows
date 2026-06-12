using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using RtdDolarNative.Flow;
using RtdDolarNative.MarketData;
using RtdDolarNative.Quant;

namespace RtdDolarNative.Charts
{
    public sealed class VolumeProfileChartControl : FrameworkElement
    {
        private readonly Typeface _mono = new Typeface("Consolas");
        private List<ProfileChartRow> _rows = new List<ProfileChartRow>();
        private MarketSnapshot _snapshot;
        private string _source = "Proxy";
        private decimal _valueAreaPercent = 0.70m;

        public void SetData(VolumeProfileMetrics intraday, VolumeProfileResult proxy, MarketSnapshot snapshot, decimal valueAreaPercent)
        {
            _snapshot = snapshot;
            _valueAreaPercent = valueAreaPercent <= 0m ? 0.70m : valueAreaPercent;

            if (intraday != null && intraday.Bins != null && intraday.Bins.Count > 0)
            {
                _source = "Intraday";
                _rows = intraday.Bins
                    .Select(x => new ProfileChartRow
                    {
                        Price = x.Price,
                        Volume = x.Volume,
                        InValueArea = x.InValueArea,
                        IsPoc = x.IsPoc,
                        IsHvn = x.IsHvn,
                        IsLvn = x.IsLvn
                    })
                    .OrderBy(x => x.Price)
                    .ToList();
            }
            else if (proxy != null && proxy.Bins != null && proxy.Bins.Count > 0)
            {
                _source = string.IsNullOrWhiteSpace(proxy.Source) ? "CSV" : proxy.Source;
                _rows = proxy.Bins
                    .Select(x => new ProfileChartRow
                    {
                        Price = x.Price,
                        Volume = Convert.ToDecimal(x.Volume),
                        InValueArea = x.InValue,
                        IsPoc = proxy.Poc != null && x.Price == proxy.Poc.Price,
                        IsHvn = x.IsHvn,
                        IsLvn = x.IsLvn
                    })
                    .OrderBy(x => x.Price)
                    .ToList();
            }
            else
            {
                _source = "Sem dados";
                _rows = new List<ProfileChartRow>();
            }

            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            Rect bounds = new Rect(0, 0, ActualWidth, ActualHeight);
            Brush background = new SolidColorBrush(Color.FromRgb(0, 0, 0));
            Brush panel = new SolidColorBrush(Color.FromRgb(11, 12, 10));
            Pen border = new Pen(new SolidColorBrush(Color.FromRgb(48, 56, 68)), 1);
            Brush text = new SolidColorBrush(Color.FromRgb(230, 237, 243));
            Brush muted = new SolidColorBrush(Color.FromRgb(169, 179, 191));
            Brush accent = new SolidColorBrush(Color.FromRgb(255, 184, 0));

            dc.DrawRectangle(background, null, bounds);
            dc.DrawRectangle(panel, border, new Rect(0.5, 0.5, Math.Max(0, ActualWidth - 1), Math.Max(0, ActualHeight - 1)));

            if (ActualWidth < 260 || ActualHeight < 140)
            {
                return;
            }

            DrawText(dc, "VOLUME PROFILE " + _source.ToUpperInvariant(), 12, 14, text, 13, FontWeights.Bold);
            string badge = "POC/VA " + (_valueAreaPercent * 100m).ToString("N0", CultureInfo.InvariantCulture) + "%";
            double badgeWidth = 88;
            Rect badgeRect = new Rect(ActualWidth - badgeWidth - 14, 12, badgeWidth, 28);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(63, 45, 0)), new Pen(accent, 1), badgeRect, 5, 5);
            DrawText(dc, badge, badgeRect.Left + 9, badgeRect.Top + 7, accent, 12, FontWeights.Bold);

            Rect plot = new Rect(12, 54, Math.Max(20, ActualWidth - 24), Math.Max(20, ActualHeight - 66));

            if (_rows.Count == 0)
            {
                DrawText(dc, "Volume Profile aguardando prints ou CSV.", plot.Left, plot.Top + 12, muted, 13, FontWeights.Normal);
                return;
            }

            List<ProfileChartRow> visible = VisibleRows(plot.Height);
            decimal maxVolume = visible.Max(x => x.Volume);
            decimal totalVolume = _rows.Sum(x => x.Volume);

            if (maxVolume <= 0m || totalVolume <= 0m)
            {
                return;
            }

            double labelWidth = 96;
            double percentWidth = 70;
            double rowHeight = Math.Max(18, Math.Min(26, plot.Height / visible.Count));
            Rect barArea = new Rect(plot.Left + labelWidth, plot.Top, plot.Width - labelWidth - percentWidth, rowHeight - 5);
            Pen railPen = new Pen(new SolidColorBrush(Color.FromRgb(48, 56, 68)), 1);
            Brush railFill = new SolidColorBrush(Color.FromRgb(5, 6, 5));

            for (int i = 0; i < visible.Count; i++)
            {
                ProfileChartRow row = visible[visible.Count - 1 - i];
                double y = plot.Top + i * rowHeight;
                Rect rail = new Rect(barArea.Left, y + 3, Math.Max(20, barArea.Width), Math.Max(8, rowHeight - 7));
                double width = decimal.ToDouble(row.Volume / maxVolume) * rail.Width;
                Rect fill = new Rect(rail.Left, rail.Top, Math.Max(2, width), rail.Height);
                Brush fillBrush = BarBrush(row);

                DrawText(dc, row.Price.ToString("N1", new CultureInfo("pt-BR")), plot.Left, y + 4, text, 12, FontWeights.Normal);
                dc.DrawRectangle(railFill, railPen, rail);
                dc.DrawRectangle(fillBrush, null, fill);

                if (row.IsPoc)
                {
                    dc.DrawRectangle(null, new Pen(accent, 1.5), rail);
                }

                if (_snapshot != null && _snapshot.Ultimo.HasValue && Math.Abs(row.Price - _snapshot.Ultimo.Value) <= 0.25m)
                {
                    dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(0, 230, 118)), 2), new Point(rail.Left - 6, rail.Top), new Point(rail.Left - 6, rail.Bottom));
                }

                decimal percent = row.Volume / totalVolume * 100m;
                DrawText(dc, percent.ToString("N1", new CultureInfo("pt-BR")) + "%", rail.Right + 12, y + 4, text, 12, FontWeights.Normal);

                string tag = LevelTag(row);

                if (!string.IsNullOrWhiteSpace(tag))
                {
                    DrawText(dc, tag, rail.Left + 8, y + 4, row.IsPoc ? new SolidColorBrush(Color.FromRgb(0, 0, 0)) : text, 11, FontWeights.Bold);
                }
            }
        }

        private List<ProfileChartRow> VisibleRows(double height)
        {
            int maxRows = Math.Max(6, Math.Min(_rows.Count, (int)Math.Floor(height / 22d)));

            if (_rows.Count <= maxRows)
            {
                return _rows.ToList();
            }

            decimal anchor = _snapshot != null && _snapshot.Ultimo.HasValue
                ? _snapshot.Ultimo.Value
                : _rows.OrderByDescending(x => x.Volume).First().Price;
            int center = _rows
                .Select((x, i) => new { Row = x, Index = i })
                .OrderBy(x => Math.Abs(x.Row.Price - anchor))
                .First()
                .Index;
            int start = Math.Max(0, center - maxRows / 2);

            if (start + maxRows > _rows.Count)
            {
                start = _rows.Count - maxRows;
            }

            return _rows.Skip(start).Take(maxRows).ToList();
        }

        private Brush BarBrush(ProfileChartRow row)
        {
            if (row.IsPoc)
            {
                return new SolidColorBrush(Color.FromRgb(234, 185, 38));
            }

            if (row.IsLvn)
            {
                return new SolidColorBrush(Color.FromRgb(38, 113, 100));
            }

            if (row.IsHvn)
            {
                return new SolidColorBrush(Color.FromRgb(74, 158, 217));
            }

            if (row.InValueArea)
            {
                return new SolidColorBrush(Color.FromRgb(53, 125, 174));
            }

            return new SolidColorBrush(Color.FromRgb(38, 88, 128));
        }

        private string LevelTag(ProfileChartRow row)
        {
            if (row.IsPoc)
            {
                return "POC";
            }

            if (row.IsHvn)
            {
                return "HVN";
            }

            if (row.IsLvn)
            {
                return "LVN";
            }

            return row.InValueArea ? "VA" : string.Empty;
        }

        private void DrawText(DrawingContext dc, string value, double x, double y, Brush brush, double size, FontWeight weight)
        {
            FormattedText text = new FormattedText(
                value ?? string.Empty,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                _mono,
                size,
                brush);
            text.SetFontWeight(weight);
            dc.DrawText(text, new Point(x, y));
        }

        private sealed class ProfileChartRow
        {
            public decimal Price { get; set; }
            public decimal Volume { get; set; }
            public bool InValueArea { get; set; }
            public bool IsPoc { get; set; }
            public bool IsHvn { get; set; }
            public bool IsLvn { get; set; }
        }
    }
}
