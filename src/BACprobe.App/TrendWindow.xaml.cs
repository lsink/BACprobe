using System.Windows;
using BACprobe.Core.Export;
using Microsoft.Win32;

namespace BACprobe.App;

/// <summary>A trend log's history as a chart and a table. Opens next to the main window and can stay open while you keep working.</summary>
public partial class TrendWindow : Window
{
    public TrendWindow(TrendViewModel viewModel)
    {
        InitializeComponent();
        WindowFit.Apply(this);
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.LoadAsync();
        Closed += (_, _) => viewModel.Dispose();
    }

    /// <summary>The Save dialog for trend data: Excel or CSV only.</summary>
    public static (string Path, ExportFormat Format)? PickFile(Window owner, string suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export trend data",
            FileName = suggestedName,
            Filter = "Excel workbook (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(owner) != true) return null;
        return (dialog.FileName, dialog.FilterIndex == 2 ? ExportFormat.Csv : ExportFormat.Xlsx);
    }
}
