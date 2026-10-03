using System.IO.BACnet;
using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;
using BACprobe.Core.Export;
using BACprobe.Core.Search;

namespace BACprobe.Core.Tests;

public class PointSearchTests
{
    private static ObjectSummary P(BacnetObjectTypes type, uint instance, string name, string? description = null,
        string? value = null, string? units = null, bool overridden = false) => new()
    {
        Id = new BacnetObjectId(type, instance), Name = name, Description = description, PresentValue = value, Units = units,
        PrioritySlots = overridden ? [new PrioritySlot(8, value ?? "1")] : [],
    };

    private static ExportDevice Dev(uint instance, string name, params ObjectSummary[] points)
    {
        var d = new DiscoveredDevice
        {
            InstanceId = instance, Address = new BacnetAddress(BacnetAddressTypes.IP, "10.0.0.1:47808", 0), MaxApdu = 480,
            Segmentation = BacnetSegmentations.SEGMENTATION_NONE, VendorId = 999, ObjectName = name,
        };
        return new ExportDevice(d, name, [P(BacnetObjectTypes.OBJECT_DEVICE, instance, name), .. points]);
    }

    private static readonly ExportDevice Ahu = Dev(1001, "AHU-1",
        P(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1, "Zone Temp", "Zone temperature", "70.1", "°F"),
        P(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 2, "Supply Air Temp", "Discharge air temperature", "55.2", "°F"),
        P(BacnetObjectTypes.OBJECT_BINARY_OUTPUT, 1, "Supply Fan Command", "Fan start/stop", "Active", null, overridden: true));

    private static readonly ExportDevice Vav = Dev(2001, "VAV-12",
        P(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1, "Zone Temperature Sensor", "Room sensor", "68.0", "°F"),
        P(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, 1, "Damper Position", "Supply damper command", "40", "%", overridden: true));

    private static IReadOnlyList<string> Names(IReadOnlyList<PointHit> hits) => [.. hits.Select(h => h.Point.Name!)];

    [Fact]
    public void Every_word_must_match_so_two_words_narrow_the_results()
    {
        var hits = PointSearch.Search([Ahu, Vav], "zone temp");
        Assert.Equal(["Zone Temp", "Zone Temperature Sensor"], Names(hits).Order());
        Assert.DoesNotContain(hits, h => h.Point.Name == "Supply Air Temp");
    }

    [Fact]
    public void Search_is_case_insensitive_and_treats_dashes_and_underscores_as_spaces()
    {
        Assert.Equal(2, PointSearch.Search([Ahu, Vav], "ZONE-TEMP").Count);
        Assert.Equal(2, PointSearch.Search([Ahu, Vav], "zone_temp").Count);
    }

    [Fact]
    public void A_match_in_the_name_ranks_above_one_in_the_description()
    {
        var a = Dev(1, "A", P(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 1, "Setpoint", "Adjusts the fan speed"));
        var b = Dev(2, "B", P(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 1, "Fan Speed", "Speed command"));
        var hits = PointSearch.Search([a, b], "fan");
        Assert.Equal("Fan Speed", hits[0].Point.Name);
        Assert.Equal("Setpoint", hits[1].Point.Name);
        Assert.True(hits[0].Score > hits[1].Score);
    }

    [Fact]
    public void An_exact_name_beats_a_partial_one()
    {
        var hits = PointSearch.Search([Ahu, Vav], "zone temp");
        Assert.Equal("Zone Temp", hits[0].Point.Name);
    }

    [Fact]
    public void A_quoted_phrase_must_match_as_written()
    {
        Assert.Single(PointSearch.Search([Ahu, Vav], "\"supply fan\""));
        Assert.Empty(PointSearch.Search([Ahu, Vav], "\"fan supply\""));
    }

    [Fact]
    public void Device_names_object_labels_units_and_values_are_searchable()
    {
        Assert.Equal(3, PointSearch.Search([Ahu, Vav], "ahu-1").Count);
        Assert.Contains(PointSearch.Search([Ahu, Vav], "binary output"), h => h.Point.Name == "Supply Fan Command");
        Assert.Contains(PointSearch.Search([Ahu, Vav], "ai 2"), h => h.Point.Name == "Supply Air Temp");
        Assert.Single(PointSearch.Search([Ahu, Vav], "55.2"));
        Assert.Equal(["Damper Position"], Names(PointSearch.Search([Ahu, Vav], "damper %")));
    }

    [Fact]
    public void Is_overridden_shows_only_overridden_points_and_combines_with_words()
    {
        Assert.Equal(["Damper Position", "Supply Fan Command"], Names(PointSearch.Search([Ahu, Vav], "is:overridden")).Order());
        Assert.Equal(["Damper Position"], Names(PointSearch.Search([Ahu, Vav], "is:overridden damper")));
        Assert.Empty(PointSearch.Search([Ahu, Vav], "is:overridden zone"));
    }

    [Fact]
    public void The_device_object_itself_is_not_a_search_result() =>
        Assert.DoesNotContain(PointSearch.Search([Ahu, Vav], "ahu"), h => h.Point.Id.type == BacnetObjectTypes.OBJECT_DEVICE);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    public void An_empty_query_finds_nothing_rather_than_everything(string query) =>
        Assert.Empty(PointSearch.Search([Ahu, Vav], query));

    [Fact]
    public void Results_are_capped_and_missing_text_never_crashes()
    {
        var many = Dev(1, "Big", [.. Enumerable.Range(1, 300).Select(i => P(BacnetObjectTypes.OBJECT_ANALOG_VALUE, (uint)i, $"Point {i}"))]);
        Assert.Equal(25, PointSearch.Search([many], "point", max: 25).Count);

        var bare = Dev(2, "Bare", new ObjectSummary { Id = new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 1) }); // no name, no values
        Assert.Empty(PointSearch.Search([bare], "anything"));
    }

    [Fact]
    public void Tokens_split_on_spaces_but_keep_quoted_phrases_together()
    {
        Assert.Equal(["zone", "temp"], PointSearch.Tokens("zone temp"));
        Assert.Equal(["supply fan", "is:overridden"], PointSearch.Tokens("\"supply fan\" is:overridden"));
        Assert.Equal(["unterminated quote"], PointSearch.Tokens("\"unterminated quote"));
        Assert.Empty(PointSearch.Tokens(""));
    }

    [Fact]
    public void Hits_say_which_device_they_came_from()
    {
        var hit = PointSearch.Search([Ahu, Vav], "damper").Single();
        Assert.Equal(2001u, hit.DeviceInstance);
        Assert.Equal("VAV-12", hit.DeviceName);
    }
}
