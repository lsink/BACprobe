using System.Windows;
using BACprobe.Core.Settings;
using Microsoft.Win32;

namespace BACprobe.App;

/// <summary>
/// Light, dark, or whatever Windows says, and the meaning colours (alarm, fault, override...) to match. The Fluent theme draws the
/// controls; Themes/Light.xaml or Themes/Dark.xaml supplies our colours, which must sit after the Fluent dictionary to win.
/// </summary>
internal static class ThemeManager
{
    private static ResourceDictionary? _colours;
    private static ThemeChoice _choice = ThemeChoice.System;
    private static bool _listening;

    public static ThemeChoice Choice => _choice;

    public static void Apply(ThemeChoice choice)
    {
        _choice = choice;
        var app = Application.Current;
        if (app is null) return;
        app.ThemeMode = choice switch { ThemeChoice.Light => ThemeMode.Light, ThemeChoice.Dark => ThemeMode.Dark, _ => ThemeMode.System };

        var light = choice switch { ThemeChoice.Light => true, ThemeChoice.Dark => false, _ => WindowsUsesLightTheme() };
        var merged = app.Resources.MergedDictionaries;
        if (_colours is not null) merged.Remove(_colours);
        _colours = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{(light ? "Light" : "Dark")}.xaml") };
        merged.Add(_colours); // last, so our keys (including the DataGrid selection colours) win over Fluent's

        if (!_listening)
        {
            _listening = true;
            // Windows switched light/dark while we run: follow it when the choice is System.
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && _choice == ThemeChoice.System)
                    app.Dispatcher.BeginInvoke(() => Apply(ThemeChoice.System));
            };
        }
    }

    private static bool WindowsUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
        }
        catch (Exception) { return true; }
    }
}
