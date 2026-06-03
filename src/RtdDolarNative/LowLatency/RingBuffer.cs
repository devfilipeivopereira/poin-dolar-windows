using System.Collections.Generic;
using System.Linq;

namespace RtdDolarNative.LowLatency
{
    public sealed class RingBuffer<T>
    {
        private readonly object _lock = new object();
        private readonly Queue<T> _items;
        private readonly int _capacity;

        public RingBuffer(int capacity)
        {
            _capacity = capacity < 1 ? 1 : capacity;
            _items = new Queue<T>(_capacity);
        }

        public void Add(T item)
        {
            lock (_lock)
            {
                while (_items.Count >= _capacity)
                {
                    _items.Dequeue();
                }

                _items.Enqueue(item);
            }
        }

        public List<T> SnapshotNewestFirst()
        {
            lock (_lock)
            {
                return _items.Reverse().ToList();
            }
        }

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _items.Count;
                }
            }
        }
    }
}
