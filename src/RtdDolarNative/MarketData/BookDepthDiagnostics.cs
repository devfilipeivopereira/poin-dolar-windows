using System;
using System.Collections.Generic;

namespace RtdDolarNative.MarketData
{
    public sealed class BookDepthDiagnosticResult
    {
        public int RawValueCount { get; set; }
        public int ErrorValueCount { get; set; }
        public int DisplayValueCount { get; set; }
        public int DisplayRowCount { get; set; }
        public string FirstErrorText { get; set; }

        public bool HasDisplayData
        {
            get { return DisplayRowCount > 0; }
        }

        public bool HasOnlyErrors
        {
            get { return RawValueCount > 0 && ErrorValueCount == RawValueCount && DisplayValueCount == 0; }
        }
    }

    public static class BookDepthDiagnostics
    {
        private static readonly string[] Fields = new[]
        {
            "HORC",
            "ACP",
            "VOC",
            "OCP",
            "OVD",
            "VOV",
            "AVD",
            "HORV"
        };

        public static BookDepthDiagnosticResult Inspect(MarketSnapshot snapshot)
        {
            return Inspect(snapshot, 49);
        }

        public static BookDepthDiagnosticResult Inspect(MarketSnapshot snapshot, int maxIndex)
        {
            BookDepthDiagnosticResult result = new BookDepthDiagnosticResult();

            if (snapshot == null || snapshot.Raw == null)
            {
                return result;
            }

            HashSet<int> displayRows = new HashSet<int>();
            int lastIndex = Math.Max(0, maxIndex);

            for (int index = 0; index <= lastIndex; index++)
            {
                foreach (string field in Fields)
                {
                    string raw;
                    if (!snapshot.Raw.TryGetValue(BookField(field, index), out raw) || string.IsNullOrWhiteSpace(raw))
                    {
                        continue;
                    }

                    result.RawValueCount++;

                    string trimmed = raw.Trim();
                    if (RtdValueSanitizer.IsRtdErrorText(trimmed))
                    {
                        result.ErrorValueCount++;
                        if (string.IsNullOrWhiteSpace(result.FirstErrorText))
                        {
                            result.FirstErrorText = trimmed;
                        }

                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(RtdValueSanitizer.CleanDisplayText(trimmed)))
                    {
                        result.DisplayValueCount++;
                        displayRows.Add(index);
                    }
                }
            }

            result.DisplayRowCount = displayRows.Count;
            return result;
        }

        private static string BookField(string field, int index)
        {
            return "BOOK_" + field + "_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
