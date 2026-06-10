using System;

namespace RtdDolarNative.MarketData
{
    public sealed class IntradayBar
    {
        public string Asset { get; set; }
        public DateTimeOffset Start { get; set; }
        public int Seconds { get; set; }
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public decimal Volume { get; set; }
        public decimal Quantity { get; set; }
        public int TradeCount { get; set; }
        public decimal Delta { get; set; }
    }
}
