using System.IO.BACnet;
using BACprobe.Core.Live;

namespace BACprobe.Core.Tests;

public class CovTests
{
    // --- refusals ---

    [Theory]
    [InlineData("Error from device: ERROR_CLASS_SERVICES - ERROR_CODE_SERVICE_REQUEST_DENIED", "does not support COV", true)]
    [InlineData("Error from device: ERROR_CODE_OPTIONAL_FUNCTIONALITY_NOT_SUPPORTED", "does not support COV", true)]
    [InlineData("Reject: unrecognized service", "does not support COV", true)]
    [InlineData("Error from device: ERROR_CLASS_RESOURCES - ERROR_CODE_NO_SPACE_TO_ADD_LIST_ELEMENT", "run out of COV subscription slots", true)]
    [InlineData("Error from device: ERROR_CODE_COV_SUBSCRIPTION_FAILED", "run out of COV subscription slots", true)]
    [InlineData("Error from device: ERROR_CODE_NOT_COV_PROPERTY", "does not report COV", false)]
    [InlineData("Error from device: ERROR_CODE_UNKNOWN_OBJECT", "does not exist", false)]
    [InlineData("Wait Timeout", "did not answer", false)]
    public void Refusals_are_explained_and_say_whether_to_keep_asking(string message, string reasonPart, bool stopTrying)
    {
        var r = CovRefusal.Explain(new InvalidOperationException(message));
        Assert.Contains(reasonPart, r.Reason);
        Assert.Equal(stopTrying, r.StopTrying);
    }

    [Fact]
    public void An_unrecognised_error_keeps_its_text_so_nothing_is_hidden()
    {
        var r = CovRefusal.Explain(new InvalidOperationException("something odd 123"));
        Assert.Contains("something odd 123", r.Reason);
        Assert.False(r.StopTrying);
    }

    [Fact]
    public void A_timeout_exception_is_not_treated_as_the_device_lacking_cov() =>
        Assert.False(CovRefusal.Explain(new TimeoutException()).StopTrying);

    // --- notifications ---

    private static BacnetPropertyValue Prop(BacnetPropertyIds id, params BacnetValue[] values) =>
        new() { property = new BacnetPropertyReference((uint)id, uint.MaxValue), value = values };

    [Fact]
    public void Present_value_is_pulled_out_of_a_notification_and_formatted()
    {
        var values = new[]
        {
            Prop(BacnetPropertyIds.PROP_STATUS_FLAGS, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_BIT_STRING, BacnetBitString.ConvertFromInt(0, 4))),
            Prop(BacnetPropertyIds.PROP_PRESENT_VALUE, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, 71.4000015f)),
        };
        Assert.Equal("71.4", CovNotification.PresentValueText(BacnetObjectTypes.OBJECT_ANALOG_INPUT, values));
    }

    [Fact]
    public void Binary_notifications_read_active_inactive()
    {
        var on = new[] { Prop(BacnetPropertyIds.PROP_PRESENT_VALUE, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED, 1u)) };
        Assert.Equal("Active", CovNotification.PresentValueText(BacnetObjectTypes.OBJECT_BINARY_OUTPUT, on));
    }

    [Fact]
    public void A_notification_without_a_present_value_gives_null()
    {
        var onlyFlags = new[] { Prop(BacnetPropertyIds.PROP_STATUS_FLAGS, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_BIT_STRING, BacnetBitString.ConvertFromInt(0, 4))) };
        Assert.Null(CovNotification.PresentValueText(BacnetObjectTypes.OBJECT_ANALOG_INPUT, onlyFlags));
        Assert.Null(CovNotification.PresentValueText(BacnetObjectTypes.OBJECT_ANALOG_INPUT, []));
    }

    [Fact]
    public void A_notification_whose_present_value_is_an_error_gives_null()
    {
        var err = new[]
        {
            Prop(BacnetPropertyIds.PROP_PRESENT_VALUE, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR,
                new BacnetError(BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_UNKNOWN_PROPERTY))),
        };
        Assert.Null(CovNotification.PresentValueText(BacnetObjectTypes.OBJECT_ANALOG_INPUT, err));
    }

    // --- status wording ---

    private static readonly TimeSpan Two = TimeSpan.FromSeconds(2);

    [Fact]
    public void Status_says_how_points_are_being_kept_current()
    {
        Assert.Equal("Live: polling every 2 s", LiveWatcher.DescribeMode(false, 0, 7, Two, null));
        Assert.Equal("Live: 7 point(s) by COV", LiveWatcher.DescribeMode(true, 7, 0, Two, null));
        Assert.Equal("Live: 3 by COV, 4 polled every 2 s", LiveWatcher.DescribeMode(true, 3, 4, Two, null));
    }

    [Fact]
    public void Status_explains_why_cov_is_not_in_use()
    {
        Assert.Equal("Live: polling every 2 s (the device does not support COV)",
            LiveWatcher.DescribeMode(true, 0, 7, Two, "the device does not support COV"));
    }

    [Fact]
    public void Defaults_are_a_five_minute_subscription_and_a_thirty_second_safety_poll()
    {
        var o = new LiveOptions(Two);
        Assert.True(o.UseCov);
        Assert.Equal(300u, o.CovLifetimeSeconds);
        Assert.Equal(TimeSpan.FromSeconds(30), o.SafetyPoll);
    }
}
