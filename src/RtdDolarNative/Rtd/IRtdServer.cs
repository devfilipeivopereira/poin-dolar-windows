using System.Runtime.InteropServices;

namespace RtdDolarNative.Rtd
{
    [ComImport]
    [Guid("EC0E6191-DB51-11D3-8F3E-00C04F3651B8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface IRtdServer
    {
        [DispId(10)]
        int ServerStart(IRTDUpdateEvent callback);

        [DispId(11)]
        object ConnectData(
            int topicId,
            [MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] ref object[] strings,
            ref bool getNewValues);

        [DispId(12)]
        [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)]
        object[,] RefreshData(ref int topicCount);

        [DispId(13)]
        void DisconnectData(int topicId);

        [DispId(14)]
        int Heartbeat();

        [DispId(15)]
        void ServerTerminate();
    }
}
