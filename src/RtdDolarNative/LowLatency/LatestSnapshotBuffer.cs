using RtdDolarNative.MarketData;

namespace RtdDolarNative.LowLatency
{
    public sealed class LatestSnapshotBuffer
    {
        private readonly object _lock = new object();
        private MarketSnapshot _latest;
        private long _version;

        public long Publish(MarketSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return CurrentVersion;
            }

            lock (_lock)
            {
                _latest = snapshot.Clone();
                _version++;
                return _version;
            }
        }

        public bool TryRead(out MarketSnapshot snapshot, out long version)
        {
            lock (_lock)
            {
                if (_latest == null)
                {
                    snapshot = null;
                    version = _version;
                    return false;
                }

                snapshot = _latest.Clone();
                version = _version;
                return true;
            }
        }

        public long CurrentVersion
        {
            get
            {
                lock (_lock)
                {
                    return _version;
                }
            }
        }
    }
}
