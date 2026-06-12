using System;
using System.Collections.Generic;
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
            Brush accent = new SolidColorBrush(Color.FromRgb(255, 184, 0));
            Brush buy = new SolidColorBrush(Color.FromRgb(18, 184, 134));
            Brush sell = new SolidColorBrush(Color.FromRgb(255, 82, 82));
            Brush flowSql = new SolidColorBrush(Color.FromRgb(88, 166, 255));
            Pen border = new Pen(new SolidColorBrush(Color.FromRgb(48, 56, 68)), 1);

            dc.DrawRectangle(background, null, bounds);
            dc.DrawRectangle(panel, border, new Rect(0.5, 0.5, Math.Max(0, ActualWidth - 1), Math.Max(0, ActualHeight - 1)));

            if (ActualWidth < 320 || ActualHeight < 160)
            {
                return;
            }

            DrawText(dc, "HEATMAP OPERACIONAL", 12, 12, text, 14, FontWeights.Bold);
            double statusY = 32d;
            double plotTop = 56d;

            if (_snapshot != null)
            {
                string[] badgeValues = new[]
                {
                    "CONF " + _snapshot.MaxConfidenceScore.ToString("N0", CultureInfo.InvariantCulture) + "/" + _snapshot.MaxConflictScore.ToString("N0", CultureInfo.InvariantCulture),
                    "SQL " + _snapshot.MaxHistoricalScore.ToString("N0", CultureInfo.InvariantCulture) + "/" + _snapshot.MaxHistoricalFlowScore.ToString("N0", CultureInfo.InvariantCulture),
                    "CVD " + _snapshot.CumulativeDelta.ToString("N0", CultureInfo.InvariantCulture),
                    "STAB " + _snapshot.MaxPersistenceScore.ToString("N0", CultureInfo.InvariantCulture),
                    "SPOOF " + _snapshot.MaxSpoofRiskScore.ToString("N0", CultureInfo.InvariantCulture),
                    "TOP " + Empty(_snapshot.DominantRead),
                    "WALL " + _snapshot.MaxWallScore.ToString("N0", CultureInfo.InvariantCulture)
                };
                Brush[] badgeBrushes = new[]
                {
                    _snapshot.MaxConflictScore >= 50m ? sell : _snapshot.MaxConfidenceScore >= 70m ? buy : muted,
                    _snapshot.MaxHistoricalFlowScore >= 70m ? flowSql : _snapshot.MaxHistoricalScore >= 70m ? accent : muted,
                    _snapshot.CumulativeDelta >= 0m ? buy : sell,
                    _snapshot.MaxPersistenceScore >= 70m ? buy : muted,
                    _snapshot.MaxSpoofRiskScore >= 70m ? sell : muted,
                    DirectionBrush(_snapshot.DominantSide),
                    accent
                };
                List<Rect> badgeRects = HeatmapBadgeLayout.Calculate(ActualWidth, badgeValues.Length, 96d, 24d, 6d, 12d, 300d);

                for (int i = 0; i < badgeValues.Length && i < badgeRects.Count; i++)
                {
                    DrawBadge(dc, badgeValues[i], badgeRects[i], badgeBrushes[i]);
                }

                if (badgeRects.Count > 0)
                {
                    double badgeBottom = badgeRects.Max(x => x.Bottom);

                    if (badgeBottom > 36d)
                    {
                        statusY = badgeBottom + 4d;
                        plotTop = statusY + 22d;
                    }
                }
            }

            DrawText(dc, _snapshot == null ? "sem dados" : (_snapshot.Asset + " | " + _snapshot.StorageStatus), 12, statusY, muted, 11, FontWeights.Normal);

            Rect plot = new Rect(12, plotTop, Math.Max(20, ActualWidth - 24), Math.Max(20, ActualHeight - plotTop - 12));

            if (_snapshot == null || _snapshot.Cells == null || _snapshot.Cells.Count == 0)
            {
                DrawText(dc, "Aguardando Book ou Times and Trades.", plot.Left, plot.Top + 10, muted, 13, FontWeights.Normal);
                return;
            }

            double labelWidth = 84;
            double readWidth = 182;
            double scoreWidth = 54;
            double deltaWidth = 86;
            double bodyWidth = Math.Max(120, plot.Width - labelWidth - readWidth - scoreWidth);
            double sideWidth = Math.Max(30, (bodyWidth - deltaWidth) / 2d);
            double bidRight = plot.Left + labelWidth + sideWidth;
            double deltaLeft = bidRight;
            double deltaCenter = deltaLeft + deltaWidth / 2d;
            double askLeft = deltaLeft + deltaWidth;
            double rowHeight = Math.Max(16, Math.Min(24, plot.Height / _snapshot.Cells.Count));
            decimal maxBid = _snapshot.MaxBidLiquidity <= 0m ? 1m : _snapshot.MaxBidLiquidity;
            decimal maxAsk = _snapshot.MaxAskLiquidity <= 0m ? 1m : _snapshot.MaxAskLiquidity;
            decimal maxTrade = _snapshot.MaxTradeVolume <= 0m ? 1m : _snapshot.MaxTradeVolume;
            Pen rowPen = new Pen(new SolidColorBrush(Color.FromRgb(34, 42, 51)), 1);

            DrawText(dc, "Preco", plot.Left, plot.Top - 18, muted, 11, FontWeights.Bold);
            DrawText(dc, "Bid", bidRight - sideWidth + 6, plot.Top - 18, buy, 11, FontWeights.Bold);
            DrawText(dc, "Delta", deltaLeft + 20, plot.Top - 18, muted, 11, FontWeights.Bold);
            DrawText(dc, "Ask", askLeft + 6, plot.Top - 18, sell, 11, FontWeights.Bold);
            DrawText(dc, "Score", plot.Right - readWidth - scoreWidth + 4, plot.Top - 18, accent, 11, FontWeights.Bold);
            DrawText(dc, "Leitura", plot.Right - readWidth + 8, plot.Top - 18, muted, 11, FontWeights.Bold);
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(84, 94, 108)), 1), new Point(deltaCenter, plot.Top), new Point(deltaCenter, plot.Bottom));

            for (int i = 0; i < _snapshot.Cells.Count; i++)
            {
                HeatmapCell cell = _snapshot.Cells[i];
                double y = plot.Top + i * rowHeight;
                Rect row = new Rect(plot.Left, y, plot.Width, Math.Max(8, rowHeight - 1));
                byte interestAlpha = (byte)Math.Max(8, Math.Min(46, 8 + decimal.ToDouble(cell.InterestScore) * 0.38d));
                Brush rowFill = string.Equals(cell.Direction, "Compra", StringComparison.OrdinalIgnoreCase)
                    ? new SolidColorBrush(Color.FromArgb(interestAlpha, 18, 184, 134))
                    : string.Equals(cell.Direction, "Venda", StringComparison.OrdinalIgnoreCase)
                        ? new SolidColorBrush(Color.FromArgb(interestAlpha, 255, 82, 82))
                        : new SolidColorBrush(Color.FromRgb(5, 6, 5));
                dc.DrawRectangle(rowFill, rowPen, row);

                if (cell.HistoricalScore > 0m)
                {
                    byte historicalAlpha = (byte)Math.Max(54, Math.Min(220, 54 + decimal.ToDouble(cell.HistoricalScore) * 1.66d));
                    Brush historicalBrush = new SolidColorBrush(Color.FromArgb(historicalAlpha, 255, 184, 0));
                    dc.DrawRectangle(historicalBrush, null, new Rect(plot.Left + labelWidth - 7, y + 3, 4, Math.Max(4, rowHeight - 7)));
                }

                if (cell.HistoricalFlowScore > 0m)
                {
                    byte flowAlpha = (byte)Math.Max(54, Math.Min(220, 54 + decimal.ToDouble(cell.HistoricalFlowScore) * 1.66d));
                    Brush flowBrush = new SolidColorBrush(Color.FromArgb(flowAlpha, 88, 166, 255));
                    dc.DrawRectangle(flowBrush, null, new Rect(plot.Left + labelWidth - 13, y + 3, 4, Math.Max(4, rowHeight - 7)));
                }

                double bidWidth = decimal.ToDouble(cell.BidLiquidity / maxBid) * sideWidth;
                double askWidth = decimal.ToDouble(cell.AskLiquidity / maxAsk) * sideWidth;
                decimal tradeVolume = cell.BuyVolume + cell.SellVolume + cell.NeutralVolume;
                double deltaRatio = maxTrade <= 0m ? 0d : decimal.ToDouble(Math.Min(1m, Math.Abs(cell.Delta) / maxTrade));
                double deltaBar = deltaRatio * (deltaWidth / 2d - 6d);

                if (bidWidth > 1d)
                {
                    dc.DrawRectangle(HeatBrush(true, cell.BidLiquidity / maxBid), null, new Rect(bidRight - bidWidth, y + 2, bidWidth, rowHeight - 5));
                }

                if (askWidth > 1d)
                {
                    dc.DrawRectangle(HeatBrush(false, cell.AskLiquidity / maxAsk), null, new Rect(askLeft, y + 2, askWidth, rowHeight - 5));
                }

                if (cell.BidChange != 0m)
                {
                    Pen changePen = new Pen(cell.BidChange > 0m ? buy : sell, 2);
                    dc.DrawLine(changePen, new Point(bidRight - Math.Max(2, bidWidth), y + 2), new Point(bidRight - Math.Max(2, bidWidth), y + rowHeight - 4));
                }

                if (cell.AskChange != 0m)
                {
                    Pen changePen = new Pen(cell.AskChange > 0m ? sell : buy, 2);
                    dc.DrawLine(changePen, new Point(askLeft + Math.Max(2, askWidth), y + 2), new Point(askLeft + Math.Max(2, askWidth), y + rowHeight - 4));
                }

                if (tradeVolume > 0m)
                {
                    Brush tradeBrush = cell.Delta > 0m ? buy : cell.Delta < 0m ? sell : accent;
                    Rect deltaRect = cell.Delta >= 0m
                        ? new Rect(deltaCenter, y + 4, Math.Max(3, deltaBar), Math.Max(4, rowHeight - 9))
                        : new Rect(deltaCenter - Math.Max(3, deltaBar), y + 4, Math.Max(3, deltaBar), Math.Max(4, rowHeight - 9));
                    dc.DrawRectangle(tradeBrush, null, deltaRect);
                }

                if (_snapshot.CurrentPrice.HasValue && Math.Abs(cell.Price - _snapshot.CurrentPrice.Value) <= 0.0001m)
                {
                    dc.DrawRectangle(null, new Pen(accent, 2), row);
                }

                DrawText(dc, cell.Price.ToString("N2", new CultureInfo("pt-BR")), plot.Left + 4, y + 3, DirectionBrush(cell.Direction), 12, FontWeights.Bold);
                DrawText(dc, cell.InterestScore.ToString("N0", new CultureInfo("pt-BR")), plot.Right - readWidth - scoreWidth + 8, y + 3, accent, 11, FontWeights.Bold);
                DrawText(dc, cell.Read ?? "-", plot.Right - readWidth + 8, y + 3, DirectionBrush(cell.Direction), 11, FontWeights.Normal);
            }

            DrawZoneOverlays(dc, plot, rowHeight, readWidth);
        }

        private void DrawZoneOverlays(DrawingContext dc, Rect plot, double rowHeight, double readWidth)
        {
            if (_snapshot == null || _snapshot.Zones == null || _snapshot.Zones.Count == 0 || _snapshot.Cells == null)
            {
                return;
            }

            foreach (HeatmapZone zone in _snapshot.Zones.Take(8))
            {
                var visible = _snapshot.Cells
                    .Select((x, i) => new { Cell = x, Index = i })
                    .Where(x => x.Cell.Price >= zone.LowPrice && x.Cell.Price <= zone.HighPrice)
                    .ToList();

                if (visible.Count == 0)
                {
                    continue;
                }

                int topIndex = visible.Min(x => x.Index);
                int bottomIndex = visible.Max(x => x.Index);
                double top = plot.Top + topIndex * rowHeight;
                double height = Math.Max(rowHeight, (bottomIndex - topIndex + 1) * rowHeight - 1);
                Brush brush = DirectionBrush(zone.Direction);
                Pen pen = new Pen(brush, zone.ActionScore >= 70m ? 2.5 : zone.Score >= 75m ? 2 : 1);
                Rect rect = new Rect(plot.Left + 1, top + 1, Math.Max(10, plot.Width - 2), Math.Max(8, height - 2));

                dc.DrawRectangle(null, pen, rect);
                DrawText(dc, "Z" + zone.Score.ToString("N0", CultureInfo.InvariantCulture), plot.Right - readWidth - 42, top + 3, brush, 10, FontWeights.Bold);
                DrawText(dc, ActionShort(zone), plot.Left + 6, top + 3, brush, 10, FontWeights.Bold);
            }
        }

        private static string ActionShort(HeatmapZone zone)
        {
            if (zone == null || string.IsNullOrWhiteSpace(zone.Action) || zone.ActionScore <= 0m)
            {
                return string.Empty;
            }

            if (string.Equals(zone.Action, "Aguardar", StringComparison.OrdinalIgnoreCase))
            {
                return "AG " + zone.ActionScore.ToString("N0", CultureInfo.InvariantCulture);
            }

            if (zone.Action.IndexOf("Compra", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "C " + zone.ActionScore.ToString("N0", CultureInfo.InvariantCulture);
            }

            if (zone.Action.IndexOf("Venda", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "V " + zone.ActionScore.ToString("N0", CultureInfo.InvariantCulture);
            }

            return zone.ActionScore.ToString("N0", CultureInfo.InvariantCulture);
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
                ? new SolidColorBrush(Color.FromArgb(alpha, 18, 184, 134))
                : new SolidColorBrush(Color.FromArgb(alpha, 255, 82, 82));
        }

        private void DrawBadge(DrawingContext dc, string value, Rect rect, Brush brush)
        {
            if (rect.Width <= 0d || rect.Height <= 0d)
            {
                return;
            }

            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(20, 24, 29)), new Pen(brush, 1), rect, 4, 4);
            DrawText(dc, value, rect.Left + 8, rect.Top + 6, brush, 10, FontWeights.Bold);
        }

        private string Empty(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
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
