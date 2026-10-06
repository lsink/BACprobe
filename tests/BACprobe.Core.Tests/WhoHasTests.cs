using System.IO.BACnet;
using System.IO.BACnet.Serialize;
using BACprobe.Core.Discovery;
using BACprobe.Core.Simulation;

namespace BACprobe.Core.Tests;

public class WhoHasTests
{
    private static readonly BacnetObjectId Ao1 = new(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, 1);

    [Fact]
    public void An_I_Have_from_the_library_decodes()
    {
        var buffer = new EncodeBuffer();
        Services.EncodeIhaveBroadcast(buffer, new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, 1002), Ao1, "Damper Position");

        Assert.True(IHaveCodec.TryDecode(buffer.buffer, 0, buffer.offset, out var device, out var obj, out var name));
        Assert.Equal(1002u, device);
        Assert.Equal(Ao1, obj);
        Assert.Equal("Damper Position", name);
    }

    [Fact]
    public void Junk_and_cut_off_messages_are_ignored()
    {
        var buffer = new EncodeBuffer();
        Services.EncodeIhaveBroadcast(buffer, new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, 1002), Ao1, "Damper Position");
        Assert.False(IHaveCodec.TryDecode(buffer.buffer, 0, 7, out _, out _, out _)); // cut short
        Assert.False(IHaveCodec.TryDecode([0x21, 0x05, 0x21, 0x05], 0, 4, out _, out _, out _)); // two unsigned numbers
        var notDevice = new EncodeBuffer();
        Services.EncodeIhaveBroadcast(notDevice, Ao1, Ao1, "x"); // first identifier must be a device
        Assert.False(IHaveCodec.TryDecode(notDevice.buffer, 0, notDevice.offset, out _, out _, out _));
    }

    [Theory]
    [InlineData("ai:1", true)]
    [InlineData("AI 1", true)]
    [InlineData("Zone Temp", false)]
    [InlineData("  Damper Position ", false)]
    public void A_query_is_an_object_or_a_name(string text, bool isObject)
    {
        var (id, name) = IHaveCodec.ParseQuery(text);
        Assert.Equal(isObject, id is not null);
        Assert.Equal(isObject, name is null);
        if (!isObject) Assert.Equal(text.Trim(), name);
    }

    [Fact]
    public void The_simulator_matches_names_exactly_and_objects_by_identifier()
    {
        var m = SimulatedDeviceModel.CreateSample(1001);
        Assert.Equal([(Ao1, "Damper Position")], m.FindObjects(null, "Damper Position"));
        Assert.Empty(m.FindObjects(null, "damper position")); // BACnet names are compared exactly
        Assert.Equal("Damper Position", Assert.Single(m.FindObjects(Ao1, null)).Name);
        Assert.Empty(m.FindObjects(new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, 99), null));
    }
}
