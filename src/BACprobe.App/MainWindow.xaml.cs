using System.ComponentModel;
using System.Windows;
using BACprobe.Core.Export;
using Microsoft.Win32;

namespace BACprobe.App;

public partial class MainWindow : Window
{
    private bool _closeApproved;

    public MainWindow()
    {
        InitializeComponent();
        if (DataContext is MainViewModel vm)
        {
            vm.Confirm = (title, text) =>
                MessageBox.Show(this, text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
            vm.PickExportFile = suggested =>
            {
                var dialog = new SaveFileDialog
                {
                    Title = "Export point list",
                    FileName = suggested,
                    Filter = "Excel workbook (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv|EDE file (*.csv)|*.csv",
                    AddExtension = true,
                    OverwritePrompt = true,
                };
                if (dialog.ShowDialog(this) != true) return null;
                var format = dialog.FilterIndex switch { 1 => ExportFormat.Xlsx, 3 => ExportFormat.Ede, _ => ExportFormat.Csv };
                return (dialog.FileName, format);
            };
            vm.AskYesNoCancel = (title, text) =>
                MessageBox.Show(this, text, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        }
    }

    /// <summary>Disconnecting: list any overrides this session left and offer to release them first.</summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        // Nothing to ask about: close normally (calling Close() from inside OnClosing is not allowed).
        if (_closeApproved || DataContext is not MainViewModel { HasOverrides: true } vm)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true; // hold the window open while we ask (and possibly release)
        await Task.Yield();
        if (await vm.ResolveOverridesAsync())
        {
            _closeApproved = true;
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as IDisposable)?.Dispose();
        base.OnClosed(e);
    }
}
