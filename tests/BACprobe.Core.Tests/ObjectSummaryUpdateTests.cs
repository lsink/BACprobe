using System.IO.BACnet;
using BACprobe.Core.Browsing;

namespace BACprobe.Core.Tests;

public class ObjectSummaryUpdateTests
{
    private static ObjectSummary Overridden() => new()
    {
        Id = new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, 1),
        PrioritySlots = [new PrioritySlot(8, "50")],
        PriorityArrayRead = true,
    };

    [Fact]
    public void A_follow_up_read_without_a_priority_array_keeps_the_known_override()
    {
        var s = Overridden();
        s.UpdateFrom(new ObjectSummary { Id = s.Id, PresentValue = "50" }); // the array read timed out: slots stay empty, unread
        Assert.True(s.IsOverridden);
    }

    [Fact]
    public void A_follow_up_read_that_found_the_array_empty_clears_the_override()
    {
        var s = Overridden();
        s.UpdateFrom(new ObjectSummary { Id = s.Id, PriorityArrayRead = true });
        Assert.False(s.IsOverridden);
    }
}
