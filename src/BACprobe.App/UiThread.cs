using System.Windows;

namespace BACprobe.App;

/// <summary>Getting back to the UI thread from a network or serial thread. Without a running application (tests) it just runs the action.</summary>
internal static class UiThread
{
    /// <summary>Run now, on the UI thread: directly if already there, otherwise wait for it.</summary>
    public static void Invoke(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action(); else d.Invoke(action);
    }

    /// <summary>Queue it for the UI thread and carry on without waiting. For frequent updates from a worker.</summary>
    public static void Post(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null) action(); else d.BeginInvoke(action);
    }
}
