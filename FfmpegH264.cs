using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Channels;

namespace LanChat;

internal static class FfmpegRuntime
{
    private static readonly Lazy<string> Executable = new(ExtractRuntime, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string Path => Executable.Value;

    private static string ExtractRuntime()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("H.264 远控编码仅随 Windows x64 版本提供。");

        var assembly = Assembly.GetExecutingAssembly();
        const string prefix = "LanChat.FFmpeg.";
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();
        if (!resources.Any(name => name.Equals(prefix + "ffmpeg.exe", StringComparison.Ordinal)))
            throw new FileNotFoundException("内置的 FFmpeg 编码器资源未找到。");

        var directory = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LanChat", "codec", "ffmpeg-9.0.2-x64");
        Directory.CreateDirectory(directory);
        foreach (var resource in resources)
        {
            var fileName = resource[prefix.Length..];
            if (fileName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || fileName.Contains('\\') || fileName.Contains('/'))
                throw new InvalidDataException("内置编码器资源名称无效。");

            var target = System.IO.Path.Combine(directory, fileName);
            using var input = assembly.GetManifestResourceStream(resource)
                ?? throw new FileNotFoundException($"无法读取内置编码器资源：{fileName}");
            if (File.Exists(target) && new FileInfo(target).Length == input.Length) continue;

            var temporary = target + ".new";
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                input.CopyTo(output);
            File.Move(temporary, target, true);
        }

        return System.IO.Path.Combine(directory, "ffmpeg.exe");
    }
}

internal sealed class FfmpegH264Encoder : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Channel<byte[]> _output = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(3)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = true
    });
    private readonly Task _readTask;
    private readonly SemaphoreSlim _inputLock = new(1, 1);
    private readonly ConcurrentQueue<string> _errors = new();
    private bool _disposed;

    private FfmpegH264Encoder(Process process)
    {
        _process = process;
        _process.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _errors.Enqueue(e.Data);
                while (_errors.Count > 8) _errors.TryDequeue(out var dropped);
            }
        };
        _process.BeginErrorReadLine();
        _readTask = ReadOutputAsync();
    }

    public static async Task<(FfmpegH264Encoder Encoder, byte[] FirstPacket)?> TryCreateAsync(
        int width, int height, byte[] firstFrame, CancellationToken cancellationToken)
    {
        foreach (var hardware in new[] { true, false })
        {
            FfmpegH264Encoder? encoder = null;
            try
            {
                encoder = Start(width, height, hardware);
                await encoder.WriteFrameAsync(firstFrame, cancellationToken).ConfigureAwait(false);
                var firstPacket = await encoder.ReadNextPacketAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                if (firstPacket is not null)
                {
                    AppLog.Write(hardware
                        ? "远控 H.264 硬件编码已启动（Media Foundation）。"
                        : "远控 H.264 硬件编码不可用，已回退到 Media Foundation 软件编码。");
                    return (encoder, firstPacket);
                }
            }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException or UnauthorizedAccessException)
            {
                if (ex is OperationCanceledException) throw;
                AppLog.Write($"远控 H.264 {(hardware ? "硬件" : "软件")}编码启动失败：{encoder?.GetLastError() ?? ex.Message}");
            }

            if (encoder is not null) await encoder.DisposeAsync().ConfigureAwait(false);
        }

        return null;
    }

    private static FfmpegH264Encoder Start(int width, int height, bool hardware)
    {
        var start = CreateStartInfo();
        Add(start, "-f", "rawvideo");
        Add(start, "-pixel_format", "bgra");
        Add(start, "-video_size", $"{width}x{height}");
        Add(start, "-framerate", "10");
        Add(start, "-i", "pipe:0");
        Add(start, "-an", "-c:v", "h264_mf");
        Add(start, "-hw_encoding", hardware ? "1" : "0");
        Add(start, "-scenario", "display_remoting");
        Add(start, "-quality", "70");
        Add(start, "-g", "10");
        Add(start, "-bf", "0");
        Add(start, "-flush_packets", "1");
        Add(start, "-f", "h264", "pipe:1");
        return new FfmpegH264Encoder(StartProcess(start));
    }

    public async Task WriteFrameAsync(byte[] bgra, CancellationToken cancellationToken)
    {
        await _inputLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.BaseStream.WriteAsync(bgra, cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _inputLock.Release(); }
    }

    public async Task<byte[]?> ReadNextPacketAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            if (!await _output.Reader.WaitToReadAsync(timeoutSource.Token).ConfigureAwait(false)) return null;
            return await _output.Reader.ReadAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (ChannelClosedException) { return null; }
    }

    public bool TryReadPacket(out byte[] packet) => _output.Reader.TryRead(out packet!);

    private async Task ReadOutputAsync()
    {
        try
        {
            var buffer = new byte[32 * 1024];
            while (true)
            {
                var count = await _process.StandardOutput.BaseStream.ReadAsync(buffer).ConfigureAwait(false);
                if (count == 0) break;
                await _output.Writer.WriteAsync(buffer.AsMemory(0, count).ToArray()).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or ChannelClosedException) { }
        finally { _output.Writer.TryComplete(); }
    }

    private string GetLastError() => string.Join(" | ", _errors.ToArray());

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _output.Writer.TryComplete();
        try { _process.StandardInput.Close(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { _process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ObjectDisposedException) { }
        }
        try { await _readTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch (TimeoutException) { }
        _process.Dispose();
        _inputLock.Dispose();
    }

    internal static ProcessStartInfo CreateStartInfo()
    {
        var executable = FfmpegRuntime.Path;
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = System.IO.Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        Add(start, "-hide_banner", "-loglevel", "error", "-nostats");
        return start;
    }

    internal static Process StartProcess(ProcessStartInfo start)
    {
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("无法启动内置视频编码器。");
        return process;
    }

    internal static void Add(ProcessStartInfo start, params string[] args)
    {
        foreach (var arg in args) start.ArgumentList.Add(arg);
    }
}

internal sealed class FfmpegH264Decoder : IAsyncDisposable
{
    private const long MaxQueuedInputBytes = 8L * 1024 * 1024;
    private readonly Process _process;
    private readonly Action<byte[]> _frameReceived;
    private readonly Task _readTask;
    private readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true
    });
    private readonly CancellationTokenSource _inputLifetime = new();
    private readonly Task _inputTask;
    private long _queuedInputBytes;
    private bool _disposed;

    private FfmpegH264Decoder(Process process, Action<byte[]> frameReceived)
    {
        _process = process;
        _frameReceived = frameReceived;
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) AppLog.Write($"远控 H.264 解码：{e.Data}");
        };
        _process.BeginErrorReadLine();
        _readTask = ReadJpegFramesAsync();
        _inputTask = WriteInputAsync();
    }

    public static FfmpegH264Decoder Start(Action<byte[]> frameReceived)
    {
        var start = FfmpegH264Encoder.CreateStartInfo();
        FfmpegH264Encoder.Add(start, "-flags", "low_delay", "-f", "h264", "-i", "pipe:0");
        FfmpegH264Encoder.Add(start, "-an", "-f", "image2pipe", "-vcodec", "mjpeg", "-q:v", "6", "pipe:1");
        return new FfmpegH264Decoder(FfmpegH264Encoder.StartProcess(start), frameReceived);
    }

    public bool QueuePacket(byte[] packet)
    {
        if (_disposed || packet.Length == 0) return false;
        var queued = Interlocked.Add(ref _queuedInputBytes, packet.Length);
        if (queued > MaxQueuedInputBytes)
        {
            Interlocked.Add(ref _queuedInputBytes, -packet.Length);
            return false;
        }
        if (_input.Writer.TryWrite(packet)) return true;
        Interlocked.Add(ref _queuedInputBytes, -packet.Length);
        return false;
    }

    private async Task WriteInputAsync()
    {
        try
        {
            await foreach (var packet in _input.Reader.ReadAllAsync(_inputLifetime.Token).ConfigureAwait(false))
            {
                try
                {
                    await _process.StandardInput.BaseStream.WriteAsync(packet, _inputLifetime.Token).ConfigureAwait(false);
                    await _process.StandardInput.BaseStream.FlushAsync(_inputLifetime.Token).ConfigureAwait(false);
                }
                finally { Interlocked.Add(ref _queuedInputBytes, -packet.Length); }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or ChannelClosedException)
        {
            if (!_disposed) AppLog.Write($"远控 H.264 解码输入管道已结束：{ex.Message}");
        }
    }

    private async Task ReadJpegFramesAsync()
    {
        var buffer = new byte[256 * 1024];
        var count = 0;
        var input = new byte[32 * 1024];
        try
        {
            while (true)
            {
                var read = await _process.StandardOutput.BaseStream.ReadAsync(input).ConfigureAwait(false);
                if (read == 0) break;
                EnsureCapacity(ref buffer, count + read);
                Buffer.BlockCopy(input, 0, buffer, count, read);
                count += read;

                while (true)
                {
                    var start = FindMarker(buffer, 0, count, 0xD8);
                    if (start < 0)
                    {
                        count = 0;
                        break;
                    }
                    if (start > 0)
                    {
                        Buffer.BlockCopy(buffer, start, buffer, 0, count - start);
                        count -= start;
                    }
                    var end = FindMarker(buffer, 2, count, 0xD9);
                    if (end < 0) break;
                    var frame = new byte[end + 2];
                    Buffer.BlockCopy(buffer, 0, frame, 0, frame.Length);
                    _frameReceived(frame);
                    Buffer.BlockCopy(buffer, frame.Length, buffer, 0, count - frame.Length);
                    count -= frame.Length;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or ChannelClosedException)
        { if (!_disposed) AppLog.Write($"远控 H.264 解码管道已结束：{ex.Message}"); }
    }

    private static int FindMarker(byte[] buffer, int offset, int count, byte marker)
    {
        for (var i = Math.Max(0, offset); i + 1 < count; i++)
            if (buffer[i] == 0xFF && buffer[i + 1] == marker) return i;
        return -1;
    }

    private static void EnsureCapacity(ref byte[] buffer, int required)
    {
        if (required <= buffer.Length) return;
        Array.Resize(ref buffer, Math.Max(required, buffer.Length * 2));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _input.Writer.TryComplete();
        _inputLifetime.Cancel();
        try { if (!_process.HasExited) _process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ObjectDisposedException) { }
        try { _process.StandardInput.Close(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        try
        {
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            try { _process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ObjectDisposedException) { }
        }
        try { await _readTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch (TimeoutException) { }
        try { await _inputTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch (TimeoutException) { }
        _process.Dispose();
        _inputLifetime.Dispose();
    }
}
