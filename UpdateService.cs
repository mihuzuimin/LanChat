using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace LanChat;

public sealed record UpdateOffer(Version Version, string Address)
{
    public string VersionText => UpdateService.Format(Version);
}

/// <summary>version.json 内容：版本号、更新说明、是否强制更新、安装包 SHA256。</summary>
public sealed record UpdateManifest(Version Version, string Notes, bool ForceUpdate, string Sha256, long Size)
{
    public string VersionText => UpdateService.Format(Version);
}

/// <summary>
/// 局域网更新：UDP 9178 广播发现版本，HTTP 9178 提供 /api/version 与 /api/download，
/// 按版本号比对新旧，下载后用 SHA256 校验，forceUpdate=true 时强制自动更新。
/// </summary>
internal static class UpdateService
{
    public const int UpdatePort = 9178;

    public static Version CurrentVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    public static string CurrentVersionText => Format(CurrentVersion);

    public static string Format(Version version) => $"{version.Major}.{version.Minor}.{version.Build}";

    private static UdpClient? _udp;
    private static TcpListener? _tcp;
    private static CancellationTokenSource? _cts;
    private static readonly SemaphoreSlim ShaLock = new(1, 1);
    private static string? _sha256Cache;

    public static void StartServer()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _ = ServeUdpAsync(_cts.Token);
        _ = ServeHttpAsync(_cts.Token);
        AppLog.Write($"更新服务已启动，端口 {UpdatePort}（UDP 发现 + HTTP 接口），本机版本 {CurrentVersionText}。");
    }

    public static void StopServer()
    {
        _cts?.Cancel();
        _udp?.Dispose();
        _udp = null;
        try { _tcp?.Stop(); } catch (SocketException) { }
        _tcp = null;
        _cts?.Dispose();
        _cts = null;
    }

    // ======================= 服务端：UDP 发现 + HTTP 接口 =======================

    private static async Task ServeUdpAsync(CancellationToken token)
    {
        try
        {
            var udp = new UdpClient(new IPEndPoint(IPAddress.Any, UpdatePort)) { EnableBroadcast = true };
            _udp = udp;
            while (!token.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(token);
                var message = TryParse(result.Buffer);
                if (message?.GetPropertyOrDefault("type") != "lanchat-update-query") continue;
                var reply = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    type = "lanchat-update-offer",
                    version = CurrentVersionText
                }));
                await udp.SendAsync(reply, result.RemoteEndPoint, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            AppLog.Write($"更新端口 {UpdatePort}(UDP) 已被占用，本机不参与更新广播。");
        }
        catch (Exception ex) { AppLog.Write($"更新发现服务异常：{ex.Message}"); }
    }

    private static async Task ServeHttpAsync(CancellationToken token)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Any, UpdatePort);
            listener.Start();
            _tcp = listener;
            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(token);
                _ = HandleHttpClientAsync(client, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            AppLog.Write($"更新端口 {UpdatePort}(HTTP) 已被占用，本机无法提供更新接口。");
        }
        catch (Exception ex) { AppLog.Write($"更新 HTTP 服务异常：{ex.Message}"); }
    }

    private static async Task HandleHttpClientAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 2048, true);
                var requestLine = await reader.ReadLineAsync(token);
                if (string.IsNullOrWhiteSpace(requestLine)) return;
                var parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase)) return;
                var path = parts[1].Split('?')[0];

                while (true)
                {
                    var line = await reader.ReadLineAsync(token);
                    if (line is null || line.Length == 0) break;
                }

                if (path == "/api/version")
                {
                    var body = Encoding.UTF8.GetBytes(await BuildVersionJsonAsync(token));
                    await WriteResponseAsync(stream, "200 OK", "application/json; charset=utf-8", body, token);
                    AppLog.Write($"已向 {client.Client.RemoteEndPoint} 返回更新清单。");
                }
                else if (path == "/api/download")
                {
                    var exe = Environment.ProcessPath;
                    if (exe is null || !File.Exists(exe))
                    {
                        await WriteResponseAsync(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("安装包不存在"), token);
                        return;
                    }
                    var bytes = await File.ReadAllBytesAsync(exe, token);
                    await WriteResponseAsync(stream, "200 OK", "application/octet-stream", bytes, token);
                    AppLog.Write($"已向 {client.Client.RemoteEndPoint} 发送更新包（{bytes.LongLength:N0} 字节）。");
                }
                else
                {
                    await WriteResponseAsync(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not Found"), token);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { AppLog.Write($"更新 HTTP 请求处理异常：{ex.Message}"); }
    }

    private static async Task WriteResponseAsync(Stream stream, string status, string contentType, byte[] body, CancellationToken token)
    {
        var header = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, token);
        await stream.WriteAsync(body, token);
        await stream.FlushAsync(token);
    }

    /// <summary>构建 version.json：优先读取程序目录下的 version.json（含 notes/forceUpdate），并附带安装包 SHA256。</summary>
    private static async Task<string> BuildVersionJsonAsync(CancellationToken token)
    {
        var notes = "局域网内检测到的新版本。";
        var forceUpdate = false;
        var sidecar = Path.Combine(AppContext.BaseDirectory, "version.json");
        if (File.Exists(sidecar))
        {
            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(sidecar, token));
                if (document.RootElement.TryGetProperty("notes", out var n)) notes = n.GetString() ?? notes;
                if (document.RootElement.TryGetProperty("forceUpdate", out var f)) forceUpdate = f.ValueKind == JsonValueKind.True;
            }
            catch (Exception ex) { AppLog.Write($"读取 version.json 失败，使用默认更新说明：{ex.Message}"); }
        }
        var sha = await ComputePackageSha256Async(token);
        return JsonSerializer.Serialize(new
        {
            version = CurrentVersionText,
            notes,
            forceUpdate,
            sha256 = sha,
            size = new FileInfo(Environment.ProcessPath!).Length
        });
    }

    /// <summary>计算并缓存本机安装包的 SHA256（十六进制小写）。</summary>
    private static async Task<string> ComputePackageSha256Async(CancellationToken token)
    {
        if (_sha256Cache is not null) return _sha256Cache;
        await ShaLock.WaitAsync(token);
        try
        {
            if (_sha256Cache is not null) return _sha256Cache;
            var exe = Environment.ProcessPath;
            if (exe is null || !File.Exists(exe)) return string.Empty;
            await using var stream = File.OpenRead(exe);
            var hash = await SHA256.HashDataAsync(stream, token);
            _sha256Cache = Convert.ToHexString(hash).ToLowerInvariant();
            return _sha256Cache;
        }
        finally { ShaLock.Release(); }
    }

    // ======================= 客户端 =======================

    /// <summary>在 duration 内搜索局域网中版本更高的实例，返回最高版本。</summary>
    public static async Task<UpdateOffer?> DiscoverAsync(TimeSpan duration, CancellationToken token)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            var query = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "lanchat-update-query" }));
            await udp.SendAsync(query, new IPEndPoint(IPAddress.Broadcast, UpdatePort), token);
            var deadline = DateTime.UtcNow + duration;
            UpdateOffer? best = null;
            while (DateTime.UtcNow < deadline)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(deadline - DateTime.UtcNow);
                try
                {
                    var result = await udp.ReceiveAsync(timeout.Token);
                    var message = TryParse(result.Buffer);
                    if (message?.GetPropertyOrDefault("type") != "lanchat-update-offer") continue;
                    var versionText = message.Value.GetPropertyOrDefault("version");
                    if (!Version.TryParse(versionText, out var version) || version <= CurrentVersion) continue;
                    if (best is not null && version <= best.Version) continue;
                    best = new UpdateOffer(version, result.RemoteEndPoint.Address.ToString());
                    AppLog.Write($"发现新版本 {Format(version)}：{best.Address}。");
                    return best;
                }
                catch (OperationCanceledException) { break; }
            }
            return best;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            AppLog.Write($"搜索更新失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>通过 HTTP /api/version 获取提供方的 version.json 清单。</summary>
    public static async Task<UpdateManifest?> FetchManifestAsync(UpdateOffer offer, CancellationToken token)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var json = await http.GetStringAsync($"http://{offer.Address}:{UpdatePort}/api/version", token);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var versionText = root.GetProperty("version").GetString();
            if (!Version.TryParse(versionText, out var version)) return null;
            var notes = root.TryGetProperty("notes", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            var force = root.TryGetProperty("forceUpdate", out var f) && f.ValueKind == JsonValueKind.True;
            var sha = root.TryGetProperty("sha256", out var s) ? s.GetString() ?? string.Empty : string.Empty;
            var size = root.TryGetProperty("size", out var z) && z.TryGetInt64(out var sz) ? sz : 0L;
            return new UpdateManifest(version, notes, force, sha, size);
        }
        catch (Exception ex)
        {
            AppLog.Write($"获取更新清单失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>通过 HTTP /api/download 下载安装包到临时目录，并用 SHA256 校验完整性。</summary>
    public static async Task<string> DownloadAsync(UpdateOffer offer, UpdateManifest? manifest, IProgress<double>? progress, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var response = await http.GetAsync($"http://{offer.Address}:{UpdatePort}/api/download", HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength ?? manifest?.Size ?? 0;
        if (length <= 0 || length > 500L * 1024 * 1024) throw new InvalidDataException("更新包大小异常。");

        var path = Path.Combine(Path.GetTempPath(), $"LanChat-{offer.VersionText}.exe");
        await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        await using (var source = await response.Content.ReadAsStreamAsync(token))
        {
            var buffer = new byte[81920];
            long received = 0;
            while (received < length)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - received)), token);
                if (read == 0) break;
                await file.WriteAsync(buffer.AsMemory(0, read), token);
                received += read;
                progress?.Report(received * 100.0 / length);
            }
            if (received < length) throw new IOException("更新包传输中断。");
        }

        // SHA256 校验：清单提供哈希时必须匹配，否则丢弃。
        if (manifest is { Sha256.Length: > 0 })
        {
            await using var verify = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(verify, token)).ToLowerInvariant();
            if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(path); } catch (IOException) { }
                throw new InvalidDataException("更新包 SHA256 校验失败，已丢弃，请重新下载。");
            }
            AppLog.Write($"更新包 SHA256 校验通过：{actual[..16]}…");
        }

        AppLog.Write($"更新包下载完成：{path}（{length:N0} 字节）。");
        return path;
    }

    /// <summary>退出当前进程，等待文件解锁后覆盖并重新启动。</summary>
    public static void ApplyAndRestart(string packagePath)
    {
        var target = Environment.ProcessPath;
        if (target is null) return;
        var script = Path.Combine(Path.GetTempPath(), $"lanchat-update-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(script, $"""
            @echo off
            timeout /t 2 /nobreak >nul
            copy /y "{packagePath}" "{target}" >nul
            start "" "{target}"
            del "%~f0" >nul
            """, Encoding.Default);
        AppLog.Write($"准备覆盖程序并重启：{target}。");
        AppLog.SaveRuntimeLog();
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { CreateNoWindow = true, UseShellExecute = false });
        Application.Current.Shutdown();
    }

    private static JsonElement? TryParse(byte[] data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            return document.RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }

    private static string? GetPropertyOrDefault(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetString() : null;
}
