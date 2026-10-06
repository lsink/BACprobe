using System.Windows;
using System.Windows.Input;

namespace BACprobe.App;

/// <summary>Search every point on every device read so far. Stays open next to the main window.</summary>
public partial class FindWindow : Window
{
    public FindWindow(FindViewModel viewModel)
    {
        InitializeComponent();
        WindowFit.Apply(this);
        DataContext = viewModel;
        Loaded += (_, _) =>
        {
            viewModel.Search();
            QueryBox.Focus();
        };
    }

    private async Task GoToSelectedAsync()
    {
        if (DataContext is not FindViewModel { Selected: not null } vm) return;
        await vm.GoToCommand.ExecuteAsync(null);
        Owner?.Activate(); // show the point we just selected
    }

    private async void OnResultDoubleClick(object sender, MouseButtonEventArgs e) => await GoToSelectedAsync();

    private async void OnGoToClick(object sender, RoutedEventArgs e) => await GoToSelectedAsync();

    private async void OnResultKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await GoToSelectedAsync();
    }
}
