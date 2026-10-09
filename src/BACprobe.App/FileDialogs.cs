using System.Windows;
using Microsoft.Win32;

namespace BACprobe.App;

/// <summary>The app's Save and Open dialogs: a Save always adds the extension and asks before replacing a file.</summary>
public static class FileDialogs
{
    /// <summary>A Save dialog. Null when cancelled.</summary>
    public static string? Save(Window owner, string title, string suggested, string filter, string? defaultExt = null) =>
        SaveWithType(owner, title, suggested, filter, defaultExt)?.Path;

    /// <summary>A Save dialog that also says which file type was picked (1-based, in <paramref name="filter"/> order). Null when cancelled.</summary>
    public static (string Path, int FilterIndex)? SaveWithType(Window owner, string title, string suggested, string filter, string? defaultExt = null)
    {
        var dialog = new SaveFileDialog { Title = title, FileName = suggested, Filter = filter, AddExtension = true, OverwritePrompt = true };
        if (defaultExt is not null) dialog.DefaultExt = defaultExt;
        return dialog.ShowDialog(owner) == true ? (dialog.FileName, dialog.FilterIndex) : null;
    }

    /// <summary>An Open dialog for an existing file. Null when cancelled.</summary>
    public static string? Open(Window owner, string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    /// <summary>The window a dialog belongs to: the active one this window owns (a chart, a trend), else this window.</summary>
    public static Window ActiveOwner(Window window) => window.OwnedWindows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? window;
}
