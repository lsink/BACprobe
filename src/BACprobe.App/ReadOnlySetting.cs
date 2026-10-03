using System.IO;

namespace BACprobe.App;

/// <summary>
/// Remembers the read-only switch between runs. Read-only is the default: BACprobe starts safe, and a tech has to untick it
/// to write. The choice to write is remembered as a flag file next to the write log; if it cannot be read, read-only wins.
/// </summary>
internal static class ReadOnlySetting
{
    private static string FlagPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BACprobe", "read-write.flag");

    public static bool Load()
    {
        try { return !File.Exists(FlagPath); }
        catch (Exception) { return true; }
    }

    public static void Save(bool readOnly)
    {
        try
        {
            if (!readOnly)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FlagPath)!);
                File.WriteAllText(FlagPath, "on");
            }
            else if (File.Exists(FlagPath)) File.Delete(FlagPath);
        }
        catch (Exception) { /* a setting that cannot be saved just resets to read-only next run */ }
    }
}
