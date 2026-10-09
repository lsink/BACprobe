using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using BACprobe.Core.Alarms;
using BACprobe.Core.Bbmd;
using BACprobe.Core.Browsing;
using BACprobe.Core.Discovery;
using BACprobe.Core.Export;
using BACprobe.Core.Jobs;
using BACprobe.Core.Learning;
using BACprobe.Core.Live;
using BACprobe.Core.Networking;
using BACprobe.Core.Writing;
using System.IO.BACnet;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BACprobe.App;

public sealed class AdapterChoice(AdapterInfo info)
{
    public AdapterInfo Info { get; } = info;
    public string Display => $"{Info.Address}  -  {Info.Name} ({Info.Cidr}){(Info.IsUp ? "" : " [down]")}{(Info.IsVirtual ? " [virtual]" : "")}";
}

public sealed class PreflightRow(PreflightResult r)
{
    public string Icon { get; } = r.Severity switch { PreflightSeverity.Pass => "✔", PreflightSeverity.Warning => "⚠", _ => "✖" };
    public string Check { get; } = r.Check;
    public string Message { get; } = r.Message;
    public string? Help { get; } = r.LikelyCause is null ? null : $"Likely cause: {r.LikelyCause}\nNext step: {r.NextStep}";
    public bool HasHelp => Help is not null;
    public PreflightSeverity Severity { get; } = r.Severity;
}

/// <summary>One line of the network check: what is wrong, why it is probably wrong, and what to do.</summary>
public sealed class FindingRow(NetworkFinding f)
{
    public string Icon { get; } = f.Severity switch { FindingSeverity.Problem => "✖", FindingSeverity.Warning => "⚠", _ => "ℹ" };
    public FindingSeverity Severity { get; } = f.Severity;
    public string Title { get; } = f.Title;
    public string Detail { get; } = f.Detail;
    public string Help { get; } = $"Likely cause: {f.LikelyCause}{Environment.NewLine}Next step: {f.NextStep}";
    private readonly Lesson? _lesson = Lessons.ForFinding(f);
    public string? LessonId => _lesson?.Id;
    public string LessonText => _lesson is null ? "" : $"Learn more: {_lesson.Title}";
    public Visibility LessonVisibility => _lesson is null ? Visibility.Collapsed : Visibility.Visible;
}

public sealed class DeviceRow(DiscoveredDevice d) : ObservableObject
{
    public DiscoveredDevice Device => d;
    public uint Instance => d.InstanceId;
    public string Address => d.AddressText;
    public string Vendor => d.VendorName ?? $"vendor {d.VendorId}";
    public string Model => d.ModelName ?? "-";
    public string Firmware => d.FirmwareRevision ?? "-";
    public string Name => d.ObjectName ?? "-";
    public string DisplayName => d.DisplayName;
    public string ExportName => d.ExportName;
    /// <summary>"1001 - AHU-1", for pickers.</summary>
    public string Label => $"{d.InstanceId} - {d.ObjectName ?? "unnamed"}";
    public string Response => d.ResponseTime is { } r ? $"{r.TotalMilliseconds:0} ms" : "-";
    public string Segmentation => d.Segmentation switch
    {
        BacnetSegmentations.SEGMENTATION_BOTH => "both",
        BacnetSegmentations.SEGMENTATION_TRANSMIT => "send only",
        BacnetSegmentations.SEGMENTATION_RECEIVE => "receive only",
        _ => "none",
    };
    public string MaxApdu => d.MaxApdu.ToString();
    public string Clock => d.ClockSkew is { } s ? (s.Duration() < DeviceHealth.ClockSkewLimit ? "ok" : DeviceHealth.ShortSkew(s)) : "-";

    /// <summary>The group the device list shows it under: this subnet first, then each routed network.</summary>
    public string NetworkLabel => d.Network == 0 ? "This network" : $"Network {d.Network}";
    public int NetworkSort => d.Network;

    /// <summary>Hover text in the device list, which only has room for the number and name.</summary>
    public string Tooltip => $"{Address}{Environment.NewLine}{Vendor}  {Model}  {Firmware}";

    /// <summary>The device's details were read: show them.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>One entry in the state dropdown ("On (Active)", "Standby (3)"): what the tech sees, and the text the write parser understands.</summary>
public sealed record StateChoice(string Label, string Text);

public sealed class ObjectRow(ObjectSummary s) : ObservableObject
{
    private ObjectSummary _s = s;
    public ObjectSummary Summary => _s;
    public string Id => $"{BacnetNames.ObjectTypeShort(_s.Id.type)} {_s.Id.instance}";
    public string Type => _s.TypeName;
    public string Name => _s.Name ?? "-";
    public string Value => _s.ValueText;
    public string Description => _s.Description ?? "";
    public bool IsOverridden => _s.IsOverridden;
    public string Override => _s.OverrideText;
    public string OverrideTooltip => _s.OverrideTooltip;
    public bool HasProblem => _s.HasProblem;
    /// <summary>"Fault: open loop", "In alarm", "Out of service"; empty for a healthy point.</summary>
    public string Status => _s.ProblemText;
    /// <summary>Each problem with its likely cause and next step.</summary>
    public string StatusTooltip => _s.ProblemTooltip;

    private bool _recentlyChanged;

    /// <summary>True for a moment after a live refresh changed this point, so the eye can find it.</summary>
    public bool RecentlyChanged
    {
        get => _recentlyChanged;
        private set => SetProperty(ref _recentlyChanged, value);
    }

    public void MarkChanged() => _ = FlashAsync();

    private async Task FlashAsync()
    {
        RecentlyChanged = true;
        await Task.Delay(1500);
        RecentlyChanged = false;
    }

    /// <summary>Swap in freshly read values (e.g. after a write) without losing the grid selection.</summary>
    public void Refresh(ObjectSummary fresh)
    {
        _s = fresh;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>One choice in the object list's Show filter; a null test means every point.</summary>
public sealed record PointFilterChoice(string Label, Func<ObjectSummary, bool>? Test);

/// <summary>What the user chose when asked about overrides they are about to walk away from.</summary>
public enum OverrideChoice { Release, Leave, GoBack }
