using System;
using System.Collections.Generic;
using System.Windows;

namespace RtdDolarNative.Charts
{
    public static class HeatmapBadgeLayout
    {
        public static List<Rect> Calculate(double availableWidth, int badgeCount, double badgeWidth, double badgeHeight, double gap, double padding, double firstRowReservedLeft)
        {
            List<Rect> rects = new List<Rect>();

            if (badgeCount <= 0 || availableWidth <= 0d || badgeHeight <= 0d)
            {
                return rects;
            }

            double safePadding = Math.Max(0d, padding);
            double safeGap = Math.Max(0d, gap);
            double safeBadgeWidth = Math.Max(24d, badgeWidth);
            double right = Math.Max(safePadding, availableWidth - safePadding);
            int row = 0;
            int rowCount = 0;
            double x = right - safeBadgeWidth;

            for (int i = 0; i < badgeCount; i++)
            {
                double rowLeft = row == 0 ? Math.Max(safePadding, firstRowReservedLeft) : safePadding;

                if (x < rowLeft && rowCount > 0)
                {
                    row++;
                    rowCount = 0;
                    x = right - safeBadgeWidth;
                    rowLeft = safePadding;
                }

                double width = safeBadgeWidth;

                if (x < rowLeft)
                {
                    x = rowLeft;
                    width = Math.Max(0d, right - rowLeft);
                }

                rects.Add(new Rect(x, safePadding + row * (badgeHeight + safeGap), width, badgeHeight));
                x -= safeBadgeWidth + safeGap;
                rowCount++;
            }

            return rects;
        }
    }
}
