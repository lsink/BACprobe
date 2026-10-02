using System.Windows;
using System.Windows.Controls;
using BACprobe.Core.Writing;

namespace BACprobe.App;

/// <summary>A button in a prompt: what it says, and whether it is the one that backs out.</summary>
/// <param name="Label">Says what will happen, e.g. "Release them", never just "Yes".</param>
/// <param name="IsCancel">Escape and the window's close button count as this button.</param>
public sealed record PromptButton(string Label, bool IsCancel = false);

/// <summary>
/// The one confirmation dialog the whole app uses (writes, releases, leaving with overrides in place),
/// so every "are you sure?" looks and reads the same.
/// </summary>
public partial class PromptWindow : Window
{
    private PromptWindow(PromptContent content, IReadOnlyList<PromptButton> buttons, int focusIndex)
    {
        InitializeComponent();
        DataContext = new PromptViewModel(content);

        for (var i = 0; i < buttons.Count; i++)
        {
            var index = i;
            var b = new Button
            {
                Content = buttons[i].Label,
                MinWidth = 100,
                Padding = new Thickness(16, 6, 16, 6),
                Margin = new Thickness(i == 0 ? 0 : 10, 0, 0, 0),
                IsCancel = buttons[i].IsCancel,
            };
            b.Click += (_, _) =>
            {
                Choice = index;
                DialogResult = true;
            };
            ButtonPanel.Children.Add(b);
        }
        Loaded += (_, _) => ButtonPanel.Children[Math.Clamp(focusIndex, 0, buttons.Count - 1)].Focus();
    }

    /// <summary>Index of the button that was clicked, or -1 if the window was closed or Escape was pressed.</summary>
    public int Choice { get; private set; } = -1;

    /// <summary>
    /// Show the prompt and return the index of the chosen button (-1 if dismissed).
    /// <paramref name="focusIndex"/> is the button that has focus, so a stray Enter does the safe thing.
    /// </summary>
    public static int Show(Window owner, PromptContent content, IReadOnlyList<PromptButton> buttons, int focusIndex = 0)
    {
        var w = new PromptWindow(content, buttons, focusIndex) { Owner = owner };
        w.ShowDialog();
        return w.Choice;
    }
}

public sealed class PromptViewModel(PromptContent content)
{
    public string WindowTitle => content.Title;
    public string Headline => content.Headline;
    public IReadOnlyList<ConfirmFact> Facts => content.Facts;
    public string Body => content.Body;
    public string? Warning => content.Warning;
    public Visibility FactsVisibility => content.Facts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility WarningVisibility => content.Warning is null ? Visibility.Collapsed : Visibility.Visible;
}
