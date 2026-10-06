using System.Windows;

namespace BACprobe.App;

/// <summary>
/// Keeps a window inside the screen's work area. Window sizes in XAML are what looks right on a big screen; on a 1366×768 laptop,
/// or at 125% scaling, they would open taller than the screen with the title bar out of reach. Call after InitializeComponent.
/// </summary>
internal static class WindowFit
{
    private const double Share = 0.95;

    public static void Apply(Window w)
    {
        var area = SystemParameters.WorkArea; // device-independent units, like Width/Height
        var maxW = area.Width * Share;
        var maxH = area.Height * Share;
        w.MinWidth = Math.Min(w.MinWidth, maxW);
        w.MinHeight = Math.Min(w.MinHeight, maxH);
        if (!double.IsNaN(w.Width)) w.Width = Math.Min(w.Width, maxW);
        if (!double.IsNaN(w.Height)) w.Height = Math.Min(w.Height, maxH);
        w.MaxHeight = Math.Min(w.MaxHeight, area.Height); // a window that sizes to its content must still fit
        w.Loaded += (_, _) => KeepOnScreen(w);
    }

    /// <summary>Centring on the owner can still push a window past an edge: pull it back so the title bar can be grabbed.</summary>
    private static void KeepOnScreen(Window w)
    {
        if (w.WindowState != WindowState.Normal) return;
        var area = SystemParameters.WorkArea;
        w.Left = Math.Max(area.Left, Math.Min(w.Left, area.Right - w.ActualWidth));
        w.Top = Math.Max(area.Top, Math.Min(w.Top, area.Bottom - w.ActualHeight));
    }
}
