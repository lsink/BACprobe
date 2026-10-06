using System.Text.Json;
using System.Text.Json.Serialization;

namespace BACprobe.Core.Settings;

/// <summary>Light or dark, or whatever Windows is set to.</summary>
public enum ThemeChoice { System, Light, Dark }

/// <summary>What the app remembers between runs. Read-only is the default: BACprobe starts safe.</summary>
public sealed record UserSettings
{
    public bool ReadOnly { get; init; } = true;
    public ThemeChoice Theme { get; init; } = ThemeChoice.System;
}

/// <summary>
/// Keeps <see cref="UserSettings"/> in settings.json next to the write log. A file that is missing, unreadable or damaged gives
/// the defaults (read-only), never a half-read setting. The old read-write.flag (present = writes allowed) is taken over once.
/// </summary>
public static class UserSettingsStore
{
    public const string FileName = "settings.json";
    public const string LegacyFlagName = "read-write.flag";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BACprobe");

    public static UserSettings Load(string folder)
    {
        try
        {
            var path = Path.Combine(folder, FileName);
            if (File.Exists(path)) return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path), Json) ?? new UserSettings();
            // Before settings.json, the only setting was "writes allowed", kept as the presence of a flag file.
            return new UserSettings { ReadOnly = !File.Exists(Path.Combine(folder, LegacyFlagName)) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new UserSettings();
        }
    }

    /// <summary>Returns false if it could not be saved; the app carries on, and the setting resets to the default next run.</summary>
    public static bool Save(string folder, UserSettings settings)
    {
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(settings, Json));
            var legacy = Path.Combine(folder, LegacyFlagName);
            if (File.Exists(legacy)) File.Delete(legacy); // settings.json is the one place now
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
