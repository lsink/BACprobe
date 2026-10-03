using System.IO;

namespace BACprobe.App;

/// <summary>Remembers the read-only switch between runs: a flag file next to the write log. Absent or unreadable means off.</summary>
internal static class ReadOnlySetting
{
    private static string FlagPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BACprobe", "read-only.flag");

    public static bool Load()
    {
        try { return File.Exists(FlagPath); }
        catch (Exception) { return false; }
    }

    public static void Save(bool on)
    {
        try
        {
            if (on)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FlagPath)!);
                File.WriteAllText(FlagPath, "on");
            }
            else if (File.Exists(FlagPath)) File.Delete(FlagPath);
        }
        catch (Exception) { /* a setting that cannot be saved just resets next run */ }
    }
}
