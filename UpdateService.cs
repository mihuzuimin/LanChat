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

public enum UpdateSource { Lan, GitHub }

/// <summary>更新来源：局域网（Address 为提供方 IP）或 GitHub Releases（AssetUrl 为发布资产地址）。</summary>
public sealed record UpdateOffer(Version Version, string Address, UpdateSource Source = UpdateSource.Lan,
    string? AssetUrl = null, string Notes = "", long Size = 0, bool ForceUpdate = false)
{
    public string VersionText => UpdateService.Format(Version);
}

/// <summary>version.json 内容：版本号、更新说明、是否强制更新、安装包 SHA256。</summary>
public sealed record UpdateManifest(Version Version, string Notes, bool ForceUpdate, string Sha256, long Size)
{
    public string VersionText => UpdateService.Format(Version);
}

/// <summary>
/// 更新检查：先查 GitHub Releases（3 秒超时，同步版本号，发现新版本自动从仓库下载）；
/// 超时无响应回退局域网（UDP 9178 广播发现 + HTTP 9178 下载，每轮 3 秒、最多重试 3 轮）；
/// 下载后 SHA256 校验（局域网清单提供哈希时强制比对），forceUpdate=true 时强制自动更新。
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

    // ======================= 客户端：GitHub 优先 → 局域网兜底 =======================

    private const string GitHubLatestUrl = "https://api.github.com/repos/mihuzuimin/LanChat/releases/latest";
    private static readonly TimeSpan GithubCheckTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LanSearchRound = TimeSpan.FromSeconds(3);
    private const int LanSearchAttempts = 3;

    /// <summary>
    /// 更新检查总流程：① 查 GitHub Releases（3 秒超时，同步版本号，发现新版本即从仓库下载）；
    /// ② 超时无响应转局域网搜索（每轮 3 秒，最多重试 3 轮，收到应答但无更高版本则提前结束）；
    /// ③ 仍未发现则结束，返回 null。
    /// </summary>
    public static async Task<UpdateOffer?> CheckForUpdatesAsync(CancellationToken token)
    {
        try
        {
            AppLog.Write($"开始检查更新：查询 GitHub Releases（{GithubCheckTimeout.TotalSeconds:0} 秒超时）。");
            var (github, reached) = await CheckGitHubAsync(GithubCheckTimeout, token);
            if (github is not null) return github;
            if (reached)
            {
                AppLog.Write("GitHub 最新发布不高于当前版本，无需更新。");
                return null;
            }
            AppLog.Write("GitHub 3 秒无响应，转局域网搜索新版本。");
            for (var attempt = 1; attempt <= LanSearchAttempts; attempt++)
            {
                AppLog.Write($"局域网搜索新版本（第 {attempt}/{LanSearchAttempts} 轮，每轮 {LanSearchRound.TotalSeconds:0} 秒）…");
                var (offer, gotReply) = await DiscoverOnceAsync(LanSearchRound, token);
                if (offer is not null) return offer;
                if (gotReply)
                {
                    AppLog.Write("局域网内未发现更高版本，结束更新检查。");
                    return null;
                }
            }
            AppLog.Write($"局域网连续 {LanSearchAttempts} 轮搜索均无响应，结束更新检查。");
            return null;
        }
        catch (OperationCanceledException)
        {
            AppLog.Write("更新检查被取消。");
            return null;
        }
    }

    /// <summary>查询 GitHub Releases latest 并同步版本号比对；返回 (新版本 Offer, GitHub 是否给出响应)。</summary>
    private static async Task<(UpdateOffer? Offer, bool Reached)> CheckGitHubAsync(TimeSpan timeout, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeout);
        try
        {
            using var http = new HttpClient { Timeout = timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("LanChat-Updater");
            var json = await http.GetStringAsync(GitHubLatestUrl, cts.Token);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
            {
                AppLog.Write($"GitHub 发布标签无法识别为版本号：{tag}。");
                return (null, true);
            }
            if (version <= CurrentVersion)
            {
                AppLog.Write($"GitHub 最新发布 {Format(version)} 不高于当前 {CurrentVersionText}。");
                return (null, true);
            }
            string? assetUrl = null;
            var assetSize = 0L;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? string.Empty;
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    assetUrl = asset.GetProperty("browser_download_url").GetString();
                    assetSize = asset.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var s) ? s : 0L;
                    break;
                }
            }
            if (assetUrl is null)
            {
                AppLog.Write("GitHub 已有更高版本发布，但其中没有 .exe 资产，无法自动更新。");
                return (null, true);
            }
            var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty;
            var force = notes.Contains("forceUpdate:true", StringComparison.OrdinalIgnoreCase);
            AppLog.Write($"GitHub 发现新版本 {Format(version)}，将自动从仓库下载更新。");
            return (new UpdateOffer(version, "GitHub", UpdateSource.GitHub, assetUrl, notes, assetSize, force), true);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            AppLog.Write("GitHub 更新检查 3 秒无响应。");
            return (null, false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException)
        {
            AppLog.Write($"GitHub 更新检查未取得有效响应：{ex.Message}");
            return (null, false);
        }
    }

    /// <summary>局域网搜索一轮：返回更高版本 Offer；gotReply 表示本轮收到过任何实例应答（网络可用）。</summary>
    private static async Task<(UpdateOffer? Offer, bool GotReply)> DiscoverOnceAsync(TimeSpan duration, CancellationToken token)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            var query = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "lanchat-update-query" }));
            await udp.SendAsync(query, new IPEndPoint(IPAddress.Broadcast, UpdatePort), token);
            var deadline = DateTime.UtcNow + duration;
            var gotReply = false;
            while (DateTime.UtcNow < deadline)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(deadline - DateTime.UtcNow);
                try
                {
                    var result = await udp.ReceiveAsync(timeout.Token);
                    var message = TryParse(result.Buffer);
                    if (message?.GetPropertyOrDefault("type") != "lanchat-update-offer") continue;
                    gotReply = true;
                    var versionText = message.Value.GetPropertyOrDefault("version");
                    if (!Version.TryParse(versionText, out var version) || version <= CurrentVersion) continue;
                    AppLog.Write($"局域网发现新版本 {Format(version)}：{result.RemoteEndPoint.Address}。");
                    return (new UpdateOffer(version, result.RemoteEndPoint.Address.ToString()), true);
                }
                catch (OperationCanceledException) { break; }
            }
            return (null, gotReply);
        }
        catch (OperationCanceledException) { return (null, false); }
        catch (Exception ex)
        {
            AppLog.Write($"局域网更新搜索异常：{ex.Message}");
            return (null, false);
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
        var url = offer.Source == UpdateSource.GitHub
            ? offer.AssetUrl ?? throw new InvalidDataException("GitHub 更新缺少资产下载地址。")
            : $"http://{offer.Address}:{UpdatePort}/api/download";
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength ?? (offer.Source == UpdateSource.GitHub ? offer.Size : manifest?.Size ?? 0);
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
