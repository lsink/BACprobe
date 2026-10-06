using System.Windows;

namespace BACprobe.App;

/// <summary>Two devices side by side: what is set differently, and one point's properties on both. Read-only.</summary>
public partial class CompareWindow : Window
{
    public CompareWindow(CompareViewModel viewModel)
    {
        InitializeComponent();
        WindowFit.Apply(this);
        DataContext = viewModel;
        Loaded += (_, _) => { if (viewModel.CompareCommand.CanExecute(null)) viewModel.CompareCommand.Execute(null); };
    }
}
