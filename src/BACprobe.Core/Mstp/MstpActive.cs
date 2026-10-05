using System.IO.BACnet;
using BACprobe.Core.Discovery;

namespace BACprobe.Core.Mstp;

/// <summary>What the passive survey heard, and whether it is safe to join.</summary>
public sealed record MstpSurveyResult(MstpSnapshot Survey, MstpJoinPlan Plan);

/// <summary>
/// Becoming a master on a live MS/TP trunk (Phase 3). Always starts with a passive survey, never transmits until the plan says it is
/// safe, and uses the library's own MS/TP master state machine for the token handling. Windows only (serial ports and the FTDI check).
/// </summary>
public static class MstpActive
{
    /// <summary>Listen for <paramref name="seconds"/> and decide whether, and at which MAC, to join.</summary>
    public static async Task<MstpSurveyResult> SurveyAsync(string port, int baud, byte? requestedMac, int seconds,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var monitor = new MstpMonitor(baud);
        progress?.Report($"Listening on {port} at {baud} baud for {seconds} s before transmitting anything...");
        using (var capture = new MstpPortCapture(port, monitor))
        {
            capture.Start();
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), ct); }
            finally { capture.Stop(); } // frees the port: the library opens it again to join
        }
        var survey = monitor.Snapshot();
        var latencyTooHigh = OperatingSystem.IsWindows() &&
                             FtdiLatency.ReadAll().Any(l => string.Equals(l.PortName, port, StringComparison.OrdinalIgnoreCase) && l.IsTooHigh);
        return new MstpSurveyResult(survey, MstpJoinPlanner.Plan(survey, baud, requestedMac, latencyTooHigh));
    }

    /// <summary>
    /// Join the trunk as a master and return a connection that works like the IP one. The caller must already have shown the user the
    /// plan and got a yes. Throws if the plan says not to join.
    /// </summary>
    public static DiscoveryService Join(string port, int baud, MstpJoinPlan plan, int timeoutMs = 4000, int retries = 1)
    {
        if (!plan.CanJoin) throw new InvalidOperationException("The survey says it is not safe to join this trunk.");
        var transport = SerialTransport.Mstp(port, baud, plan.Mac, plan.MaxMaster, maxInfoFrames: 1);
        var svc = DiscoveryService.ForTransport(transport, timeoutMs, retries);
        try
        {
            svc.Start();
            return svc;
        }
        catch
        {
            svc.Dispose();
            throw;
        }
    }

    /// <summary>The plain-English confirmation a tech must agree to before BACprobe transmits on the trunk.</summary>
    public static string ConfirmationText(string port, int baud, MstpJoinPlan plan) =>
        $"BACprobe will now TRANSMIT on the MS/TP trunk on {port} at {baud} baud, as master MAC {plan.Mac}.\n" +
        "It answers Poll For Master, takes the token, passes it on and sends your reads. A wrong address or a slow adapter can disturb the equipment on this trunk.\n" +
        "Only continue if MAC " + plan.Mac + " is not used by any device on it.";
}
