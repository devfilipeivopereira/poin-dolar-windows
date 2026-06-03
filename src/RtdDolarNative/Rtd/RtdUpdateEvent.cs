using System.Runtime.InteropServices;
using System.Threading;

namespace RtdDolarNative.Rtd
{
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class RtdUpdateEvent : IRTDUpdateEvent
    {
        private int _updatePending;

        public RtdUpdateEvent()
        {
            HeartbeatInterval = 1000;
        }

        public int HeartbeatInterval { get; set; }

        public void UpdateNotify()
        {
            Interlocked.Exchange(ref _updatePending, 1);
        }

        public void Disconnect()
        {
        }

        public bool ConsumeUpdate()
        {
            return Interlocked.Exchange(ref _updatePending, 0) == 1;
        }
    }
}
