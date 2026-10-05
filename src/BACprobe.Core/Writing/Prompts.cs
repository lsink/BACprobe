using BACprobe.Core.Browsing;

namespace BACprobe.Core.Writing;

/// <summary>
/// What a confirmation dialog says, independent of how it is drawn: a title, a one-line question, labelled
/// details to check, an explanation, and an optional red warning. Every dialog in the app is built from this.
/// </summary>
public sealed record PromptContent(
    string Title,
    string Headline,
    IReadOnlyList<ConfirmFact> Facts,
    string Body,
    string? Warning = null);

/// <summary>Wording for the app's confirmations, kept here so it is consistent and testable.</summary>
public static class Prompts
{
    private const int MaxRows = 8;

    /// <summary>Shown when leaving (exit, rescan, opening a job) while this session still has overrides in place.</summary>
    public static PromptContent OverridesInPlace(IReadOnlyList<TrackedOverride> overrides)
    {
        var n = overrides.Count;
        var facts = overrides.Take(MaxRows)
            .Select(o => new ConfirmFact(o.ObjectName,
                $"{o.DeviceName} (device {o.Device.InstanceId}): {o.HeldAs}{o.UnconfirmedNote}"))
            .ToList();
        if (n > MaxRows) facts.Add(new ConfirmFact("", $"...and {n - MaxRows} more"));

        return new PromptContent(
            "Overrides still in place",
            n == 1 ? "You left 1 override in place" : $"You left {n} overrides in place",
            facts,
            n == 1
                ? "While an override is in place, the controller will not run that point automatically. " +
                  "Release it now unless you are leaving it on purpose, for example as a temporary fix."
                : "While an override is in place, the controller will not run those points automatically. " +
                  "Release them now unless you are leaving them on purpose, for example as a temporary fix.");
    }

    /// <summary>Shown when releasing at exit did not work for some points. Always gives a cause and a next step.</summary>
    public static PromptContent ReleaseFailed(int failed) => new(
        "Some overrides could not be released",
        failed == 1 ? "1 override is still in place" : $"{failed} overrides are still in place",
        [],
        "The device did not accept the release, so the point is still being held at the override value. " +
        "Likely cause: the device stopped answering or the network dropped (the write log has the exact reason). " +
        "Next step: go back and try again, or release it from the controller's own tool.",
        "If you continue, those points stay overridden on the device until someone releases them.");

    public static PromptContent ForAck(Alarms.AlarmAckRequest r) => new("Acknowledge alarm", r.Headline, r.Facts, r.Consequence, r.Warning);

    public static PromptContent ForOutOfService(OutOfServiceRequest r) => new(
        r.TurnOn ? "Take out of service" : "Put back in service", r.Headline, r.Facts, r.Consequence);

    /// <summary>The dialog for a write or release, built from the same pieces as every other prompt.</summary>
    public static PromptContent ForWrite(WriteRequest r) => new(
        r.IsRelease ? "Release override" : "Confirm write", r.Headline, r.Facts, r.Consequence, r.Warning);
}
