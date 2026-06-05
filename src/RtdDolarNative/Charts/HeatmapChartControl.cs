using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using RtdDolarNative.Heatmap;

namespace RtdDolarNative.Charts
{
    public sealed class HeatmapChartControl : FrameworkElement
    {
        private readonly Typeface _mono = new Typeface("Consolas");
        private HeatmapSnapshot _snapshot;

        public void SetData(HeatmapSnapshot snapshot)
        {
            _snapshot = snapshot;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            Rect bounds = new Rect(0, 0, ActualWidth, ActualHeight);
            Brush background = new SolidColorBrush(Color.FromRgb(0, 0, 0));
            Brush panel = new SolidColorBrush(Color.FromRgb(11, 12, 10));
            Brush text = new SolidColorBrush(Color.FromRgb(230, 237, 243));
            Brush muted = new SolidColorBrush(Color.FromRgb(169, 179, 191));
            Pen border = new Pen(new SolidColorBrush(Color.FromRgb(48, 56, 68)), 1);

            dc.DrawRectangle(background, null, bounds);
            dc.DrawRectangle(panel, border, new Rect(0.5, 0.5, Math.Max(0, ActualWidth - 1), Math.Max(0, ActualHeight - 1)));

            if (ActualWidth < 320 || ActualHeight < 160)
            {
                return;
            }

            DrawText(dc, "Heatmap Book + Negocios", 12, 12, text, 14, FontWeights.Bold);
            DrawText(dc, _snapshot == null ? "sem dados" : (_snapshot.Asset + " | " + _snapshot.StorageStatus), 12, 32, muted, 11, FontWeights.Normal);

            Rect plot = new Rect(12, 56, Math.Max(20, ActualWidth - 24), Math.Max(20, ActualHeight - 68));

            if (_snapshot == null || _snapshot.Cells == null || _snapshot.Cells.Count == 0)
            {
                DrawText(dc, "Aguardando Book ou Times and Trades.", plot.Left, plot.Top + 10, muted, 13, FontWeights.Normal);
                return;
            }

            double labelWidth = 86;
            double readWidth = 150;
            double center = plot.Left + labelWidth + (plot.Width - labelWidth - readWidth) / 2d;
            double sideWidth = Math.Max(30, (plot.Width - labelWidth - readWidth) / 2d);
            double rowHeight = Math.Max(16, Math.Min(24, plot.Height / _snapshot.Cells.Count));
            decimal maxBid = _snapshot.MaxBidLiquidity <= 0m ? 1m : _snapshot.MaxBidLiquidity;
            decimal maxAsk = _snapshot.MaxAskLiquidity <= 0m ? 1m : _snapshot.MaxAskLiquidity;
            decimal maxTrade = _snapshot.MaxTradeVolume <= 0m ? 1m : _snapshot.MaxTradeVolume;
            Pen rowPen = new Pen(new SolidColorBrush(Color.FromRgb(34, 42, 51)), 1);

            DrawText(dc, "Preco", plot.Left, plot.Top - 18, muted, 11, FontWeights.Bold);
            DrawText(dc, "Book compra", center - sideWidth + 6, plot.Top - 18, new SolidColorBrush(Color.FromRgb(0, 230, 118)), 11, FontWeights.Bold);
            DrawText(dc, "Book venda", center + 6, plot.Top - 18, new SolidColorBrush(Color.FromRgb(255, 59, 48)), 11, FontWeights.Bold);
            DrawText(dc, "Leitura", plot.Right - readWidth + 8, plot.Top - 18, muted, 11, FontWeights.Bold);
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(84, 94, 108)), 1), new Point(center, plot.Top), new Point(center, plot.Bottom));

            for (int i = 0; i < _snapshot.Cells.Count; i++)
            {
                HeatmapCell cell = _snapshot.Cells[i];
                double y = plot.Top + i * rowHeight;
                Rect row = new Rect(plot.Left, y, plot.Width, Math.Max(8, rowHeight - 1));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(5, 6, 5)), rowPen, row);

                double bidWidth = decimal.ToDouble(cell.BidLiquidity / maxBid) * sideWidth;
                double askWidth = decimal.ToDouble(cell.AskLiquidity / maxAsk) * sideWidth;
                double tradeRatio = decimal.ToDouble((cell.BuyVolume + cell.SellVolume + cell.NeutralVolume) / maxTrade);
                double dot = Math.Max(3, Math.Min(15, 3 + tradeRatio * 12));

                if (bidWidth > 1d)
                {
                    dc.DrawRectangle(HeatBrush(true, cell.BidLiquidity / maxBid), null, new Rect(center - bidWidth, y + 2, bidWidth, rowHeight - 5));
                }

                if (askWidth > 1d)
                {
                    dc.DrawRectangle(HeatBrush(false, cell.AskLiquidity / maxAsk), null, new Rect(center, y + 2, askWidth, rowHeight - 5));
                }

                if (cell.BuyVolume + cell.SellVolume + cell.NeutralVolume > 0m)
                {
                    Brush tradeBrush = cell.Delta > 0m
                        ? new SolidColorBrush(Color.FromRgb(0, 230, 118))
                        : cell.Delta < 0m
                            ? new SolidColorBrush(Color.FromRgb(255, 59, 48))
                            : new SolidColorBrush(Color.FromRgb(255, 184, 0));
                    dc.DrawEllipse(tradeBrush, null, new Point(center, y + rowHeight / 2d), dot, dot);
                }

                if (_snapshot.CurrentPrice.HasValue && Math.Abs(cell.Price - _snapshot.CurrentPrice.Value) <= 0.0001m)
                {
                    dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0, 230, 118)), 2), row);
                }

                DrawText(dc, cell.Price.ToString("N2", new CultureInfo("pt-BR")), plot.Left + 4, y + 3, DirectionBrush(cell.Direction), 12, FontWeights.Bold);
                DrawText(dc, cell.Read ?? "-", plot.Right - readWidth + 8, y + 3, DirectionBrush(cell.Direction), 11, FontWeights.Normal);
            }
        }

        private Brush DirectionBrush(string direction)
        {
            if (string.Equals(direction, "Compra", StringComparison.OrdinalIgnoreCase))
            {
                return new SolidColorBrush(Color.FromRgb(0, 230, 118));
            }

            if (string.Equals(direction, "Venda", StringComparison.OrdinalIgnoreCase))
            {
                return new SolidColorBrush(Color.FromRgb(255, 59, 48));
            }

            return new SolidColorBrush(Color.FromRgb(230, 237, 243));
        }

        private Brush HeatBrush(bool bid, decimal ratio)
        {
            byte alpha = (byte)Math.Max(40, Math.Min(230, 40 + decimal.ToDouble(ratio) * 190d));
            return bid
                ? new SolidColorBrush(Color.FromArgb(alpha, 0, 230, 118))
                : new SolidColorBrush(Color.FromArgb(alpha, 255, 59, 48));
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
    }
}
