using System.Windows;

namespace BACprobe.App;

/// <summary>Shows a BBMD's tables and what looks wrong with them. Read-only.</summary>
public partial class BbmdCheckWindow : Window
{
    public BbmdCheckWindow(BbmdCheckViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.CheckAsync();
    }
}
