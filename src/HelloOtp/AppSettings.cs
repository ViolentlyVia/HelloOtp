using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace HelloOtp;

/// <summary>Non-secret preferences: where the icon sits, whether it is minimized to the tray, and where sync files live.</summary>
internal sealed class AppSettings
{
    public string? SyncFolder { get; set; }
    public string? SyncFile { get; set; }
    public bool HideFloatingIcon { get; set; }
    public string? IconDevice { get; set; }
    public int IconOffsetX { get; set; }
    public int IconOffsetY { get; set; }

    private static string PathIn(string directory) => Path.Combine(directory, "settings.json");

    public static AppSettings Load(string directory)
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathIn(directory))) ?? new AppSettings();
        }
        catch (Exception)
        {
            return new AppSettings();
        }
    }

    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(PathIn(directory), JsonSerializer.Serialize(this));
    }
}

/// <summary>The per-user entry that starts the app at sign-in.</summary>
internal static class StartWithWindows
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HelloOtp";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) != null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value)
                key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
