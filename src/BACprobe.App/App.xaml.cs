using System.Windows;

namespace BACprobe.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ThemeManager.Apply(AppSettings.Current.Theme); // before the first window, so it never flashes the wrong colours
        base.OnStartup(e);
    }

    /// <summary>The "Learn more" link under a finding: opens the lesson that explains it.</summary>
    private void OnLearnMore(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FindingRow { LessonId: { } id } }) LessonWindow.Open(id);
    }
}
