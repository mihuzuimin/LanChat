using System.Collections.Concurrent;
using System.IO;
using SharpHook;
using SharpHook.Data;

namespace LanChat;

/// <summary>
/// 快捷键转发（观看端）：全局键盘钩子捕获带修饰键（Ctrl/Alt/Shift/Win）的组合，
/// 抑制本机响应并通过房间通道发给被控端注入。
/// 按住 Ctrl+Alt 半秒且期间未按任何普通键 → 触发 onRelease，释放本机热键（关闭转发）；
/// 若期间按下其他键则视为正常组合（如 Ctrl+Alt+T），取消释放判定并照常转发。
/// 方案来自 GitHub 开源项目 SharpHook（MIT，github.com/TolikPylypchuk/SharpHook，libuiohook 封装）。
/// 限制：Ctrl+Alt+Del、Win+L 等系统安全序列（SAS）由系统独占处理，用户态无法拦截或转发。
/// </summary>
internal sealed class RemoteShortcutRelay : IDisposable
{
    /// <summary>按住 Ctrl+Alt 达到该时长（毫秒）即判定为“释放本机热键”。</summary>
    private const int ReleaseHoldMs = 500;

    private readonly ConcurrentDictionary<int, bool> _tracked = new();
    private readonly object _stateLock = new();
    private SimpleGlobalHook? _hook;
    private Func<int, bool, CancellationToken, Task>? _send;
    private Action? _onRelease;
    private CancellationToken _token;
    private Task? _runTask;
    private Timer? _releaseTimer;
    private bool _ctrlDown;
    private bool _altDown;
    private bool _disposed;

    public bool IsActive => _hook is not null;

    /// <summary>启动钩子（立即返回；钩子在后台线程运行）。
    /// send 回调：键码、是否抬起；onRelease：按住 Ctrl+Alt 半秒时在线程池线程调用，调用方需自行切换到 UI 线程。</summary>
    public async Task StartAsync(Func<int, bool, CancellationToken, Task> send, Action onRelease, CancellationToken token)
    {
        if (_hook is not null) return;
        _send = send;
        _onRelease = onRelease;
        _token = token;
        lock (_stateLock)
        {
            _ctrlDown = false;
            _altDown = false;
        }
        var hook = new SimpleGlobalHook();
        hook.KeyPressed += OnKeyPressed;
        hook.KeyReleased += OnKeyReleased;
        _hook = hook;
        _runTask = hook.RunAsync(GlobalHookType.Keyboard, useBackgroundThread: true);
        _ = _runTask.ContinueWith(t =>
        {
            if (t.IsFaulted) AppLog.Write($"快捷键转发钩子异常退出：{t.Exception?.GetBaseException().Message}");
        }, TaskScheduler.Default);
        AppLog.Write("快捷键转发已开启：带修饰键的组合将发送到远端，本机不响应；单键不受影响；按住 Ctrl+Alt 半秒可释放本机热键。");
        await Task.CompletedTask;
    }

    /// <summary>停止钩子，恢复本机键盘响应。</summary>
    public void Stop()
    {
        var hook = _hook;
        if (hook is null) return;
        hook.KeyPressed -= OnKeyPressed;
        hook.KeyReleased -= OnKeyReleased;
        _hook = null;
        _tracked.Clear();
        lock (_stateLock)
        {
            _ctrlDown = false;
            _altDown = false;
            _releaseTimer?.Dispose();
            _releaseTimer = null;
        }
        try { hook.Stop(); } catch (Exception ex) { AppLog.Write($"停止快捷键转发钩子失败：{ex.Message}"); }
        AppLog.Write("快捷键转发已关闭，本机键盘恢复原生响应。");
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        var key = e.Data.KeyCode;
        var code = (int)key;
        var isModifier = IsModifier(key);
        var hasModifiers = (e.RawEvent.Mask & (EventMask.Ctrl | EventMask.Alt | EventMask.Shift | EventMask.Meta)) != EventMask.None;

        if (isModifier) UpdateModifierState(key, pressed: true);
        else CancelPendingRelease(); // 按下普通键说明是正常输入/组合，取消“释放本机热键”判定

        if (!isModifier && !hasModifiers) return; // 无修饰单键：本机正常，不转发

        // 组合主键抑制本机响应；修饰键本身放行（单独按 Ctrl 仍可在本机使用）
        var suppress = !isModifier;
        _tracked[code] = suppress;
        if (suppress) e.SuppressEvent = true;
        _ = SendAsync(code, isUp: false);
    }

    private void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        var key = e.Data.KeyCode;
        if (IsModifier(key)) UpdateModifierState(key, pressed: false);
        var code = (int)key;
        // down 时已转发的键，其 up 必然转发（即使抬起时修饰键已释放），防止远端粘键
        if (!_tracked.TryRemove(code, out var suppress)) return;
        if (suppress) e.SuppressEvent = true;
        _ = SendAsync(code, isUp: true);
    }

    /// <summary>记录 Ctrl/Alt 按下状态；两键同按启动“释放本机热键”计时，任一抬起则取消。</summary>
    private void UpdateModifierState(KeyCode key, bool pressed)
    {
        lock (_stateLock)
        {
            if (key is KeyCode.VcLeftControl or KeyCode.VcRightControl) _ctrlDown = pressed;
            if (key is KeyCode.VcLeftAlt or KeyCode.VcRightAlt) _altDown = pressed;

            if (!pressed)
            {
                _releaseTimer?.Dispose();
                _releaseTimer = null;
                return;
            }
            var isChordKey = key is KeyCode.VcLeftControl or KeyCode.VcRightControl or KeyCode.VcLeftAlt or KeyCode.VcRightAlt;
            if (!isChordKey || !_ctrlDown || !_altDown || _hook is null) return;
            _releaseTimer?.Dispose();
            _releaseTimer = new Timer(_ => FireRelease(), null, ReleaseHoldMs, Timeout.Infinite);
        }
    }

    private void CancelPendingRelease()
    {
        lock (_stateLock)
        {
            _releaseTimer?.Dispose();
            _releaseTimer = null;
        }
    }

    /// <summary>按住 Ctrl+Alt 达到阈值：向被控端补发已转发按键的抬起事件（防粘键），并通知调用方释放本机热键。</summary>
    private void FireRelease()
    {
        lock (_stateLock)
        {
            if (_hook is null || !_ctrlDown || !_altDown) return;
            _releaseTimer?.Dispose();
            _releaseTimer = null;
        }
        foreach (var entry in _tracked) _ = SendAsync(entry.Key, isUp: true);
        _tracked.Clear();
        AppLog.Write("检测到按住 Ctrl+Alt 半秒，触发释放本机热键。");
        try { _onRelease?.Invoke(); }
        catch (Exception ex) { AppLog.Write($"释放本机热键回调失败：{ex.Message}"); }
    }

    private async Task SendAsync(int code, bool isUp)
    {
        var send = _send;
        if (send is null) return;
        try { await send(code, isUp, _token); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            AppLog.Write($"转发按键到远端失败：{code} · {ex.Message}");
        }
    }

    private static bool IsModifier(KeyCode code) => code is
        KeyCode.VcLeftShift or KeyCode.VcRightShift or
        KeyCode.VcLeftControl or KeyCode.VcRightControl or
        KeyCode.VcLeftAlt or KeyCode.VcRightAlt or
        KeyCode.VcLeftMeta or KeyCode.VcRightMeta;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
