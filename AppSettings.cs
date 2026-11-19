using System.Text.Json;
using System.IO;

namespace LanChat;

internal static class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanChat", "settings.json");
    private static string? _downloadFolder;
    private static bool? _sendOnEnter;
    private static bool _settingsLoaded;

    public static string DownloadFolder
    {
        get
        {
            EnsureLoaded();
            _downloadFolder ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            return _downloadFolder;
        }
        set
        {
            EnsureLoaded();
            _downloadFolder = value;
            Save();
        }
    }

    public static bool SendOnEnter
    {
        get
        {
            EnsureLoaded();
            return _sendOnEnter ?? true;
        }
        set
        {
            EnsureLoaded();
            _sendOnEnter = value;
            Save();
        }
    }

    private static void EnsureLoaded()
    {
        if (_settingsLoaded) return;
        _settingsLoaded = true;
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
            _downloadFolder = settings?.DownloadFolder;
            _sendOnEnter = settings?.SendOnEnter;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings(_downloadFolder, _sendOnEnter)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed record Settings(string? DownloadFolder, bool? SendOnEnter);
}
