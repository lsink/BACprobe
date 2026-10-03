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
}
