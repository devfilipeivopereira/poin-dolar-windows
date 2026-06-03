using System;

namespace RtdDolarNative.Csv
{
    public sealed class DailyBar
    {
        public string Asset { get; set; }
        public DateTime Date { get; set; }
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public decimal? Volume { get; set; }
        public decimal? Quantity { get; set; }

        public decimal Range
        {
            get { return High - Low; }
        }

        public string DateText
        {
            get { return Date.ToString("dd/MM/yyyy"); }
        }
    }
}
