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
                    _snapshot.Plan != null
                        ? "PLAN " + Empty(_snapshot.Plan.State) + " " + _snapshot.Plan.ConfidenceScore.ToString("N0", CultureInfo.InvariantCulture)
                        : "PLAN -",
                    "CONF " + _snapshot.MaxConfidenceScore.ToString("N0", CultureInfo.InvariantCulture) + "/" + _snapshot.MaxConflictScore.ToString("N0", CultureInfo.InvariantCulture),
                    _snapshot.SqlMemory != null && _snapshot.SqlMemory.IsAvailable
                        ? "SQL " + Empty(_snapshot.SqlMemory.Direction) + " " + _snapshot.SqlMemory.PressureScore.ToString("+0;-0;0", CultureInfo.InvariantCulture)
                        : "SQL " + _snapshot.MaxHistoricalScore.ToString("N0", CultureInfo.InvariantCulture) + "/" + _snapshot.MaxHistoricalFlowScore.ToString("N0", CultureInfo.InvariantCulture),
                    "CVD " + _snapshot.CumulativeDelta.ToString("N0", CultureInfo.InvariantCulture),
                    "STAB " + _snapshot.MaxPersistenceScore.ToString("N0", CultureInfo.InvariantCulture),
                    "SPOOF " + _snapshot.MaxSpoofRiskScore.ToString("N0", CultureInfo.InvariantCulture),
                    "TOP " + Empty(_snapshot.DominantRead),
                    "WALL " + _snapshot.MaxWallScore.ToString("N0", CultureInfo.InvariantCulture)
                };
                Brush[] badgeBrushes = new[]
                {
                    _snapshot.Plan != null ? DirectionBrush(_snapshot.Plan.Direction) : muted,
                    _snapshot.MaxConflictScore >= 50m ? sell : _snapshot.MaxConfidenceScore >= 70m ? buy : muted,
                    _snapshot.SqlMemory != null && _snapshot.SqlMemory.IsAvailable ? DirectionBrush(_snapshot.SqlMemory.Direction) : _snapshot.MaxHistoricalFlowScore >= 70m ? flowSql : _snapshot.MaxHistoricalScore >= 70m ? accent : muted,
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

            DrawText(dc, _snapshot == null ? "sem dados" : (_snapshot.Asset + " | " + ViewportStatus(_snapshot) + " | " + _snapshot.StorageStatus), 12, statusY, muted, 11, FontWeights.Normal);

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
            DrawSqlMemoryOverlay(dc, plot, rowHeight, labelWidth, flowSql, muted);
            DrawCorridorOverlay(dc, plot, rowHeight, labelWidth, accent, muted);
            DrawPlanOverlay(dc, plot, rowHeight, labelWidth, text, muted);
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

        private void DrawSqlMemoryOverlay(DrawingContext dc, Rect plot, double rowHeight, double labelWidth, Brush flowSql, Brush muted)
        {
            if (_snapshot == null ||
                _snapshot.SqlMemory == null ||
                !_snapshot.SqlMemory.IsAvailable ||
                _snapshot.Cells == null ||
                _snapshot.Cells.Count == 0)
            {
                return;
            }

            Pen pen = new Pen(flowSql, 1.2);
            pen.DashStyle = DashStyles.Dash;
            double labelX = plot.Left + Math.Max(88d, labelWidth + 4d);

            if (_snapshot.SqlMemory.SupportPrice.HasValue)
            {
                DrawSqlMemoryLine(dc, plot, rowHeight, _snapshot.SqlMemory.SupportPrice.Value, "SQL SUP " + _snapshot.SqlMemory.SupportScore.ToString("N0", CultureInfo.InvariantCulture), DirectionBrush("Compra"), pen, labelX);
            }

            if (_snapshot.SqlMemory.ResistancePrice.HasValue)
            {
                DrawSqlMemoryLine(dc, plot, rowHeight, _snapshot.SqlMemory.ResistancePrice.Value, "SQL RES " + _snapshot.SqlMemory.ResistanceScore.ToString("N0", CultureInfo.InvariantCulture), DirectionBrush("Venda"), pen, labelX);
            }

            DrawText(dc, _snapshot.SqlMemory.PressureScore.ToString("+0;-0;0", CultureInfo.InvariantCulture), plot.Right - 48, plot.Top + 4, DirectionBrush(_snapshot.SqlMemory.Direction), 10, FontWeights.Bold);
        }

        private void DrawSqlMemoryLine(DrawingContext dc, Rect plot, double rowHeight, decimal price, string label, Brush brush, Pen guidePen, double labelX)
        {
            var target = _snapshot.Cells
                .Select((x, i) => new { Cell = x, Index = i })
                .OrderBy(x => Math.Abs(x.Cell.Price - price))
                .FirstOrDefault();

            if (target == null)
            {
                return;
            }

            double y = plot.Top + target.Index * rowHeight + rowHeight / 2d;
            dc.DrawLine(guidePen, new Point(plot.Left + 2, y), new Point(plot.Right - 2, y));
            DrawText(dc, label, labelX, y - 7, brush, 10, FontWeights.Bold);
        }

        private void DrawCorridorOverlay(DrawingContext dc, Rect plot, double rowHeight, double labelWidth, Brush accent, Brush muted)
        {
            if (_snapshot == null ||
                _snapshot.Corridor == null ||
                !_snapshot.Corridor.IsAvailable ||
                _snapshot.Cells == null ||
                _snapshot.Cells.Count == 0)
            {
                return;
            }

            var support = _snapshot.Cells
                .Select((x, i) => new { Cell = x, Index = i })
                .OrderBy(x => Math.Abs(x.Cell.Price - _snapshot.Corridor.SupportPrice))
                .FirstOrDefault();
            var resistance = _snapshot.Cells
                .Select((x, i) => new { Cell = x, Index = i })
                .OrderBy(x => Math.Abs(x.Cell.Price - _snapshot.Corridor.ResistancePrice))
                .FirstOrDefault();

            if (support == null || resistance == null)
            {
                return;
            }

            double supportY = plot.Top + support.Index * rowHeight + rowHeight / 2d;
            double resistanceY = plot.Top + resistance.Index * rowHeight + rowHeight / 2d;
            double top = Math.Min(supportY, resistanceY);
            double bottom = Math.Max(supportY, resistanceY);
            double bracketX = plot.Left + Math.Max(64d, labelWidth - 8d);
            Pen pen = new Pen(accent, 1.5);

            dc.DrawLine(pen, new Point(bracketX, top), new Point(bracketX, bottom));
            dc.DrawLine(pen, new Point(bracketX - 8, top), new Point(bracketX + 8, top));
            dc.DrawLine(pen, new Point(bracketX - 8, bottom), new Point(bracketX + 8, bottom));
            DrawText(dc, "COR " + _snapshot.Corridor.WidthTicks.ToString("N0", CultureInfo.InvariantCulture) + "t " + Empty(_snapshot.Corridor.Phase), bracketX + 12, top + 2, accent, 10, FontWeights.Bold);
            DrawText(dc, Empty(_snapshot.Corridor.Location) + " " + _snapshot.Corridor.CurrentPositionPct.ToString("N0", CultureInfo.InvariantCulture) + "%", bracketX + 12, bottom - 14, muted, 10, FontWeights.Normal);
        }

        private void DrawPlanOverlay(DrawingContext dc, Rect plot, double rowHeight, double labelWidth, Brush text, Brush muted)
        {
            if (_snapshot == null || _snapshot.Cells == null || _snapshot.Cells.Count == 0)
            {
                return;
            }

            HeatmapPlanOverlay overlay = HeatmapPlanOverlay.Build(_snapshot.Plan);

            if (overlay == null || !overlay.IsAvailable || overlay.Lines == null || overlay.Lines.Count == 0)
            {
                return;
            }

            Brush planBrush = DirectionBrush(_snapshot.Plan == null ? null : _snapshot.Plan.Direction);
            double panelWidth = Math.Min(390d, Math.Max(210d, plot.Width - labelWidth - 20d));
            Rect panel = new Rect(plot.Left + Math.Max(88d, labelWidth + 8d), plot.Top + 8d, panelWidth, 30d);
            Brush panelFill = new SolidColorBrush(Color.FromArgb(214, 10, 13, 15));
            Pen panelPen = new Pen(planBrush, 1.2);

            dc.DrawRoundedRectangle(panelFill, panelPen, panel, 4d, 4d);
            DrawText(dc, overlay.Summary, panel.Left + 10d, panel.Top + 8d, planBrush, 10, FontWeights.Bold);

            foreach (HeatmapPlanOverlayLine line in overlay.Lines)
            {
                if (line == null)
                {
                    continue;
                }

                bool clamped;
                double y = ResolvePriceY(line.Price, plot, rowHeight, out clamped);

                if (double.IsNaN(y))
                {
                    continue;
                }

                Brush brush = PlanLineBrush(line);
                Pen guide = new Pen(brush, string.Equals(line.Role, "ENT", StringComparison.OrdinalIgnoreCase) ? 2.2d : 1.6d);

                if (string.Equals(line.Role, "STOP", StringComparison.OrdinalIgnoreCase))
                {
                    guide.DashStyle = DashStyles.Dash;
                }
                else if (clamped)
                {
                    guide.DashStyle = DashStyles.Dot;
                }

                double x1 = plot.Left + 2d;
                double x2 = plot.Right - 2d;
                dc.DrawLine(guide, new Point(x1, y), new Point(x2, y));

                string label = clamped ? line.Label + (line.Price > MaxVisiblePrice() ? " acima" : " abaixo") : line.Label;
                Rect labelRect = new Rect(panel.Left, Math.Max(plot.Top + 2d, Math.Min(plot.Bottom - 22d, y - 11d)), Math.Min(240d, Math.Max(124d, plot.Width - labelWidth - 36d)), 21d);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(226, 0, 0, 0)), new Pen(brush, 1d), labelRect, 3d, 3d);
                DrawText(dc, label, labelRect.Left + 8d, labelRect.Top + 5d, brush, 10, FontWeights.Bold);
            }
        }

        private double ResolvePriceY(decimal price, Rect plot, double rowHeight, out bool clamped)
        {
            clamped = false;

            if (_snapshot == null || _snapshot.Cells == null || _snapshot.Cells.Count == 0)
            {
                return double.NaN;
            }

            decimal maxVisible = MaxVisiblePrice();
            decimal minVisible = MinVisiblePrice();

            if (price > maxVisible)
            {
                clamped = true;
                return plot.Top + 6d;
            }

            if (price < minVisible)
            {
                clamped = true;
                return plot.Bottom - 6d;
            }

            var target = _snapshot.Cells
                .Select((x, i) => new { Cell = x, Index = i })
                .OrderBy(x => Math.Abs(x.Cell.Price - price))
                .FirstOrDefault();

            return target == null
                ? double.NaN
                : plot.Top + target.Index * rowHeight + rowHeight / 2d;
        }

        private decimal MaxVisiblePrice()
        {
            return _snapshot == null || _snapshot.Cells == null || _snapshot.Cells.Count == 0
                ? 0m
                : _snapshot.Cells.Max(x => x.Price);
        }

        private decimal MinVisiblePrice()
        {
            return _snapshot == null || _snapshot.Cells == null || _snapshot.Cells.Count == 0
                ? 0m
                : _snapshot.Cells.Min(x => x.Price);
        }

        private Brush PlanLineBrush(HeatmapPlanOverlayLine line)
        {
            if (line == null)
            {
                return new SolidColorBrush(Color.FromRgb(230, 237, 243));
            }

            if (string.Equals(line.Role, "STOP", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(line.Direction, "Risco", StringComparison.OrdinalIgnoreCase))
            {
                return new SolidColorBrush(Color.FromRgb(255, 184, 0));
            }

            return DirectionBrush(line.Direction);
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

        private static string ViewportStatus(HeatmapSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return "janela -";
            }

            string mode = string.Equals(snapshot.ViewportMode, "Manual", StringComparison.OrdinalIgnoreCase) ? "MANUAL" : "AUTO";
            string range = snapshot.VisibleBottomPrice.HasValue && snapshot.VisibleTopPrice.HasValue
                ? snapshot.VisibleBottomPrice.Value.ToString("N2", new CultureInfo("pt-BR")) + "-" + snapshot.VisibleTopPrice.Value.ToString("N2", new CultureInfo("pt-BR"))
                : "-";
            int visible = snapshot.Cells == null ? 0 : snapshot.Cells.Count;
            return mode + " " + range + " " + visible.ToString(CultureInfo.InvariantCulture) + "/" + snapshot.TotalPriceLevels.ToString(CultureInfo.InvariantCulture);
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
