using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace HerdMe.Windows.Services;

public sealed record PortConflictDetails(
    int Port,
    bool InUse,
    int? ProcessId,
    string ProcessName
);

public static class PortConflictInspector
{
    private const int AddressFamilyInternet = 2;
    private const int InsufficientBuffer = 122;
    private const int TcpTableOwnerPidListener = 3;

    public static PortConflictDetails Inspect(int port)
    {
        if (port is <= 0 or > 65_535) throw new ArgumentOutOfRangeException(nameof(port));
        if (WindowsServiceManager.IsPortAvailable(port))
        {
            return new PortConflictDetails(port, false, null, string.Empty);
        }
        var processId = OperatingSystem.IsWindows() ? ListenerProcessId(port) : null;
        var name = processId is { } id ? ProcessName(id) : string.Empty;
        return new PortConflictDetails(port, true, processId, name);
    }

    private static int? ListenerProcessId(int port)
    {
        var size = 0;
        var status = GetExtendedTcpTable(
            IntPtr.Zero, ref size, true, AddressFamilyInternet, TcpTableOwnerPidListener, 0
        );
        if (status != InsufficientBuffer || size <= sizeof(int)) return null;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            status = GetExtendedTcpTable(
                buffer, ref size, true, AddressFamilyInternet, TcpTableOwnerPidListener, 0
            );
            if (status != 0) return null;
            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var rowAddress = IntPtr.Add(buffer, sizeof(int));
            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(
                    IntPtr.Add(rowAddress, index * rowSize)
                );
                if (Port(row.LocalPort) == port) return checked((int)row.OwningPid);
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static int Port(uint networkOrderPort)
    {
        var bytes = BitConverter.GetBytes(networkOrderPort);
        return IPAddress.NetworkToHostOrder(BitConverter.ToInt16(bytes, 0)) & 0xffff;
    }

    private static string ProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return string.Empty;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(
        IntPtr table,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        int tableClass,
        uint reserved
    );
}
