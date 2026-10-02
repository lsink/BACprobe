using System.IO.BACnet;
using BACprobe.Core.Discovery;
using BACprobe.Core.Writing;

namespace BACprobe.Core.Tests;

public class PromptsTests
{
    private static DiscoveredDevice Dev(uint instance) => new()
    {
        InstanceId = instance, Address = null!, MaxApdu = 480,
        Segmentation = BacnetSegmentations.SEGMENTATION_NONE, VendorId = 999,
    };

    private static TrackedOverride Override(uint device, string deviceName, uint instance, string point, int priority, string value) =>
        new(Dev(device), deviceName, new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, instance), point, priority, value);

    [Fact]
    public void One_override_is_described_in_the_singular_with_device_value_and_priority_name()
    {
        var p = Prompts.OverridesInPlace([Override(1003, "SIM-VAV-1003", 1, "Damper Position", 8, "25")]);
        Assert.Equal("You left 1 override in place", p.Headline);
        Assert.Equal("Overrides still in place", p.Title);
        var row = Assert.Single(p.Facts);
        Assert.Equal("Damper Position", row.Label);
        Assert.Equal("SIM-VAV-1003 (device 1003): 25 at priority 8 (Manual Operator)", row.Value);
        Assert.Contains("will not run that point automatically", p.Body);
        Assert.Contains("Release it now", p.Body);
        Assert.Null(p.Warning);
    }

    [Fact]
    public void Several_overrides_use_the_plural_and_list_each_one()
    {
        var p = Prompts.OverridesInPlace(
        [
            Override(1001, "A", 1, "Damper", 8, "25"),
            Override(1002, "B", 2, "Fan", 6, "Active"),
        ]);
        Assert.Equal("You left 2 overrides in place", p.Headline);
        Assert.Equal(2, p.Facts.Count);
        Assert.Contains("those points", p.Body);
        Assert.Contains("Release them now", p.Body);
        Assert.Contains("priority 6 (Minimum On/Off)", p.Facts[1].Value);
    }

    [Fact]
    public void A_long_list_is_cut_off_with_a_count_of_the_rest()
    {
        var many = Enumerable.Range(1, 12).Select(i => Override(1001, "A", (uint)i, $"Point {i}", 8, "1")).ToList();
        var p = Prompts.OverridesInPlace(many);
        Assert.Equal("You left 12 overrides in place", p.Headline);
        Assert.Equal(9, p.Facts.Count); // 8 shown + the "and N more" line
        Assert.Equal("...and 4 more", p.Facts[^1].Value);
    }

    [Theory]
    [InlineData(1, "1 override is still in place")]
    [InlineData(3, "3 overrides are still in place")]
    public void Failed_release_prompt_gives_cause_next_step_and_a_warning(int failed, string headline)
    {
        var p = Prompts.ReleaseFailed(failed);
        Assert.Equal(headline, p.Headline);
        Assert.Contains("Likely cause", p.Body);
        Assert.Contains("Next step", p.Body);
        Assert.NotNull(p.Warning);
    }

    [Fact]
    public void Write_prompt_carries_the_same_facts_consequence_and_warning_as_the_request()
    {
        var req = new WriteRequest(Dev(1003), "SIM-VAV-1003",
            new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, 1), "Damper Position",
            new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, 25f), "25", 1, "50 %");
        var p = Prompts.ForWrite(req);
        Assert.Equal("Confirm write", p.Title);
        Assert.Equal(req.Headline, p.Headline);
        Assert.Equal(req.Facts, p.Facts);
        Assert.Equal(req.Consequence, p.Body);
        Assert.Equal(req.Warning, p.Warning);
        Assert.NotNull(p.Warning); // priority 1
        Assert.Equal("Write at priority 1", req.ConfirmLabel);
    }

    [Fact]
    public void Release_prompt_is_titled_and_labelled_as_a_release()
    {
        var req = new WriteRequest(Dev(1003), "D", new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, 1), "P", null, "release", 8);
        Assert.Equal("Release override", Prompts.ForWrite(req).Title);
        Assert.Equal("Release", req.ConfirmLabel);
    }
}
