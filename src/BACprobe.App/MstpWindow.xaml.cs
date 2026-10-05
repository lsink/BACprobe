using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace BACprobe.App;

/// <summary>Listens to an MS/TP trunk (receive-only) and shows who is on it, how healthy it is, and the frames going by.</summary>
public partial class MstpWindow : Window
{
    private readonly MstpViewModel _vm;

    public MstpWindow(MstpViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = viewModel;

        viewModel.PickOpenPath = () =>
        {
            var dlg = new OpenFileDialog { Title = "Open an MS/TP capture", Filter = "MS/TP capture (*.bin)|*.bin|All files (*.*)|*.*" };
            return dlg.ShowDialog(this) == true ? dlg.FileName : null;
        };
        viewModel.PickSavePath = suggested =>
        {
            var dlg = new SaveFileDialog { Title = "Save the MS/TP capture", FileName = suggested, Filter = "MS/TP capture (*.bin)|*.bin", DefaultExt = ".bin", AddExtension = true, OverwritePrompt = true };
            return dlg.ShowDialog(this) == true ? dlg.FileName : null;
        };
        viewModel.PickPcapPath = suggested =>
        {
            var dlg = new SaveFileDialog { Title = "Export frames for Wireshark", FileName = suggested, Filter = "Packet capture (*.pcap)|*.pcap", DefaultExt = ".pcap", AddExtension = true, OverwritePrompt = true };
            return dlg.ShowDialog(this) == true ? dlg.FileName : null;
        };
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
