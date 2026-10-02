namespace BACprobe.Core.Networking;

public enum PreflightSeverity { Pass, Warning, Fail }

/// <summary>One pre-flight finding. Every non-pass result carries a likely cause and a next step.</summary>
public sealed record PreflightResult(
    string Check,
    PreflightSeverity Severity,
    string Message,
    string? LikelyCause = null,
    string? NextStep = null);
