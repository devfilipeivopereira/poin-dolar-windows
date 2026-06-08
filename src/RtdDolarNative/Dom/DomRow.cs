namespace RtdDolarNative.Dom
{
    public sealed class DomRow
    {
        public decimal Price { get; set; }
        public string PriceText { get; set; }
        public string AskVol { get; set; }
        public string BidVol { get; set; }
        public string Markings { get; set; }
        public string Flags { get; set; }
        public string Band { get; set; }
        public bool IsCurrent { get; set; }
        public bool IsBid { get; set; }
        public bool IsAsk { get; set; }
    }
}
