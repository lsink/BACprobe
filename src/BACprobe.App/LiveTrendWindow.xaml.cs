using System.Windows;

namespace BACprobe.App;

/// <summary>A temporary live trend of one point. Samples while open; several can be open side by side.</summary>
public partial class LiveTrendWindow : Window
{
    public LiveTrendWindow(LiveTrendViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => viewModel.Start();
        Closed += (_, _) => viewModel.Dispose(); // stops sampling: no more requests to the controller
    }
}
