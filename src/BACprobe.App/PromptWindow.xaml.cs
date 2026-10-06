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
        WindowFit.Apply(this);
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
                Password = PasswordBox.Password.Length == 0 ? null : PasswordBox.Password;
                DialogResult = true;
            };
            ButtonPanel.Children.Add(b);
        }
        _expected = content.TypeToConfirm;
        if (_expected is not null && ButtonPanel.Children.Count > 0) ButtonPanel.Children[0].IsEnabled = false; // until the number is typed
        Loaded += (_, _) =>
        {
            if (_expected is not null) TypeBox.Focus();
            else ButtonPanel.Children[Math.Clamp(focusIndex, 0, buttons.Count - 1)].Focus();
        };
    }

    private readonly string? _expected;

    private void OnTypeChanged(object sender, TextChangedEventArgs e)
    {
        if (_expected is not null && ButtonPanel.Children.Count > 0)
            ButtonPanel.Children[0].IsEnabled = TypeBox.Text.Trim() == _expected;
    }

    /// <summary>What was typed in the password box (null if empty or not shown).</summary>
    public string? Password { get; private set; }

    /// <summary>Index of the button that was clicked, or -1 if the window was closed or Escape was pressed.</summary>
    public int Choice { get; private set; } = -1;

    /// <summary>
    /// Show the prompt and return the index of the chosen button (-1 if dismissed).
    /// <paramref name="focusIndex"/> is the button that has focus, so a stray Enter does the safe thing.
    /// </summary>
    public static int Show(Window owner, PromptContent content, IReadOnlyList<PromptButton> buttons, int focusIndex = 0) =>
        Show(owner, content, buttons, out _, focusIndex);

    /// <summary>As <see cref="Show(Window, PromptContent, IReadOnlyList{PromptButton}, int)"/>, also giving the password typed (null if none).</summary>
    public static int Show(Window owner, PromptContent content, IReadOnlyList<PromptButton> buttons, out string? password, int focusIndex = 0)
    {
        var w = new PromptWindow(content, buttons, focusIndex) { Owner = owner };
        w.ShowDialog();
        password = w.Password;
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
    public Visibility TypeVisibility => content.TypeToConfirm is null ? Visibility.Collapsed : Visibility.Visible;
    public string TypePrompt => $"To go ahead, type the device number: {content.TypeToConfirm}";
    public Visibility PasswordVisibility => content.AskPassword ? Visibility.Visible : Visibility.Collapsed;
}
