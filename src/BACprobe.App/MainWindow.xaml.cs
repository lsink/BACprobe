using System.ComponentModel;
using System.Windows;
using BACprobe.Core.Export;
using BACprobe.Core.Writing;
using Microsoft.Win32;

namespace BACprobe.App;

public partial class MainWindow : Window
{
    private bool _closeApproved;

    private void OnOpenLessons(object sender, RoutedEventArgs e) => LessonWindow.Open();

    public MainWindow()
    {
        InitializeComponent();
        if (DataContext is MainViewModel vm)
        {
            // Every confirmation goes through PromptWindow, so they all look and read the same.
            vm.ConfirmWrite = request =>
            {
                try
                {
                    // Life-safety priorities start with Cancel focused, so a stray Enter cannot confirm.
                    return PromptWindow.Show(this, Prompts.ForWrite(request),
                        [new PromptButton(request.ConfirmLabel), new PromptButton("Cancel", IsCancel: true)],
                        focusIndex: request.Warning is null ? 0 : 1) == 0;
                }
                catch (Exception ex)
                {
                    // Last resort, so a broken dialog never looks like "nothing happened" and never writes without a confirmation.
                    MessageBox.Show(this, "BACprobe could not show the confirmation, so nothing was written." + Environment.NewLine + Environment.NewLine + ex.Message,
                        "Something went wrong", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }
            };
            vm.ConfirmOutOfService = request =>
            {
                try
                {
                    return PromptWindow.Show(this, Prompts.ForOutOfService(request),
                        [new PromptButton(request.ConfirmLabel), new PromptButton("Cancel", IsCancel: true)]) == 0;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "BACprobe could not show the confirmation, so nothing was changed." + Environment.NewLine + Environment.NewLine + ex.Message,
                        "Something went wrong", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }
            };
            vm.ConfirmAck = request =>
            {
                try
                {
                    // Over the alarm list when that is where the tech pressed Acknowledge. A life-safety alarm starts with Cancel focused.
                    var owner = OwnedWindows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this;
                    return PromptWindow.Show(owner, Prompts.ForAck(request),
                        [new PromptButton(request.ConfirmLabel), new PromptButton("Cancel", IsCancel: true)],
                        focusIndex: request.Warning is null ? 0 : 1) == 0;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "BACprobe could not show the confirmation, so nothing was sent." + Environment.NewLine + Environment.NewLine + ex.Message,
                        "Something went wrong", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }
            };
            vm.ShowExplanation = content => PromptWindow.Show(this, content, [new PromptButton("OK", IsCancel: true)]);
            vm.AskOverrides = overrides => PromptWindow.Show(this, Prompts.OverridesInPlace(overrides),
                overrides.Count == 1
                    ? [new PromptButton("Release it"), new PromptButton("Leave it in place"), new PromptButton("Go back", IsCancel: true)]
                    : [new PromptButton("Release them"), new PromptButton("Leave them in place"), new PromptButton("Go back", IsCancel: true)]) switch
            {
                0 => OverrideChoice.Release,
                1 => OverrideChoice.Leave,
                _ => OverrideChoice.GoBack,
            };
            vm.ConfirmContinueAfterFailedRelease = failed => PromptWindow.Show(this, Prompts.ReleaseFailed(failed),
                [new PromptButton("Go back", IsCancel: true), new PromptButton("Continue anyway")]) == 1;
            vm.ShowFind = find => new FindWindow(find) { Owner = this }.Show();
            vm.ShowTrend = trend => new TrendWindow(trend) { Owner = this }.Show();
            vm.ShowLiveTrend = trend => new LiveTrendWindow(trend) { Owner = this }.Show();
            vm.ShowBbmdCheck = check => new BbmdCheckWindow(check) { Owner = this }.Show();
            vm.ShowWatch = watch => new WatchWindow(watch) { Owner = this }.Show();
            vm.ShowCompare = compare => new CompareWindow(compare) { Owner = this }.Show();
            vm.ShowAlarms = alarms => new AlarmsWindow(alarms) { Owner = this }.Show();
            vm.ShowMstp = mstp => new MstpWindow(mstp) { Owner = this }.Show();
            vm.PickTrendFile = suggested =>
                TrendWindow.PickFile(OwnedWindows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this, suggested);
            // Trend and alarm windows read through the connection that opened them: close them when it is replaced.
            vm.ConnectionReset += () =>
            {
                foreach (var w in OwnedWindows.OfType<Window>().Where(w => w is TrendWindow or LiveTrendWindow or AlarmsWindow).ToList()) w.Close();
            };
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
            vm.PickJobSavePath = suggested =>
            {
                var dialog = new SaveFileDialog
                {
                    Title = "Save job",
                    FileName = suggested,
                    Filter = "BACprobe job (*.bacprobe)|*.bacprobe",
                    DefaultExt = ".bacprobe",
                    AddExtension = true,
                    OverwritePrompt = true,
                };
                return dialog.ShowDialog(this) == true ? dialog.FileName : null;
            };
            vm.PickJobOpenPath = () =>
            {
                var dialog = new OpenFileDialog
                {
                    Title = "Open job",
                    Filter = "BACprobe job (*.bacprobe)|*.bacprobe|All files (*.*)|*.*",
                    CheckFileExists = true,
                };
                return dialog.ShowDialog(this) == true ? dialog.FileName : null;
            };
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
