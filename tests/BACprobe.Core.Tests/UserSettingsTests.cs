using BACprobe.Core.Settings;

namespace BACprobe.Core.Tests;

public sealed class UserSettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bacprobe-settings-" + Guid.NewGuid().ToString("N"));

    public UserSettingsTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void A_first_run_starts_read_only_and_following_windows()
    {
        var s = UserSettingsStore.Load(_folder);
        Assert.True(s.ReadOnly);
        Assert.Equal(ThemeChoice.System, s.Theme);
    }

    [Fact]
    public void Settings_survive_a_restart()
    {
        Assert.True(UserSettingsStore.Save(_folder, new UserSettings { ReadOnly = false, Theme = ThemeChoice.Dark }));
        var s = UserSettingsStore.Load(_folder);
        Assert.False(s.ReadOnly);
        Assert.Equal(ThemeChoice.Dark, s.Theme);
        Assert.Contains("\"Dark\"", File.ReadAllText(Path.Combine(_folder, UserSettingsStore.FileName))); // readable by a person
    }

    [Fact]
    public void The_old_read_write_flag_is_taken_over_then_removed()
    {
        File.WriteAllText(Path.Combine(_folder, UserSettingsStore.LegacyFlagName), "on");
        var s = UserSettingsStore.Load(_folder);
        Assert.False(s.ReadOnly);

        UserSettingsStore.Save(_folder, s with { Theme = ThemeChoice.Light });
        Assert.False(File.Exists(Path.Combine(_folder, UserSettingsStore.LegacyFlagName)));
        Assert.False(UserSettingsStore.Load(_folder).ReadOnly);
    }

    [Fact]
    public void A_damaged_file_means_read_only()
    {
        File.WriteAllText(Path.Combine(_folder, UserSettingsStore.FileName), "{ not json");
        Assert.True(UserSettingsStore.Load(_folder).ReadOnly);
        File.WriteAllText(Path.Combine(_folder, UserSettingsStore.FileName), "{\"Theme\":\"Purple\"}");
        Assert.True(UserSettingsStore.Load(_folder).ReadOnly);
    }
}
