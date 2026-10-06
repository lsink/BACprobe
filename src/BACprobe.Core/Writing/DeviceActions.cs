using System.IO.BACnet;
using BACprobe.Core.Discovery;

namespace BACprobe.Core.Writing;

/// <summary>Things done to a whole device rather than to a point.</summary>
public enum DeviceActionKind
{
    /// <summary>TimeSynchronization: set the device clock to this PC's local time.</summary>
    SyncTime,

    /// <summary>UTCTimeSynchronization: set it to UTC; the device adds its own UTC_Offset.</summary>
    SyncTimeUtc,

    /// <summary>ReinitializeDevice, warm start: the program restarts with its data kept.</summary>
    WarmStart,

    /// <summary>ReinitializeDevice, cold start: like a power cycle; values go back to their defaults.</summary>
    ColdStart,

    /// <summary>DeviceCommunicationControl, disable: the device stops talking on the network (except to be enabled again).</summary>
    Mute,

    /// <summary>DeviceCommunicationControl, disable initiation: it answers, but sends nothing on its own (no I-Am, no COV, no alarms).</summary>
    MuteInitiation,

    /// <summary>DeviceCommunicationControl, enable: back to normal.</summary>
    Unmute,
}

/// <summary>
/// One device action with its confirmation wording. Restarts and muting are expert actions: the tech must type the device number to
/// confirm, a stray click cannot do them. Muting always has a time limit, so a forgotten mute heals itself.
/// </summary>
public sealed record DeviceActionRequest(DiscoveredDevice Device, string DeviceName, DeviceActionKind Kind, uint Minutes = 0, string? Password = null)
{
    /// <summary>The longest mute BACprobe will send. "Forever" (0) is never sent: only a restart would undo it if the tech forgot.</summary>
    public const uint MaxMuteMinutes = 60;
    public const uint DefaultMuteMinutes = 10;

    public bool IsTimeSync => Kind is DeviceActionKind.SyncTime or DeviceActionKind.SyncTimeUtc;
    public bool IsRestart => Kind is DeviceActionKind.WarmStart or DeviceActionKind.ColdStart;
    public bool IsMute => Kind is DeviceActionKind.Mute or DeviceActionKind.MuteInitiation;

    /// <summary>Expert actions need the device number typed in; null for the rest.</summary>
    public string? TypeToConfirm => IsRestart || IsMute ? Device.InstanceId.ToString() : null;

    /// <summary>Restarts and communication control may need the device's password (BACnet sends it in the request).</summary>
    public bool MayNeedPassword => IsRestart || IsMute || Kind == DeviceActionKind.Unmute;

    public string Title => Kind switch
    {
        DeviceActionKind.SyncTime or DeviceActionKind.SyncTimeUtc => "Set the device clock",
        DeviceActionKind.WarmStart or DeviceActionKind.ColdStart => "Restart the device",
        DeviceActionKind.Unmute => "Let the device talk again",
        _ => "Mute the device",
    };

    public string Headline => Kind switch
    {
        DeviceActionKind.SyncTime => $"Set {DeviceName}'s clock to this PC's time?",
        DeviceActionKind.SyncTimeUtc => $"Set {DeviceName}'s clock to this PC's time (as UTC)?",
        DeviceActionKind.WarmStart => $"Warm start {DeviceName}?",
        DeviceActionKind.ColdStart => $"Cold start {DeviceName}?",
        DeviceActionKind.Mute => $"Mute {DeviceName} for {Minutes} minutes?",
        DeviceActionKind.MuteInitiation => $"Stop {DeviceName} sending on its own for {Minutes} minutes?",
        _ => $"Let {DeviceName} talk on the network again?",
    };

    public IReadOnlyList<ConfirmFact> Facts
    {
        get
        {
            var facts = new List<ConfirmFact> { new("Device", $"{DeviceName} (device {Device.InstanceId})") };
            if (IsTimeSync)
            {
                facts.Add(new("New time", $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} (this PC{(Kind == DeviceActionKind.SyncTimeUtc ? ", sent as UTC" : "")})"));
                if (Device.ClockSkew is { } skew) facts.Add(new("Device clock now", DeviceHealth.ShortSkew(skew) + " from this PC"));
            }
            if (IsMute) facts.Add(new("For", $"{Minutes} minutes, then it un-mutes by itself"));
            return facts;
        }
    }

    public string Consequence => Kind switch
    {
        DeviceActionKind.SyncTime =>
            "Schedules, trends and alarm times in this device use its clock. Make sure this PC's clock and time zone are right first.",
        DeviceActionKind.SyncTimeUtc =>
            "The device works out local time from its own UTC offset and daylight-saving settings. Use this only if those are set up; otherwise use the local time option.",
        DeviceActionKind.WarmStart =>
            "The controller restarts its program and keeps its settings. It stops controlling and goes quiet on the network for a moment, " +
            "and forgets COV subscriptions (BACprobe's included).",
        DeviceActionKind.ColdStart =>
            "Like switching it off and on: the controller restarts and values go back to their power-up defaults. Equipment it runs may stop and restart.",
        DeviceActionKind.Mute =>
            "The device stops answering everything on the network: front-ends lose it, and other controllers that read from it lose their values. " +
            "It still runs its own program. BACprobe will offer to un-mute it before you leave.",
        DeviceActionKind.MuteInitiation =>
            "The device still answers, but sends nothing by itself: no I-Am, no change-of-value updates, no alarms. BACprobe will offer to undo this before you leave.",
        _ => "The device answers and reports again as normal.",
    };

    public string? Warning => Kind switch
    {
        DeviceActionKind.ColdStart => "A cold start can restart equipment and lose values that were not saved. Only do this with the equipment in a safe state.",
        DeviceActionKind.WarmStart => "Only restart a controller you are allowed to. The equipment it runs is not controlled while it restarts.",
        DeviceActionKind.Mute => "Anything that depends on this device over the network will lose it until it is un-muted or the time runs out.",
        _ => null,
    };

    public string ConfirmLabel => Kind switch
    {
        DeviceActionKind.SyncTime or DeviceActionKind.SyncTimeUtc => "Set the clock",
        DeviceActionKind.WarmStart => "Warm start",
        DeviceActionKind.ColdStart => "Cold start",
        DeviceActionKind.Unmute => "Un-mute",
        _ => $"Mute for {Minutes} min",
    };

    /// <summary>The write log's action column.</summary>
    public string LogAction => Kind switch
    {
        DeviceActionKind.SyncTime => $"time sync to {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
        DeviceActionKind.SyncTimeUtc => $"UTC time sync to {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z",
        DeviceActionKind.WarmStart => "reinitialize: warm start",
        DeviceActionKind.ColdStart => "reinitialize: cold start",
        DeviceActionKind.Mute => $"communication control: disable for {Minutes} min",
        DeviceActionKind.MuteInitiation => $"communication control: disable initiation for {Minutes} min",
        _ => "communication control: enable",
    };

    /// <summary>Checks before anything is sent: a mute needs a time limit within <see cref="MaxMuteMinutes"/>.</summary>
    public string? Problem => IsMute && (Minutes < 1 || Minutes > MaxMuteMinutes)
        ? $"Mute for 1 to {MaxMuteMinutes} minutes. BACprobe never mutes a device with no time limit."
        : null;
}

/// <summary>Why a device action failed, with a likely cause and next step.</summary>
public static class DeviceActionErrors
{
    public static WriteErrorText Explain(Exception ex)
    {
        var m = ex.Message;
        bool Has(string s) => m.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (BacnetFailure.IsTimeout(ex))
            return new("The device did not answer (timeout).",
                "Network drop or a busy controller. It may or may not have done it; a restarting device also stops answering.",
                "Wait a moment, then Scan to see whether it is back.");
        if (Has("PASSWORD_FAILURE"))
            return new("The device refused: wrong or missing password.",
                "This controller protects restarts and communication control with a password.",
                "Enter the device password (from the site documentation or the controller's own tool) and try again.");
        if (Has("SERVICE_REQUEST_DENIED") || Has("REJECT") || Has("UNRECOGNIZED_SERVICE") || Has("OPTIONAL_FUNCTIONALITY"))
            return new($"The device refused ({m}).",
                "It does not support this service, or only allows it from its own tool.",
                "Use the controller's own tool, or the front-end, for this.");
        if (Has("SECURITY"))
            return new($"The device refused for security reasons ({m}).",
                "It needs a password or does not allow this from the network.",
                "Check the device's password settings in its own tool.");
        return new($"The device returned an error: {m}",
            "The controller refused for a reason BACprobe does not recognise.",
            "Look the error up in the controller's documentation.");
    }
}
