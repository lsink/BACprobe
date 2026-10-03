namespace BACprobe.Core;

/// <summary>Classifies the library's plain exceptions.</summary>
public static class BacnetFailure
{
    /// <summary>
    /// The device did not answer (as opposed to answering "no"). After a timeout, trying the same device again with
    /// smaller requests only waits out more timeouts.
    /// </summary>
    public static bool IsTimeout(Exception ex) =>
        ex is TimeoutException || ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The device aborted the request, which for a read almost always means the answer would not fit in what it can
    /// send (no segmentation, or a small max APDU). Asking for less usually works.
    /// </summary>
    public static bool IsAbort(Exception ex) => ex.Message.Contains("abort", StringComparison.OrdinalIgnoreCase);

    /// <summary>After this many timeouts in a row from one device, treat it as not answering instead of waiting out every request.</summary>
    public const int MaxTimeoutsInARow = 3;
}
