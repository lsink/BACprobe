using System.Windows;

namespace BACprobe.App;

/// <summary>Several watched points on one chart, sampled from the watch list. Closing it discards the samples.</summary>
public partial class MultiTrendWindow : Window
{
    public MultiTrendWindow(MultiTrendViewModel viewModel)
    {
        InitializeComponent();
        WindowFit.Apply(this);
        DataContext = viewModel;
        viewModel.Sampled += Chart.Refresh;
        Closed += (_, _) =>
        {
            viewModel.Sampled -= Chart.Refresh;
            viewModel.Dispose();
        };
    }
}
