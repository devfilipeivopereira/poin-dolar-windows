using System;
using System.Collections.Generic;

namespace RtdDolarNative.MarketData
{
    public sealed class MarketSnapshot
    {
        public MarketSnapshot()
        {
            LocalTimestamp = DateTimeOffset.Now;
            Status = "starting";
            Rtd = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            Raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public DateTimeOffset LocalTimestamp { get; set; }
        public string Asset { get; set; }
        public string Status { get; set; }
        public Dictionary<string, object> Rtd { get; set; }
        public Dictionary<string, string> Raw { get; set; }

        public string DataProfit
        {
            get { return GetText("DAT"); }
        }

        public string HoraProfit
        {
            get { return GetText("HOR"); }
        }

        public decimal? Ultimo
        {
            get { return GetDecimal("ULT"); }
        }

        public decimal? Volume
        {
            get { return GetDecimal("VOL"); }
        }

        public MarketSnapshot Clone()
        {
            MarketSnapshot clone = new MarketSnapshot();
            clone.LocalTimestamp = LocalTimestamp;
            clone.Asset = Asset;
            clone.Status = Status;
            clone.Rtd = new Dictionary<string, object>(Rtd, StringComparer.OrdinalIgnoreCase);
            clone.Raw = new Dictionary<string, string>(Raw, StringComparer.OrdinalIgnoreCase);
            return clone;
        }

        private decimal? GetDecimal(string field)
        {
            object value;

            if (!Rtd.TryGetValue(field, out value))
            {
                return null;
            }

            return ValueParser.ToDecimal(value);
        }

        private string GetText(string field)
        {
            object value;

            if (!Rtd.TryGetValue(field, out value))
            {
                return null;
            }

            return ValueParser.ToText(value);
        }
    }
}
