using System.IO.BACnet;
using BACprobe.Core.Discovery;

namespace BACprobe.Core.Tests;

public class ReadLanesTests
{
    private static DiscoveredDevice Ip(uint instance) => new()
    {
        InstanceId = instance, Address = new BacnetAddress(BacnetAddressTypes.IP, $"10.0.0.{instance % 250}:47808"), MaxApdu = 1476,
        Segmentation = BacnetSegmentations.SEGMENTATION_BOTH, VendorId = 5,
    };

    /// <summary>A device behind a BACnet router: the router's IP address, plus the real network and MAC.</summary>
    private static DiscoveredDevice Routed(uint instance, ushort network, byte mac)
    {
        var a = new BacnetAddress(BacnetAddressTypes.IP, "10.0.0.200:47808") { RoutedSource = new BacnetAddress(BacnetAddressTypes.MSTP, network, [mac]) };
        return new DiscoveredDevice { InstanceId = instance, Address = a, MaxApdu = 480, Segmentation = BacnetSegmentations.SEGMENTATION_NONE, VendorId = 5 };
    }

    [Fact]
    public void Ip_devices_get_a_lane_each_and_a_trunk_shares_one()
    {
        var devices = new[] { Ip(1), Routed(101, 2001, 1), Ip(2), Routed(102, 2001, 2), Routed(201, 2002, 1) };
        var lanes = ReadLanes.Plan(devices, sharedMedium: false);
        Assert.Equal([[1u], [101u, 102u], [2u], [201u]], lanes.Select(l => l.Select(d => d.InstanceId).ToList()));
    }

    [Fact]
    public void Over_mstp_everything_shares_the_one_trunk()
    {
        var devices = new[] { Ip(1), Ip(2), Ip(3) };
        Assert.Single(ReadLanes.Plan(devices, sharedMedium: true));
        Assert.Empty(ReadLanes.Plan([], sharedMedium: true));
        Assert.Equal("one device at a time", ReadLanes.Describe(ReadLanes.Plan(devices, sharedMedium: true)));
        Assert.Equal("3 devices at a time", ReadLanes.Describe(ReadLanes.Plan(devices, sharedMedium: false)));
        Assert.Equal("4 devices at a time", ReadLanes.Describe(ReadLanes.Plan([.. Enumerable.Range(1, 9).Select(i => Ip((uint)i))], false)));
    }

    [Fact]
    public async Task Results_keep_the_device_order_and_one_failure_does_not_stop_the_rest()
    {
        var devices = new[] { Ip(1), Ip(2), Ip(3), Routed(101, 2001, 1) };
        var results = await ReadLanes.RunAsync(devices, false, async d =>
        {
            await Task.Delay(d.InstanceId == 1 ? 60 : 5); // the first device is the slowest: its result must still come first
            return d.InstanceId == 2 ? throw new TimeoutException("Wait Timeout") : d.InstanceId * 10;
        });
        Assert.Equal([1u, 2u, 3u, 101u], results.Select(r => r.Device.InstanceId));
        Assert.Equal(10u, results[0].Value);
        Assert.IsType<TimeoutException>(results[1].Error);
        Assert.Equal(1010u, results[3].Value);
    }

    [Fact]
    public async Task No_more_than_the_limit_run_at_once_and_a_trunk_is_read_one_device_at_a_time()
    {
        var devices = new List<DiscoveredDevice> { Routed(101, 2001, 1), Routed(102, 2001, 2), Routed(103, 2001, 3) };
        devices.AddRange(Enumerable.Range(1, 8).Select(i => Ip((uint)i)));
        int running = 0, peak = 0, onTrunk = 0, trunkPeak = 0;
        await ReadLanes.RunAsync(devices, false, async d =>
        {
            var behindRouter = d.Network != 0;
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            if (behindRouter) InterlockedMax(ref trunkPeak, Interlocked.Increment(ref onTrunk));
            await Task.Delay(20);
            if (behindRouter) Interlocked.Decrement(ref onTrunk);
            Interlocked.Decrement(ref running);
            return 0;
        }, maxParallel: 3);
        Assert.True(peak <= 3, $"peak {peak}");
        Assert.True(peak >= 2, "lanes should overlap");
        Assert.Equal(1, trunkPeak); // the three devices behind network 2001 never overlap
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }
}
