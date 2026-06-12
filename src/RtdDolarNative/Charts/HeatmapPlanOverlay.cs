using System;
using System.Collections.Generic;
using System.Globalization;
using RtdDolarNative.Heatmap;

namespace RtdDolarNative.Charts
{
    public sealed class HeatmapPlanOverlay
    {
        public HeatmapPlanOverlay()
        {
            Lines = new List<HeatmapPlanOverlayLine>();
        }

        public bool IsAvailable { get; set; }
        public string Summary { get; set; }
        public List<HeatmapPlanOverlayLine> Lines { get; set; }

        public static HeatmapPlanOverlay Build(HeatmapOperationalPlan plan)
        {
            HeatmapPlanOverlay overlay = new HeatmapPlanOverlay();

            if (plan == null ||
                !plan.AnchorPrice.HasValue ||
                string.IsNullOrWhiteSpace(plan.State) ||
                string.Equals(plan.Direction, "Neutro", StringComparison.OrdinalIgnoreCase))
            {
                overlay.Summary = "PLANO -";
                return overlay;
            }

            overlay.IsAvailable = true;
            overlay.Summary = plan.State.ToUpperInvariant() +
                              " | CONF " + plan.ConfidenceScore.ToString("N0", CultureInfo.InvariantCulture) +
                              " | R/R " + plan.RiskReward.ToString("0.00", CultureInfo.InvariantCulture);

            HeatmapPlanOverlayLine entry = new HeatmapPlanOverlayLine();
            entry.Role = "ENT";
            entry.Price = plan.AnchorPrice.Value;
            entry.Direction = plan.Direction;
            entry.Label = "ENT " + FormatPrice(plan.AnchorPrice.Value) + " " + FormatTicks(plan.AnchorDistanceTicks);
            overlay.Lines.Add(entry);

            if (plan.TargetPrice.HasValue)
            {
                HeatmapPlanOverlayLine target = new HeatmapPlanOverlayLine();
                target.Role = "ALVO";
                target.Price = plan.TargetPrice.Value;
                target.Direction = plan.Direction;
                target.Label = "ALVO " + FormatPrice(plan.TargetPrice.Value) + " " + FormatTicks(plan.RewardTicks);
                overlay.Lines.Add(target);
            }

            if (plan.StopPrice.HasValue)
            {
                HeatmapPlanOverlayLine stop = new HeatmapPlanOverlayLine();
                stop.Role = "STOP";
                stop.Price = plan.StopPrice.Value;
                stop.Direction = "Risco";
                stop.Label = "STOP " + FormatPrice(plan.StopPrice.Value) + " R" + Math.Abs(plan.RiskTicks).ToString(CultureInfo.InvariantCulture) + "t";
                overlay.Lines.Add(stop);
            }

            return overlay;
        }

        private static string FormatPrice(decimal price)
        {
            return price.ToString("N2", new CultureInfo("pt-BR"));
        }

        private static string FormatTicks(int ticks)
        {
            return ticks.ToString("+0;-0;0", CultureInfo.InvariantCulture) + "t";
        }
    }

    public sealed class HeatmapPlanOverlayLine
    {
        public string Role { get; set; }
        public decimal Price { get; set; }
        public string Direction { get; set; }
        public string Label { get; set; }
    }
}
