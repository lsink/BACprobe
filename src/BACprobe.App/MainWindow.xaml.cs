using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using BACprobe.Core.Export;
using BACprobe.Core.Settings;
using BACprobe.Core.Writing;

namespace BACprobe.App;

public partial class MainWindow : Window
{
    private bool _closeApproved;

    private void OnOpenLessons(object sender, RoutedEventArgs e) => LessonWindow.Open();

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        (DataContext as MainViewModel)?.SelectFromTree(e.NewValue as Core.Browsing.StructureNode); // still asks about overrides left in place (OnClosing)

    /// <summary>File > Job name and notes: the same drop-down as the Job button.</summary>
    private void OnJobDetails(object sender, RoutedEventArgs e)
    {
        JobPopup.IsOpen = true;
        Dispatcher.BeginInvoke(() => JobNameBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Tools > Check BBMD: straight to the check when a BBMD is entered, otherwise to the box where it goes.</summary>
    private void OnCheckBbmd(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.CheckBbmdCommand.CanExecute(null))
        {
            vm.CheckBbmdCommand.Execute(null);
            return;
        }
        ScanOptionsPopup.IsOpen = true;
        Dispatcher.BeginInvoke(() => BbmdBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnNetworkCheck(object sender, RoutedEventArgs e) => NetworkCheckPopup.IsOpen = true;

    /// <summary>A toolbar button that opens its drop-down (named in its Tag). Clicking outside closes it.</summary>
    private void OnOpenPopup(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name } && FindName(name) is System.Windows.Controls.Primitives.Popup popup) popup.IsOpen = true;
    }

    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(WriteLog.DefaultPath)!;
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open {folder}: {ex.Message}", "BACprobe", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>View > Theme: switch now and remember it.</summary>
    private void OnTheme(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<ThemeChoice>(tag, out var choice)) return;
        ThemeManager.Apply(choice);
        AppSettings.Update(s => s with { Theme = choice });
        ShowThemeChoice();
    }

    private void ShowThemeChoice()
    {
        ThemeSystem.IsChecked = ThemeManager.Choice == ThemeChoice.System;
        ThemeLight.IsChecked = ThemeManager.Choice == ThemeChoice.Light;
        ThemeDark.IsChecked = ThemeManager.Choice == ThemeChoice.Dark;
    }

    public MainWindow()
    {
        InitializeComponent();
        WindowFit.Apply(this);
        ShowThemeChoice();
        if (DataContext is MainViewModel vm)
        {
            // The device list groups by network (this subnet first, then each routed network), in device-number order.
            var devices = CollectionViewSource.GetDefaultView(vm.Devices);
            devices.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DeviceRow.NetworkLabel)));
            devices.SortDescriptions.Add(new SortDescription(nameof(DeviceRow.NetworkSort), ListSortDirection.Ascending));
            devices.SortDescriptions.Add(new SortDescription(nameof(DeviceRow.Instance), ListSortDirection.Ascending));

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
            vm.ConfirmDeviceAction = request =>
            {
                try
                {
                    // Restarts and mutes start with Cancel focused, and the confirm button stays off until the device number is typed.
                    var choice = PromptWindow.Show(this, Prompts.ForDeviceAction(request),
                        [new PromptButton(request.ConfirmLabel), new PromptButton("Cancel", IsCancel: true)], out var password,
                        focusIndex: request.Warning is null ? 0 : 1);
                    return (choice == 0, password);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "BACprobe could not show the confirmation, so nothing was sent." + Environment.NewLine + Environment.NewLine + ex.Message,
                        "Something went wrong", MessageBoxButton.OK, MessageBoxImage.Error);
                    return (false, null);
                }
            };
            vm.ConfirmAlarmListen = requests =>
            {
                try
                {
                    var owner = OwnedWindows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this;
                    return PromptWindow.Show(owner, Prompts.ForAlarmListen(requests),
                        [new PromptButton(requests[0].ConfirmLabel), new PromptButton("Cancel", IsCancel: true)]) == 0;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "BACprobe could not show the confirmation, so nothing was changed." + Environment.NewLine + Environment.NewLine + ex.Message,
                        "Something went wrong", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }
            };
            vm.ConfirmPropertyWrite = request =>
            {
                try
                {
                    // Renaming a point starts with Cancel focused: other systems may find it by name.
                    return PromptWindow.Show(this, Prompts.ForPropertyWrite(request),
                        [new PromptButton(request.ConfirmLabel), new PromptButton("Cancel", IsCancel: true)],
                        focusIndex: request.Warning is null ? 0 : 1) == 0;
                }
                catch (Exception ex)
                {
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
                TrendWindow.PickFile(FileDialogs.ActiveOwner(this), suggested);
            // Trend and alarm windows read through the connection that opened them: close them when it is replaced.
            vm.ConnectionReset += () =>
            {
                foreach (var w in OwnedWindows.OfType<Window>().Where(w => w is TrendWindow or LiveTrendWindow or AlarmsWindow).ToList()) w.Close();
            };
            vm.PickExportFile = suggested =>
            {
                if (FileDialogs.SaveWithType(this, "Export point list", suggested,
                        "Excel workbook (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv|EDE file (*.csv)|*.csv") is not { } picked) return null;
                var format = picked.FilterIndex switch { 1 => ExportFormat.Xlsx, 3 => ExportFormat.Ede, _ => ExportFormat.Csv };
                return (picked.Path, format);
            };
            vm.PickJobSavePath = suggested => FileDialogs.Save(this, "Save job", suggested, "BACprobe job (*.bacprobe)|*.bacprobe", ".bacprobe");
            vm.PickJobOpenPath = () => FileDialogs.Open(this, "Open job", "BACprobe job (*.bacprobe)|*.bacprobe|All files (*.*)|*.*");
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
