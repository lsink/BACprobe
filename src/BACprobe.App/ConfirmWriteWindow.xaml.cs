using System.Windows;
using BACprobe.Core.Writing;

namespace BACprobe.App;

/// <summary>The "are you sure?" dialog for a write or a release. Shows exactly what is about to happen, in plain English.</summary>
public partial class ConfirmWriteWindow : Window
{
    public ConfirmWriteWindow(WriteRequest request)
    {
        InitializeComponent();
        DataContext = new ConfirmWriteViewModel(request);

        // For life-safety priorities, a stray Enter must not confirm: start on Cancel.
        Loaded += (_, _) => (request.Warning is not null ? CancelButton : ConfirmButton).Focus();
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}

public sealed class ConfirmWriteViewModel(WriteRequest request)
{
    public string WindowTitle => request.IsRelease ? "Release override" : "Confirm write";
    public string Headline => request.Headline;
    public IReadOnlyList<ConfirmFact> Facts => request.Facts;
    public string Consequence => request.Consequence;
    public string? Warning => request.Warning;
    public Visibility WarningVisibility => request.Warning is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Says what the button does, including the priority, so it is never just "Yes".</summary>
    public string ConfirmText => request.IsRelease ? "Release" : $"Write at priority {request.Priority}";
}
