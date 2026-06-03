namespace RtdDolarNative.Rtd
{
    public sealed class RtdTopic
    {
        public int TopicId { get; set; }
        public string Asset { get; set; }
        public string Field { get; set; }
        public object LastValue { get; set; }

        public string Key
        {
            get { return Asset + ":" + Field; }
        }
    }
}
