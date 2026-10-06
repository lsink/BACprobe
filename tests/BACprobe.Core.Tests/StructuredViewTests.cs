using System.IO.BACnet;
using BACprobe.Core.Browsing;
using BACprobe.Core.Simulation;

namespace BACprobe.Core.Tests;

public class StructuredViewTests
{
    private static BacnetObjectId Sv(uint i) => new(BacnetObjectTypes.OBJECT_STRUCTURED_VIEW, i);
    private static BacnetObjectId Ai(uint i) => new(BacnetObjectTypes.OBJECT_ANALOG_INPUT, i);
    private static BacnetValue Oid(BacnetObjectId id) => new(BacnetApplicationTags.BACNET_APPLICATION_TAG_OBJECT_ID, id);
    private static ObjectSummary Point(BacnetObjectId id, string name) => new() { Id = id, Name = name, PresentValue = "70" };

    [Fact]
    public void A_device_in_front_of_an_entry_says_it_lives_elsewhere()
    {
        var subs = StructureTree.ParseSubordinates([Oid(Ai(1)), Oid(new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, 2002)), Oid(Ai(7)), Oid(Sv(2))]);
        Assert.Equal([new SubordinateRef(Ai(1)), new SubordinateRef(Ai(7), 2002), new SubordinateRef(Sv(2))], subs);
        Assert.Empty(StructureTree.ParseSubordinates(null));
    }

    [Fact]
    public void Views_nest_and_points_outside_every_view_are_still_shown()
    {
        var points = new[] { Point(Ai(1), "Zone Temp"), Point(Ai(2), "DAT"), Point(Ai(3), "Outside Air"), Point(Sv(1), "AHU"), Point(Sv(2), "Temps") };
        var views = new Dictionary<BacnetObjectId, IReadOnlyList<SubordinateRef>>
        {
            [Sv(1)] = [new(Sv(2)), new(Ai(2)), new(Ai(9), 2002)],
            [Sv(2)] = [new(Ai(1))],
        };
        var tree = StructureTree.Build(points, views);

        Assert.Equal(["AHU", "Not in any view"], tree.Select(n => n.Label));
        var ahu = tree[0];
        Assert.Equal(["Temps", "DAT", "Analog Input 9 on device 2002"], ahu.Children.Select(c => c.Label));
        Assert.Equal("Zone Temp", Assert.Single(ahu.Children[0].Children).Label);
        Assert.Equal(2002u, ahu.Children[2].OtherDevice);
        Assert.Equal("Outside Air", Assert.Single(tree[1].Children).Label);
        Assert.Equal("AHU (3)", ahu.Text);
    }

    [Fact]
    public void A_loop_of_views_is_cut_where_it_closes()
    {
        var views = new Dictionary<BacnetObjectId, IReadOnlyList<SubordinateRef>>
        {
            [Sv(1)] = [new(Sv(2))],
            [Sv(2)] = [new(Sv(1)), new(Ai(1))],
        };
        var tree = StructureTree.Build([Point(Ai(1), "Zone Temp")], views);
        Assert.NotEmpty(tree); // every view is inside another: it still starts somewhere, and does not recurse forever
        Assert.Contains(StructureTree.Lines(tree), l => l.Contains("Zone Temp"));
    }

    [Fact]
    public void No_views_means_no_tree()
    {
        Assert.Empty(StructureTree.Build([Point(Ai(1), "Zone Temp")], new Dictionary<BacnetObjectId, IReadOnlyList<SubordinateRef>>()));
    }

    [Fact]
    public void The_simulators_views_are_encoded_as_devices_send_them()
    {
        var m = SimulatedDeviceModel.CreateSample(1001);
        Assert.True(m.TryRead(Sv(1), BacnetPropertyIds.PROP_SUBORDINATE_LIST, uint.MaxValue, out var values, out _));
        Assert.Equal(3, values.Count);
        Assert.All(values, v => Assert.Equal(BacnetApplicationTags.BACNET_APPLICATION_TAG_CONTEXT_SPECIFIC_ENCODED, v.Tag));
        Assert.Equal([0x1C, 0x07, 0x40, 0x00, 0x02], (byte[])values[0].Value); // context tag 1, Structured View (type 29) 2
    }
}
