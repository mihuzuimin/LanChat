using System.IO;
using System.Text.Json;

namespace LanChat;

internal static class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanChat", "settings.json");
    private static Settings? _cached;

    private static Settings Load()
    {
        if (_cached is not null) return _cached;
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
                if (loaded is not null)
                {
                    _cached = loaded;
                    return _cached;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        _cached = new Settings
        {
            DownloadFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        };
        return _cached;
    }

    private static void Save()
    {
        if (_cached is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_cached));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static string DownloadFolder
    {
        get => Load().DownloadFolder ?? string.Empty;
        set
        {
            Load().DownloadFolder = value;
            Save();
        }
    }

    /// <summary>深色模式。</summary>
    public static bool DarkMode
    {
        get => Load().DarkMode;
        set
        {
            Load().DarkMode = value;
            Save();
        }
    }

    /// <summary>启动时是否自动在局域网内检查更新（默认开启）。</summary>
    public static bool AutoCheckUpdate
    {
        get => Load().AutoCheckUpdate;
        set
        {
            Load().AutoCheckUpdate = value;
            Save();
        }
    }

    public static void SaveNow() => Save();

    private sealed class Settings
    {
        public string? DownloadFolder { get; set; }
        public bool AutoCheckUpdate { get; set; } = true;
        public bool DarkMode { get; set; }
    }
}
