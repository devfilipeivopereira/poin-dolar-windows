using System.Linq;

namespace RtdDolarNative.Config
{
    public static class UiConfig
    {
        private static readonly int[] AllowedCalculationDays = new[] { 21, 45, 63, 90 };

        public static int NormalizeCalculationDays(int days)
        {
            return AllowedCalculationDays.Contains(days) ? days : 45;
        }
    }
}
