using System.IO.BACnet;
using BACprobe.Core.Networking;

namespace BACprobe.Core.Simulation;

/// <summary>One network behind the simulated router and how many devices it presents on it.</summary>
public sealed record SimNetwork(ushort Number, int DeviceCount);

/// <summary>
/// A pretend BACnet router for testing the network view without hardware. It answers Who-Is-Router-To-Network with the
/// networks it serves, and answers Who-Is on behalf of virtual devices "behind" it (I-Am carrying a source network and MAC).
/// The virtual devices cannot be read: this only exercises discovery and the network map.
/// </summary>
public sealed class SimulatedRouter : IDisposable
{
    private readonly BacnetClient _client;
    private readonly IReadOnlyList<SimNetwork> _networks;
    private readonly int _routerIndex;

    public Action<string>? Log { get; set; }

    public SimulatedRouter(AdapterInfo adapter, IReadOnlyList<SimNetwork> networks, int routerIndex = 0, int port = PreflightRules.BacnetPort)
    {
        _networks = networks;
        _routerIndex = routerIndex;
        var transport = new BacnetIpUdpProtocolTransport(port, useExclusivePort: false, localEndpointIp: adapter.Address.ToString());
        _client = new BacnetClient(transport);
        _client.OnWhoIsRouterToNetworkMessage += OnWhoIsRouter;
        _client.OnWhoIs += OnWhoIs;
    }

    /// <summary>Device number of the n-th virtual device on a network. Each router gets its own block so two routers never clash.</summary>
    public static uint VirtualInstance(ushort network, int index, int routerIndex = 0) => (uint)(routerIndex * 1_000_000 + network * 10 + index + 1);

    public void Start()
    {
        _client.Start();
        _client.SendIAmRouterToNetwork([.. _networks.Select(n => n.Number)]); // announce, like a router powering up
    }

    private void OnWhoIsRouter(BacnetClient sender, BacnetAddress adr, BacnetNpduControls npduFunction, byte[] buffer, int offset, int messageLength)
    {
        Log?.Invoke($"[router] Who-Is-Router-To-Network from {adr} -> I-Am-Router ({string.Join(", ", _networks.Select(n => n.Number))})");
        sender.SendIAmRouterToNetwork([.. _networks.Select(n => n.Number)]);
    }

    private void OnWhoIs(BacnetClient sender, BacnetAddress adr, int low, int high)
    {
        foreach (var net in _networks)
            for (var i = 0; i < net.DeviceCount; i++)
            {
                var instance = VirtualInstance(net.Number, i, _routerIndex);
                if ((low >= 0 && instance < low) || (high >= 0 && instance > high)) continue;
                var behind = new BacnetAddress(BacnetAddressTypes.MSTP, net.Number, [(byte)(i + 1)]); // network, and a one-byte MS/TP MAC
                Log?.Invoke($"[router] Who-Is from {adr} -> I-Am for device {instance} on network {net.Number}");
                sender.Iam(instance, BacnetSegmentations.SEGMENTATION_NONE, null, behind);
            }
    }

    public void Dispose() => _client.Dispose();
}
