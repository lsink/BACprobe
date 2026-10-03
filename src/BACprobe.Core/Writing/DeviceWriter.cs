using System.IO.BACnet;
using BACprobe.Core.Discovery;

namespace BACprobe.Core.Writing;

/// <param name="Explanation">
/// Set when something deserves a proper explanation: the device refused, or accepted the write but the point did not change.
/// Null for a write that worked.
/// </param>
public sealed record WriteOutcome(bool Success, string Message, PromptContent? Explanation = null);

/// <summary>
/// Sends writes and releases, logs every attempt, and tracks the overrides left in place.
/// Callers must have shown the user <see cref="WriteRequest.ConfirmationText"/> before calling.
/// </summary>
public sealed class DeviceWriter(BacnetClient client, WriteLog log, OverrideTracker tracker)
{
    public WriteLog Log => log;
    public OverrideTracker Overrides => tracker;

    public async Task<WriteOutcome> ExecuteAsync(WriteRequest request, CancellationToken ct = default)
    {
        var action = request.IsRelease ? "release" : $"write {request.ValueText}";
        try
        {
            if (request.Priority is < 1 or > 16)
                throw new ArgumentOutOfRangeException(nameof(request), "Priority must be 1 to 16.");

            var value = request.Value ?? new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL, null);
            await client.WritePropertyAsync(request.Device.Address, request.Point, BacnetPropertyIds.PROP_PRESENT_VALUE,
                [value], priority: (byte)request.Priority, cancellationToken: ct);

            if (request.IsRelease)
                tracker.Remove(request.Device.InstanceId, request.Point, request.Priority);
            else
                tracker.Record(new TrackedOverride(request.Device, request.DeviceName, request.Point, request.ObjectName,
                    request.Priority, request.ValueText));

            if (request.IsRelease)
            {
                Record(request, action, true, "released");
                return new WriteOutcome(true, "released");
            }

            // The device said yes. That does not mean the point changed (a higher priority may be holding it): look.
            var probe = await VerifyAsync(request, ct);
            var explanation = WriteExplainer.ExplainIneffective(request, probe);
            var msg = explanation is not null ? $"accepted, but it had no effect: {explanation.Headline.ToLowerInvariant()}"
                : probe.Reachable ? "device accepted the write"
                : "device accepted the write (the point could not be read back to check)";
            Record(request, action, true, msg);
            return new WriteOutcome(true, msg, explanation);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var text = WriteErrors.Explain(ex);
            Record(request, action, false, text.Summary);

            // Find out why: look at the point, then explain what we found.
            PointProbe? probe = null;
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(TimeSpan.FromSeconds(4));
                probe = await new PointProber(client, request.Device).ProbeAsync(request.Point, limit.Token);
            }
            catch (Exception) { /* the explanation says it could not check */ }

            // A timed-out write may still have landed: track it, so leaving offers to release it.
            if (WriteExplainer.MayHaveLanded(request, WriteErrors.IsTimeout(ex), probe))
                tracker.Record(new TrackedOverride(request.Device, request.DeviceName, request.Point, request.ObjectName,
                    request.Priority, request.ValueText, Unconfirmed: true));
            return new WriteOutcome(false, text.Full, WriteExplainer.ExplainFailure(request, ex.Message, probe));
        }
    }

    /// <summary>Read the point back until it shows the written value, for up to <see cref="VerifyFor"/> (controllers update on their own schedule).</summary>
    private async Task<PointProbe> VerifyAsync(WriteRequest request, CancellationToken ct)
    {
        var prober = new PointProber(client, request.Device);
        var deadline = DateTime.UtcNow + VerifyFor;
        PointProbe probe;
        do
        {
            probe = await prober.ProbeAsync(request.Point, ct);
            if (!probe.Reachable || WriteExplainer.IsEffective(request, probe)) return probe;
            await Task.Delay(VerifyEvery, ct);
        }
        while (DateTime.UtcNow < deadline);
        return await prober.ProbeAsync(request.Point, ct); // one last look
    }

    /// <summary>How long to wait for a slow controller to show the new value before calling the write ineffective.</summary>
    public TimeSpan VerifyFor { get; set; } = TimeSpan.FromSeconds(3);

    public TimeSpan VerifyEvery { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Release every override this session left in place. Returns (released, failed).</summary>
    public async Task<(int Released, int Failed)> ReleaseAllAsync(CancellationToken ct = default)
    {
        int ok = 0, bad = 0;
        foreach (var o in tracker.Active)
        {
            var req = new WriteRequest(o.Device, o.DeviceName, o.Point, o.ObjectName, null, "release", o.Priority);
            var outcome = await ExecuteAsync(req, ct);
            if (outcome.Success) ok++; else bad++;
        }
        return (ok, bad);
    }

    private void Record(WriteRequest r, string action, bool success, string result) =>
        log.Add(new WriteLogEntry(DateTimeOffset.Now, r.Device.InstanceId, r.DeviceName, r.ObjectLabel, action, r.Priority, success, result));
}

public sealed record WriteErrorText(string Summary, string Cause, string NextStep)
{
    public string Full => $"{Summary}\n  Likely cause: {Cause}\n  Next step:    {NextStep}";
}

public static class WriteErrors
{
    public static bool IsTimeout(Exception ex) => BacnetFailure.IsTimeout(ex);

    /// <summary>Map the library's plain exceptions to a likely cause and a next step.</summary>
    public static WriteErrorText Explain(Exception ex)
    {
        var m = ex.Message;
        bool Has(string s) => m.Contains(s, StringComparison.OrdinalIgnoreCase);

        if (IsTimeout(ex))
            return new("The device did not answer the write (timeout).",
                "Network drop, a busy controller, or a router dropping the request. The write may or may not have happened.",
                "Read the point again to see its real state before retrying.");
        if (Has("WRITE_ACCESS_DENIED"))
            return new("The device refused the write: access denied.",
                "The point is read-only (an input, or not commandable), or the device is protected against writes.",
                "Pick an output or value point that has a priority array, or check the point's Out Of Service setting.");
        if (Has("VALUE_OUT_OF_RANGE"))
            return new("The device refused the value: out of range.",
                "The number is outside the limits the controller allows for this point.",
                "Try a value inside the point's normal range.");
        if (Has("INVALID_DATA_TYPE") || Has("DATATYPE_NOT_SUPPORTED"))
            return new("The device refused the value: wrong data type.",
                "The point expects a different type (for example a number instead of on/off).",
                "Read the point to see what type it holds, then write a matching value.");
        if (Has("UNKNOWN_OBJECT"))
            return new("The device says that object does not exist.",
                "The object was deleted or the instance number is wrong.",
                "Select the device again to refresh its object list.");
        if (Has("UNKNOWN_PROPERTY"))
            return new("The device says that point has no Present Value to write.",
                "This object type is not writable this way.",
                "Choose a different point.");
        if (Has("SERVICE_REQUEST_DENIED") || Has("REJECT") || Has("ABORT"))
            return new($"The device rejected the request ({m}).",
                "The controller is busy, in a state where it will not take writes, or does not support WriteProperty here.",
                "Wait a moment and try again, or check the controller's mode.");
        return new($"The device returned an error: {m}",
            "The controller refused the write for a reason BACprobe does not recognise.",
            "Read the point to check its state, and look up the error text in the controller's documentation.");
    }
}
