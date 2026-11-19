using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Net.Sockets;
using Microsoft.Win32;
using System.Windows.Threading;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.ComponentModel;

namespace LanChat;

public partial class ChatRoomView : UserControl
{
    /// <summary>请求宿主切回主页（关闭房间）。</summary>
    public event Action? CloseRequested;

    private readonly ObservableCollection<string> _roomLogEntries = new();

    private Window HostWindow => Window.GetWindow(this)!;

    private void ShowMessage(string text, string caption, MessageBoxButton button, MessageBoxImage icon)
    {
        if (HostWindow is { } owner) MessageBox.Show(owner, text, caption, button, icon);
        else MessageBox.Show(text, caption, button, icon);
    }

    private const long MaxAttachmentBytes = 1024L * 1024 * 1024;
    private const int AttachmentChunkBytes = 512 * 1024;
    private const int MaxAttachmentChunks = 4096;
    private const string MaxAttachmentText = "1 GB";
    private readonly string _roomName;
    private readonly string _password;
    private readonly string _userName;
    private readonly TcpListener? _listener;
    private readonly TcpClient? _upstream;
    private readonly NetworkStream? _upstreamStream;
    private readonly UdpClient? _discovery;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Peer> _peers = new();
    private readonly object _peerLock = new();
    private readonly SemaphoreSlim _upstreamWriteLock = new(1, 1);
    private readonly ConcurrentDictionary<string, AttachmentAccumulator> _incomingAttachments = new();
    private readonly AttachmentStore _attachments = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingUploadAcks = new();
    private readonly ConcurrentDictionary<string, RemoteSession> _remoteSessions = new();
    private readonly ConcurrentDictionary<string, RemoteSession> _localRemoteSessions = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _remoteCaptures = new();
    private readonly ConcurrentDictionary<string, (string Name, string Ip)> _outgoingRemoteSessions = new();
    private readonly ConcurrentDictionary<string, Process> _rustDeskProcesses = new();
    private readonly ConcurrentDictionary<string, string> _rustDeskIds = new();
    private readonly HashSet<string> _mutedNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _mutedIps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _bannedNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _bannedIps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ipBannedIps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _kickedNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _kickedIps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _adminNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _adminIps = new(StringComparer.OrdinalIgnoreCase);
    private string _myRole = "访客";
    private string _muteActor = "房主";
    private RoomMember? _contextMember;
    private readonly ObservableCollection<ChatMessage> _messages = new();
    private readonly ObservableCollection<RoomMember> _members = new();
    private readonly DateTime _roomStartedAt = DateTime.Now;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private long _lastServerHeartbeatTicks = DateTime.UtcNow.Ticks;
    private volatile bool _upstreamClosed;
    private string _serverEndpoint = "无";
    private string? _lastStatusState;
    private bool _closingStarted;
    private bool _allowClose;
    private bool _isMuted;
    private bool _sendShortcutConfigured;
    private bool _isUploading;
    private int _remoteConsentOpen;
    private string? _replySender;
    private string? _replyText;
    private string? _replyToId;
    private readonly string _clientTag = Guid.NewGuid().ToString("N")[..6];
    private int _messageSeq;
    private TextPointer? _mentionFragmentStart;
    private bool _suppressMentionSuggestions;
    private bool _mentionDismissed;
    private static readonly Brush MentionForeground = CreateMentionBrush();

    private static Brush CreateMentionBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF));
        brush.Freeze();
        return brush;
    }
    private int _uploadGeneration;

    private const uint InputTypeKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const ushort VirtualKeyLeftWindows = 0x5B;
    private const ushort VirtualKeyPeriod = 0xBE;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, NativeInput[] inputs, int inputSize);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int addressFamily, int tableClass, uint reserved);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);

    /// <summary>
    /// 必须与 C 的 INPUT 结构完全一致：SendInput 会校验 cbSize，
    /// 联合体要按最大的 MOUSEINPUT 取尺寸（x64 下 INPUT 为 40 字节），
    /// 否则会返回 ERROR_INVALID_PARAMETER（错误码 87）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParamLow;
        public ushort ParamHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint ProcessId;
    }

    public ChatRoomView(string roomName, int port, string password, string userName, bool isPublic)
    {
        InitializeComponent();
        ConfigureSendShortcut();
        InitRoomLog();
        DataObject.AddPastingHandler(MessageInput, MessageInput_Pasting);
        StartStatusLogging();
        _roomName = roomName;
        _password = password;
        _userName = string.IsNullOrWhiteSpace(userName) || userName == "输入用户名" ? "房主" : userName;
        _myRole = "创建者";
        IsPublic = isPublic;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        MessagesList.ItemsSource = _messages;
        MembersList.ItemsSource = _members;
        RoomTitle.Text = roomName;
        _members.Add(new RoomMember(_userName, GetLocalPrivateIpv4(), "创建者"));
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
            .Select(a => a.Address.ToString()).Distinct().ToArray();
        RoomSubtitle.Text = $"{string.Join(" · ", addresses)}:{port}  ·  {(IsPublic ? "公开" : "私密，仅手动加入")} · {(_password.Length == 0 ? "无密码" : "已设置密码")}";
        _ = AcceptPeersAsync(_shutdown.Token);
        _ = HeartbeatPeersAsync(_shutdown.Token);
        try
        {
            if (IsPublic)
            {
                _discovery = new UdpClient(47820) { EnableBroadcast = true };
                _ = AnswerDiscoveryAsync(_shutdown.Token);
            }
        }
        catch (SocketException) { RoomSubtitle.Text += " · 局域网搜索不可用"; }
        AddMessage("系统", "聊天室已启动。输入文字发送，或使用按钮附加文件与图片。", null, null);
        AppLog.Write($"已创建聊天室“{roomName}”，端口 {port}，{(IsPublic ? "公开" : "私密")}。");
        TrackMemberNames();
    }

    private void TrackMemberNames()
    {
        _members.CollectionChanged += (_, _) =>
        {
            if (Dispatcher.HasShutdownStarted) return;
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!Dispatcher.HasShutdownStarted)
                    MentionTextBlock.SetKnownNames(_members.Select(member => member.Name).ToArray());
            }), DispatcherPriority.Background);
        };
        MentionTextBlock.SetKnownNames(_members.Select(member => member.Name).ToArray());
    }

    public bool IsPublic { get; }

    private void Pin_Checked(object sender, RoutedEventArgs e)
    {
        if (HostWindow is { } window) window.Topmost = true;
    }

    private void Pin_Unchecked(object sender, RoutedEventArgs e)
    {
        if (HostWindow is { } window) window.Topmost = false;
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => RequestClose();

    private void InitRoomLog()
    {
        RoomLogList.ItemsSource = _roomLogEntries;
        foreach (var entry in AppLog.GetSnapshot()) _roomLogEntries.Add(entry);
        AppLog.EntryAdded += OnRoomLogEntryAdded;
    }

    private void OnRoomLogEntryAdded(string entry)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _roomLogEntries.Add(entry);
            if (_roomLogEntries.Count > 500) _roomLogEntries.RemoveAt(0);
            if (RoomLogList.Items.Count > 0) RoomLogList.ScrollIntoView(RoomLogList.Items[^1]);
        });
    }

    private void RoomLogToggle_Checked(object sender, RoutedEventArgs e) => RoomLogPanel.Visibility = Visibility.Visible;
    private void RoomLogToggle_Unchecked(object sender, RoutedEventArgs e) => RoomLogPanel.Visibility = Visibility.Collapsed;

    private void ClearRoomLog_Click(object sender, RoutedEventArgs e) => _roomLogEntries.Clear();

    private void StartStatusLogging()
    {
        _statusTimer.Tick += (_, _) =>
        {
            string state;
            string message;
            if (_listener is not null)
            {
                int count;
                lock (_peerLock) count = _peers.Count;
                state = $"host|{count}|{IsPublic}";
                message = $"运行状态：房间“{_roomName}”监听中，成员数 {count}，运行时长 {DateTime.Now - _roomStartedAt:hh\\:mm\\:ss}，公开发现 {(IsPublic ? "启用" : "关闭")}。";
            }
            else
            {
                // 连接断开后 TcpClient.Client 可能已被释放，不能再访问套接字属性（否则会抛异常导致闪退），
                // 这里只使用连接时缓存的服务器地址和断开标记。
                var connection = _upstreamClosed ? "已断开" : (_upstream is { Connected: true } ? "正常" : "已断开");
                state = $"guest|{connection}";
                message = $"运行状态：房间“{_roomName}”客户端连接 {connection}，服务器 {_serverEndpoint}。";
            }
            // 只在状态变化时记录，避免长时间挂机把日志刷满。
            if (state == _lastStatusState) return;
            _lastStatusState = state;
            AppLog.Write(message);
        };
        _statusTimer.Start();
    }

    public ChatRoomView(string roomName, string host, int port, string password, string userName)
    {
        InitializeComponent();
        ConfigureSendShortcut();
        InitRoomLog();
        DataObject.AddPastingHandler(MessageInput, MessageInput_Pasting);
        StartStatusLogging();
        _roomName = roomName;
        _password = password;
        _userName = string.IsNullOrWhiteSpace(userName) || userName == "输入用户名" ? "访客" : userName;
        _listener = null;
        _upstream = new TcpClient();
        _upstream.Connect(host, port);
        _upstreamStream = _upstream.GetStream();
        MessagesList.ItemsSource = _messages;
        MembersList.ItemsSource = _members;
        RoomTitle.Text = roomName;
        RoomSubtitle.Text = $"{host}:{port} · 正在连接";
        _serverEndpoint = $"{host}:{port}";
        var writer = new StreamWriter(_upstreamStream, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        writer.WriteLine(JsonSerializer.Serialize(new RoomPacket { Type = "join", Room = roomName, Password = password, Sender = _userName }));
        _ = ReadServerAsync(_upstreamStream, _shutdown.Token);
        _lastServerHeartbeatTicks = DateTime.UtcNow.Ticks;
        _ = MonitorServerHeartbeatAsync(_shutdown.Token);
        AppLog.Write($"正在连接聊天室“{roomName}”：{host}:{port}。");
        TrackMemberNames();
    }

    private async Task AnswerDiscoveryAsync(CancellationToken cancellationToken)
    {
        if (_discovery is null) return;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var request = await _discovery.ReceiveAsync(cancellationToken);
                var packet = JsonSerializer.Deserialize<RoomPacket>(request.Buffer);
                if (packet?.Type != "discover") continue;
                AppLog.Write($"收到 {request.RemoteEndPoint.Address} 发来的房间发现请求。");
                var hostAddress = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Select(a => a.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
                var response = JsonSerializer.SerializeToUtf8Bytes(new RoomPacket
                {
                    Type = "room", Name = _roomName, HostAddress = hostAddress?.ToString() ?? request.RemoteEndPoint.Address.ToString(),
                    Port = ((IPEndPoint)_listener!.LocalEndpoint).Port, PasswordRequired = _password.Length > 0
                });
                await _discovery.SendAsync(response, request.RemoteEndPoint, cancellationToken);
                AppLog.Write($"已向 {request.RemoteEndPoint.Address} 返回房间“{_roomName}”信息。");
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { AppLog.Write($"局域网发现服务异常：{ex}"); }
    }

    private async Task ReadServerAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, true);
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null) break;
                var packet = JsonSerializer.Deserialize<RoomPacket>(line);
                if (packet is null) continue;
                Interlocked.Exchange(ref _lastServerHeartbeatTicks, DateTime.UtcNow.Ticks);
                if (packet.Type == "heartbeat")
                {
                    await WriteUpstreamPacketAsync(new RoomPacket { Type = "heartbeat-ack" }, cancellationToken);
                    continue;
                }
                if (_upstreamStream is not null && packet.Type?.StartsWith("remote-", StringComparison.Ordinal) == true)
                {
                    await HandleRemotePacketFromServerAsync(packet, cancellationToken);
                    continue;
                }
                if (packet.Type == "host-closed")
                {
                    AppLog.Write($"聊天室“{_roomName}”的主机关闭了连接。");
                    await Dispatcher.InvokeAsync(() =>
                    {
                        var notice = new ConnectionClosedWindow { Owner = HostWindow };
                        notice.ShowDialog();
                        RequestClose();
                    });
                    return;
                }
                if (packet.Type is "removed" or "error")
                {
                    var reason = packet.Text ?? "您已被移出聊天室";
                    if (IsAccessActionNotice(reason))
                    {
                        AppLog.Write($"房间访问被拒绝：{reason}。");
                        await Dispatcher.InvokeAsync(() =>
                        {
                            new RoomAccessNoticeWindow(reason) { Owner = HostWindow }.ShowDialog();
                            RequestClose();
                        });
                        return;
                    }
                }
                if (packet.Type == "mute-state")
                {
                    _isMuted = packet.Text == "muted";
                    _muteActor = packet.SenderRole == "管理员" ? "管理员" : "房主";
                    AppLog.Write(_isMuted ? $"运行状态：本账号已被{_muteActor}禁言。" : "运行状态：本账号禁言已解除。");
                    await Dispatcher.InvokeAsync(() => AddMessage("系统", _isMuted ? $"您已被{_muteActor}禁言。" : "您的禁言已解除。", null, null));
                    continue;
                }
                if (packet.Type is "member-joined" or "member-left")
                {
                    await Dispatcher.InvokeAsync(() => UpdateMember(packet));
                    continue;
                }
                if (packet.Type == "member-role")
                {
                    await Dispatcher.InvokeAsync(() => ApplyRoleChange(packet));
                    continue;
                }
                if (packet.Type is "admin-result" or "admin-ban-list")
                {
                    await Dispatcher.InvokeAsync(() => HandleAdminResponse(packet));
                    continue;
                }
                if (packet.Type == "attachment-chunk")
                {
                    var complete = AcceptAttachmentChunk(packet);
                    if (complete is not null)
                        await Dispatcher.InvokeAsync(() => AddPacket(complete.Value.Packet, complete.Value.Path));
                    continue;
                }
                if (packet.Type == "attachment-ack" && packet.TransferId is not null)
                {
                    if (_pendingUploadAcks.TryRemove(packet.TransferId, out var ack)) ack.TrySetResult(true);
                    continue;
                }
                await Dispatcher.InvokeAsync(() =>
                {
                    if (packet.Type == "error") RoomSubtitle.Text = packet.Text ?? "无法加入房间";
                    else
                    {
                        RoomSubtitle.Text = $"{_serverEndpoint} · 已连接";
                        AddPacket(packet);
                        if (packet.Type is "text" or "file" or "image" or "system")
                            AppLog.Write($"收到房间数据：类型 {packet.Type}，发送者 {packet.Sender ?? "系统"}，内容/附件 {TrimForLog(packet.FileName ?? packet.Text)}。");
                    }
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException ex) { if (!cancellationToken.IsCancellationRequested) AppLog.Write($"房间连接读取失败：{ex}"); }
        catch (JsonException ex) { AppLog.Write($"收到无效房间数据：{ex}"); }
        finally
        {
            _upstreamClosed = true;
            AbortIncomingAttachments();
            foreach (var pending in _pendingUploadAcks.Values)
                pending.TrySetException(new IOException("与房主的连接已断开，未收到附件接收确认。"));
            if (!cancellationToken.IsCancellationRequested) AppLog.Write($"房间“{_roomName}”连接结束。");
            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    var sessions = _localRemoteSessions.Keys.Concat(_remoteCaptures.Keys).Concat(_outgoingRemoteSessions.Keys).Distinct().ToArray();
                    foreach (var sessionId in sessions) EndRemoteSessionLocally(sessionId);
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        AddMessage("系统", "与房间主机的连接已断开，远程控制会话已结束。", null, null);
                        RoomSubtitle.Text = "连接已断开";
                    }
                });
            }
            catch (InvalidOperationException) { }
        }
    }

    private async Task MonitorServerHeartbeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastServerHeartbeatTicks) <= TimeSpan.FromSeconds(20).Ticks) continue;
                AppLog.Write($"房间“{_roomName}”超过 20 秒未收到主机心跳，结束本地远程控制会话。");
                try
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        var sessions = _localRemoteSessions.Keys.Concat(_remoteCaptures.Keys).Concat(_outgoingRemoteSessions.Keys).Distinct().ToArray();
                        foreach (var sessionId in sessions) EndRemoteSessionLocally(sessionId);
                        AddMessage("系统", "与房间主机的连接超时，远程控制会话已结束。", null, null);
                        RoomSubtitle.Text = "连接已超时";
                    });
                }
                catch (InvalidOperationException) { }
                _upstreamClosed = true;
                _upstream?.Close();
                return;
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task HeartbeatPeersAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                Peer[] peers;
                lock (_peerLock) peers = _peers.ToArray();
                foreach (var peer in peers)
                {
                    if (DateTime.UtcNow.Ticks - Interlocked.Read(ref peer.LastHeartbeatTicks) > TimeSpan.FromSeconds(20).Ticks)
                    {
                        AppLog.Write($"成员 {peer.Name} 心跳超时，关闭其连接并清理关联远控会话。");
                        peer.Client.Close();
                        continue;
                    }
                    try { await SendPeerPacketAsync(peer, new RoomPacket { Type = "heartbeat" }, cancellationToken); }
                    catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
                    {
                        if (!cancellationToken.IsCancellationRequested) peer.Client.Close();
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task AcceptPeersAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(cancellationToken);
                AppLog.Write($"TCP 连接进入：{client.Client.RemoteEndPoint}。");
                _ = HandlePeerAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { AppLog.Write($"TCP 监听异常：{ex}"); }
    }

    private async Task HandlePeerAsync(TcpClient client, CancellationToken cancellationToken)
    {
        Peer? peer = null;
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, true);
            var firstLine = await reader.ReadLineAsync(cancellationToken);
            if (firstLine is null) return;
            var hello = JsonSerializer.Deserialize<RoomPacket>(firstLine);
            if (hello is null || hello.Type is not ("join" or "validate") || hello.Room != _roomName)
            {
                AppLog.Write($"拒绝 {client.Client.RemoteEndPoint} 的请求：房间名不匹配或请求格式无效。");
                await WritePacketAsync(stream, new RoomPacket { Type = "error", Text = "房间名错误或聊天室不存在。" }, cancellationToken);
                return;
            }
            if (hello.Password != _password)
            {
                AppLog.Write($"拒绝 {client.Client.RemoteEndPoint} 的加入请求：密码错误。");
                await WritePacketAsync(stream, new RoomPacket { Type = "error", Text = "密码错误" }, cancellationToken);
                return;
            }

            var peerIp = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.MapToIPv4().ToString();
            var peerName = string.IsNullOrWhiteSpace(hello.Sender) ? "访客" : hello.Sender;
            string? denial = null;
            lock (_peerLock)
            {
                if (_ipBannedIps.Contains(peerIp)) denial = "您的 IP 已被封禁";
                else if (_bannedIps.Contains(peerIp) || _bannedNames.Contains(peerName)) denial = "您已被封禁";
                else if (_kickedIps.Contains(peerIp) || _kickedNames.Contains(peerName)) denial = "您已被踢出聊天室";
            }
            if (denial is not null)
            {
                AppLog.Write($"拒绝成员 {peerName} ({peerIp})：{denial}。");
                await WritePacketAsync(stream, new RoomPacket { Type = "error", Text = denial }, cancellationToken);
                return;
            }
            if (hello.Type == "validate")
            {
                AppLog.Write($"加入预验证成功：{peerName} ({peerIp})。");
                await WritePacketAsync(stream, new RoomPacket { Type = "joined", Room = _roomName, Text = "验证成功" }, cancellationToken);
                return;
            }
            peer = new Peer(client, stream, peerName, peerIp);
            lock (_peerLock) _peers.Add(peer);
            await WritePacketAsync(stream, new RoomPacket { Type = "joined", Room = _roomName, Text = "已加入聊天室" }, cancellationToken);
            Peer[] existing;
            lock (_peerLock) existing = _peers.Where(p => !ReferenceEquals(p, peer)).ToArray();
            await WritePacketAsync(stream, new RoomPacket { Type = "member-joined", Sender = _userName, SenderIp = GetLocalPrivateIpv4(), SenderRole = "创建者" }, cancellationToken);
            foreach (var existingPeer in existing)
                await WritePacketAsync(stream, new RoomPacket { Type = "member-joined", Sender = existingPeer.Name, SenderIp = existingPeer.Ip, SenderRole = RoleOf(existingPeer) }, cancellationToken);
            await BroadcastAsync(new RoomPacket { Type = "member-joined", Sender = peer.Name, SenderIp = peer.Ip, SenderRole = RoleOf(peer) }, cancellationToken);
            await Dispatcher.InvokeAsync(() => _members.Add(new RoomMember(peer.Name, peer.Ip, RoleOf(peer))));
            var joinNotice = new RoomPacket { Type = "system", Room = _roomName, Text = $"{peer.Name} · {peer.Ip} 加入了聊天室。" };
            await BroadcastAsync(joinNotice, cancellationToken);
            await Dispatcher.InvokeAsync(() => AddPacket(joinNotice));
            AppLog.Write($"{peer.Name} ({peer.Ip}) 加入聊天室“{_roomName}”。");
            bool initiallyMuted;
            lock (_peerLock) initiallyMuted = _mutedNames.Contains(peer.Name) || _mutedIps.Contains(peer.Ip);
            if (initiallyMuted)
                await WritePacketAsync(stream, new RoomPacket { Type = "mute-state", Text = "muted" }, cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null) break;
                var packet = JsonSerializer.Deserialize<RoomPacket>(line);
                if (packet is null) continue;
                Interlocked.Exchange(ref peer.LastHeartbeatTicks, DateTime.UtcNow.Ticks);
                if (packet.Type == "heartbeat-ack")
                {
                    continue;
                }
                if (packet.Type?.StartsWith("remote-", StringComparison.Ordinal) == true)
                {
                    await HandleRemotePacketFromPeerAsync(peer, packet, cancellationToken);
                    continue;
                }
                if (packet.Type?.StartsWith("admin-", StringComparison.Ordinal) == true)
                {
                    await HandleAdminPacketAsync(peer, packet, cancellationToken);
                    continue;
                }
                if (packet.Type is not ("text" or "attachment-chunk")) continue;
                bool muted;
                lock (_peerLock) muted = _mutedNames.Contains(peer.Name) || _mutedIps.Contains(peer.Ip);
                if (muted)
                {
                    await WritePacketAsync(stream, new RoomPacket { Type = "system", Text = "你当前已被房主禁言。" }, cancellationToken);
                    continue;
                }
                packet.Room = _roomName;
                packet.Sender = peer.Name;
                packet.SenderRole = RoleOf(peer);
                packet.SenderIp = peer.Ip;
                // 旧版本对端可能没有带消息 ID，由房主补上，保证所有成员都能互相跳转引用。
                packet.MessageId ??= NextMessageId();
                await BroadcastAsync(packet, cancellationToken);
                if (packet.Type == "attachment-chunk")
                {
                    var complete = AcceptAttachmentChunk(packet);
                    if (complete is not null)
                    {
                        Dispatcher.Invoke(() => AddPacket(complete.Value.Packet, complete.Value.Path));
                        AppLog.Write($"收到成员附件：{peer.Name} ({peer.Ip})，{complete.Value.Packet.FileName}，{new FileInfo(complete.Value.Path).Length:N0} 字节。");
                        await peer.WriteLock.WaitAsync(cancellationToken);
                        try { await WritePacketAsync(peer.Stream, new RoomPacket { Type = "attachment-ack", TransferId = packet.TransferId }, cancellationToken); }
                        finally { peer.WriteLock.Release(); }
                    }
                }
                else
                {
                    Dispatcher.Invoke(() => AddPacket(packet));
                    AppLog.Write($"收到成员消息：{peer.Name} ({peer.Ip})，类型 {packet.Type}，文本 {packet.Text ?? ""}。");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException ex) { if (!cancellationToken.IsCancellationRequested) AppLog.Write($"成员 {peer?.Name ?? "未知"} 连接错误：{ex}"); }
        catch (JsonException ex) { AppLog.Write($"成员 {peer?.Name ?? "未知"} 发送了无效数据：{ex}"); }
        finally
        {
            if (peer is not null)
            {
                lock (_peerLock) _peers.Remove(peer);
                foreach (var session in _remoteSessions.Values.Where(s => ReferenceEquals(s.RequesterPeer, peer) || ReferenceEquals(s.TargetPeer, peer)).ToArray())
                {
                    _remoteSessions.TryRemove(session.SessionId, out _);
                    var otherPeer = ReferenceEquals(session.RequesterPeer, peer) ? session.TargetPeer : session.RequesterPeer;
                    if (otherPeer is not null)
                    {
                        try { await SendPeerPacketAsync(otherPeer, new RoomPacket { Type = "remote-stop", SessionId = session.SessionId }, cancellationToken); }
                        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
                    }
                    try { await Dispatcher.InvokeAsync(() => EndRemoteSessionLocally(session.SessionId)); } catch (InvalidOperationException) { }
                }
                foreach (var transfer in _incomingAttachments.Where(entry => entry.Value.SenderIp == peer.Ip).Select(entry => entry.Key).ToArray())
                {
                    if (_incomingAttachments.TryRemove(transfer, out var abandoned)) abandoned.Dispose();
                }
                try
                {
                    await BroadcastAsync(new RoomPacket { Type = "member-left", Sender = peer.Name, SenderIp = peer.Ip }, cancellationToken);
                }
                catch (OperationCanceledException) { }
                try { await Dispatcher.InvokeAsync(() => _members.Remove(_members.FirstOrDefault(m => m.Ip == peer.Ip && m.Name == peer.Name)!)); } catch (InvalidOperationException) { }
                try { Dispatcher.Invoke(() => AddMessage("系统", $"{peer.Name} 离开了聊天室。", null, null)); } catch (InvalidOperationException) { }
            }
            client.Dispose();
            if (peer is not null) AppLog.Write($"成员断开：{peer.Name} ({peer.Ip})。");
        }
    }

    private async Task BroadcastAsync(RoomPacket packet, CancellationToken cancellationToken)
    {
        string json;
        try { json = JsonSerializer.Serialize(packet) + "\n"; }
        catch (ArgumentException ex)
        {
            AppLog.Write($"房间数据序列化失败，已跳过该数据包：{ex.Message}");
            await Dispatcher.InvokeAsync(() => AddMessage("系统", "数据包过大，未能发送。请缩小消息或附件后重试。", null, null));
            return;
        }
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        Peer[] peers;
        lock (_peerLock) peers = _peers.ToArray();
        foreach (var peer in peers)
        {
            try
            {
                await peer.WriteLock.WaitAsync(cancellationToken);
                try { await peer.Stream.WriteAsync(bytes, cancellationToken); await peer.Stream.FlushAsync(cancellationToken); }
                finally { peer.WriteLock.Release(); }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private static async Task WritePacketAsync(NetworkStream stream, RoomPacket packet, CancellationToken cancellationToken)
    {
        var bytes = SerializePacket(packet);
        await WritePacketBytesAsync(stream, bytes, cancellationToken);
    }

    private static byte[] SerializePacket(RoomPacket packet) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(packet) + "\n");

    private static async Task WritePacketBytesAsync(NetworkStream stream, byte[] bytes, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var text = GetMessageInputText().Trim();
        if (text.Length == 0) return;
        if (_upstreamStream is not null && _upstreamClosed)
        {
            AddMessage("系统", "与房间主机的连接已断开，消息未发送。内容仍保留在输入框，可复制后重新加入房间。", null, null);
            return;
        }
        if (_upstreamStream is not null && _isMuted)
        {
            AppLog.Write($"已拦截禁言访客 {_userName} 的消息发送操作。");
            AddMessage("系统", $"您已被{_muteActor}禁言，暂时无法发送消息或附件。", null, null);
            return;
        }
        if (text.StartsWith('/'))
        {
            ClearMessageInput();
            if (_listener is not null) await RunOwnerCommandAsync(text);
            else if (_myRole == "管理员") await SendAdminCommandAsync(text);
            else AddMessage("系统", "只有房主或管理员可以使用管理指令。", null, null);
            return;
        }
        var packet = new RoomPacket
        {
            Type = "text", Room = _roomName, Sender = _userName, SenderRole = _myRole,
            SenderIp = GetLocalPrivateIpv4(), Text = text, ReplySender = _replySender, ReplyText = _replyText,
            MessageId = NextMessageId(), ReplyToId = _replyToId
        };
        AppLog.Write($"{_roomName} · {_userName}: {text}");
        if (_upstreamStream is not null)
        {
            // 先确认写入成功再清空输入框，连接已断开时保住用户输入的内容。
            if (await WriteUpstreamPacketAsync(packet, _shutdown.Token))
            {
                ClearMessageInput();
                ClearReply();
            }
            else AddMessage("系统", "消息未能发送：与房间主机的连接已断开，内容仍保留在输入框。", null, null);
            return;
        }
        ClearMessageInput();
        ClearReply();
        AddPacket(packet);
        await BroadcastAsync(packet, _shutdown.Token);
    }

    private string GetMessageInputText()
    {
        var range = new TextRange(MessageInput.Document.ContentStart, MessageInput.Document.ContentEnd);
        return range.Text.Replace("\r\n", "\n").TrimEnd('\n');
    }

    private void ClearMessageInput()
    {
        _suppressMentionSuggestions = true;
        try
        {
            MessageInput.Document.Blocks.Clear();
            MessageInput.Document.Blocks.Add(new Paragraph());
            MessageInput.CaretPosition = MessageInput.Document.ContentEnd;
        }
        finally { _suppressMentionSuggestions = false; }
        HideMentionPopup();
    }

    private void ClearReply()
    {
        _replySender = null;
        _replyText = null;
        _replyToId = null;
        ReplyBar.Visibility = Visibility.Collapsed;
        ReplySenderText.Text = string.Empty;
        ReplyPreviewText.Text = string.Empty;
    }

    private void CancelReply_Click(object sender, MouseButtonEventArgs e) => ClearReply();

    private async Task<string> RunOwnerCommandAsync(string raw, bool fromAdmin = false, string? requesterName = null)
    {
        // 管理员发起的指令只回给发起人，避免房主聊天区出现无关提示。
        void Report(string text)
        {
            if (fromAdmin) AppLog.Write($"管理员指令反馈：{text}");
            else AddMessage("系统", text, null, null);
        }

        var parts = raw.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1 && parts[0].Equals("/help", StringComparison.OrdinalIgnoreCase))
        {
            const string help = "用法：/mute 用户名或IP · /kick 用户名或IP · /ban 用户名或IP · /ipban IP · /unban 用户名或IP · /unipban IP";
            Report(help);
            return help;
        }
        if (parts.Length != 2 || parts[1].Length == 0)
        {
            const string usage = "指令格式：/指令 用户名或IP。输入 /help 查看可用指令。";
            Report(usage);
            return usage;
        }

        var command = parts[0].ToLowerInvariant();
        var target = parts[1].Trim();
        if (command is "/admin" or "/unadmin")
        {
            const string onlyOwner = "只有房主可以设置或取消管理员。";
            Report(onlyOwner);
            return onlyOwner;
        }
        if (fromAdmin)
        {
            var denied = DenyModerationReason(target, requesterName);
            if (denied is not null)
            {
                Report(denied);
                return denied;
            }
        }
        AppLog.Write($"房主命令请求：{command} {target}（来源：{(fromAdmin ? "管理员" : "房主")}）。");
        if (command is "/ipban" or "/unipban" && !IPAddress.TryParse(target, out _))
        {
            var invalid = $"{command} 的目标必须是有效 IP 地址。";
            Report(invalid);
            return invalid;
        }

        Peer[] matches;
        lock (_peerLock)
        {
            matches = _peers.Where(p => Matches(p, target)).ToArray();
            switch (command)
            {
                case "/mute":
                    ToggleTargets(_mutedNames, _mutedIps, target);
                    break;
                case "/kick":
                    AddTarget(IPAddress.TryParse(target, out _) ? _kickedIps : _kickedNames, target);
                    break;
                case "/ban":
                    if (IPAddress.TryParse(target, out _)) _bannedIps.Add(target);
                    else _bannedNames.Add(target);
                    break;
                case "/ipban":
                    _bannedIps.Add(target);
                    _ipBannedIps.Add(target);
                    break;
                case "/unban":
                    if (IPAddress.TryParse(target, out _))
                    {
                        _bannedIps.Remove(target);
                        _ipBannedIps.Remove(target);
                    }
                    else _bannedNames.Remove(target);
                    break;
                case "/unipban":
                    _bannedIps.Remove(target);
                    _ipBannedIps.Remove(target);
                    break;
                default:
                    const string unknown = "未知指令。可用：/mute、/kick、/ban、/ipban、/unban、/unipban、/help";
                    Report(unknown);
                    return unknown;
            }

            if (command is "/kick" or "/ban" or "/ipban")
            {
                foreach (var peer in matches) _peers.Remove(peer);
            }
        }

        var removalNotice = command switch
        {
            "/kick" => "您已被踢出聊天室",
            "/ban" => "您已被封禁",
            "/ipban" => "您的 IP 已被封禁",
            _ => null
        };
        if (command == "/mute")
        {
            foreach (var peer in matches)
            {
                bool muted;
                lock (_peerLock) muted = _mutedNames.Contains(peer.Name) || _mutedIps.Contains(peer.Ip);
                try
                {
                    await peer.WriteLock.WaitAsync(_shutdown.Token);
                    try
                    {
                        await WritePacketAsync(peer.Stream, new RoomPacket
                        {
                            Type = "mute-state", Text = muted ? "muted" : "unmuted",
                            Sender = fromAdmin ? requesterName : _userName,
                            SenderRole = fromAdmin ? "管理员" : "创建者"
                        }, _shutdown.Token);
                    }
                    finally { peer.WriteLock.Release(); }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
                { AppLog.Write($"无法更新 {peer.Name} 的禁言状态：{ex.Message}"); }
            }
        }
        foreach (var peer in matches.Where(p => removalNotice is not null))
        {
            try
            {
                await peer.WriteLock.WaitAsync(_shutdown.Token);
                try { await WritePacketAsync(peer.Stream, new RoomPacket { Type = "removed", Text = removalNotice }, _shutdown.Token); }
                finally { peer.WriteLock.Release(); }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            { AppLog.Write($"无法向 {peer.Name} 发送移出通知：{ex.Message}"); }
            try { peer.Client.Dispose(); } catch (ObjectDisposedException) { }
        }

        var report = command switch
        {
            "/mute" => $"已切换 {target} 的禁言状态。",
            "/kick" => matches.Length == 0 ? $"当前没有匹配的在线成员：{target}" : $"已将 {target} 移出聊天室。",
            "/ban" => $"已禁止 {target} 再次加入。",
            "/ipban" => $"已禁止 IP {target} 再次加入。",
            "/unban" => $"已解除 {target} 的封禁（如存在）。",
            "/unipban" => $"已解除 IP {target} 的封禁（如存在）。",
            _ => ""
        };
        Report(report);
        if (command == "/mute")
        {
            await BroadcastAsync(new RoomPacket
            {
                Type = "system", Room = _roomName,
                Text = $"{(fromAdmin ? "管理员" : "房主")}更新了 {target} 的发言权限。"
            }, _shutdown.Token);
        }
        return report;
    }

    private bool TargetsHost(string target)
    {
        if (string.Equals(target, _userName, StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(target, out _) && IsLocalAddress(target);
    }

    /// <summary>管理员不能对房主、其他管理员或自己执行管理操作。</summary>
    private string? DenyModerationReason(string target, string? requesterName)
    {
        const string denied = "不能对管理员和自己进行管理操作。";
        if (!string.IsNullOrWhiteSpace(requesterName) && string.Equals(target, requesterName, StringComparison.OrdinalIgnoreCase))
            return denied;
        if (TargetsHost(target)) return denied;
        lock (_peerLock)
        {
            // 优先按用户名判定：同一公网 IP 下可能有多名成员，不能因为 IP 相同就拒绝。
            var peer = _peers.FirstOrDefault(p => p.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
            if (peer is not null) return _adminNames.Contains(peer.Name) ? denied : null;
            if (IPAddress.TryParse(target, out _) && _adminIps.Contains(target)) return denied;
        }
        return null;
    }

    private async Task SendAdminCommandAsync(string command)
    {
        if (_upstreamStream is null) return;
        var sent = await WriteUpstreamPacketAsync(new RoomPacket
        {
            Type = "admin-command", Room = _roomName, Sender = _userName, SenderIp = GetLocalPrivateIpv4(), Text = command
        }, _shutdown.Token);
        if (sent) AppLog.Write($"已向房主提交管理员指令：{command}。");
        else AddMessage("系统", "管理员指令未发送：与房间主机的连接已断开。", null, null);
    }

    private void MembersToggle_Changed(object sender, RoutedEventArgs e)
    {
        var show = MembersToggle.IsChecked == true;
        MemberPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        MemberColumn.Width = show ? new GridLength(245) : new GridLength(0);
    }

    private void UpdateMember(RoomPacket packet)
    {
        var name = packet.Sender ?? string.Empty;
        var ip = packet.SenderIp ?? string.Empty;
        if (packet.Type == "member-left")
        {
            var old = _members.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && m.Ip == ip);
            if (old is not null) _members.Remove(old);
            return;
        }
        if (_members.All(m => !(m.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && m.Ip == ip)))
            _members.Add(new RoomMember(name, ip, packet.SenderRole ?? "访客"));
        if (string.Equals(name, _userName, StringComparison.OrdinalIgnoreCase))
            _myRole = packet.SenderRole ?? "访客";
    }

    private void ApplyRoleChange(RoomPacket packet)
    {
        var name = packet.Sender ?? string.Empty;
        var ip = packet.SenderIp ?? string.Empty;
        var role = string.IsNullOrWhiteSpace(packet.SenderRole) ? "访客" : packet.SenderRole;
        var member = _members.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && m.Ip == ip);
        if (member is not null)
        {
            var index = _members.IndexOf(member);
            if (index >= 0) _members[index] = member with { Role = role };
        }
        if (string.Equals(name, _userName, StringComparison.OrdinalIgnoreCase)) _myRole = role;
        AppLog.Write($"成员身份更新：{name} 现在是{role}。");
    }

    private void HandleAdminResponse(RoomPacket packet)
    {
        if (packet.Type == "admin-result")
        {
            AddMessage("系统", string.IsNullOrWhiteSpace(packet.Text) ? "管理员指令已执行。" : packet.Text, null, null);
            return;
        }
        BanRecord[] records;
        try { records = JsonSerializer.Deserialize<BanRecord[]>(packet.Text ?? "[]") ?? []; }
        catch (JsonException) { records = []; }
        new BanListWindow(records,
            record => _ = SendAdminCommandAsync($"{(record.Kind == "用户封禁" ? "/unban" : "/unipban")} {record.Target}"))
        { Owner = HostWindow }.ShowDialog();
    }

    private void MembersList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var member = _contextMember ?? MembersList.SelectedItem as RoomMember;
        if (member is null || MembersList.ContextMenu is null) { e.Handled = true; return; }
        var isHost = _listener is not null;
        var canManage = CanManageMember(member);
        var targetIsHost = member.Role == "创建者";
        var isSelf = string.Equals(member.Name, _userName, StringComparison.OrdinalIgnoreCase);
        foreach (var item in MembersList.ContextMenu.Items.OfType<MenuItem>())
        {
            if (item.Tag is string)
            {
                item.Visibility = canManage ? Visibility.Visible : Visibility.Collapsed;
            }
            else if (ReferenceEquals(item, AdminToggleItem))
            {
                var visible = isHost && !targetIsHost && !isSelf;
                item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                if (visible) item.Header = member.Role == "管理员" ? "取消管理员" : "设为管理员";
            }
        }
    }

    /// <summary>房主可以管理除自己以外的所有成员；管理员只能管理访客。</summary>
    private bool CanManageMember(RoomMember target)
    {
        if (string.Equals(target.Name, _userName, StringComparison.OrdinalIgnoreCase)) return false;
        if (target.Role is "创建者" or "管理员") return false;
        return _listener is not null || _myRole == "管理员";
    }

    private void MembersList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        var item = ItemsControl.ContainerFromElement(MembersList, source) as ListBoxItem;
        if (item?.DataContext is RoomMember member)
        {
            item.IsSelected = true;
            _contextMember = member;
        }
        else _contextMember = null;
    }

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            e.Handled = true;
            _ = UploadFilesAsync(paths, false);
        }
    }

    private void MessageInput_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        // 1) 从资源管理器复制的文件：图片直接按图片发送，其他文件按附件发送。
        if (e.DataObject.GetDataPresent(DataFormats.FileDrop, true) && e.DataObject.GetData(DataFormats.FileDrop, true) is string[] paths && paths.Length > 0)
        {
            e.CancelCommand();
            var allImages = paths.All(IsImagePath);
            _ = UploadFilesAsync(paths, allImages);
            return;
        }

        // 2) 剪贴板里是位图（截图、复制的图片）：自动以图片消息发送。
        var imageBytes = TryReadClipboardImage(e.DataObject);
        if (imageBytes is not null)
        {
            e.CancelCommand();
            _ = SendPastedImageAsync(imageBytes);
            return;
        }

        // 3) 纯文本：粘贴为无格式文本，避免带入外部字体和颜色。
        if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText, true)) return;
        var text = e.DataObject.GetData(DataFormats.UnicodeText, true) as string ?? string.Empty;
        var plain = new DataObject();
        plain.SetText(text);
        e.DataObject = plain;
    }

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"];

    private static bool IsImagePath(string path)
    {
        var extension = Path.GetExtension(path);
        return ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>把剪贴板中的位图转成 PNG 字节；不是图片时返回 null。</summary>
    private static byte[]? TryReadClipboardImage(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent("PNG", true) && data.GetData("PNG", true) is Stream pngStream)
            {
                using (pngStream)
                {
                    using var buffer = new MemoryStream();
                    pngStream.CopyTo(buffer);
                    if (buffer.Length > 0) return buffer.ToArray();
                }
            }
            if (data.GetDataPresent(DataFormats.Bitmap, true) && data.GetData(DataFormats.Bitmap, true) is BitmapSource source)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                using var output = new MemoryStream();
                encoder.Save(output);
                return output.Length > 0 ? output.ToArray() : null;
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
        {
            AppLog.Write($"读取剪贴板图片失败：{ex.Message}");
        }
        return null;
    }

    /// <summary>把粘贴进来的图片写入临时文件并以图片消息发送，避免粘贴内容停留在输入框。</summary>
    private async Task SendPastedImageAsync(byte[] png)
    {
        // 粘贴的图片同样落到会话附件目录，发送后仍可点击“下载附件”保存。
        var path = _attachments.CreatePath("粘贴的图片.png", ".png");
        try
        {
            await Task.Run(() => File.WriteAllBytes(path, png)).ConfigureAwait(false);
            AppLog.Write($"检测到剪贴板图片，{png.Length:N0} 字节，自动按图片发送。");
            // 上传流程会操作界面控件，必须回到 UI 线程执行。
            await Dispatcher.InvokeAsync(() => UploadFilesAsync(new[] { path }, true)).Task.Unwrap().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"粘贴图片发送失败：{ex}");
            AddMessage("系统", $"粘贴的图片无法发送：{ex.Message}", null, null);
        }
    }

    private async void MemberCommand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string command } || MembersList.SelectedItem is not RoomMember member) return;
        if (!CanManageMember(member))
        {
            AddMessage("系统", "不能对管理员和自己进行管理操作。", null, null);
            return;
        }
        if (_listener is not null) await RunOwnerCommandAsync($"{command} {member.Name}");
        else if (_myRole == "管理员") await SendAdminCommandAsync($"{command} {member.Name}");
        else AddMessage("系统", "只有房主或管理员可以管理成员。", null, null);
    }

    private async void ToggleAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (_listener is null || MembersList.SelectedItem is not RoomMember member) return;
        if (member.Role == "创建者") return;
        await SetAdminAsync(member, member.Role != "管理员");
    }

    private async Task SetAdminAsync(RoomMember member, bool makeAdmin)
    {
        lock (_peerLock)
        {
            if (makeAdmin)
            {
                _adminNames.Add(member.Name);
                _adminIps.Add(member.Ip);
            }
            else
            {
                _adminNames.Remove(member.Name);
                _adminIps.Remove(member.Ip);
            }
        }
        var role = makeAdmin ? "管理员" : "访客";
        var index = _members.IndexOf(member);
        if (index >= 0) _members[index] = member with { Role = role };
        var notice = makeAdmin ? $"已设置 {member.Name} 为管理员。" : $"已取消 {member.Name} 的管理员权限。";
        AddMessage("系统", notice, null, null);
        AppLog.Write(notice);
        await BroadcastAsync(new RoomPacket
        {
            Type = "member-role", Room = _roomName, Sender = member.Name, SenderIp = member.Ip, SenderRole = role
        }, _shutdown.Token);
    }

    private string RoleOf(Peer peer)
    {
        lock (_peerLock)
            return _adminNames.Contains(peer.Name) || _adminIps.Contains(peer.Ip) ? "管理员" : "访客";
    }

    private bool IsPeerAdmin(Peer peer)
    {
        lock (_peerLock)
            return _adminNames.Contains(peer.Name) || _adminIps.Contains(peer.Ip);
    }

    private async Task HandleAdminPacketAsync(Peer peer, RoomPacket packet, CancellationToken cancellationToken)
    {
        if (!IsPeerAdmin(peer))
        {
            await SendPeerPacketAsync(peer, new RoomPacket { Type = "admin-result", Text = "您没有管理员权限。" }, cancellationToken);
            return;
        }
        switch (packet.Type)
        {
            case "admin-command":
                var command = packet.Text?.Trim() ?? string.Empty;
                var result = command.Length == 0
                    ? "指令不能为空。"
                    : await RunOwnerCommandAsync(command, fromAdmin: true, requesterName: peer.Name);
                await SendPeerPacketAsync(peer, new RoomPacket { Type = "admin-result", Text = result }, cancellationToken);
                break;
            case "admin-ban-list-request":
                var records = BuildBanRecords();
                await SendPeerPacketAsync(peer, new RoomPacket
                {
                    Type = "admin-ban-list", Text = JsonSerializer.Serialize(records)
                }, cancellationToken);
                break;
        }
    }

    private BanRecord[] BuildBanRecords()
    {
        lock (_peerLock)
        {
            return _bannedNames.Select(name => new BanRecord(name, "用户封禁"))
                .Concat(_bannedIps.Select(ip => new BanRecord(ip, "IP 封禁")))
                .ToArray();
        }
    }

    private async void ShowBanList_Click(object sender, RoutedEventArgs e)
    {
        if (_listener is not null)
        {
            new BanListWindow(BuildBanRecords(),
                record => _ = RunOwnerCommandAsync($"{(record.Kind == "用户封禁" ? "/unban" : "/unipban")} {record.Target}"))
            { Owner = HostWindow }.ShowDialog();
            return;
        }
        if (_myRole != "管理员")
        {
            AddMessage("系统", "只有房主或管理员可以查看封禁列表。", null, null);
            return;
        }
        await WriteUpstreamPacketAsync(new RoomPacket
        {
            Type = "admin-ban-list-request", Room = _roomName, Sender = _userName, SenderIp = GetLocalPrivateIpv4()
        }, _shutdown.Token);
        AppLog.Write("已向房主请求封禁列表。");
    }

    private async void RequestRemoteControl_Click(object sender, RoutedEventArgs e)
    {
        if (MembersList.SelectedItem is not RoomMember member) return;
        if (IsLocalAddress(member.Ip))
        {
            AddMessage("系统", "不能向本机申请远程控制。", null, null);
            return;
        }
        var sessionId = Guid.NewGuid().ToString("N");
        var packet = new RoomPacket
        {
            Type = "remote-request", SessionId = sessionId, TargetIp = member.Ip,
            Sender = _userName, SenderIp = GetLocalPrivateIpv4()
        };
        if (_listener is not null)
        {
            var target = FindPeer(member.Ip);
            if (target is null) { AddMessage("系统", "该成员当前未连接。", null, null); return; }
            _outgoingRemoteSessions[sessionId] = (member.Name, member.Ip);
            var session = new RemoteSession(sessionId, _userName, GetLocalPrivateIpv4(), member.Name, member.Ip, null, target, true, false);
            _remoteSessions[sessionId] = session;
            await SendPeerPacketAsync(target, packet, _shutdown.Token);
            _ = ExpireRemoteRequestAsync(sessionId);
        }
        else
        {
            _outgoingRemoteSessions[sessionId] = (member.Name, member.Ip);
            await WriteUpstreamPacketAsync(packet, _shutdown.Token);
        }
        UpdateRemoteControlStatus();
        AddMessage("系统", $"已向 {member.Name} 发送远程控制申请，等待对方确认。", null, null);
        AppLog.Write($"已请求远程控制：{member.Name} ({member.Ip})，会话 {sessionId}。");
        RustDeskRuntime.RegisterClientSession(sessionId, member.Ip);
        _ = PrepareControllerAsync(sessionId, member.Name, member.Ip);
    }

    /// <summary>发起申请时就启动 RustDesk（仅托盘），对方接受后直接弹出连接窗口。</summary>
    private async Task PrepareControllerAsync(string sessionId, string targetName, string targetIp)
    {
        try
        {
            var running = await RustDeskRuntime.EnsureRunningAsync(_shutdown.Token);
            // 准备期间对方可能已经拒绝或超时：此时会话已结束，不能再留下一个空的 RustDesk 进程。
            if (!_outgoingRemoteSessions.ContainsKey(sessionId))
            {
                AppLog.Write($"远程控制申请已结束，取消为 {targetName} 准备的 RustDesk 控制端。");
                if (running) RustDeskRuntime.EndClientSession(sessionId, targetIp);
                return;
            }
            if (!running) return;
            AppLog.Write($"RustDesk 控制端已就绪（无主窗口），等待对方接受：{targetName}。");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Write($"RustDesk 控制端启动失败：{ex.Message}"); }
    }

    private async Task HandleRemotePacketFromPeerAsync(Peer peer, RoomPacket packet, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(packet.SessionId)) return;
        switch (packet.Type)
        {
            case "remote-request":
            {
                if (string.IsNullOrWhiteSpace(packet.TargetIp) || IsLocalAddress(peer.Ip)) return;
                var targetIsLocal = IsLocalAddress(packet.TargetIp);
                var targetPeer = targetIsLocal ? null : FindPeer(packet.TargetIp);
                if (!targetIsLocal && targetPeer is null)
                {
                    await SendPeerPacketAsync(peer, new RoomPacket { Type = "remote-error", SessionId = packet.SessionId, Text = "对方当前未连接。" }, cancellationToken);
                    return;
                }
                var targetName = targetIsLocal ? _userName : targetPeer!.Name;
                var session = new RemoteSession(packet.SessionId, peer.Name, peer.Ip, targetName, targetIsLocal ? GetLocalPrivateIpv4() : targetPeer!.Ip, peer, targetPeer, false, targetIsLocal);
                if (!_remoteSessions.TryAdd(session.SessionId, session)) return;
                packet.Sender = peer.Name;
                packet.SenderIp = peer.Ip;
                packet.TargetIp = session.TargetIp;
                _ = ExpireRemoteRequestAsync(session.SessionId);
                if (targetIsLocal)
                    _ = Dispatcher.InvokeAsync(() => HandleLocalRemoteRequestSafelyAsync(session)).Task.Unwrap();
                else await SendPeerPacketAsync(targetPeer!, packet, cancellationToken);
                AppLog.Write($"远程控制申请已转发：{peer.Name} ({peer.Ip}) → {session.TargetName} ({session.TargetIp})。");
                break;
            }
            case "remote-response":
                await HandleRemoteResponseFromPeerAsync(peer, packet, cancellationToken);
                break;
            case "remote-stop":
                await HandleRemoteStopFromPeerAsync(peer, packet, cancellationToken);
                break;
        }
    }

    private async Task HandleLocalRemoteRequestSafelyAsync(RemoteSession session)
    {
        try { await HandleLocalRemoteRequestAsync(session); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Write($"处理远程控制请求失败：{ex}"); }
    }

    private async Task HandleIncomingRemoteRequestSafelyAsync(RoomPacket packet)
    {
        try { await HandleIncomingRemoteRequestAsync(packet); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Write($"处理远程控制申请失败：{ex}"); }
    }

    private async Task HandleLocalRemoteRequestAsync(RemoteSession session)
    {
        if (Interlocked.CompareExchange(ref _remoteConsentOpen, 1, 0) != 0)
        {
            await RespondToRemoteRequestAsync(session.SessionId, false);
            return;
        }
        var consent = new RemoteControlConsentWindow(session.RequesterName, session.RequesterIp) { Owner = HostWindow };
        var previousForeground = GetForegroundWindow();
        bool accepted;
        try { accepted = consent.ShowDialog() == true; }
        finally
        {
            Interlocked.Exchange(ref _remoteConsentOpen, 0);
            if (previousForeground != IntPtr.Zero) SetForegroundWindow(previousForeground);
        }
        if (!_remoteSessions.ContainsKey(session.SessionId)) return;
        if (!accepted)
        {
            await RespondToRemoteRequestAsync(session.SessionId, false);
            return;
        }
        AppLog.Write($"本机用户接受远程控制请求，会话 {session.SessionId}。");
        if (!await StartRemoteCaptureAsync(session.SessionId, session.RequesterName))
        {
            await RespondToRemoteRequestAsync(session.SessionId, false, "对方未能启动 RustDesk。");
            return;
        }
        if (!session.TryAccept()) return;
        var response = new RoomPacket
        {
            Type = "remote-accepted", SessionId = session.SessionId, Sender = session.TargetName, SenderIp = session.TargetIp,
            Port = RustDeskRuntime.DirectAccessPort, RustDeskId = _rustDeskIds.GetValueOrDefault(session.SessionId)
        };
        if (session.RequesterIsLocal) _ = LaunchRustDeskConnectionAsync(session.SessionId, session.TargetName, session.TargetIp, _rustDeskIds.GetValueOrDefault(session.SessionId));
        else if (session.RequesterPeer is not null) await SendPeerPacketAsync(session.RequesterPeer, response, _shutdown.Token);
        AppLog.Write($"本机已接受远程控制会话 {session.SessionId}，请求者 {session.RequesterName} ({session.RequesterIp})。");
    }

    private async Task RespondToRemoteRequestAsync(string sessionId, bool accepted, string? reason = null)
    {
        if (!_remoteSessions.TryGetValue(sessionId, out var session)) return;
        var startFailed = false;
        if (accepted)
        {
            if (!await StartRemoteCaptureAsync(sessionId, session.RequesterName)) { accepted = false; startFailed = true; }
            else if (!session.TryAccept()) return;
        }
        var response = new RoomPacket
        {
            Type = accepted ? "remote-accepted" : "remote-declined", SessionId = sessionId, Sender = session.TargetName, SenderIp = session.TargetIp,
            Port = accepted ? RustDeskRuntime.DirectAccessPort : 0,
            RustDeskId = accepted ? _rustDeskIds.GetValueOrDefault(sessionId) : null,
            Text = reason ?? (startFailed ? "对方未能启动 RustDesk。" : null)
        };
        if (session.RequesterIsLocal)
        {
            if (accepted) _ = LaunchRustDeskConnectionAsync(sessionId, session.TargetName, session.TargetIp, _rustDeskIds.GetValueOrDefault(sessionId));
            else ShowMessage("对方已拒绝", "远程控制", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else if (session.RequesterPeer is not null) await SendPeerPacketAsync(session.RequesterPeer, response, _shutdown.Token);
        if (accepted && session.TargetPeer is not null)
            await SendPeerPacketAsync(session.TargetPeer, new RoomPacket { Type = "remote-start", SessionId = sessionId, Sender = session.RequesterName, SenderIp = session.RequesterIp }, _shutdown.Token);
        if (!accepted) _remoteSessions.TryRemove(sessionId, out _);
    }

    private async Task HandleRemoteResponseFromPeerAsync(Peer peer, RoomPacket packet, CancellationToken cancellationToken)
    {
        if (!_remoteSessions.TryGetValue(packet.SessionId!, out var session) || !ReferenceEquals(session.TargetPeer, peer)) return;
        var accepted = packet.Accepted;
        if (accepted)
        {
            if (!session.TryAccept()) return;
            await SendPeerPacketAsync(peer, new RoomPacket { Type = "remote-start", SessionId = session.SessionId, Sender = session.RequesterName, SenderIp = session.RequesterIp }, cancellationToken);
        }
        var response = new RoomPacket
        {
            Type = accepted ? "remote-accepted" : "remote-declined", SessionId = session.SessionId, Sender = session.TargetName, SenderIp = session.TargetIp,
            Port = accepted ? RustDeskRuntime.DirectAccessPort : 0, RustDeskId = packet.RustDeskId, Text = packet.Text
        };
        if (session.RequesterIsLocal)
        {
            if (accepted) _ = LaunchRustDeskConnectionAsync(session.SessionId, session.TargetName, session.TargetIp, packet.RustDeskId);
            else await Dispatcher.InvokeAsync(() => ShowMessage("对方已拒绝", "远程控制", MessageBoxButton.OK, MessageBoxImage.Information));
        }
        else if (session.RequesterPeer is not null) await SendPeerPacketAsync(session.RequesterPeer, response, cancellationToken);
        if (!accepted)
        {
            _remoteSessions.TryRemove(session.SessionId, out _);
            ReleaseControllerSession(session.SessionId);
            await Dispatcher.InvokeAsync(UpdateRemoteControlStatus);
        }
        AppLog.Write(accepted ? $"远程控制申请已接受，会话 {session.SessionId}。" : $"远程控制申请被拒绝，会话 {session.SessionId}。");
    }

    private async Task HandleRemoteStopFromPeerAsync(Peer peer, RoomPacket packet, CancellationToken cancellationToken)
    {
        if (!_remoteSessions.TryGetValue(packet.SessionId!, out var session)) return;
        var requesterStopped = ReferenceEquals(session.RequesterPeer, peer);
        var targetStopped = ReferenceEquals(session.TargetPeer, peer);
        if (!requesterStopped && !targetStopped) return;
        var otherPeer = requesterStopped ? session.TargetPeer : session.RequesterPeer;
        if (otherPeer is not null)
            await SendPeerPacketAsync(otherPeer, new RoomPacket { Type = "remote-stop", SessionId = session.SessionId }, cancellationToken);
        if (session.TargetIsLocal || session.RequesterIsLocal)
            await Dispatcher.InvokeAsync(() => EndRemoteSessionLocally(session.SessionId));
        _remoteSessions.TryRemove(session.SessionId, out _);
    }

    private async Task ExpireRemoteRequestAsync(string sessionId)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), _shutdown.Token); }
        catch (OperationCanceledException) { return; }
        if (!_remoteSessions.TryGetValue(sessionId, out var session) || !session.TryExpire()) return;
        if (!_remoteSessions.TryRemove(sessionId, out var removed) || !ReferenceEquals(removed, session)) return;
        var packet = new RoomPacket { Type = "remote-error", SessionId = sessionId, Text = "对方未响应远程控制申请。" };
        if (session.RequesterPeer is not null) await SendPeerPacketAsync(session.RequesterPeer, packet, _shutdown.Token);
        else if (session.RequesterIsLocal)
        {
            ReleaseControllerSession(sessionId);
            await Dispatcher.InvokeAsync(() => AddMessage("系统", "远程控制申请超时，对方未响应。", null, null));
        }
    }

    /// <summary>申请被拒绝或失败时，释放发起申请时预启动的 RustDesk 控制端。</summary>
    private void ReleaseControllerSession(string sessionId)
    {
        if (_outgoingRemoteSessions.TryRemove(sessionId, out var outgoing))
            RustDeskRuntime.EndClientSession(sessionId, outgoing.Ip);
        UpdateRemoteControlStatus();
    }

    private async Task HandleRemotePacketFromServerAsync(RoomPacket packet, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(packet.SessionId)) return;
        switch (packet.Type)
        {
            case "remote-request":
                _ = Dispatcher.InvokeAsync(() => HandleIncomingRemoteRequestSafelyAsync(packet)).Task.Unwrap();
                break;
            case "remote-start":
                if (_localRemoteSessions.TryGetValue(packet.SessionId, out var targetSession))
                    _ = StartRemoteCaptureAsync(packet.SessionId, targetSession.RequesterName);
                break;
            case "remote-accepted":
                await Dispatcher.InvokeAsync(() =>
                {
                    AddMessage("系统", $"{packet.Sender ?? "对方"} 已接受远控申请，正在启动 RustDesk。", null, null);
                });
                _ = LaunchRustDeskConnectionAsync(packet.SessionId, packet.Sender ?? "远程用户", packet.SenderIp ?? string.Empty, packet.RustDeskId);
                break;
            case "remote-declined":
                ReleaseControllerSession(packet.SessionId);
                await Dispatcher.InvokeAsync(() => ShowMessage(packet.Text ?? "对方已拒绝", "远程控制", MessageBoxButton.OK, MessageBoxImage.Information));
                AppLog.Write($"远程控制申请被拒绝，会话 {packet.SessionId}。");
                break;
            case "remote-error":
                ReleaseControllerSession(packet.SessionId);
                await Dispatcher.InvokeAsync(() => ShowMessage(packet.Text ?? "远程控制申请失败。", "远程控制", MessageBoxButton.OK, MessageBoxImage.Information));
                break;
            case "remote-stop":
                await Dispatcher.InvokeAsync(() =>
                {
                    EndRemoteSessionLocally(packet.SessionId);
                    AddMessage("系统", "远程控制会话已结束。", null, null);
                });
                break;
        }
    }

    private async Task HandleIncomingRemoteRequestAsync(RoomPacket packet)
    {
        var sessionId = packet.SessionId!;
        var requesterName = packet.Sender ?? "局域网用户";
        var requesterIp = packet.SenderIp ?? "未知 IP";
        var session = new RemoteSession(sessionId, requesterName, requesterIp, _userName, GetLocalPrivateIpv4(), null, null, false, true);
        _localRemoteSessions[sessionId] = session;
        AppLog.Write($"收到远程控制申请：{requesterName} ({requesterIp})。");
        if (Interlocked.CompareExchange(ref _remoteConsentOpen, 1, 0) != 0)
        {
            _localRemoteSessions.TryRemove(sessionId, out _);
            await WriteUpstreamPacketAsync(new RoomPacket { Type = "remote-response", SessionId = sessionId, Accepted = false }, _shutdown.Token);
            return;
        }
        var consent = new RemoteControlConsentWindow(requesterName, requesterIp) { Owner = HostWindow };
        var previousForeground = GetForegroundWindow();
        bool accepted;
        try { accepted = consent.ShowDialog() == true; }
        finally
        {
            Interlocked.Exchange(ref _remoteConsentOpen, 0);
            if (previousForeground != IntPtr.Zero) SetForegroundWindow(previousForeground);
        }
        if (!_localRemoteSessions.ContainsKey(sessionId))
        {
            AppLog.Write($"远程控制申请 {sessionId} 已结束，忽略本机用户的接受操作。");
            return;
        }
        if (!accepted)
        {
            _localRemoteSessions.TryRemove(sessionId, out _);
            AppLog.Write($"本机用户拒绝远程控制请求，会话 {sessionId}。");
            await WriteUpstreamPacketAsync(new RoomPacket { Type = "remote-response", SessionId = sessionId, Accepted = false }, _shutdown.Token);
            return;
        }
        AppLog.Write($"本机用户接受远程控制请求，会话 {sessionId}。");
        if (!await StartRemoteCaptureAsync(sessionId, requesterName))
        {
            _localRemoteSessions.TryRemove(sessionId, out _);
            await WriteUpstreamPacketAsync(new RoomPacket
            {
                Type = "remote-response", SessionId = sessionId, Accepted = false, Text = "对方未能启动 RustDesk。"
            }, _shutdown.Token);
            return;
        }
        await WriteUpstreamPacketAsync(new RoomPacket
        {
            Type = "remote-response", SessionId = sessionId, Accepted = true, Port = RustDeskRuntime.DirectAccessPort,
            RustDeskId = _rustDeskIds.GetValueOrDefault(sessionId)
        }, _shutdown.Token);
        AddMessage("系统", $"已接受 {requesterName} 的远程控制申请，RustDesk 正在等待对方连接。", null, null);
    }

    private async Task<bool> StartRemoteCaptureAsync(string sessionId, string controllerName)
    {
        if (_remoteCaptures.ContainsKey(sessionId)) return true;
        var cancel = new CancellationTokenSource();
        if (!_remoteCaptures.TryAdd(sessionId, cancel)) { cancel.Dispose(); return true; }
        UpdateRemoteControlStatus();
        AppLog.Write($"正在为 {controllerName} 启动 RustDesk 直连，会话 {sessionId}。");
        RustDeskHostState state;
        try { state = await RustDeskRuntime.EnsureHostReadyAsync(_shutdown.Token); }
        catch (OperationCanceledException) { state = RustDeskHostState.StartFailed; }
        if (state == RustDeskHostState.Ready)
        {
            var rustDeskId = await RustDeskRuntime.GetIdAsync(_shutdown.Token);
            if (!string.IsNullOrEmpty(rustDeskId)) _rustDeskIds[sessionId] = rustDeskId;
            AppLog.Write($"RustDesk 直连已就绪（TCP {RustDeskRuntime.DirectAccessPort}），会话 {sessionId}，RustDesk ID {rustDeskId ?? "未知"}。");
            Notify("RustDesk 已就绪并在等待对方连接。若 Windows 首次弹出防火墙提示，请允许 RustDesk 访问局域网；若对方被要求输入密码，请把 RustDesk 主窗口显示的临时密码告诉对方。");
            return true;
        }
        if (_remoteCaptures.TryRemove(sessionId, out var failed))
        {
            failed.Cancel();
            failed.Dispose();
        }
        UpdateRemoteControlStatus();
        var reason = state switch
        {
            RustDeskHostState.ConfigUnavailable => "无法写入 RustDesk 配置，未能开启直接 IP 访问。请检查 %APPDATA%\\RustDesk 目录的写入权限。",
            RustDeskHostState.Unavailable => "内置 RustDesk 无法启动。请确认程序所在目录和用户缓存目录可写入，并允许安全软件放行。",
            RustDeskHostState.NotListening => "RustDesk 已在运行但没有监听直连端口。请彻底退出 RustDesk（含托盘图标）后重试，或在它的设置中开启“直接 IP 访问”。",
            _ => "RustDesk 未能就绪。"
        };
        AppLog.Write($"远程控制会话 {sessionId} 无法启动 RustDesk：{reason}");
        Notify($"远程控制未建立：{reason}");
        return false;
    }

    private void Notify(string text)
    {
        if (Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(() => AddMessage("系统", text, null, null));
    }

    private async Task LaunchRustDeskConnectionAsync(string sessionId, string targetName, string targetIp, string? rustDeskId = null)
    {
        var mode = await Dispatcher.InvokeAsync(() =>
        {
            var dialog = new RustDeskConnectModeWindow(targetName, targetIp, rustDeskId) { Owner = HostWindow };
            return dialog.ShowDialog() == true ? dialog.Mode : RustDeskConnectMode.None;
        });
        if (mode == RustDeskConnectMode.None)
        {
            await SendRemoteStopAsync(sessionId);
            return;
        }
        if (!_outgoingRemoteSessions.ContainsKey(sessionId))
        {
            AppLog.Write($"远控会话 {sessionId} 在选择连接方式期间已结束，放弃本次连接。");
            return;
        }

        IPAddress? address = null;
        string target;
        if (mode == RustDeskConnectMode.DirectIp)
        {
            if (!IPAddress.TryParse(targetIp, out address) || address.AddressFamily != AddressFamily.InterNetwork)
            {
                await Dispatcher.InvokeAsync(() => ShowMessage("无法获取对方的 IPv4 地址，不能使用 IP 直连。", "RustDesk", MessageBoxButton.OK, MessageBoxImage.Warning));
                await SendRemoteStopAsync(sessionId);
                return;
            }
            target = address.ToString();
        }
        else
        {
            if (string.IsNullOrWhiteSpace(rustDeskId))
            {
                await Dispatcher.InvokeAsync(() => ShowMessage("对方没有提供 RustDesk ID，无法使用 ID 连接。", "RustDesk", MessageBoxButton.OK, MessageBoxImage.Warning));
                await SendRemoteStopAsync(sessionId);
                return;
            }
            target = rustDeskId.Trim();
        }

        try
        {
            if (!await RustDeskRuntime.EnsureRunningAsync(_shutdown.Token))
            {
                await Dispatcher.InvokeAsync(() => ShowMessage(
                    "RustDesk 客户端未能启动。请确认程序目录可写入，并允许安全软件放行内置的 RustDesk。",
                    "无法启动 RustDesk", MessageBoxButton.OK, MessageBoxImage.Warning));
                await SendRemoteStopAsync(sessionId);
                return;
            }

            if (mode == RustDeskConnectMode.DirectIp && !await WaitForRustDeskDirectAccessAsync(address!, _shutdown.Token))
            {
                await Dispatcher.InvokeAsync(() => ShowMessage(
                    $"{targetName} 的 RustDesk 直连端口未响应。\n\n如果双方不在同一局域网，请重新发起远控并选择“RustDesk ID 连接”；若在同一局域网，请确认对方 LanChat 已显示“RustDesk 已就绪”，并允许 RustDesk 通过 Windows 防火墙。",
                    "无法连接 RustDesk", MessageBoxButton.OK, MessageBoxImage.Information));
                await SendRemoteStopAsync(sessionId);
                return;
            }

            var process = RustDeskRuntime.Connect(target)
                ?? throw new InvalidOperationException("RustDesk 未能启动连接窗口。");
            RustDeskRuntime.RegisterClientSession(sessionId, target);
            await Task.Delay(TimeSpan.FromSeconds(2), _shutdown.Token);
            if (!process.HasExited)
            {
                _rustDeskProcesses[sessionId] = process;
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) =>
                {
                    _rustDeskProcesses.TryRemove(sessionId, out _);
                    if (!_shutdown.IsCancellationRequested) _ = Dispatcher.BeginInvoke(UpdateRemoteControlStatus);
                };
            }
            else process.Dispose();

            var modeText = mode == RustDeskConnectMode.DirectIp ? $"IP 直连（{target}）" : $"RustDesk ID 连接（{target}）";
            AppLog.Write($"已向 RustDesk 提交连接请求：{targetName}，{modeText}，会话 {sessionId}。");
            if (mode == RustDeskConnectMode.DirectIp) _ = MonitorRustDeskConnectionAsync(sessionId, address!, _shutdown.Token);
            else _ = MonitorRustDeskProcessAsync(sessionId, _shutdown.Token);
            await Dispatcher.InvokeAsync(() =>
            {
                AddMessage("系统", $"RustDesk 正在连接 {targetName}（{modeText}）。若 RustDesk 提示输入密码，请向对方索取其 RustDesk 临时密码。", null, null);
                UpdateRemoteControlStatus();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception or UnauthorizedAccessException or SocketException)
        {
            AppLog.Write($"RustDesk 连接启动失败（{sessionId}）：{ex}");
            await Dispatcher.InvokeAsync(() => ShowMessage($"RustDesk 连接启动失败：{ex.Message}", "RustDesk", MessageBoxButton.OK, MessageBoxImage.Error));
            await SendRemoteStopAsync(sessionId);
        }
    }

    private async Task MonitorRustDeskProcessAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
            while (!cancellationToken.IsCancellationRequested && _outgoingRemoteSessions.ContainsKey(sessionId))
            {
                if (RustDeskRuntime.IsRunning())
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    continue;
                }
                await Dispatcher.InvokeAsync(() => AddMessage("系统", "RustDesk 已关闭，远控会话已结束。", null, null));
                await SendRemoteStopAsync(sessionId);
                return;
            }
        }
        catch (OperationCanceledException) { }
    }

    private static async Task<bool> WaitForRustDeskDirectAccessAsync(IPAddress address, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var client = new TcpClient(AddressFamily.InterNetwork);
                await client.ConnectAsync(address, 21118, cancellationToken).AsTask().WaitAsync(TimeSpan.FromMilliseconds(500), cancellationToken);
                return true;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or TimeoutException)
            {
                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
        }
        return false;
    }

    private async Task MonitorRustDeskConnectionAsync(string sessionId, IPAddress targetAddress, CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        DateTime? disconnectedAt = null;
        var connectedOnce = false;
        while (!cancellationToken.IsCancellationRequested && _outgoingRemoteSessions.ContainsKey(sessionId))
        {
            var connected = HasRustDeskTcpConnection(targetAddress, 21118);
            if (connected)
            {
                connectedOnce = true;
                disconnectedAt = null;
            }
            else if (connectedOnce)
            {
                disconnectedAt ??= DateTime.UtcNow;
                if (DateTime.UtcNow - disconnectedAt.Value >= TimeSpan.FromSeconds(8))
                {
                    await Dispatcher.InvokeAsync(() => AddMessage("系统", "RustDesk 连接已断开，聊天室远控状态已同步结束。", null, null));
                    await SendRemoteStopAsync(sessionId);
                    return;
                }
            }
            else if (DateTime.UtcNow - startedAt >= TimeSpan.FromSeconds(90))
            {
                await Dispatcher.InvokeAsync(() => AddMessage("系统", "未检测到 RustDesk 远程连接。请确认对方允许了防火墙提示，并在 RustDesk 窗口中完成密码验证。", null, null));
                await SendRemoteStopAsync(sessionId);
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private static bool HasRustDeskTcpConnection(IPAddress remoteAddress, int remotePort)
    {
        const int afInet = 2;
        const int tcpTableOwnerPidAll = 5;
        const uint errorInsufficientBuffer = 122;
        var size = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref size, true, afInet, tcpTableOwnerPidAll, 0);
        if (result != errorInsufficientBuffer || size <= sizeof(uint)) return false;
        var table = Marshal.AllocHGlobal(size);
        try
        {
            result = GetExtendedTcpTable(table, ref size, true, afInet, tcpTableOwnerPidAll, 0);
            if (result != 0) return false;
            var count = (int)(uint)Marshal.ReadInt32(table);
            var rowSize = Marshal.SizeOf<TcpRowOwnerPid>();
            var expectedAddress = remoteAddress.GetAddressBytes();
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<TcpRowOwnerPid>(IntPtr.Add(table, sizeof(uint) + i * rowSize));
                if (row.State != 5 || (ushort)IPAddress.NetworkToHostOrder(unchecked((short)row.RemotePort)) != remotePort)
                    continue;
                if (BitConverter.GetBytes(row.RemoteAddress).SequenceEqual(expectedAddress)) return true;
            }
            return false;
        }
        finally { Marshal.FreeHGlobal(table); }
    }

    private async Task SendRemoteStopAsync(string sessionId)
    {
        try
        {
            if (_listener is null) await WriteUpstreamPacketAsync(new RoomPacket { Type = "remote-stop", SessionId = sessionId }, _shutdown.Token);
            else if (_remoteSessions.TryGetValue(sessionId, out var session))
            {
                var otherPeer = session.RequesterIsLocal ? session.TargetPeer : session.RequesterPeer;
                if (otherPeer is not null)
                    await SendPeerPacketAsync(otherPeer, new RoomPacket { Type = "remote-stop", SessionId = sessionId }, _shutdown.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        { if (!_shutdown.IsCancellationRequested) AppLog.Write($"远程会话结束消息发送失败：{ex.Message}"); }
        _remoteSessions.TryRemove(sessionId, out _);
        EndRemoteSessionLocally(sessionId);
    }

    private void EndRemoteSessionLocally(string sessionId)
    {
        if (_remoteCaptures.TryRemove(sessionId, out var cancellation))
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
        if (_rustDeskProcesses.TryRemove(sessionId, out var controllerProcess)) _ = CloseRustDeskProcessAsync(controllerProcess);
        _rustDeskIds.TryRemove(sessionId, out _);
        _localRemoteSessions.TryRemove(sessionId, out _);
        _remoteSessions.TryRemove(sessionId, out _);
        var wasControlling = _outgoingRemoteSessions.TryRemove(sessionId, out var outgoing);
        RustDeskRuntime.EndClientSession(sessionId, wasControlling ? outgoing.Ip : null);
        if (_remoteCaptures.IsEmpty)
        {
            if (_outgoingRemoteSessions.IsEmpty && _rustDeskProcesses.IsEmpty) RustDeskRuntime.StopHostIfStartedByUs();
            RustDeskRuntime.RestoreDirectAccessOption();
        }
        UpdateRemoteControlStatus();
    }

    private void UpdateRemoteControlStatus()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(UpdateRemoteControlStatus);
            return;
        }
        var controlled = _remoteCaptures.Count;
        var controlling = _outgoingRemoteSessions.Count;
        var active = controlled + controlling;
        RemoteControlStatusPanel.Visibility = active > 0 ? Visibility.Visible : Visibility.Collapsed;
        RemoteControlStatusText.Text = (controlled, controlling) switch
        {
            (> 0, > 0) => $"本机有 {controlled} 个被控会话、{controlling} 个控制会话。",
            (> 0, 0) => controlled == 1 ? "此计算机正在被远程控制。可随时结束本次会话。" : $"此计算机有 {controlled} 个远控会话。",
            (0, > 0) => controlling == 1 ? "RustDesk 远程控制进行中。可结束本次会话。" : $"RustDesk 正在控制 {controlling} 个会话。",
            _ => string.Empty
        };
    }

    private async void StopRemoteControlFromChat_Click(object sender, RoutedEventArgs e)
    {
        foreach (var sessionId in _remoteCaptures.Keys.ToArray())
            await SendRemoteStopAsync(sessionId);
        foreach (var sessionId in _rustDeskProcesses.Keys.ToArray())
            await SendRemoteStopAsync(sessionId);
        foreach (var sessionId in _outgoingRemoteSessions.Keys.ToArray())
            await SendRemoteStopAsync(sessionId);
    }

    private static async Task CloseRustDeskProcessAsync(Process process)
    {
        try
        {
            if (process.HasExited) { process.Dispose(); return; }
            process.CloseMainWindow();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { if (!process.HasExited) process.Kill(true); }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or ObjectDisposedException) { }
        finally { process.Dispose(); }
    }

    private bool IsLocalAddress(string address)
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork && a.Address.ToString() == address);
    }

    private Peer? FindPeer(string ip)
    {
        lock (_peerLock) return _peers.FirstOrDefault(peer => peer.Ip.Equals(ip, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task SendPeerPacketAsync(Peer peer, RoomPacket packet, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try { bytes = SerializePacket(packet); }
        catch (ArgumentException) { return; }
        try
        {
            await peer.WriteLock.WaitAsync(cancellationToken);
            try { await WritePacketBytesAsync(peer.Stream, bytes, cancellationToken); }
            finally { peer.WriteLock.Release(); }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or InvalidOperationException)
        {
            // 对方已断开：丢弃该数据包，连接由心跳和监听线程负责清理，
            // 不能让异常冒泡到 async void 事件处理器导致整个程序退出。
        }
    }

    private static bool Matches(Peer peer, string target) =>
        peer.Name.Equals(target, StringComparison.OrdinalIgnoreCase) || peer.Ip.Equals(target, StringComparison.OrdinalIgnoreCase);

    private static void AddTarget(HashSet<string> set, string target) => set.Add(target);

    private static bool IsAccessActionNotice(string text) =>
        text is "您已被踢出聊天室" or "您已被封禁" or "您的 IP 已被封禁";

    private static void ToggleTargets(HashSet<string> names, HashSet<string> ips, string target)
    {
        var set = IPAddress.TryParse(target, out _) ? ips : names;
        if (!set.Add(target)) set.Remove(target);
    }

    private async void File_Click(object sender, RoutedEventArgs e) => await PickAttachmentAsync(false);
    private async void Image_Click(object sender, RoutedEventArgs e) => await PickAttachmentAsync(true);

    private async Task PickAttachmentAsync(bool imageOnly)
    {
        var dialog = new OpenFileDialog
        {
            Title = imageOnly ? "选择要发送的图片" : "选择要发送的文件",
            Filter = imageOnly ? "图片文件|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp" : "所有文件|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog(HostWindow) != true) return;
        await UploadFilesAsync(new[] { dialog.FileName }, imageOnly);
    }

    private async Task UploadFilesAsync(IEnumerable<string> paths, bool imageOnly)
    {
        if (_isUploading)
        {
            AddMessage("系统", "当前有文件正在上传，请完成后再添加其他文件。", null, null);
            return;
        }
        if (_upstreamStream is not null && _isMuted)
        {
            AppLog.Write($"已拦截禁言访客 {_userName} 的附件发送操作。");
            AddMessage("系统", $"您已被{_muteActor}禁言，暂时无法发送消息或附件。", null, null);
            return;
        }
        var files = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0)
        {
            AddMessage("系统", "没有找到可上传的文件。", null, null);
            return;
        }
        foreach (var path in files)
        {
            try { await UploadSingleFileAsync(path, imageOnly); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Write($"无法读取待上传文件 {path}：{ex}");
                AddMessage("系统", $"无法读取文件“{Path.GetFileName(path)}”：{ex.Message}", null, null);
            }
        }
    }

    private async Task UploadSingleFileAsync(string filePath, bool imageOnly)
    {
        var info = new FileInfo(filePath);
        if (info.Length > MaxAttachmentBytes)
        {
            AddMessage("系统", $"文件“{info.Name}”超过 {MaxAttachmentText}，已跳过。", null, null);
            return;
        }
        _isUploading = true;
        var uploadGeneration = Interlocked.Increment(ref _uploadGeneration);
        SendFileButton.IsEnabled = false;
        SendImageButton.IsEnabled = false;
        UploadProgressPanel.Visibility = Visibility.Visible;
        SetUploadProgress($"正在准备：{info.Name}", 0, Math.Max(1, info.Length));
        await Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        var uploadSucceeded = false;
        string? transferId = null;
        try
        {
            // 大文件分块读取，避免把整个文件读进内存（1 GB 上限）。
            var totalBytes = info.Length;
            if (totalBytes > MaxAttachmentBytes) throw new IOException($"附件大小超过 {MaxAttachmentText} 限制。");
            var chunkCount = Math.Max(1, (int)((totalBytes + AttachmentChunkBytes - 1) / AttachmentChunkBytes));
            if (chunkCount > MaxAttachmentChunks) throw new IOException("附件分块数量超过上限。");
            AppLog.Write($"开始分块发送附件：{info.Name}，{totalBytes:N0} 字节，{chunkCount} 个数据块。");
            var isImage = imageOnly;
            transferId = Guid.NewGuid().ToString("N");
            TaskCompletionSource<bool>? uploadAck = null;
            if (_upstreamStream is not null)
            {
                uploadAck = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingUploadAcks[transferId] = uploadAck;
            }
            var buffer = new byte[AttachmentChunkBytes];
            await using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true))
            {
                for (var index = 0; index < chunkCount; index++)
                {
                    var offset = (long)index * AttachmentChunkBytes;
                    var length = (int)Math.Min(AttachmentChunkBytes, totalBytes - offset);
                    if (length > 0)
                    {
                        var read = 0;
                        while (read < length)
                        {
                            var got = await stream.ReadAsync(buffer.AsMemory(read, length - read), _shutdown.Token);
                            if (got == 0) break;
                            read += got;
                        }
                        if (read != length) throw new IOException("文件在发送过程中被修改或截断。");
                    }
                    var packet = new RoomPacket
                    {
                        Type = "attachment-chunk", AttachmentType = isImage ? "image" : "file", TransferId = transferId,
                        ChunkIndex = index, ChunkCount = chunkCount, TotalBytes = totalBytes, Room = _roomName, Sender = _userName,
                        SenderRole = _listener is null ? "访客" : "创建者", SenderIp = GetLocalPrivateIpv4(),
                        FileName = info.Name, MimeType = isImage ? GetImageMime(info.Extension) : "application/octet-stream",
                        Data = Convert.ToBase64String(buffer, 0, Math.Max(0, length))
                    };
                    if (_upstreamStream is not null)
                    {
                        if (!await WriteUpstreamPacketAsync(packet, _shutdown.Token))
                            throw new IOException("与房间主机的连接已断开，附件上传已中止。");
                    }
                    else
                    {
                        // 房主本机不需要再组装一份：消息直接引用原始文件，省掉一整份内存/磁盘副本。
                        await BroadcastAsync(packet, _shutdown.Token);
                    }
                    var sentBytes = Math.Min(totalBytes, (long)(index + 1) * AttachmentChunkBytes);
                    SetUploadProgress($"正在上传：{info.Name}", sentBytes, Math.Max(1, totalBytes));
                    if (index % 2 == 1) await Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                }
            }
            if (uploadAck is not null)
            {
                SetUploadProgress($"正在等待接收确认：{info.Name}", totalBytes * 0.99, Math.Max(1, totalBytes));
                await uploadAck.Task.WaitAsync(_shutdown.Token);
            }
            if (_upstreamStream is null)
                AddPacket(new RoomPacket { Type = isImage ? "image" : "file", Sender = _userName, SenderRole = "创建者", SenderIp = GetLocalPrivateIpv4(), FileName = info.Name, MimeType = isImage ? GetImageMime(info.Extension) : "application/octet-stream" }, filePath);
            AppLog.Write($"附件发送成功：{info.Name}，{totalBytes:N0} 字节，房间“{_roomName}”。");
            uploadSucceeded = true;
            SetUploadProgress($"上传成功：{info.Name}", Math.Max(1, totalBytes), Math.Max(1, totalBytes));
        }
        catch (IOException ex)
        {
            AppLog.Write($"附件读取或发送失败：{filePath} · {ex}");
            if (!_shutdown.IsCancellationRequested)
            {
                SetUploadProgress($"上传失败：{info.Name}", UploadProgressBar.Value, Math.Max(1, UploadProgressBar.Maximum));
                ShowMessage($"无法读取或发送文件：{ex.Message}", "发送失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException or ArgumentException)
        {
            AppLog.Write($"附件分块发送失败：{filePath} · {ex}");
            if (!_shutdown.IsCancellationRequested)
            {
                SetUploadProgress($"上传失败：{info.Name}", UploadProgressBar.Value, Math.Max(1, UploadProgressBar.Maximum));
                ShowMessage($"附件发送失败：{ex.Message}", "发送失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            if (transferId is not null) _pendingUploadAcks.TryRemove(transferId, out _);
            _isUploading = false;
            SendFileButton.IsEnabled = true;
            SendImageButton.IsEnabled = true;
            if (_shutdown.IsCancellationRequested) UploadProgressPanel.Visibility = Visibility.Collapsed;
            else if (uploadSucceeded)
            {
                await Task.Delay(1600);
                if (uploadGeneration == _uploadGeneration) UploadProgressPanel.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void SetUploadProgress(string status, double current, double total)
    {
        UploadProgressBar.Maximum = Math.Max(1, total);
        UploadProgressBar.Value = Math.Clamp(current, 0, UploadProgressBar.Maximum);
        var percent = UploadProgressBar.Value / UploadProgressBar.Maximum * 100;
        UploadProgressText.Text = $"{status} · {FormatBytes((long)current)} / {FormatBytes((long)total)}";
        UploadProgressPercent.Text = $"{percent:0}%";
    }

    private static string FormatBytes(long value) => value >= 1024L * 1024 * 1024
        ? $"{value / (1024d * 1024 * 1024):0.0} GB"
        : value >= 1024L * 1024 ? $"{value / (1024d * 1024):0.0} MB"
        : value >= 1024 ? $"{value / 1024d:0.0} KB" : $"{value} B";

    /// <summary>
    /// 写数据到房主连接。连接已断开时返回 false 而不是抛异常，
    /// 避免 async void 事件处理器把 ObjectDisposedException 变成整程序崩溃。
    /// </summary>
    private async Task<bool> WriteUpstreamPacketAsync(RoomPacket packet, CancellationToken cancellationToken)
    {
        var stream = _upstreamStream;
        if (stream is null) return false;
        if (_upstreamClosed) return false;
        byte[] bytes;
        try { bytes = SerializePacket(packet); }
        catch (ArgumentException ex)
        {
            AppLog.Write($"房间数据序列化失败，已跳过该数据包：{ex.Message}");
            return false;
        }
        try
        {
            await _upstreamWriteLock.WaitAsync(cancellationToken);
            try
            {
                if (_upstreamClosed) return false;
                await WritePacketBytesAsync(stream, bytes, cancellationToken);
                return true;
            }
            finally { _upstreamWriteLock.Release(); }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or SocketException or InvalidOperationException)
        {
            MarkUpstreamClosed(ex);
            return false;
        }
    }

    /// <summary>标记与房主的连接已断开，之后所有发送直接跳过并提示用户。</summary>
    private void MarkUpstreamClosed(Exception? reason)
    {
        if (_upstreamClosed) return;
        _upstreamClosed = true;
        AppLog.Write($"与房主的连接已断开（{reason?.GetType().Name}: {reason?.Message}），后续消息将不再发送。");
        try { _upstream?.Close(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        if (Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            RoomSubtitle.Text = "连接已断开";
            AddMessage("系统", "与房间主机的连接已断开，无法再发送消息。可以复制内容后重新搜索并加入房间。", null, null);
        }));
    }

    private (RoomPacket Packet, string Path)? AcceptAttachmentChunk(RoomPacket packet)
    {
        if (string.IsNullOrWhiteSpace(packet.TransferId) || string.IsNullOrWhiteSpace(packet.SenderIp) || packet.Data is null ||
            packet.ChunkCount is < 1 or > MaxAttachmentChunks || packet.ChunkIndex < 0 || packet.ChunkIndex >= packet.ChunkCount ||
            packet.TotalBytes is < 0 or > MaxAttachmentBytes || packet.ChunkCount != Math.Max(1, (packet.TotalBytes + AttachmentChunkBytes - 1) / AttachmentChunkBytes) ||
            packet.AttachmentType is not ("file" or "image")) return null;
        var transferKey = $"{packet.SenderIp}:{packet.TransferId}";
        if (!_incomingAttachments.TryGetValue(transferKey, out var accumulator))
        {
            AttachmentAccumulator created;
            try
            {
                var path = _attachments.CreatePath(packet.FileName, packet.AttachmentType == "image" ? ".png" : ".bin");
                created = new AttachmentAccumulator(packet, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Write($"无法创建附件临时文件，已放弃该附件：{ex.Message}");
                return null;
            }
            if (_incomingAttachments.TryAdd(transferKey, created)) accumulator = created;
            else
            {
                created.Dispose();
                if (!_incomingAttachments.TryGetValue(transferKey, out var existing)) return null;
                accumulator = existing;
            }
        }
        if (accumulator.SenderIp != packet.SenderIp) return null;
        if (!accumulator.Add(packet, out var reason))
        {
            if (reason is not null)
            {
                AppLog.Write($"附件数据块被丢弃：{reason}（会话 {transferKey}）。");
                // 数据块无效说明传输已不可信，清掉半成品让发送方重新发起。
                if (_incomingAttachments.TryRemove(transferKey, out var broken)) broken.Dispose();
            }
            return null;
        }
        _incomingAttachments.TryRemove(transferKey, out _);
        var completePacket = new RoomPacket
        {
            Type = packet.AttachmentType, Room = packet.Room, Sender = packet.Sender, SenderRole = packet.SenderRole,
            SenderIp = packet.SenderIp, FileName = packet.FileName, MimeType = packet.MimeType
        };
        return (completePacket, accumulator.FilePath);
    }

    private void Emoji_Click(object sender, RoutedEventArgs e)
    {
        MessageInput.Focus();
        Keyboard.Focus(MessageInput);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var inputs = new[]
            {
                KeyboardEvent(VirtualKeyLeftWindows, 0),
                KeyboardEvent(VirtualKeyPeriod, 0),
                KeyboardEvent(VirtualKeyPeriod, KeyEventKeyUp),
                KeyboardEvent(VirtualKeyLeftWindows, KeyEventKeyUp)
            };
            var size = Marshal.SizeOf<NativeInput>();
            var sent = SendInput((uint)inputs.Length, inputs, size);
            if (sent == inputs.Length)
            {
                AppLog.Write("已请求打开 Windows 表情面板（Win+.）。");
                return;
            }

            var error = Marshal.GetLastWin32Error();
            AppLog.Write($"SendInput 打开表情面板失败：已发送 {sent}/{inputs.Length}，cbSize={size}，错误码 {error}。尝试改用 keybd_event。");

            // 回退方案：部分环境（例如输入被另一个桌面或钩子拦截）下 SendInput 不生效。
            keybd_event((byte)VirtualKeyLeftWindows, 0, 0, UIntPtr.Zero);
            keybd_event((byte)VirtualKeyPeriod, 0, 0, UIntPtr.Zero);
            keybd_event((byte)VirtualKeyPeriod, 0, KeyEventKeyUp, UIntPtr.Zero);
            keybd_event((byte)VirtualKeyLeftWindows, 0, KeyEventKeyUp, UIntPtr.Zero);
            AppLog.Write("已通过 keybd_event 再次请求打开 Windows 表情面板。");
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private static NativeInput KeyboardEvent(ushort virtualKey, uint flags) => new()
    {
        Type = InputTypeKeyboard,
        Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = flags } }
    };

    private void DownloadFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择默认下载文件夹",
            InitialDirectory = Directory.Exists(AppSettings.DownloadFolder) ? AppSettings.DownloadFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(HostWindow) != true) return;
        AppSettings.DownloadFolder = dialog.FolderName;
        AppLog.Write($"下载默认目录已更改为：{dialog.FolderName}。");
        RoomSubtitle.Text = $"默认下载位置：{AppSettings.DownloadFolder}";
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChatMessage message || message.DataPath is null || message.Attachment is null) return;
        try
        {
            if (!File.Exists(message.DataPath))
            {
                AddMessage("系统", $"附件“{message.Attachment}”的临时文件已不存在，请让对方重新发送。", null, null);
                return;
            }
            var defaultFolder = AppSettings.DownloadFolder;
            var dialog = new SaveFileDialog
            {
                FileName = Path.GetFileName(message.Attachment),
                InitialDirectory = Directory.Exists(defaultFolder) ? defaultFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Title = "选择附件保存位置",
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(HostWindow) != true) return;
            Directory.CreateDirectory(Path.GetDirectoryName(dialog.FileName)!);
            // 逐块复制，避免大文件整份读进内存。
            using (var source = new FileStream(message.DataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024))
            using (var target = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
                source.CopyTo(target, 128 * 1024);
            AddMessage("系统", $"文件已保存到：{dialog.FileName}", null, null);
            AppLog.Write($"附件“{message.Attachment}”已保存到 {dialog.FileName}。");
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            ShowMessage($"无法保存附件：{ex.Message}", "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ConfigureSendShortcut()
    {
        SendShortcutSelector.SelectedIndex = AppSettings.SendOnEnter ? 1 : 0;
        _sendShortcutConfigured = true;
    }

    private void SendShortcutSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_sendShortcutConfigured || SendShortcutSelector.SelectedItem is not ComboBoxItem item) return;
        var sendOnEnter = Equals(item.Tag, "Enter");
        AppSettings.SendOnEnter = sendOnEnter;
        AppLog.Write($"聊天发送快捷键已更改为 {(sendOnEnter ? "Enter" : "Ctrl+Enter")} 发送。另一个按键组合用于换行。");
    }

    private void MessageInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (MentionPanel.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Down) { MoveMentionSelection(1); e.Handled = true; return; }
            if (e.Key == Key.Up) { MoveMentionSelection(-1); e.Handled = true; return; }
            if (e.Key is Key.Enter or Key.Tab)
            {
                if (MentionList.SelectedItem is string candidate) ConfirmMention(candidate);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape) { _mentionDismissed = true; HideMentionPopup(); e.Handled = true; return; }
        }
        if (e.Key == Key.ImeProcessed && e.ImeProcessedKey == Key.Enter) return;
        if (e.Key != Key.Enter) return;
        var isSendShortcut = AppSettings.SendOnEnter
            ? Keyboard.Modifiers == ModifierKeys.None
            : Keyboard.Modifiers == ModifierKeys.Control;
        e.Handled = true;
        if (isSendShortcut) Send_Click(sender, new RoutedEventArgs());
        else MessageInput.CaretPosition.InsertLineBreak();
    }

    private void MessageInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        _mentionDismissed = false;
        if (_suppressMentionSuggestions) return;
        UpdateMentionSuggestions();
    }

    private void MessageInput_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressMentionSuggestions || _mentionDismissed) return;
        UpdateMentionSuggestions();
    }

    private void MessageInput_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (MentionPanel.IsKeyboardFocusWithin) return;
        HideMentionPopup();
    }

    private void MoveMentionSelection(int delta)
    {
        if (MentionList.Items.Count == 0) return;
        var index = MentionList.SelectedIndex + delta;
        if (index < 0) index = MentionList.Items.Count - 1;
        else if (index >= MentionList.Items.Count) index = 0;
        MentionList.SelectedIndex = index;
        MentionList.ScrollIntoView(MentionList.SelectedItem);
    }

    private void UpdateMentionSuggestions()
    {
        var caret = MessageInput.CaretPosition;
        var paragraph = caret?.Paragraph;
        if (paragraph is null) { HideMentionPopup(); return; }
        var before = new TextRange(paragraph.ContentStart, caret).Text.TrimEnd('\r', '\n');
        var at = before.LastIndexOf('@');
        if (at < 0) { HideMentionPopup(); return; }
        var fragment = before[(at + 1)..];
        if (fragment.Contains(' ') || fragment.Contains('\r') || fragment.Contains('\n')) { HideMentionPopup(); return; }
        var candidates = _members
            .Where(member => !string.Equals(member.Name, _userName, StringComparison.OrdinalIgnoreCase))
            .Select(member => member.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Where(name => name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        if (candidates.Length == 0) { HideMentionPopup(); return; }
        _mentionFragmentStart = paragraph.ContentStart.GetPositionAtOffset(at);
        if (_mentionFragmentStart is null) { HideMentionPopup(); return; }
        MentionList.ItemsSource = candidates;
        MentionList.SelectedIndex = 0;
        MentionPanel.Visibility = Visibility.Visible;
    }

    private void HideMentionPopup()
    {
        MentionPanel.Visibility = Visibility.Collapsed;
        _mentionFragmentStart = null;
    }

    private void MentionList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(MentionList, source) is not ListBoxItem { DataContext: string name }) return;
        ConfirmMention(name);
    }

    private void ConfirmMention(string name)
    {
        var start = _mentionFragmentStart;
        _mentionDismissed = true;
        HideMentionPopup();
        var caret = MessageInput.CaretPosition;
        if (start is null || caret is null) return;
        if (start.CompareTo(caret) > 0) return;
        var range = new TextRange(start, caret) { Text = "@" + name + " " };
        var mentionEnd = range.Start.GetPositionAtOffset(1 + name.Length) ?? range.End;
        new TextRange(range.Start, mentionEnd).ApplyPropertyValue(TextElement.ForegroundProperty, MentionForeground);
        MessageInput.CaretPosition = range.End;
        MessageInput.Focus();
    }

    private void InsertMentionAtCaret(string name)
    {
        var caret = MessageInput.CaretPosition;
        if (caret is null) return;
        var insertion = new TextRange(caret, caret) { Text = "@" + name + " " };
        var mentionEnd = insertion.Start.GetPositionAtOffset(1 + name.Length) ?? insertion.End;
        new TextRange(insertion.Start, mentionEnd).ApplyPropertyValue(TextElement.ForegroundProperty, MentionForeground);
        MessageInput.CaretPosition = insertion.End;
        MessageInput.Focus();
    }

    private void MentionMember_Click(object sender, RoutedEventArgs e)
    {
        if (MembersList.SelectedItem is not RoomMember member) return;
        InsertMentionAtCaret(member.Name);
        HideMentionPopup();
    }

    private void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: ChatMessage message }) return;
        CopyToClipboard(message.Text, "消息已复制到剪贴板。", "消息");
    }

    private void CopyMemberName_Click(object sender, RoutedEventArgs e)
    {
        if (MembersList.SelectedItem is not RoomMember member) return;
        CopyToClipboard(member.Name, $"已复制用户名：{member.Name}", "用户名");
    }

    private void CopyMemberIp_Click(object sender, RoutedEventArgs e)
    {
        if (MembersList.SelectedItem is not RoomMember member) return;
        CopyToClipboard(member.Ip, $"已复制 IP：{member.Ip}", "IP");
    }

    /// <summary>
    /// 剪贴板经常被其他程序短暂占用，而 WPF 的 Clipboard 走 OLE 通道在部分机器上会一直失败；
    /// 这里改用 Win32 剪贴板 API 写文本，并在独立线程里重试，避免阻塞界面。
    /// </summary>
    private void CopyToClipboard(string text, string successMessage, string label)
    {
        var thread = new Thread(() =>
        {
            var copied = TrySetClipboardText(text);
            void Report()
            {
                if (copied) AddMessage("系统", successMessage, null, null);
                else
                {
                    AppLog.Write($"复制{label}失败：剪贴板被其他程序占用。");
                    AddMessage("系统", "复制失败：剪贴板正被其他程序占用，请稍后重试。", null, null);
                }
            }
            if (Dispatcher.HasShutdownStarted) return;
            _ = Dispatcher.BeginInvoke(Report);
        })
        { IsBackground = true, Name = "LanChatClipboard" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    private static bool TrySetClipboardText(string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (TryWriteClipboardText(text)) return true;
            Thread.Sleep(120);
        }
        return false;
    }

    private static bool TryWriteClipboardText(string text)
    {
        if (!OpenClipboard(IntPtr.Zero)) return false;
        try
        {
            if (!EmptyClipboard()) return false;
            var bytes = new byte[(text.Length + 1) * sizeof(char)];
            Encoding.Unicode.GetBytes(text, 0, text.Length, bytes, 0);
            var handle = GlobalAlloc(GmemMoveable, (UIntPtr)bytes.Length);
            if (handle == IntPtr.Zero) return false;
            var target = GlobalLock(handle);
            if (target == IntPtr.Zero)
            {
                GlobalFree(handle);
                return false;
            }
            try { Marshal.Copy(bytes, 0, target, bytes.Length); }
            finally { GlobalUnlock(handle); }
            if (SetClipboardData(CfUnicodeText, handle) == IntPtr.Zero)
            {
                GlobalFree(handle);
                return false;
            }
            return true;
        }
        finally { CloseClipboard(); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr memory);

    /// <summary>生成本机消息 ID：客户端标签 + 自增序号，保证同一房间内不重复。</summary>
    private string NextMessageId() => $"{_clientTag}-{Interlocked.Increment(ref _messageSeq)}";

    private void ReplyMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: ChatMessage message }) return;
        var author = string.IsNullOrWhiteSpace(message.Author) ? message.Sender : message.Author;
        _replySender = author;
        _replyText = message.Text;
        _replyToId = message.Id;
        ReplySenderText.Text = $"{author}：";
        ReplyPreviewText.Text = message.Text;
        ReplyBar.Visibility = Visibility.Visible;
        InsertMentionAtCaret(author);
        AppLog.Write($"正在回复 {author} 的消息。");
    }

    private void AddPacket(RoomPacket packet, string? attachmentPath = null)
    {
        if (packet.Type is not "system") packet.MessageId ??= NextMessageId();
        BitmapImage? thumbnail = null;
        if (packet.Type == "image" && attachmentPath is not null)
        {
            try
            {
                using var stream = new FileStream(attachmentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                thumbnail = new BitmapImage();
                thumbnail.BeginInit();
                thumbnail.CacheOption = BitmapCacheOption.OnLoad;
                thumbnail.DecodePixelWidth = 640;
                thumbnail.StreamSource = stream;
                thumbnail.EndInit();
                thumbnail.Freeze();
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException or IOException or UnauthorizedAccessException) { }
        }

        var text = packet.Type switch
        {
            "file" => $"发送了文件：{packet.FileName}",
            "image" => $"发送了图片：{packet.FileName}",
            "system" => packet.Text ?? string.Empty,
            _ => packet.Text ?? string.Empty
        };
        var sender = packet.Type == "system" ? "系统" : packet.Sender ?? "访客";
        var author = packet.Sender ?? "访客";
        if (packet.Type is "text" or "file" or "image")
            sender += $" · {packet.SenderRole ?? "访客"}";
        if (!string.IsNullOrWhiteSpace(packet.SenderIp)) sender += $" · {packet.SenderIp}";
        AddMessage(sender, text, packet.Type is "file" or "image" ? packet.FileName : null, thumbnail, attachmentPath,
            packet.MimeType, packet.Type == "system" ? null : author, packet.ReplySender, packet.ReplyText, packet.MessageId, packet.ReplyToId);
        if (packet.Type is "file" or "image") AppLog.Write($"附件进入收件列表：{packet.Type} · {packet.FileName ?? "(未命名)"} · 来自 {sender}。");
        if (packet.Type == "text" && !string.Equals(packet.Sender, _userName, StringComparison.OrdinalIgnoreCase) && MentionsUser(packet.Text, _userName))
        {
            AppLog.Write($"收到 {author} 的 @ 提醒。");
            ToastNotifier.ShowMention(author);
        }
        MessageScroll.ScrollToEnd();
    }

    private static bool MentionsUser(string? text, string userName)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(userName)) return false;
        var token = "@" + userName;
        var index = text.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var end = index + token.Length;
            if (end >= text.Length || !char.IsLetterOrDigit(text[end]) && text[end] != '_') return true;
            index = text.IndexOf(token, index + 1, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private void AddMessage(string sender, string text, string? attachment, BitmapImage? thumbnail, string? dataPath = null, string? mimeType = null, string? author = null, string? replySender = null, string? replyText = null, string? id = null, string? replyToId = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(() =>
                AddMessage(sender, text, attachment, thumbnail, dataPath, mimeType, author, replySender, replyText, id, replyToId)));
            return;
        }
        id ??= NextMessageId();
        var displayText = attachment is null ? text : text;
        _messages.Add(new ChatMessage(sender, DateTime.Now.ToString("HH:mm"), displayText, attachment, thumbnail, dataPath, mimeType, author, replySender, replyText, id, replyToId));
        MessageScroll.ScrollToEnd();
    }

    /// <summary>点击引用块：跳到被引用的那条消息并短暂高亮。</summary>
    private void Quote_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChatMessage message }) return;
        e.Handled = true;
        JumpToQuotedMessage(message);
    }

    private void JumpToQuotedMessage(ChatMessage message)
    {
        var target = FindQuotedMessage(message);
        if (target is null)
        {
            AddMessage("系统", "找不到被引用的消息，可能已不在当前聊天记录中。", null, null);
            return;
        }
        var container = MessagesList.ItemContainerGenerator.ContainerFromItem(target) as FrameworkElement;
        if (container is null)
        {
            AddMessage("系统", "找不到被引用的消息，可能已被滚动加载隐藏。", null, null);
            return;
        }
        container.BringIntoView();
        HighlightMessage(container);
        AppLog.Write($"已跳转到被引用的消息：{target.Time} · {target.Author ?? target.Sender}。");
    }

    /// <summary>优先按消息 ID 定位；旧版本对端没有 ID 时退回“发送者 + 内容”匹配最近一条。</summary>
    private ChatMessage? FindQuotedMessage(ChatMessage quote)
    {
        if (!string.IsNullOrEmpty(quote.QuoteTargetId))
        {
            var exact = _messages.FirstOrDefault(m => string.Equals(m.Id, quote.QuoteTargetId, StringComparison.Ordinal));
            if (exact is not null) return exact;
        }
        var quoteSender = quote.QuoteSender;
        var quoteText = quote.QuoteText;
        if (string.IsNullOrEmpty(quoteText)) return null;
        var index = _messages.IndexOf(quote);
        var limit = index >= 0 ? index : _messages.Count;
        for (var i = limit - 1; i >= 0; i--)
        {
            var candidate = _messages[i];
            if (!string.Equals(candidate.Text, quoteText, StringComparison.Ordinal)) continue;
            if (quoteSender is not null &&
                !string.Equals(candidate.Author ?? candidate.Sender, quoteSender, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(candidate.Sender, quoteSender, StringComparison.OrdinalIgnoreCase)) continue;
            return candidate;
        }
        return null;
    }

    private static void HighlightMessage(FrameworkElement container)
    {
        var bubble = FindDescendant<Border>(container, "Bubble");
        if (bubble is null) return;
        var brush = new SolidColorBrush(Color.FromRgb(0x3A, 0x57, 0x8F));
        bubble.Background = brush;
        var animation = new ColorAnimation(Color.FromRgb(0x10, 0x12, 0x18), TimeSpan.FromMilliseconds(1400))
        {
            BeginTime = TimeSpan.FromMilliseconds(500)
        };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }

    private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T element && element.Name == name) return element;
            var nested = FindDescendant<T>(child, name);
            if (nested is not null) return nested;
        }
        return null;
    }

    /// <summary>日志里的文本去掉结尾标点，避免出现“加入了聊天室。。”这样的重复句号。</summary>
    private static string TrimForLog(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(无)";
        return text.TrimEnd('。', '！', '？', '.', '!', '?', '；', ';', ' ', '\r', '\n');
    }

    private static string GetImageMime(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".bmp" => "image/bmp", ".webp" => "image/webp", _ => "application/octet-stream"
    };

    private static string GetLocalPrivateIpv4()
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .ToArray();
        var privateAddress = addresses.FirstOrDefault(a =>
        {
            var b = a.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
        });
        return (privateAddress ?? addresses.FirstOrDefault())?.ToString() ?? "IP 未知";
    }

    private void RequestClose()
    {
        if (_closingStarted) return;
        CloseRequested?.Invoke();
    }

    private async void View_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_allowClose || _closingStarted) return;
        await ShutdownAsync();
    }

    /// <summary>释放房间资源；由宿主在切回主页前调用。</summary>
    public async Task ShutdownAsync()
    {
        if (_allowClose || _closingStarted) return;
        _closingStarted = true;
        AppLog.EntryAdded -= OnRoomLogEntryAdded;
        _statusTimer.Stop();
        try
        {
            var remoteIds = _remoteSessions.Keys.Concat(_localRemoteSessions.Keys).Concat(_remoteCaptures.Keys).Concat(_outgoingRemoteSessions.Keys).Distinct().ToArray();
            foreach (var sessionId in remoteIds)
            {
                await SendRemoteStopAsync(sessionId);
                EndRemoteSessionLocally(sessionId);
            }
            if (_listener is not null)
            {
                try
                {
                    await BroadcastAsync(new RoomPacket { Type = "host-closed", Room = _roomName, Text = "主机已关闭连接" }, _shutdown.Token);
                    await Task.Delay(150);
                }
                catch (OperationCanceledException) { }
                AppLog.Write($"房主关闭了聊天室“{_roomName}”。");
            }
        }
        catch (Exception ex)
        {
            // 关闭流程即使出错也必须走完，否则窗口会卡住关不掉。
            AppLog.Write($"关闭聊天室时出现异常（已继续关闭）：{ex}");
        }
        finally
        {
            try
            {
                _shutdown.Cancel();
                _listener?.Stop();
                _discovery?.Dispose();
                _upstream?.Dispose();
                lock (_peerLock)
                {
                    foreach (var peer in _peers) peer.Client.Dispose();
                    _peers.Clear();
                }
                AbortIncomingAttachments();
                _attachments.Cleanup();
            }
            catch (Exception ex)
            {
                AppLog.Write($"释放聊天室资源时出现异常：{ex.Message}");
            }
            try { _shutdown.Dispose(); } catch (ObjectDisposedException) { }
            _allowClose = true;
        }
    }

    /// <summary>放弃所有未接收完的附件并删除半成品文件。</summary>
    private void AbortIncomingAttachments()
    {
        foreach (var key in _incomingAttachments.Keys.ToArray())
        {
            if (_incomingAttachments.TryRemove(key, out var accumulator)) accumulator.Dispose();
        }
    }

    private sealed record Peer(TcpClient Client, NetworkStream Stream, string Name, string Ip)
    {
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
        public long LastHeartbeatTicks = DateTime.UtcNow.Ticks;
    }

    private sealed record RoomMember(string Name, string Ip, string Role)
    {
        public string Details => $"{Role} · {Ip}";
    }

    private sealed class RemoteSession(
        string sessionId, string requesterName, string requesterIp, string targetName, string targetIp,
        Peer? requesterPeer, Peer? targetPeer, bool requesterIsLocal, bool targetIsLocal)
    {
        public string SessionId { get; } = sessionId;
        public string RequesterName { get; } = requesterName;
        public string RequesterIp { get; } = requesterIp;
        public string TargetName { get; } = targetName;
        public string TargetIp { get; } = targetIp;
        public Peer? RequesterPeer { get; } = requesterPeer;
        public Peer? TargetPeer { get; } = targetPeer;
        public bool RequesterIsLocal { get; } = requesterIsLocal;
        public bool TargetIsLocal { get; } = targetIsLocal;
        private int _state; // 0=pending, 1=accepted, 2=expired
        public bool Accepted => Volatile.Read(ref _state) == 1;
        public bool TryAccept()
        {
            var state = Interlocked.CompareExchange(ref _state, 1, 0);
            return state is 0 or 1;
        }
        public bool TryExpire() => Interlocked.CompareExchange(ref _state, 2, 0) == 0;
    }

    private sealed class RoomPacket
    {
        public string? Type { get; set; }
        public string? Room { get; set; }
        public string? Sender { get; set; }
        public string? SenderRole { get; set; }
        public string? SenderIp { get; set; }
        public string? Password { get; set; }
        public string? Text { get; set; }
        public string? FileName { get; set; }
        public string? MimeType { get; set; }
        public string? Data { get; set; }
        public string? AttachmentType { get; set; }
        public string? TransferId { get; set; }
        public int ChunkIndex { get; set; }
        public int ChunkCount { get; set; }
        public long TotalBytes { get; set; }
        public string? SessionId { get; set; }
        public string? TargetIp { get; set; }
        public bool Accepted { get; set; }
        public int Port { get; set; }
        public string? RustDeskId { get; set; }
        public bool PasswordRequired { get; set; }
        public string? Name { get; set; }
        public string? HostAddress { get; set; }
        public string? ReplySender { get; set; }
        public string? ReplyText { get; set; }
        public string? MessageId { get; set; }
        public string? ReplyToId { get; set; }
    }

    private sealed record ChatMessage(string Sender, string Time, string Text, string? Attachment, BitmapImage? Thumbnail, string? DataPath, string? MimeType, string? Author = null, string? QuoteSender = null, string? QuoteText = null, string? Id = null, string? QuoteTargetId = null)
    {
        public bool IsSystem => Sender == "系统";
        public string AttachmentLabel => Attachment is null ? string.Empty : $"📎 {Attachment}";
        public Visibility ThumbnailVisibility => Thumbnail is null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility DownloadVisibility => Attachment is null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility QuoteVisibility => string.IsNullOrEmpty(QuoteText) ? Visibility.Collapsed : Visibility.Visible;
        public string QuoteSenderLabel => QuoteSender ?? string.Empty;
    }

    private sealed class AttachmentAccumulator : IDisposable
    {
        private readonly FileStream _stream;
        private readonly bool[] _receivedChunks;
        private readonly long _totalBytes;
        private readonly byte[] _chunkBuffer = new byte[AttachmentChunkBytes];
        private int _received;
        private bool _completed;
        public string? SenderIp { get; }
        public string FilePath { get; }

        public AttachmentAccumulator(RoomPacket firstPacket, string filePath)
        {
            SenderIp = firstPacket.SenderIp;
            FilePath = filePath;
            _totalBytes = firstPacket.TotalBytes;
            _receivedChunks = new bool[firstPacket.ChunkCount];
            _stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 64 * 1024, useAsync: false);
            _stream.SetLength(_totalBytes);
        }

        /// <summary>写入一个数据块；返回 true 表示所有块都已到齐。</summary>
        public bool Add(RoomPacket packet, out string? reason)
        {
            reason = null;
            if (_completed) { reason = "数据块重复"; return false; }
            if (packet.ChunkCount != _receivedChunks.Length || packet.TotalBytes != _totalBytes ||
                packet.ChunkIndex < 0 || packet.ChunkIndex >= _receivedChunks.Length)
            {
                reason = "数据块参数不一致";
                return false;
            }
            var offset = (long)packet.ChunkIndex * AttachmentChunkBytes;
            var expectedLength = (int)Math.Min(AttachmentChunkBytes, _totalBytes - offset);
            // 直接把 Base64 解码进复用缓冲区，避免每个分块再分配一个大数组。
            if (!Convert.TryFromBase64Chars(packet.Data.AsSpan(), _chunkBuffer, out var written) || written != expectedLength || _receivedChunks[packet.ChunkIndex])
            {
                reason = "数据块长度不符";
                return false;
            }
            _stream.Position = offset;
            _stream.Write(_chunkBuffer, 0, written);
            _receivedChunks[packet.ChunkIndex] = true;
            _received++;
            if (_received < _receivedChunks.Length) return false;
            _stream.Flush();
            _stream.Dispose();
            _completed = true;
            return true;
        }

        /// <summary>放弃未完成的附件，删除半成品文件。</summary>
        public void Dispose()
        {
            if (!_completed)
            {
                try { _stream.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
                try { File.Delete(FilePath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}

internal sealed record BanRecord(string Target, string Kind);
