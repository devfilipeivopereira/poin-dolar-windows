namespace RtdDolarNative.Rtd
{
    public sealed class RtdTopic
    {
        public int TopicId { get; set; }
        public string Asset { get; set; }
        public string Topic { get; set; }
        public string Field { get; set; }
        public string RtdField { get; set; }
        public int? Index { get; set; }
        public string InfoField { get; set; }
        public string SourceName { get; set; }
        public string Role { get; set; }
        public object[] Arguments { get; set; }
        public object LastValue { get; set; }

        public string Key
        {
            get { return Asset + ":" + Topic + ":" + Field; }
        }
    }
}
