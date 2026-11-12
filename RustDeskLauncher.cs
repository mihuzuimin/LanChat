using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace LanChat;

/// <summary>调用 RustDesk：优先进程目录 Tools\rustdesk.exe，否则从内嵌资源解压到本地缓存后启动。</summary>
internal static class RustDeskLauncher
{
    private const string ResourceName = "LanChat.Resources.rustdesk.exe";
    private static string? _cachedPath;

    /// <summary>RustDesk 是否可用（进程目录或内嵌资源任一存在）。</summary>
    public static bool IsAvailable => ResolveExecutable() is not null;

    /// <summary>查询本机 RustDesk ID（用于对方走 ID 中继连接）：在 timeout 内轮询 `rustdesk --get-id`，返回纯数字 ID 或 null。</summary>
    public static async Task<string?> TryGetIdAsync(TimeSpan timeout, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
        {
            // 优先 %LOCALAPPDATA%\rustdesk 的真实副本：Tools\rustdesk.exe 是自解压 stub，实测不透传 --get-id 输出。
            foreach (var candidate in CandidateIdExecutables())
            {
                var id = await RunGetIdAsync(candidate, token);
                if (id is not null) return id;
                if (DateTime.UtcNow >= deadline || token.IsCancellationRequested) break;
            }
            try { await Task.Delay(700, token); }
            catch (OperationCanceledException) { return null; }
        }
        return null;
    }

    private static IEnumerable<string> CandidateIdExecutables()
    {
        var installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rustdesk", "rustdesk.exe");
        if (File.Exists(installed)) yield return installed;
        var resolved = ResolveExecutable();
        if (resolved is not null && !string.Equals(resolved, installed, StringComparison.OrdinalIgnoreCase))
            yield return resolved;
    }

    private static async Task<string?> RunGetIdAsync(string executable, CancellationToken token)
    {
        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo(executable, "--get-id")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!
            };
            process = Process.Start(startInfo);
            if (process is null) return null;
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(2.5));
            try
            {
                var output = await process.StandardOutput.ReadToEndAsync(readTimeout.Token);
                // 只接受"整行纯数字"（ID 形如 165237370），防止把日志里的零散数字拼成假 ID。
                foreach (var line in output.Split('\n'))
                {
                    var text = line.Trim();
                    if (text.Length >= 5 && text.All(char.IsDigit))
                    {
                        AppLog.Write($"已获取本机 RustDesk ID：{text}（{Path.GetFileName(executable)}）。");
                        return text;
                    }
                }
            }
            catch (OperationCanceledException) { }
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Write($"获取 RustDesk ID 异常（{executable}）：{ex.Message}");
            return null;
        }
        finally
        {
            if (process is not null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                process.Dispose();
            }
        }
    }

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
