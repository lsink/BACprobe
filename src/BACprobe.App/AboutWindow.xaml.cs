using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using BACprobe.Core.Writing;

namespace BACprobe.App;

/// <summary>Help > About: version, runtime, libraries, licence and where BACprobe keeps its files.</summary>
public partial class AboutWindow : Window
{
    private const string GitHubUrl = "https://github.com/lsink/BACprobe";

    private readonly List<(string Label, string Value)> _rows;

    public AboutWindow()
    {
        InitializeComponent();
        WindowFit.Apply(this);

        var version = VersionOf(typeof(AboutWindow).Assembly);
        VersionText.Text = $"Version {version}";

        _rows =
        [
            ("Version", version),
            ("License", "MIT (free and open source)"),
            ("Source", GitHubUrl),
            ("BACnet library", $"BACnet {VersionOf(typeof(System.IO.BACnet.BacnetClient).Assembly)} (ela-compil, MIT)"),
            ("Runtime", RuntimeInformation.FrameworkDescription),
            ("Windows", RuntimeInformation.OSDescription),
            ("Architecture", RuntimeInformation.ProcessArchitecture.ToString()),
            ("Data folder", DataFolder),
            ("Write log", WriteLog.DefaultPath),
        ];

        for (var i = 0; i < _rows.Count; i++)
        {
            InfoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = _rows[i].Label, Opacity = 0.7, Margin = new Thickness(0, 2, 16, 2) };
            var value = new TextBlock { Text = _rows[i].Value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2), IsHitTestVisible = true };
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            InfoGrid.Children.Add(label);
            InfoGrid.Children.Add(value);
        }
    }

    private static string DataFolder => Path.GetDirectoryName(WriteLog.DefaultPath)!;

    private static string VersionOf(Assembly assembly)
    {
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info)) return info.Split('+')[0]; // drop the build hash suffix
        return assembly.GetName().Version?.ToString(3) ?? "unknown";
    }

    private void OnOpenGitHub(object sender, RoutedEventArgs e) => Launch(GitHubUrl);

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(DataFolder); }
        catch { /* the open below reports the problem */ }
        Launch(DataFolder);
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText("BACprobe\n" + string.Join("\n", _rows.Select(r => $"{r.Label}: {r.Value}"))); }
        catch (Exception ex) { MessageBox.Show(this, $"Could not copy: {ex.Message}", "BACprobe", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Launch(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, $"Could not open {target}: {ex.Message}", "BACprobe", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
