using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace LanChat;

/// <summary>调用 RustDesk：优先进程目录 Tools\rustdesk.exe，否则从内嵌资源解压到本地缓存后启动。</summary>
internal static class RustDeskLauncher
{
    private const string ResourceName = "LanChat.Resources.rustdesk.exe";
    private static string? _cachedPath;

    public static bool Start(string? arguments = null)
    {
        var executable = ResolveExecutable();
        if (executable is null)
        {
            AppLog.Write("未找到 RustDesk（内嵌资源解压失败且本地未安装）。");
            return false;
        }
        try
        {
            var startInfo = new ProcessStartInfo(executable, arguments ?? string.Empty)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!
            };
            Process.Start(startInfo);
            AppLog.Write($"已启动 RustDesk：{executable} {arguments}".TrimEnd());
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write($"启动 RustDesk 失败：{ex.Message}");
            return false;
        }
    }

    private static string? ResolveExecutable()
    {
        if (_cachedPath is not null && File.Exists(_cachedPath)) return _cachedPath;

        var bundled = Path.Combine(AppContext.BaseDirectory, "Tools", "rustdesk.exe");
        if (File.Exists(bundled))
        {
            _cachedPath = bundled;
            return bundled;
        }

        var cached = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanChat", "rustdesk", "rustdesk.exe");
        if (File.Exists(cached) && new FileInfo(cached).Length > 10 * 1024 * 1024)
        {
            _cachedPath = cached;
            return cached;
        }

        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var source = assembly.GetManifestResourceStream(ResourceName)
                ?? assembly.GetManifestResourceNames()
                    .Where(name => name.Contains("rustdesk", StringComparison.OrdinalIgnoreCase))
                    .Select(assembly.GetManifestResourceStream)
                    .FirstOrDefault(stream => stream is not null);
            if (source is null) return null;

            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            var temporary = cached + ".tmp";
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                source.CopyTo(file);
            }
            File.Move(temporary, cached, overwrite: true);
            AppLog.Write($"已从内置资源解压 RustDesk 到 {cached}（{new FileInfo(cached).Length:N0} 字节）。");
            _cachedPath = cached;
            return cached;
        }
        catch (Exception ex)
        {
            AppLog.Write($"解压内置 RustDesk 失败：{ex.Message}");
            return null;
        }
    }
}
