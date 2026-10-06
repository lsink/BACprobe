using BACprobe.Core.Settings;

namespace BACprobe.App;

/// <summary>The app's remembered settings (read-only switch, theme), loaded once and saved on every change.</summary>
internal static class AppSettings
{
    public static UserSettings Current { get; private set; } = UserSettingsStore.Load(UserSettingsStore.DefaultFolder);

    public static void Update(Func<UserSettings, UserSettings> change)
    {
        Current = change(Current);
        UserSettingsStore.Save(UserSettingsStore.DefaultFolder, Current); // if it cannot be saved, it resets to the safe default next run
    }
}
