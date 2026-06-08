using System;

namespace RtdDolarNative.MarketData
{
    public sealed class MarketState
    {
        private readonly object _lock = new object();
        private readonly MarketSnapshot _snapshot = new MarketSnapshot();

        public MarketSnapshot Update(string asset, string field, object value, string status)
        {
            lock (_lock)
            {
                string normalizedField = field == null ? string.Empty : field.Trim().ToUpperInvariant();
                DateTimeOffset now = DateTimeOffset.Now;
                _snapshot.LocalTimestamp = now;
                _snapshot.Asset = asset;
                _snapshot.Status = status;
                _snapshot.Rtd[normalizedField] = ValueParser.ToJsonValue(value);
                _snapshot.Raw[normalizedField] = ValueParser.ToText(value);
                _snapshot.FieldUpdatedAt[normalizedField] = now;

                return _snapshot.Clone();
            }
        }

        public MarketSnapshot MarkStatus(string status)
        {
            lock (_lock)
            {
                _snapshot.LocalTimestamp = DateTimeOffset.Now;
                _snapshot.Status = status;

                return _snapshot.Clone();
            }
        }
    }
}
