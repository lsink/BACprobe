using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace BACprobe.Core.Networking;

/// <summary>Windows-only probing of who holds a UDP port.</summary>
public static partial class UdpPortInspector
{
    private const int AfInet = 2;
    private const int UdpTableOwnerPid = 1;

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedUdpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int af, int tableClass, uint reserved);

    public static PortProbe Probe(int port = PreflightRules.BacnetPort)
    {
        var (bindable, error) = TryBindShared(port);
        return new PortProbe(bindable, error, GetOwners(port));
    }

    /// <summary>Same bind the BACnet library does in non-exclusive mode: 0.0.0.0 with ReuseAddress.</summary>
    public static (bool Ok, string? Error) TryBindShared(int port)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            s.Bind(new IPEndPoint(IPAddress.Any, port));
            return (true, null);
        }
        catch (SocketException ex)
        {
            return (false, ex.Message);
        }
    }

    public static IReadOnlyList<PortOwner> GetOwners(int port)
    {
        var owners = new List<PortOwner>();
        if (!OperatingSystem.IsWindows()) return owners;

        var size = 0;
        _ = GetExtendedUdpTable(IntPtr.Zero, ref size, false, AfInet, UdpTableOwnerPid, 0);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedUdpTable(buffer, ref size, false, AfInet, UdpTableOwnerPid, 0) != 0) return owners;
            var count = Marshal.ReadInt32(buffer);
            var row = buffer + 4;
            for (var i = 0; i < count; i++, row += 12)
            {
                var addr = (uint)Marshal.ReadInt32(row);
                // Port is network byte order in the low 16 bits.
                var rawPort = Marshal.ReadInt32(row, 4) & 0xFFFF;
                var localPort = ((rawPort & 0xFF) << 8) | (rawPort >> 8);
                if (localPort != port) continue;
                var pid = Marshal.ReadInt32(row, 8);
                owners.Add(new PortOwner(pid, ProcessName(pid), new IPAddress(addr).ToString()));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return owners;
    }

    private static string ProcessName(int pid)
    {
        if (pid == 4) return "System";
        try { return Process.GetProcessById(pid).ProcessName; }
        catch (Exception) { return "unknown process"; }
    }
}
