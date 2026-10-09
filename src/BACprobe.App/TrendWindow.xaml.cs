using System.Windows;
using BACprobe.Core.Export;

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
        if (FileDialogs.SaveWithType(owner, "Export trend data", suggestedName, "Excel workbook (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv") is not { } picked)
            return null;
        return (picked.Path, picked.FilterIndex == 2 ? ExportFormat.Csv : ExportFormat.Xlsx);
    }
}
