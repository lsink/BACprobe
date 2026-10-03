using System.IO.BACnet;
using BACprobe.Core.Discovery;
using BACprobe.Core.Jobs;

namespace BACprobe.Core.Tests;

public class NetworkCheckTests
{
    private static IAmObservation Heard(uint instance, string address, ushort network = 0, string mac = "") =>
        new(instance, address, network, mac, 999);

    // --- conflicts ---

    [Fact]
    public void A_clean_network_has_no_findings()
    {
        var f = NetworkCheck.Analyze([Heard(1, "10.0.0.1:47808"), Heard(2, "10.0.0.2:47808"), Heard(3, "10.0.0.3:47808")]);
        Assert.Empty(f);
        Assert.Equal("No conflicts found", NetworkCheck.Summarize(f));
    }

    [Fact]
    public void The_same_device_announcing_twice_is_not_a_conflict() =>
        Assert.Empty(NetworkCheck.Analyze([Heard(1, "10.0.0.1:47808"), Heard(1, "10.0.0.1:47808"), Heard(1, "10.0.0.1:47808")]));

    [Fact]
    public void Two_addresses_with_one_device_number_is_a_problem_naming_both()
    {
        var f = Assert.Single(NetworkCheck.Analyze([Heard(1001, "10.0.0.1:47808"), Heard(1001, "10.0.0.9:47808"), Heard(1002, "10.0.0.2:47808")]));
        Assert.Equal(FindingSeverity.Problem, f.Severity);
        Assert.Contains("1001", f.Title);
        Assert.Contains("10.0.0.1:47808", f.Detail);
        Assert.Contains("10.0.0.9:47808", f.Detail);
        Assert.NotEmpty(f.LikelyCause);
        Assert.NotEmpty(f.NextStep);
    }

    [Fact]
    public void The_reserved_any_device_number_is_flagged_as_never_commissioned()
    {
        var f = Assert.Single(NetworkCheck.Analyze([Heard(NetworkCheck.UnassignedInstance, "10.0.0.7:47808")]));
        Assert.Equal(FindingSeverity.Problem, f.Severity);
        Assert.Contains("10.0.0.7", f.Title);
        Assert.Contains("unassigned", f.Title);
    }

    [Fact]
    public void One_address_with_several_device_numbers_is_only_a_note_since_gateways_do_this()
    {
        var f = Assert.Single(NetworkCheck.Analyze([Heard(10, "10.0.0.5:47808"), Heard(11, "10.0.0.5:47808"), Heard(12, "10.0.0.5:47808")]));
        Assert.Equal(FindingSeverity.Info, f.Severity);
        Assert.Contains("10, 11, 12", f.Title);
        Assert.Contains("gateway", f.LikelyCause);
    }

    [Fact]
    public void Two_devices_with_one_mac_on_the_same_routed_network_is_a_problem()
    {
        var f = Assert.Single(NetworkCheck.Analyze([
            Heard(21, "net 7 mac 05", 7, "05"), Heard(22, "net 7 mac 05", 7, "05"), Heard(23, "net 7 mac 06", 7, "06")]));
        Assert.Equal(FindingSeverity.Problem, f.Severity);
        Assert.Contains("Network 7", f.Title);
        Assert.Contains("21 and 22", f.Title);
        Assert.Contains("MS/TP", f.LikelyCause);
    }

    [Fact]
    public void The_same_mac_on_different_networks_is_fine() =>
        Assert.Empty(NetworkCheck.Analyze([Heard(21, "net 7 mac 05", 7, "05"), Heard(22, "net 8 mac 05", 8, "05")]));

    [Fact]
    public void Summaries_count_by_severity()
    {
        var f = new List<NetworkFinding>
        {
            new(FindingSeverity.Problem, "a", "", "", ""), new(FindingSeverity.Problem, "b", "", "", ""),
            new(FindingSeverity.Warning, "c", "", "", ""), new(FindingSeverity.Info, "d", "", "", ""),
        };
        Assert.Equal("2 problems, 1 warning, 1 note", NetworkCheck.Summarize(f));
        Assert.Equal("1 problem", NetworkCheck.Summarize([f[0]]));
    }

    // --- comparing with a saved job ---

    private static DiscoveredDevice Live(uint instance, string ip, string? name = null, string? model = null, string? firmware = null) => new()
    {
        InstanceId = instance, Address = new BacnetAddress(BacnetAddressTypes.IP, $"{ip}:47808", 0), MaxApdu = 480,
        Segmentation = BacnetSegmentations.SEGMENTATION_NONE, VendorId = 999,
        ObjectName = name, ModelName = model, FirmwareRevision = firmware,
    };

    private static SavedDevice Saved(DiscoveredDevice d, string name, string? model = null, string? firmware = null) => new(
        d.InstanceId, name, d.AddressText, d.Address.type, d.Address.net, d.Address.adr ?? [], 999, null, model, firmware, 480,
        BacnetSegmentations.SEGMENTATION_NONE, true, []);

    [Fact]
    public void An_unchanged_network_matches_the_job()
    {
        var a = Live(1, "10.0.0.1", "AHU-1", "M1", "1.0");
        Assert.Empty(NetworkCheck.Compare([Saved(a, "AHU-1", "M1", "1.0")], [a]));
    }

    [Fact]
    public void A_device_in_the_job_that_does_not_answer_is_a_warning_with_where_it_was()
    {
        var a = Live(1, "10.0.0.1", "AHU-1");
        var f = Assert.Single(NetworkCheck.Compare([Saved(a, "AHU-1")], []));
        Assert.Equal(FindingSeverity.Warning, f.Severity);
        Assert.Contains("did not answer", f.Title);
        Assert.Contains(a.AddressText, f.Detail);
    }

    [Fact]
    public void A_device_not_in_the_job_is_a_note()
    {
        var f = Assert.Single(NetworkCheck.Compare([], [Live(5, "10.0.0.5", "VAV-5")]));
        Assert.Equal(FindingSeverity.Info, f.Severity);
        Assert.Contains("is new", f.Title);
        Assert.Contains("VAV-5", f.Title);
    }

    [Fact]
    public void A_different_ip_address_means_the_device_moved()
    {
        var before = Live(1, "10.0.0.1", "AHU-1");
        var f = Assert.Single(NetworkCheck.Compare([Saved(before, "AHU-1")], [Live(1, "10.0.0.99", "AHU-1")]));
        Assert.Equal(FindingSeverity.Warning, f.Severity);
        Assert.Contains("moved", f.Title);
        Assert.Contains("10.0.0.99", f.Detail);
    }

    [Fact]
    public void A_changed_port_on_the_same_ip_is_not_a_move()
    {
        var before = Live(1, "10.0.0.1", "AHU-1");
        var restarted = new DiscoveredDevice
        {
            InstanceId = 1, Address = new BacnetAddress(BacnetAddressTypes.IP, "10.0.0.1:50123", 0), MaxApdu = 480,
            Segmentation = BacnetSegmentations.SEGMENTATION_NONE, VendorId = 999, ObjectName = "AHU-1",
        };
        Assert.Empty(NetworkCheck.Compare([Saved(before, "AHU-1")], [restarted]));
    }

    [Fact]
    public void A_rename_and_a_firmware_change_are_notes()
    {
        var before = Live(1, "10.0.0.1", "AHU-1", "M1", "1.0");
        var f = NetworkCheck.Compare([Saved(before, "AHU-1", "M1", "1.0")], [Live(1, "10.0.0.1", "AHU-One", "M1", "1.1")]);
        Assert.Equal(2, f.Count);
        Assert.All(f, x => Assert.Equal(FindingSeverity.Info, x.Severity));
        Assert.Contains(f, x => x.Title.Contains("renamed"));
        Assert.Contains(f, x => x.Detail.Contains("1.1"));
    }

    [Fact]
    public void Unknown_names_and_versions_on_the_live_side_are_not_treated_as_changes()
    {
        var before = Live(1, "10.0.0.1", "AHU-1", "M1", "1.0");
        Assert.Empty(NetworkCheck.Compare([Saved(before, "AHU-1", "M1", "1.0")], [Live(1, "10.0.0.1")])); // not enriched yet
    }

    [Theory]
    [InlineData("10.1.2.3:47808", "10.1.2.3")]
    [InlineData("10.1.2.3", "10.1.2.3")]
    [InlineData("net 7 mac 05", "net 7 mac 05")]
    [InlineData("1001:5", "1001:5")]
    public void Host_strips_the_port_only_from_ip_addresses(string text, string expected) =>
        Assert.Equal(expected, NetworkCheck.HostOf(text));

    [Theory]
    [InlineData(1500u, -1, -1, true)]
    [InlineData(1500u, 1000, 2000, true)]
    [InlineData(1000u, 1000, 2000, true)]
    [InlineData(2000u, 1000, 2000, true)]
    [InlineData(999u, 1000, 2000, false)]
    [InlineData(2001u, 1000, 2000, false)]
    [InlineData(5u, -1, 10, true)]
    [InlineData(4194303u, 100, -1, true)]
    public void Who_is_range_includes_both_ends_and_minus_one_means_no_limit(uint instance, int low, int high, bool expected) =>
        Assert.Equal(expected, BACprobe.Core.Discovery.DiscoveryService.InRange(instance, low, high));
}
