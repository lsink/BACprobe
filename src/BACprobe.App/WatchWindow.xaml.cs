using System.Windows;

namespace BACprobe.App;

/// <summary>The points being watched across devices, kept current. Reads only.</summary>
public partial class WatchWindow : Window
{
    public WatchWindow(WatchViewModel viewModel)
    {
        InitializeComponent();
        WindowFit.Apply(this);
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.StartAsync();
        Closed += (_, _) => viewModel.Dispose();
    }
}
