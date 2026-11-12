using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace LanChat;

/// <summary>房间信令通道协议：新版 WebSocket 文本帧，或旧版行分隔 JSON（换行分隔）。</summary>
internal enum ChannelProtocol
{
    WebSocket,
    LineJson
}

/// <summary>HTTP 请求信息（WebSocket 握手与附件下载路由共用）。</summary>
internal sealed record HttpRequestInfo(string Method, string Path, Dictionary<string, string> Headers)
{
    public string? WebSocketKey =>
        Headers.TryGetValue("Sec-WebSocket-Key", out var key) ? key.Trim() : null;

    public bool IsWebSocketUpgrade =>
        Headers.TryGetValue("Upgrade", out var upgrade) && upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase) &&
        Headers.TryGetValue("Connection", out var connection) && connection.Contains("Upgrade", StringComparison.OrdinalIgnoreCase) &&
        WebSocketKey is not null;
}

/// <summary>WebSocket 帧结果：文本帧直接给出解码文本；二进制帧给出池化缓冲区，用完必须 Release。</summary>
internal readonly struct WsFrame
{
    public bool IsText { get; init; }
    public string? Text { get; init; }
    public byte[] Buffer { get; init; }
    public int Length { get; init; }
    public bool FromPool { get; init; }

    public void Release()
    {
        if (FromPool) ArrayPool<byte>.Shared.Return(Buffer);
    }
}

/// <summary>
/// RFC 6455 WebSocket 通道：文本帧承载房间 JSON 信令，二进制帧承载附件分块（原始字节，不经 Base64）。
/// 局域网内自行实现握手与帧编解码，避免引入第三方依赖。
/// </summary>
internal sealed class WebSocketChannel
{
    private const int MaxFrameBytes = 4 * 1024 * 1024;
    private readonly Stream _stream;
    private readonly bool _isClient;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _disposed;
    private string? _pendingLine;
    private readonly byte[] _lineBuffer = new byte[8 * 1024];
    private int _lineBufferPos;
    private int _lineBufferLength;

    public WebSocketChannel(Stream stream, bool isClient) : this(stream, isClient, ChannelProtocol.WebSocket)
    {
    }

    private WebSocketChannel(Stream stream, bool isClient, ChannelProtocol protocol)
    {
        _stream = stream;
        _isClient = isClient;
        Protocol = protocol;
    }

    /// <summary>本连接协商出的协议，用于决定心跳、功能降级等兼容行为。</summary>
    public ChannelProtocol Protocol { get; }

    // ======================= 握手 =======================

    /// <summary>读取 HTTP 请求行与头部（连接建立后的第一段数据）。</summary>
    public static async Task<HttpRequestInfo?> ReadRequestAsync(Stream stream, CancellationToken token)
    {
        var requestLine = await ReadLineAsync(stream, token).ConfigureAwait(false);
        if (requestLine is null) return null;
        return await ReadRequestAsync(stream, requestLine, token).ConfigureAwait(false);
    }

    private static async Task<HttpRequestInfo?> ReadRequestAsync(Stream stream, string requestLine, CancellationToken token)
    {
        var parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var line = await ReadLineAsync(stream, token).ConfigureAwait(false);
            if (line is null || line.Length == 0) break;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return new HttpRequestInfo(parts[0].ToUpperInvariant(), parts[1], headers);
    }

    /// <summary>
    /// 服务端统一握手：首包是 HTTP 升级请求（新版客户端）则按 RFC 6455 建立 WebSocket；
    /// 是裸 JSON 行（旧版客户端）则整条连接回退为行分隔协议。无法识别时返回 null。
    /// </summary>
    public static async Task<WebSocketChannel?> ServerHandshakeAsync(Stream stream, CancellationToken token)
    {
        var firstLine = await ReadLineAsync(stream, token).ConfigureAwait(false);
        if (firstLine is null) return null;
        if (!LooksLikeHttpRequest(firstLine))
        {
            AppLog.Write("检测到旧版行分隔协议首包，已按兼容模式接入。");
            return new WebSocketChannel(stream, isClient: false, ChannelProtocol.LineJson) { _pendingLine = firstLine };
        }
        var request = await ReadRequestAsync(stream, firstLine, token).ConfigureAwait(false);
        if (request is null) return null;
        return await ServerAcceptAsync(stream, request, token).ConfigureAwait(false);
    }

    /// <summary>首行形如 "GET /chat HTTP/1.1" 才按升级请求解析，避免把含空格的 JSON 误判为 HTTP。</summary>
    private static bool LooksLikeHttpRequest(string firstLine)
    {
        var parts = firstLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3 && parts[2].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 客户端建连并自动协商协议：先尝试 WebSocket 升级（新版对新版）；
    /// 对端是旧版行分隔协议时无法解析升级请求并会直接断开，此时自动重建连接并降级为行分隔 JSON。
    /// </summary>
    public static async Task<(TcpClient Client, NetworkStream Stream, WebSocketChannel Channel)> ConnectAsync(string host, int port, CancellationToken token)
    {
        var client = await ConnectClientAsync(host, port, token).ConfigureAwait(false);
        try
        {
            var stream = client.GetStream();
            var channel = await ClientHandshakeAsync(stream, token).ConfigureAwait(false);
            return (client, stream, channel);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            AppLog.Write($"对端不支持 WebSocket 升级（{ex.Message}），改用行分隔协议重连。");
        }
        client.Dispose();
        client = await ConnectClientAsync(host, port, token).ConfigureAwait(false);
        var legacyStream = client.GetStream();
        return (client, legacyStream, new WebSocketChannel(legacyStream, isClient: true, ChannelProtocol.LineJson));
    }

    private static async Task<TcpClient> ConnectClientAsync(string host, int port, CancellationToken token)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, token).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>服务器端：校验升级请求并写回 101 Switching Protocols。</summary>
    public static async Task<WebSocketChannel?> ServerAcceptAsync(Stream stream, HttpRequestInfo request, CancellationToken token)
    {
        if (!request.IsWebSocketUpgrade) return null;
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(request.WebSocketKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var response = "HTTP/1.1 101 Switching Protocols\r\n" +
                       "Upgrade: websocket\r\n" +
                       "Connection: Upgrade\r\n" +
                       $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
        return new WebSocketChannel(stream, isClient: false);
    }

    /// <summary>客户端：发送 Upgrade 请求并读取 101 响应。</summary>
    public static async Task<WebSocketChannel> ClientHandshakeAsync(Stream stream, CancellationToken token)
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var request = "GET /chat HTTP/1.1\r\n" +
                      "Host: lanchat\r\n" +
                      "Upgrade: websocket\r\n" +
                      "Connection: Upgrade\r\n" +
                      $"Sec-WebSocket-Key: {key}\r\n" +
                      "Sec-WebSocket-Version: 13\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
        var status = await ReadLineAsync(stream, token).ConfigureAwait(false);
        if (status is null || !status.StartsWith("HTTP/", StringComparison.Ordinal) || !status.Contains(" 101", StringComparison.Ordinal))
            throw new IOException($"WebSocket 升级失败：{status ?? "连接已关闭"}");
        while (true)
        {
            var line = await ReadLineAsync(stream, token).ConfigureAwait(false);
            if (line is null || line.Length == 0) break;
        }
        return new WebSocketChannel(stream, isClient: true);
    }

    // ======================= 帧收发 =======================

    /// <summary>读取下一帧；返回 null 表示连接关闭或收到 Close 帧。</summary>
    public async Task<WsFrame?> ReadMessageAsync(CancellationToken token)
    {
        if (Protocol == ChannelProtocol.LineJson)
        {
            // 行分隔协议：一包一行 JSON（UTF-8），与旧版 StreamReader.ReadLine 语义一致。
            var line = await ReadJsonLineAsync(token).ConfigureAwait(false);
            return line is null ? null : new WsFrame { IsText = true, Text = line };
        }
        while (true)
        {
            var head = new byte[2];
            if (!await ReadExactAsync(_stream, head, token).ConfigureAwait(false)) return null;
            var opcode = head[0] & 0x0F;
            var masked = (head[1] & 0x80) != 0;
            long length = head[1] & 0x7F;
            if (length == 126)
            {
                var ext = new byte[2];
                if (!await ReadExactAsync(_stream, ext, token).ConfigureAwait(false)) return null;
                length = BinaryPrimitives.ReadUInt16BigEndian(ext);
            }
            else if (length == 127)
            {
                var ext = new byte[8];
                if (!await ReadExactAsync(_stream, ext, token).ConfigureAwait(false)) return null;
                length = checked((long)BinaryPrimitives.ReadUInt64BigEndian(ext));
            }
            if (length < 0 || length > MaxFrameBytes)
                throw new InvalidDataException($"WebSocket 帧超出大小限制（{length} 字节）。");

            byte[] maskKey = masked ? new byte[4] : Array.Empty<byte>();
            if (masked && !await ReadExactAsync(_stream, maskKey, token).ConfigureAwait(false)) return null;

            // 大二进制帧使用池化缓冲，避免频繁分配大数组造成 GC 抖动。
            var pooled = opcode == 0x2 && length > 8192;
            var buffer = pooled ? ArrayPool<byte>.Shared.Rent((int)length) : new byte[length];
            if (!await ReadExactAsync(_stream, buffer.AsMemory(0, (int)length), token).ConfigureAwait(false))
            {
                if (pooled) ArrayPool<byte>.Shared.Return(buffer);
                return null;
            }
            if (masked)
            {
                for (var i = 0; i < length; i++) buffer[i] ^= maskKey[i & 3];
            }

            if (opcode == 0x1) // 文本
            {
                var text = Encoding.UTF8.GetString(buffer, 0, (int)length);
                if (pooled) ArrayPool<byte>.Shared.Return(buffer);
                return new WsFrame { IsText = true, Text = text };
            }
            if (opcode == 0x2) // 二进制
            {
                return new WsFrame { IsText = false, Buffer = buffer, Length = (int)length, FromPool = pooled };
            }
            // Close / Ping / Pong / 未知
            if (pooled) ArrayPool<byte>.Shared.Return(buffer);
            if (opcode == 0x8) return null; // Close
            if (opcode == 0x9 || opcode == 0xA) continue; // Ping → 忽略；Pong → 忽略
            return null;
        }
    }

    /// <summary>发送文本帧（JSON 信令）。</summary>
    public async Task SendTextAsync(string text, CancellationToken token)
    {
        if (Protocol == ChannelProtocol.LineJson)
        {
            var bytes = Encoding.UTF8.GetBytes(text + "\n");
            await _sendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await _stream.FlushAsync(token).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
            return;
        }
        var payload = Encoding.UTF8.GetBytes(text);
        await SendFrameAsync(0x1, payload, token).ConfigureAwait(false);
    }

    /// <summary>发送二进制帧（附件分块，原始字节）。</summary>
    public async Task SendBinaryAsync(ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        await SendFrameAsync(0x2, payload, token).ConfigureAwait(false);
    }

    private async Task SendFrameAsync(byte opcode, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        await _sendLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var mask = _isClient; // RFC 6455：客户端发送必须掩码，服务器发送不掩码
            var head = new byte[14];
            var length = payload.Length;
            var headerLength = 2;
            head[0] = (byte)(0x80 | opcode); // FIN + opcode
            if (length < 126)
            {
                head[1] = (byte)((mask ? 0x80 : 0) | length);
            }
            else if (length <= ushort.MaxValue)
            {
                head[1] = (byte)((mask ? 0x80 : 0) | 126);
                BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(2, 2), (ushort)length);
                headerLength = 4;
            }
            else
            {
                head[1] = (byte)((mask ? 0x80 : 0) | 127);
                BinaryPrimitives.WriteUInt64BigEndian(head.AsSpan(2, 8), (ulong)length);
                headerLength = 10;
            }

            if (!mask)
            {
                await _stream.WriteAsync(head.AsMemory(0, headerLength), token).ConfigureAwait(false);
                await _stream.WriteAsync(payload, token).ConfigureAwait(false);
            }
            else
            {
                var maskKey = RandomNumberGenerator.GetBytes(4);
                maskKey.CopyTo(head.AsSpan(headerLength));
                headerLength += 4;
                await _stream.WriteAsync(head.AsMemory(0, headerLength), token).ConfigureAwait(false);
                var maskedPayload = payload.ToArray();
                for (var i = 0; i < maskedPayload.Length; i++) maskedPayload[i] ^= maskKey[i & 3];
                await _stream.WriteAsync(maskedPayload, token).ConfigureAwait(false);
            }
            await _stream.FlushAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sendLock.Dispose();
    }

    // ======================= 底层读取 =======================

    /// <summary>行分隔协议读取一行 JSON（带缓冲，附件分块可达数百 KB，不能逐字节读流）。</summary>
    private async Task<string?> ReadJsonLineAsync(CancellationToken token)
    {
        if (_pendingLine is not null)
        {
            var pending = _pendingLine;
            _pendingLine = null;
            return pending;
        }
        var text = new MemoryStream(1024);
        while (true)
        {
            var next = await ReadBufferedByteAsync(token).ConfigureAwait(false);
            if (next < 0) return text.Length == 0 ? null : DecodeUtf8(text);
            if (next == (byte)'\n') break;
            if (next != (byte)'\r') text.WriteByte((byte)next);
            if (text.Length > MaxFrameBytes) throw new InvalidDataException($"房间数据超出大小限制（{text.Length} 字节）。");
        }
        return DecodeUtf8(text);
    }

    private static string DecodeUtf8(MemoryStream text) => Encoding.UTF8.GetString(text.GetBuffer(), 0, (int)text.Length);

    private async Task<int> ReadBufferedByteAsync(CancellationToken token)
    {
        if (_lineBufferPos >= _lineBufferLength)
        {
            _lineBufferLength = await _stream.ReadAsync(_lineBuffer, token).ConfigureAwait(false);
            _lineBufferPos = 0;
            if (_lineBufferLength == 0) return -1;
        }
        return _lineBuffer[_lineBufferPos++];
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken token)
    {
        var bytes = new List<byte>(128);
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one.AsMemory(0, 1), token).ConfigureAwait(false);
            if (read == 0) return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
            if (one[0] == (byte)'\n') break;
            if (one[0] != (byte)'\r') bytes.Add(one[0]);
            if (bytes.Count > 8192) throw new InvalidDataException("HTTP 头部或首包过长。");
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static async Task<bool> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], token).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }
}

/// <summary>附件二进制帧协议：[1 字节类型][16 字节 GUID][4 字节大端块序号][原始字节负载]。</summary>
internal static class AttachmentFrame
{
    public const byte KindChunk = 1;
    public const int HeaderBytes = 21;

    public static byte[] Build(Guid transferId, int chunkIndex, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[HeaderBytes + payload.Length];
        frame[0] = KindChunk;
        transferId.TryWriteBytes(frame.AsSpan(1, 16));
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(17, 4), chunkIndex);
        payload.CopyTo(frame.AsSpan(HeaderBytes));
        return frame;
    }

    public static bool TryParse(ReadOnlySpan<byte> frame, out Guid transferId, out int chunkIndex, out ReadOnlySpan<byte> payload)
    {
        transferId = Guid.Empty;
        chunkIndex = -1;
        payload = default;
        if (frame.Length < HeaderBytes || frame[0] != KindChunk) return false;
        transferId = new Guid(frame.Slice(1, 16));
        chunkIndex = BinaryPrimitives.ReadInt32BigEndian(frame.Slice(17, 4));
        payload = frame[HeaderBytes..];
        return true;
    }
}
