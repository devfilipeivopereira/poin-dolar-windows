using System;

namespace RtdDolarNative.MarketData
{
    public sealed class TickEvent
    {
        public DateTimeOffset LocalTimestamp { get; set; }
        public string ProfitTime { get; set; }
        public decimal Price { get; set; }
        public decimal? Quantity { get; set; }
        public decimal? Volume { get; set; }
        public decimal Delta { get; set; }
        public string Side { get; set; }
        public decimal? Bid { get; set; }
        public decimal? Ask { get; set; }

        public string LocalTimeText
        {
            get { return LocalTimestamp.ToString("HH:mm:ss.fff"); }
        }
    }
}
