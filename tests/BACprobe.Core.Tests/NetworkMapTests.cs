using System.IO.BACnet;
using BACprobe.Core.Discovery;

namespace BACprobe.Core.Tests;

public class NetworkMapTests
{
    private static BacnetAddress Local(string ip = "10.0.0.5") => new(BacnetAddressTypes.IP, $"{ip}:47808", 0);

    private static BacnetAddress Behind(string routerIp, ushort network, byte mac)
    {
        var a = Local(routerIp);
        a.RoutedSource = new BacnetAddress(BacnetAddressTypes.MSTP, network, [mac]);
        return a;
    }

    private static DiscoveredDevice Dev(uint instance, BacnetAddress address) => new()
    {
        InstanceId = instance, Address = address, MaxApdu = 480,
        Segmentation = BacnetSegmentations.SEGMENTATION_NONE, VendorId = 999,
    };

    // --- addresses ---

    [Fact]
    public void A_plain_ip_device_is_on_network_zero_with_no_mac()
    {
        var a = Local("10.0.0.5");
        Assert.False(AddressInfo.IsRouted(a));
        Assert.Equal(0, AddressInfo.NetworkOf(a));
        Assert.Equal("", AddressInfo.MacOf(a));
        Assert.Equal(a.ToString(), AddressInfo.Describe(a));
    }

    [Fact]
    public void A_device_behind_a_router_reports_its_own_network_and_mac_and_the_router_it_came_through()
    {
        var a = Behind("10.0.0.9", 1001, 5);
        Assert.True(AddressInfo.IsRouted(a));
        Assert.Equal(1001, AddressInfo.NetworkOf(a));
        Assert.Equal("5", AddressInfo.MacOf(a)); // an MS/TP MAC is shown as a plain number
        var text = AddressInfo.Describe(a);
        Assert.Contains("network 1001", text);
        Assert.Contains("MAC 5", text);
        Assert.Contains("10.0.0.9", text);
    }

    [Fact]
    public void Devices_behind_one_router_have_different_descriptions_so_they_are_not_mistaken_for_one_device()
    {
        Assert.NotEqual(AddressInfo.Describe(Behind("10.0.0.9", 1001, 1)), AddressInfo.Describe(Behind("10.0.0.9", 1001, 2)));
        Assert.NotEqual(Dev(1, Behind("10.0.0.9", 1001, 1)).AddressText, Dev(2, Behind("10.0.0.9", 1002, 1)).AddressText);
    }

    // --- router announcements ---

    [Fact]
    public void A_router_announcement_is_a_list_of_big_endian_network_numbers()
    {
        // The bytes the simulator's router really sent: 03E9 03EA = networks 1001 and 1002.
        Assert.Equal<ushort>([1001, 1002], RouterAnnouncement.ParseNetworks([0x03, 0xE9, 0x03, 0xEA]));
    }

    [Fact]
    public void Odd_trailing_bytes_zeros_and_repeats_are_tolerated()
    {
        Assert.Equal<ushort>([7], RouterAnnouncement.ParseNetworks([0x00, 0x07, 0x00, 0x07, 0x00, 0x00, 0x05]));
        Assert.Empty(RouterAnnouncement.ParseNetworks([]));
        Assert.Empty(RouterAnnouncement.ParseNetworks([0x01]));
    }

    // --- the map ---

    private static RouterObservation Router(string address, params ushort[] networks) => new(address, networks);

    [Fact]
    public void Devices_are_grouped_by_network_with_local_first()
    {
        var map = NetworkMapBuilder.Build(
            [Dev(1, Local()), Dev(2, Local("10.0.0.6")), Dev(11, Behind("10.0.0.9", 1001, 1)), Dev(12, Behind("10.0.0.9", 1001, 2))],
            [Router("10.0.0.9:47808", 1001)]);
        Assert.Equal<ushort>([0, 1001], map.Networks.Select(n => n.Number));
        Assert.Equal(2, map.Networks[0].Devices.Count);
        Assert.Equal(2, map.Networks[1].Devices.Count);
        Assert.Equal(["10.0.0.9:47808"], map.Networks[1].Routers);
        Assert.Empty(map.Findings);
    }

    [Fact]
    public void Lines_describe_each_network_in_plain_words()
    {
        var map = NetworkMapBuilder.Build([Dev(1, Local()), Dev(11, Behind("10.0.0.9", 1001, 1))], [Router("10.0.0.9:47808", 1001, 1002)], scanWasFiltered: true);
        var lines = map.Lines("10.0.0.0/24");
        Assert.Equal("This network (10.0.0.0/24): 1 device", lines[0]);
        Assert.Equal("Network 1001 (via 10.0.0.9:47808): 1 device", lines[1]);
        Assert.Equal("Network 1002 (via 10.0.0.9:47808): no devices answered", lines[2]);
    }

    [Fact]
    public void With_no_routers_and_nothing_routed_it_says_everything_is_on_this_network()
    {
        var lines = NetworkMapBuilder.Build([Dev(1, Local())], []).Lines("10.0.0.0/24");
        Assert.Equal(2, lines.Count);
        Assert.Contains("everything found is on your own network", lines[1]);
    }

    [Fact]
    public void A_network_announced_but_with_no_devices_is_a_wiring_hint()
    {
        var map = NetworkMapBuilder.Build([Dev(1, Local())], [Router("10.0.0.9:47808", 1001)]);
        var f = Assert.Single(map.Findings);
        Assert.Equal(FindingSeverity.Warning, f.Severity);
        Assert.Contains("network 1001", f.Title);
        Assert.Contains("no devices on it answered", f.Title);
        Assert.Contains("baud", f.LikelyCause);
        Assert.NotEmpty(f.NextStep);
    }

    [Fact]
    public void An_empty_network_proves_nothing_when_the_scan_was_limited_to_a_range()
    {
        var map = NetworkMapBuilder.Build([Dev(1, Local())], [Router("10.0.0.9:47808", 1001)], scanWasFiltered: true);
        Assert.Empty(map.Findings);
    }

    [Fact]
    public void Devices_on_a_network_nobody_announced_are_a_warning_not_a_failure()
    {
        var map = NetworkMapBuilder.Build([Dev(11, Behind("10.0.0.9", 1001, 1))], []);
        var f = Assert.Single(map.Findings);
        Assert.Equal(FindingSeverity.Warning, f.Severity);
        Assert.Contains("no router announced it", f.Title);
        Assert.Contains("11", f.Detail);
    }

    [Fact]
    public void Two_routers_announcing_one_network_number_is_flagged_with_both_explanations()
    {
        var map = NetworkMapBuilder.Build([Dev(11, Behind("10.0.0.9", 1001, 1))],
            [Router("10.0.0.9:47808", 1001), Router("10.0.0.10:47808", 1001)]);
        var f = Assert.Single(map.Findings);
        Assert.Contains("announced by 2 routers", f.Title);
        Assert.Contains("10.0.0.9:47808", f.Detail);
        Assert.Contains("10.0.0.10:47808", f.Detail);
        Assert.Contains("redundant", f.LikelyCause);
        Assert.Contains("separate trunks", f.LikelyCause);
    }

    [Fact]
    public void The_same_router_repeating_itself_is_not_two_routers()
    {
        var map = NetworkMapBuilder.Build([Dev(11, Behind("10.0.0.9", 1001, 1))],
            [Router("10.0.0.9:47808", 1001), Router("10.0.0.9:47808", 1001)]);
        Assert.Empty(map.Findings);
    }

    [Fact]
    public void Device_conflict_analysis_sees_routed_devices_by_network_and_mac_not_by_the_router_address()
    {
        // Two routed devices through ONE router address used to look like "several devices at one address".
        var heard = new[] { Behind("10.0.0.9", 1001, 1), Behind("10.0.0.9", 1001, 2) }
            .Select((a, i) => new IAmObservation((uint)(11 + i), AddressInfo.Describe(a), AddressInfo.NetworkOf(a), AddressInfo.MacOf(a), 999));
        Assert.Empty(NetworkCheck.Analyze(heard));

        var clash = new[] { Behind("10.0.0.9", 1001, 5), Behind("10.0.0.9", 1001, 5) }
            .Select((a, i) => new IAmObservation((uint)(11 + i), AddressInfo.Describe(a) + i, AddressInfo.NetworkOf(a), AddressInfo.MacOf(a), 999));
        Assert.Contains(NetworkCheck.Analyze(clash), f => f.Severity == FindingSeverity.Problem && f.Title.Contains("share the same MAC"));
    }
}
