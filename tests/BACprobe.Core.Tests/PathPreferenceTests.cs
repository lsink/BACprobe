using System.IO.BACnet;
using BACprobe.Core.Discovery;

namespace BACprobe.Core.Tests;

public class PathPreferenceTests
{
    private static BacnetAddress Direct(string ip) => new(BacnetAddressTypes.IP, ip);
    private static BacnetAddress Routed() =>
        new(BacnetAddressTypes.IP, "10.0.0.200:47808") { RoutedSource = new BacnetAddress(BacnetAddressTypes.MSTP, 1001, [5]) };

    [Fact]
    public void A_direct_answer_replaces_a_routed_one() =>
        Assert.True(DiscoveryService.PrefersNewPath(Routed(), Direct("10.0.0.5:47808")));

    [Fact]
    public void A_routed_answer_never_replaces_a_direct_one() =>
        Assert.False(DiscoveryService.PrefersNewPath(Direct("10.0.0.5:47808"), Routed()));

    [Fact]
    public void Between_two_direct_answers_the_first_stays() =>
        Assert.False(DiscoveryService.PrefersNewPath(Direct("10.0.0.5:47808"), Direct("10.0.1.5:47808")));
}
