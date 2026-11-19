using System.IO;
using System.Text;

namespace LanChat;

/// <summary>
/// 附件临时落盘目录：&lt;程序目录&gt;\logs\attachments\&lt;会话&gt;。
/// 收到的附件直接写入磁盘，不再整份留在内存里，避免大文件把进程撑爆。
/// </summary>
internal sealed class AttachmentStore
{
    private readonly object _sync = new();
    private readonly string _root;
    private bool _created;

    public AttachmentStore()
    {
        _root = Path.Combine(ResolveBaseDirectory(), "attachments", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
    }

    /// <summary>优先使用程序目录下的 logs，不可写时退回 LocalAppData。</summary>
    private static string ResolveBaseDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "logs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanChat", "logs")
        };
        foreach (var candidate in candidates)
        {
            try
            {
                Directory.CreateDirectory(candidate);
                var probe = Path.Combine(candidate, ".write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return candidate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
        }
        return Path.Combine(Path.GetTempPath(), "LanChat", "logs");
    }

    public string Root => _root;

    /// <summary>为一条附件分配一个磁盘路径，扩展名沿用原文件名。</summary>
    public string CreatePath(string? fileName, string fallbackExtension = ".bin")
    {
        lock (_sync)
        {
            EnsureDirectory();
            var extension = Path.GetExtension(fileName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(extension) || extension.Length > 12) extension = fallbackExtension;
            return Path.Combine(_root, Guid.NewGuid().ToString("N") + extension);
        }
    }

    private void EnsureDirectory()
    {
        if (_created) return;
        Directory.CreateDirectory(_root);
        _created = true;
    }

    /// <summary>关闭房间时清理本次会话的临时附件。</summary>
    public void Cleanup()
    {
        lock (_sync)
        {
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            finally { _created = false; }
        }
    }

    /// <summary>启动时清理一天前的残留会话目录（异常退出可能留下）。</summary>
    public static void PurgeStale()
    {
        try
        {
            var baseDir = ResolveBaseDirectory();
            var root = Path.Combine(baseDir, "attachments");
            if (!Directory.Exists(root)) return;
            foreach (var directory in Directory.GetDirectories(root))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-1)) Directory.Delete(directory, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
    }
}
