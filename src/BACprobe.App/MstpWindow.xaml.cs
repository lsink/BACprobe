using System.Windows;
using System.Windows.Input;

namespace BACprobe.App;

/// <summary>Listens to an MS/TP trunk (receive-only) and shows who is on it, how healthy it is, and the frames going by.</summary>
public partial class MstpWindow : Window
{
    private readonly MstpViewModel _vm;

    public MstpWindow(MstpViewModel viewModel)
    {
        InitializeComponent();
        WindowFit.Apply(this);
        _vm = viewModel;
        DataContext = viewModel;

        viewModel.PickOpenPath = () =>
            FileDialogs.Open(this, "Open an MS/TP capture", "MS/TP capture (*.bin)|*.bin|All files (*.*)|*.*");
        viewModel.PickSavePath = suggested =>
            FileDialogs.Save(this, "Save the MS/TP capture", suggested, "MS/TP capture (*.bin)|*.bin", ".bin");
        viewModel.PickPcapPath = suggested =>
            FileDialogs.Save(this, "Export frames for Wireshark", suggested, "Packet capture (*.pcap)|*.pcap", ".pcap");
        // Follow the newest frame, unless the tech paused to read.
        viewModel.LogChanged += () =>
        {
            if (viewModel.Paused || LogGrid.Items.Count == 0) return;
            LogGrid.ScrollIntoView(LogGrid.Items[^1]);
        };
        Closed += (_, _) => viewModel.Dispose();
    }

    private void NodeGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (NodeGrid.SelectedItem is Core.Mstp.MstpNodeRow row) _vm.FilterToNodeCommand.Execute(row);
    }
}
