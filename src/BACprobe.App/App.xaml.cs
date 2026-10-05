using System.Windows;

namespace BACprobe.App;

public partial class App : Application
{
    /// <summary>The "Learn more" link under a finding: opens the lesson that explains it.</summary>
    private void OnLearnMore(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FindingRow { LessonId: { } id } }) LessonWindow.Open(id);
    }
}
