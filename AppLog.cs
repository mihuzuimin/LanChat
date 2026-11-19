using System.IO;
using System.Text;

namespace LanChat;

internal static class AppLog
{
    private static readonly object Sync = new();
    private static readonly List<string> History = new();
    private static readonly DateTime StartedAt = DateTime.Now;
    private static StreamWriter? _writer;
    private static string? _sessionPath;
    private static bool _sessionClosed;
    public static event Action<string>? EntryAdded;

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        lock (Sync)
        {
            History.Add(line);
            AppendToFile(line);
        }
        EntryAdded?.Invoke(line);
    }

    public static string[] GetSnapshot()
    {
        lock (Sync) return History.ToArray();
    }

    /// <summary>程序正常退出：补一条退出记录并把日志落盘。</summary>
    public static void SaveRuntimeLog()
    {
        lock (Sync)
        {
            History.Add($"{DateTime.Now:HH:mm:ss}  程序退出。");
            AppendToFile($"{DateTime.Now:HH:mm:ss}  程序退出。");
            CloseWriter();
        }
    }

    /// <summary>
    /// 崩溃时调用：日志此前已逐行写入磁盘，这里再补一段崩溃详情，
    /// 并单独生成 LanChat-crash-时间戳.txt，确保闪退也能留下记录。
    /// fatal = false 表示异常已被上层接住、程序会继续运行，日志文件保持可写。
    /// </summary>
    public static void SaveCrashLog(Exception? exception, string source, bool fatal = true)
    {
        var stamp = $"{DateTime.Now:yyyyMMdd-HHmmss}";
        var detail = BuildCrashText(exception, source, fatal);
        lock (Sync)
        {
            AppendToFile(detail);
            if (fatal) CloseWriter();
            foreach (var directory in GetLogDirectories())
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    var name = fatal ? $"LanChat-crash-{stamp}.txt" : $"LanChat-error-{stamp}.txt";
                    File.AppendAllText(Path.Combine(directory, name), detail, new UTF8Encoding(true));
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
            }
        }
    }

    private static string BuildCrashText(Exception? exception, string source, bool fatal)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"===== {(fatal ? "崩溃记录" : "异常记录")} {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
        builder.AppendLine($"来源：{source}");
        builder.AppendLine($"程序版本：{typeof(AppLog).Assembly.GetName().Version}");
        builder.AppendLine($"进程：{Environment.ProcessPath}");
        builder.AppendLine($"运行时长：{DateTime.Now - StartedAt:hh\\:mm\\:ss}");
        if (exception is null)
        {
            builder.AppendLine("异常：未知（无 Exception 对象）");
        }
        else
        {
            var current = exception;
            var depth = 0;
            while (current is not null && depth < 5)
            {
                builder.AppendLine($"异常[{depth}]：{current.GetType().FullName}: {current.Message}");
                builder.AppendLine($"HResult：0x{current.HResult:X8}");
                builder.AppendLine("堆栈：");
                builder.AppendLine(current.StackTrace ?? "(无堆栈)");
                current = current.InnerException;
                depth++;
            }
        }
        builder.AppendLine("===== 记录结束 =====");
        return builder.ToString();
    }

    /// <summary>把一行日志直接追加到会话日志文件；写入失败不影响程序运行。</summary>
    private static void AppendToFile(string line)
    {
        if (_sessionClosed) return;
        try
        {
            var writer = EnsureWriter();
            if (writer is null) return;
            writer.WriteLine(line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ObjectDisposedException)
        {
            // 磁盘写入失败时只保留内存记录，绝不因为日志影响程序行为。
            _writer = null;
        }
    }

    private static StreamWriter? EnsureWriter()
    {
        if (_writer is not null) return _writer;
        foreach (var directory in GetLogDirectories())
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"LanChat-runtime-{StartedAt:yyyyMMdd-HHmmss}.txt");
                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(stream, new UTF8Encoding(true)) { AutoFlush = true };
                _sessionPath = path;
                return _writer;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
        }
        return null;
    }

    private static void CloseWriter()
    {
        _sessionClosed = true;
        try { _writer?.Flush(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        try { _writer?.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        _writer = null;
    }

    public static string? SessionLogPath => _sessionPath;

    /// <summary>优先写入程序目录下的 logs 文件夹，不可写时退回 LocalAppData。</summary>
    private static IEnumerable<string> GetLogDirectories()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "logs");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanChat", "logs");
    }
}
