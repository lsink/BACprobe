using System.Windows;
using System.Windows.Controls;
using BACprobe.Core.Learning;

namespace BACprobe.App;

/// <summary>The built-in lessons: a list on the left, the selected lesson on the right. One window, reused.</summary>
public partial class LessonWindow : Window
{
    private static LessonWindow? _open;

    private LessonWindow()
    {
        InitializeComponent();
        WindowFit.Apply(this);
        LessonList.ItemsSource = Lessons.All;
        Closed += (_, _) => _open = null;
    }

    /// <summary>Show the window, on the given lesson if there is one (a second call reuses the open window).</summary>
    public static void Open(string? lessonId = null)
    {
        _open ??= new LessonWindow { Owner = Application.Current.MainWindow };
        _open.Select(lessonId is null ? Lessons.All[0] : Lessons.Find(lessonId) ?? Lessons.All[0]);
        _open.Show();
        _open.Activate();
    }

    private void Select(Lesson lesson) => LessonList.SelectedItem = lesson;

    private void OnSelected(object sender, SelectionChangedEventArgs e)
    {
        if (LessonList.SelectedItem is not Lesson lesson) return;
        TitleText.Text = lesson.Title;
        SummaryText.Text = lesson.Summary;
        BodyPanel.Children.Clear();
        foreach (var p in lesson.Paragraphs)
        {
            var bullet = p.StartsWith("- ");
            BodyPanel.Children.Add(new TextBlock
            {
                Text = bullet ? "•  " + p[2..] : p,
                TextWrapping = TextWrapping.Wrap,
                Margin = bullet ? new Thickness(14, 0, 0, 6) : new Thickness(0, 0, 0, 10),
            });
        }
    }
}
