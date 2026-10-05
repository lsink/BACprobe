using System.Windows;
using System.Windows.Input;

namespace BACprobe.App;

/// <summary>Every device's active and unacknowledged alarms, explained; acknowledge one, or jump to its point.</summary>
public partial class AlarmsWindow : Window
{
    private readonly AlarmsViewModel _vm;

    public AlarmsWindow(AlarmsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _vm = viewModel;
        Loaded += (_, _) => { if (viewModel.RefreshCommand.CanExecute(null)) viewModel.RefreshCommand.Execute(null); };
        Closed += (_, _) => viewModel.Dispose();
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.GoToPointCommand.CanExecute(null)) _vm.GoToPointCommand.Execute(null);
    }
}
