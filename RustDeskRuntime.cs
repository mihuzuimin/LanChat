using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace LanChat;

internal enum RustDeskHostState
{
    Ready,
    Unavailable,
    ConfigUnavailable,
    StartFailed,
    NotListening
}

/// <summary>
/// 内置 RustDesk 1.5.3 的启动、直连（直接 IP 访问）与连接封装。
/// LanChat 只负责申请、同意和会话状态，屏幕采集与键鼠注入全部交给 RustDesk 原生客户端。
/// </summary>
internal static class RustDeskRuntime
{
    public const int DirectAccessPort = 21118;

    private const string ResourcePrefix = "LanChat.RustDesk.";
    private const string DirectServerOption = "direct-server";
    private static readonly Lazy<string> Executable = new(() => ExtractResource("rustdesk-1.5.3-x86_64.exe", "RustDesk.exe"));
    private static readonly object SettingsLock = new();
    private static string? _optionsPath;
    private static string? _originalOptions;
    private static bool _originalOptionsExisted;
    private static bool _hostStartedByUs;
    private static bool _clientStartedByUs;
    private static string? _cachedId;
    private static readonly Dictionary<string, string> _clientSessions = new(StringComparer.Ordinal);

    public static bool IsRunning()
    {
        var processes = Process.GetProcessesByName("rustdesk");
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public static bool IsDirectAccessListening()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == DirectAccessPort);
        }
        catch (NetworkInformationException) { return false; }
    }

    /// <summary>
    /// 确保 RustDesk 正在运行，并且已在 21118 上监听直连。
    /// 需要时会把 RustDesk 配置中的 direct-server 打开；返回前已确认端口可用。
    /// </summary>
    public static async Task<RustDeskHostState> EnsureHostReadyAsync(CancellationToken cancellationToken)
    {
        if (IsDirectAccessListening()) return RustDeskHostState.Ready;
        if (!TryEnableDirectAccess(out var configError))
        {
            if (configError is not null) AppLog.Write($"启用 RustDesk 直接 IP 访问失败：{configError}");
            return RustDeskHostState.ConfigUnavailable;
        }
        if (!IsRunning())
        {
            try { StartHost(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            {
                AppLog.Write($"RustDesk 启动失败：{ex.Message}");
                RestoreDirectAccessOption();
                return RustDeskHostState.Unavailable;
            }
        }
        if (await WaitForDirectAccessAsync(TimeSpan.FromSeconds(30), cancellationToken)) return RustDeskHostState.Ready;
        RestoreDirectAccessOption();
        return RustDeskHostState.NotListening;
    }

    public static async Task<bool> WaitForDirectAccessAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsDirectAccessListening()) return true;
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }
        return IsDirectAccessListening();
    }

    /// <summary>
    /// 启动 RustDesk 以便它能在另一个实例已经在运行时接管连接请求。
    /// 全新启动的客户端需要先建立窗口，之后 --connect 的参数才会被转交给界面进程。
    /// </summary>
    public static async Task<bool> EnsureRunningAsync(CancellationToken cancellationToken)
    {
        if (IsRunning()) return true;
        try { Start(new[] { "--tray" }); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            AppLog.Write($"RustDesk 启动失败：{ex.Message}");
            return false;
        }
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsRunning())
            {
                _clientStartedByUs = true;
                await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken).ConfigureAwait(false);
                return true;
            }
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }
        _clientStartedByUs = IsRunning();
        return _clientStartedByUs;
    }

    /// <summary>记录一个由本机发起、正在使用 RustDesk 的远控会话。</summary>
    public static void RegisterClientSession(string sessionId, string targetIp)
    {
        lock (SettingsLock) _clientSessions[sessionId] = targetIp;
    }

    /// <summary>
    /// 控制端结束远控后关闭对应会话窗口；如果 RustDesk 是本机由 LanChat 启动的，
    /// 这个会话又是最后一个，则直接结束 RustDesk 进程，避免主窗口残留在任务栏。
    /// 用户自己启动的 RustDesk 只关闭这次打开的连接窗口。
    /// </summary>
    public static void EndClientSession(string sessionId, string? targetIp = null)
    {
        string? ip = targetIp;
        int remaining;
        lock (SettingsLock)
        {
            if (_clientSessions.TryGetValue(sessionId, out var registered)) ip = registered;
            _clientSessions.Remove(sessionId);
            remaining = _clientSessions.Count;
        }
        if (remaining > 0)
        {
            if (ip is not null) CloseRemoteSessionWindow(ip);
            return;
        }
        if (_clientStartedByUs && !_hostStartedByUs)
        {
            _clientStartedByUs = false;
            CloseRustDeskProcesses();
            return;
        }
        if (ip is not null) CloseRemoteSessionWindow(ip);
    }

    public static Process? StartHost()
    {
        var running = IsRunning();
        var process = Start();
        if (!running && process is not null) _hostStartedByUs = true;
        return process;
    }

    /// <summary>结束被控会话时关闭由 LanChat 启动的 RustDesk；用户自己运行的实例不受影响。</summary>
    public static void StopHostIfStartedByUs()
    {
        if (!_hostStartedByUs) return;
        _hostStartedByUs = false;
        CloseRustDeskProcesses();
    }

    private static void CloseRustDeskProcesses()
    {
        _ = Task.Run(() =>
        {
            var processes = Process.GetProcessesByName("rustdesk");
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        if (!process.HasExited) process.CloseMainWindow();
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1.5);
                while (DateTime.UtcNow < deadline && processes.Any(process => !IsExited(process)))
                    Thread.Sleep(100);
                foreach (var process in processes)
                {
                    try
                    {
                        if (!process.HasExited) process.Kill(true);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                    {
                        AppLog.Write($"关闭 RustDesk 失败：{ex.Message}");
                    }
                }
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        });
    }

    private static bool IsExited(Process process)
    {
        try { return process.HasExited; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return true; }
    }

    private const uint WmClose = 0x0010;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLengthW(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    /// <summary>只关闭指定 IP 的 RustDesk 远控窗口，用于用户自己启动的 RustDesk。</summary>
    private static void CloseRemoteSessionWindow(string ip)
    {
        var processes = Process.GetProcessesByName("rustdesk");
        try
        {
            if (processes.Length == 0) return;
            var ids = new HashSet<uint>(processes.Select(process => (uint)process.Id));
            var windows = new List<IntPtr>();
            EnumWindows((window, _) =>
            {
                if (!IsWindowVisible(window)) return true;
                GetWindowThreadProcessId(window, out var processId);
                if (!ids.Contains(processId)) return true;
                var length = GetWindowTextLengthW(window);
                if (length <= 0) return true;
                var buffer = new StringBuilder(length + 1);
                GetWindowTextW(window, buffer, buffer.Capacity);
                var title = buffer.ToString();
                if (title.Contains(ip, StringComparison.OrdinalIgnoreCase) &&
                    (title.Contains("Remote Desktop", StringComparison.OrdinalIgnoreCase) || title.Contains("远程桌面", StringComparison.Ordinal)))
                {
                    windows.Add(window);
                }
                return true;
            }, IntPtr.Zero);
            foreach (var window in windows) PostMessageW(window, WmClose, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write($"关闭 RustDesk 远控窗口失败：{ex.Message}");
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static Process? Start(string[]? arguments = null)
    {
        var start = new ProcessStartInfo(Executable.Value)
        {
            WorkingDirectory = Path.GetDirectoryName(Executable.Value)!,
            UseShellExecute = false
        };
        if (arguments is not null)
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start);
    }

    public static Process? Connect(string ipv4, string? password = null)
    {
        var arguments = new List<string> { "--connect", ipv4 };
        if (!string.IsNullOrEmpty(password))
        {
            arguments.Add("--password");
            arguments.Add(password);
        }
        return Start(arguments.ToArray());
    }

    /// <summary>读取本机 RustDesk ID，供跨网络（非同一局域网）的 ID 连接使用。</summary>
    public static async Task<string?> GetIdAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_cachedId)) return _cachedId;
        try
        {
            var start = new ProcessStartInfo(Executable.Value)
            {
                WorkingDirectory = Path.GetDirectoryName(Executable.Value)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("--get-id");
            using var process = Process.Start(start);
            if (process is null) return null;
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            var id = ParseId(await outputTask.ConfigureAwait(false));
            if (!string.IsNullOrEmpty(id)) _cachedId = id;
            return id;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException or OperationCanceledException)
        {
            if (ex is not OperationCanceledException) AppLog.Write($"读取 RustDesk ID 失败：{ex.Message}");
            return null;
        }
    }

    private static string? ParseId(string output)
    {
        foreach (var line in output.Split('\n').Reverse())
        {
            var text = line.Trim();
            if (text.Length is < 4 or > 32) continue;
            if (!text.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')) continue;
            return text;
        }
        return null;
    }

    /// <summary>恢复被 LanChat 改动过的 direct-server 选项；没有改动时不做任何事。</summary>
    public static void RestoreDirectAccessOption()
    {
        lock (SettingsLock)
        {
            if (_optionsPath is null) return;
            try
            {
                if (_originalOptionsExisted && _originalOptions is not null) File.WriteAllText(_optionsPath, _originalOptions);
                else if (!_originalOptionsExisted) File.Delete(_optionsPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Write($"还原 RustDesk 配置失败：{ex.Message}");
            }
            finally
            {
                _optionsPath = null;
                _originalOptions = null;
                _originalOptionsExisted = false;
            }
        }
    }

    public static void ExportSource(string destination) => CopyResource("rustdesk-1.5.3-corresponding-source.zip", destination);
    public static void ExportLicense(string destination) => CopyResource("AGPL-3.0.txt", destination);

    private static bool TryEnableDirectAccess(out string? error)
    {
        error = null;
        lock (SettingsLock)
        {
            try
            {
                var path = OptionsFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var existed = File.Exists(path);
                var original = existed ? File.ReadAllText(path) : string.Empty;
                if (IsDirectAccessEnabled(original)) return true;
                var updated = EnableDirectAccess(original);
                _optionsPath = path;
                _originalOptions = original;
                _originalOptionsExisted = existed;
                File.WriteAllText(path, updated);
                AppLog.Write($"已在 RustDesk 配置中启用直接 IP 访问（TCP {DirectAccessPort}）。");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    private static string OptionsFilePath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RustDesk", "config", "RustDesk2.toml");

    private static bool IsDirectAccessEnabled(string toml)
    {
        foreach (var line in toml.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(DirectServerOption, StringComparison.Ordinal)) continue;
            var separator = trimmed.IndexOf('=');
            if (separator < 0) continue;
            var value = trimmed[(separator + 1)..].Trim().Trim('\'', '"');
            return value.Equals("Y", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static string EnableDirectAccess(string toml)
    {
        var newline = toml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = toml.Replace("\r\n", "\n").Split('\n').ToList();
        var optionsIndex = lines.FindIndex(line => line.Trim() == "[options]");
        if (optionsIndex < 0)
        {
            while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            lines.Add("[options]");
            lines.Add($"{DirectServerOption} = 'Y'");
            lines.Add(string.Empty);
        }
        else
        {
            var sectionEnd = lines.FindIndex(optionsIndex + 1, line => line.TrimStart().StartsWith("[", StringComparison.Ordinal));
            if (sectionEnd < 0) sectionEnd = lines.Count;
            var existing = lines.FindIndex(optionsIndex + 1, sectionEnd - optionsIndex - 1,
                line => line.TrimStart().StartsWith(DirectServerOption, StringComparison.Ordinal));
            if (existing >= 0) lines[existing] = $"{DirectServerOption} = 'Y'";
            else lines.Insert(optionsIndex + 1, $"{DirectServerOption} = 'Y'");
        }
        return string.Join(newline, lines);
    }

    private static string ExtractResource(string resourceName, string outputName)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanChat", "RustDesk", "1.5.3");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, outputName);
        var assembly = Assembly.GetExecutingAssembly();
        using var input = assembly.GetManifestResourceStream(ResourcePrefix + resourceName)
            ?? throw new FileNotFoundException($"内置 RustDesk 资源缺失：{resourceName}");
        if (File.Exists(destination) && new FileInfo(destination).Length == input.Length) return destination;
        var temporary = destination + ".new";
        using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) input.CopyTo(output);
        File.Move(temporary, destination, true);
        return destination;
    }

    private static void CopyResource(string resourceName, string destination)
    {
        using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourcePrefix + resourceName)
            ?? throw new FileNotFoundException($"内置 RustDesk 资源缺失：{resourceName}");
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }
}
