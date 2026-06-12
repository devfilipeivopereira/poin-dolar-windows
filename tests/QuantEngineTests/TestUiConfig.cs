using System.Linq;

namespace RtdDolarNative.Config
{
    public static class UiConfig
    {
        private static readonly int[] AllowedCalculationDays = new[] { 21, 45, 63, 90 };
        private static readonly int[] AllowedVolumeProfileDays = new[] { 7, 14, 21, 28, 35, 42 };
        public static readonly int DefaultVolumeProfileDays = 42;

        public static int NormalizeCalculationDays(int days)
        {
            return AllowedCalculationDays.Contains(days) ? days : 45;
        }

        public static int NormalizeVolumeProfileDays(int days)
        {
            return AllowedVolumeProfileDays.Contains(days) ? days : DefaultVolumeProfileDays;
        }
    }
}
