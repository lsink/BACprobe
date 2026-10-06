using System.Windows;
using BACprobe.Core.Trends;
using Microsoft.Win32;

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

    /// <summary>Chart the selected rows together (or every row when one or none is selected), up to <see cref="MultiTrend.MaxSeries"/>.</summary>
    private void OnChart(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WatchViewModel vm) return;
        var picked = RowsGrid.SelectedItems.OfType<WatchRow>().ToList();
        var rows = picked.Count >= 2 ? picked : vm.Rows.ToList();
        if (rows.Count == 0)
        {
            vm.Status = "The watch list is empty. Add points with Watch in the main window first.";
            return;
        }
        if (rows.Count > MultiTrend.MaxSeries)
        {
            vm.Status = $"A chart shows at most {MultiTrend.MaxSeries} lines. Select up to {MultiTrend.MaxSeries} rows (Ctrl+click), then Chart.";
            return;
        }
        new MultiTrendWindow(new MultiTrendViewModel(rows, PickCsv)) { Owner = this }.Show();
    }

    private string? PickCsv(string suggested)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export chart",
            FileName = suggested,
            Filter = "CSV (*.csv)|*.csv",
            DefaultExt = ".csv",
            AddExtension = true,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(OwnedWindows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this) == true ? dialog.FileName : null;
    }
}
