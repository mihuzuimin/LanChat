using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Net.Sockets;
using Microsoft.Win32;
using System.Windows.Threading;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SharpHook;
using SharpHook.Data;
using SharpHook.Simulation;

namespace LanChat;

public partial class ChatRoomView : System.Windows.Controls.UserControl, IDisposable
{
    private const long MaxAttachmentBytes = 200L * 1024 * 1024;
    private const int AttachmentChunkBytes = 512 * 1024;
    private const int MaxClipboardChars = 100_000;
    private const int MaxRetainedMessages = 600;
    private readonly string _roomName;
    private readonly string _password;
    private readonly string _userName;
    private readonly TcpListener? _listener;
    private readonly TcpClient? _upstream;
    private readonly NetworkStream? _upstreamStream;
    private WebSocketChannel? _upstreamChannel;
    private readonly UdpClient? _discovery;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Peer> _peers = new();
    private readonly object _peerLock = new();
    private readonly SemaphoreSlim _upstreamWriteLock = new(1, 1);
    private readonly ConcurrentDictionary<string, AttachmentAccumulator> _incomingAttachments = new();
    private readonly HashSet<string> _mutedNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _mutedIps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _bannedNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _bannedIps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ipBannedIps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _kickedNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _kickedIps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _adminNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _adminIps = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<ChatMessage> _messages = new();
    private readonly ObservableCollection<RoomMember> _members = new();
    private readonly DateTime _roomStartedAt = DateTime.Now;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private static readonly string[] Emojis =
    {
        "😀", "😃", "😄", "😁", "😆", "😅", "😂", "🤣", "😊", "😇", "🙂", "🙃",
        "😉", "😍", "🥰", "😘", "😜", "🤪", "🤔", "🤨", "😐", "😴", "😷", "🤒",
        "😎", "🥳", "😭", "😤", "😱", "🤗", "👍", "👎", "👏", "🙏", "💪", "🤝",
        "🔥", "❤️", "💔", "✨", "🎉", "✅", "❌", "⭐", "🌙", "☀️", "🍀", "🎁",
        "📌", "🔔", "💡", "☕", "🚀", "🎵", "📎", "🖼"
    };
    private bool _closingStarted;
    private bool _isMuted;
    private bool _isAdmin;
    private string? _quotedSender;
    private string? _quotedPreview;
    private RemoteShortcutRelay? _shortcutRelay;
    private string? _remotePeerName;
    private string? _pendingConnectIp;
    private DispatcherTimer? _rustdeskFallbackTimer;
    /// <summary>RustDesk 直连 IP 模式默认端口（对端未自定义 direct IP access 端口时使用）。</summary>
    private const int RustDeskDirectPort = 21118;
    private readonly EventSimulator _keyInjector = EventSimulator.Create("LanChat");
    private bool _releaseByHotkey;
    private string _clipboardBaseline = string.Empty;
    private DispatcherTimer? _clipboardTimer;

    public ChatRoomView(string roomName, int port, string password, string userName, bool isPublic)
    {
        InitializeComponent();
        StartStatusLogging();
        _roomName = roomName;
        _password = password;
        _userName = string.IsNullOrWhiteSpace(userName) || userName == "输入用户名" ? "房主" : userName;
        IsPublic = isPublic;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        MessagesList.ItemsSource = _messages;
        MembersList.ItemsSource = _members;
        BuildEmojiPanel();
        RoomTitle.Text = roomName;
        RoleBadgeText.Text = "房主 · 局域网";
        _members.Add(new RoomMember(_userName, GetLocalPrivateIpv4(), "创建者"));
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
            .Select(a => a.Address.ToString()).Distinct().ToArray();
        RoomSubtitle.Text = $"{string.Join(" · ", addresses)}:{port}  ·  {(IsPublic ? "公开" : "私密，仅手动加入")} · {(_password.Length == 0 ? "无密码" : "已设置密码")}";
        _ = AcceptPeersAsync(_shutdown.Token);
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
    }

    public bool IsPublic { get; }

    public string RoomName => _roomName;

    /// <summary>离开房间（由宿主窗口切换回主页）时触发。</summary>
    public event Action? LeaveRequested;

    private Window OwnerWindow => Window.GetWindow(this) ?? Application.Current.MainWindow;

    private void Pin_Checked(object sender, RoutedEventArgs e)
    {
        if (OwnerWindow is { } owner) owner.Topmost = true;
    }

    private void Pin_Unchecked(object sender, RoutedEventArgs e)
    {
        if (OwnerWindow is { } owner) owner.Topmost = false;
    }

    private async void CloseWindow_Click(object sender, RoutedEventArgs e) => await LeaveRoomAsync();

    private void StartStatusLogging()
    {
        _statusTimer.Tick += (_, _) =>
        {
            if (_listener is not null)
            {
                int count;
                lock (_peerLock) count = _peers.Count;
                AppLog.Write($"运行状态：房间“{_roomName}”监听中，成员数 {count}，运行时长 {DateTime.Now - _roomStartedAt:hh\\:mm\\:ss}，公开发现 {(IsPublic ? "启用" : "关闭")}。");
            }
            else
            {
                AppLog.Write($"运行状态：房间“{_roomName}”客户端连接 {(_upstream?.Connected == true ? "正常" : "已断开")}，服务器 {_upstream?.Client.RemoteEndPoint}。");
            }
        };
        _statusTimer.Start();
    }

    public ChatRoomView(string roomName, string host, int port, string password, string userName)
    {
        InitializeComponent();
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
        BuildEmojiPanel();
        RoomTitle.Text = roomName;
        RoleBadgeText.Text = "访客 · 局域网";
        RoomSubtitle.Text = $"{host}:{port} · 正在连接";
        _upstreamChannel = WebSocketChannel.ClientHandshakeAsync(_upstreamStream, _shutdown.Token).GetAwaiter().GetResult();
        _upstreamChannel.SendTextAsync(JsonSerializer.Serialize(new RoomPacket { Type = "join", Room = roomName, Password = password, Sender = _userName }), _shutdown.Token).GetAwaiter().GetResult();
        _ = ReadServerAsync(_upstreamChannel, _shutdown.Token);
        AppLog.Write($"正在连接聊天室“{roomName}”：{host}:{port}。");
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

    private async Task ReadServerAsync(WebSocketChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await channel.ReadMessageAsync(cancellationToken);
                if (frame is null) break;
                var packet = JsonSerializer.Deserialize<RoomPacket>(frame.Value.Text!);
                if (packet is null) continue;
                if (packet.Type == "rustdesk-start")
                {
                    await Dispatcher.InvokeAsync(() => HandleRustDeskStart(packet));
                    continue;
                }
                if (packet.Type == "rustdesk-id")
                {
                    await Dispatcher.InvokeAsync(() => HandleRustDeskId(packet));
                    continue;
                }
                if (packet.Type == "remote-key")
                {
                    InjectRemoteKey(packet);
                    continue;
                }
                if (packet.Type == "clipboard")
                {
                    await Dispatcher.InvokeAsync(() => ApplyRemoteClipboard(packet));
                    continue;
                }
                if (packet.Type == "host-closed")
                {
                    AppLog.Write($"聊天室“{_roomName}”的主机关闭了连接。");
                    await Dispatcher.InvokeAsync(() =>
                    {
                        var notice = new ConnectionClosedWindow { Owner = OwnerWindow };
                        notice.ShowDialog();
                        ReturnToHome();
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
                            new RoomAccessNoticeWindow(reason) { Owner = OwnerWindow }.ShowDialog();
                            ReturnToHome();
                        });
                        return;
                    }
                }
                if (packet.Type == "mute-state")
                {
                    _isMuted = packet.Text == "muted";
                    AppLog.Write(_isMuted ? "运行状态：本账号已被房主禁言。" : "运行状态：本账号禁言已解除。");
                    await Dispatcher.InvokeAsync(() => AddMessage("系统", _isMuted ? "您已被房主禁言。" : "您的禁言已解除。", null, null));
                    continue;
                }
                if (packet.Type == "role-changed")
                {
                    var role = packet.Text ?? "访客";
                    var isSelf = string.Equals(packet.Sender, _userName, StringComparison.OrdinalIgnoreCase);
                    AppLog.Write($"角色变更：{packet.Sender} → {role}。");
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (isSelf)
                        {
                            _isAdmin = role == "管理员";
                            RoleBadgeText.Text = _isAdmin ? "管理员 · 局域网" : "访客 · 局域网";
                        }
                        SetMemberRole(packet.Sender ?? string.Empty, packet.SenderIp ?? string.Empty, role);
                        AddMessage("系统", isSelf ? $"您已被{(role == "管理员" ? "设为管理员" : "取消管理员权限")}。" : $"{packet.Sender} 的角色变更为{role}。", null, null);
                    });
                    continue;
                }
                if (packet.Type is "member-joined" or "member-left")
                {
                    await Dispatcher.InvokeAsync(() => UpdateMember(packet));
                    continue;
                }
                if (packet.Type == "attachment-chunk")
                {
                    var complete = AcceptAttachmentChunk(packet);
                    if (complete is not null)
                        await Dispatcher.InvokeAsync(() => AddPacket(complete.Value.Packet, complete.Value.Data));
                    continue;
                }                await Dispatcher.InvokeAsync(() =>
                {
                    if (packet.Type == "error") RoomSubtitle.Text = packet.Text ?? "无法加入房间";
                    else
                    {
                        RoomSubtitle.Text = $"{_upstream?.Client.RemoteEndPoint} · 已连接";
                        AddPacket(packet);
                        if (packet.Type is "text" or "file" or "image" or "folder" or "system")
                            AppLog.Write($"收到房间数据：类型 {packet.Type}，发送者 {packet.Sender ?? "系统"}，内容/附件 {packet.FileName ?? packet.Text ?? "(无)"}。");
                    }
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException ex) { if (!cancellationToken.IsCancellationRequested) AppLog.Write($"房间连接读取失败：{ex}"); }
        catch (JsonException ex) { AppLog.Write($"收到无效房间数据：{ex}"); }
        finally
        {
            _incomingAttachments.Clear();
            if (!cancellationToken.IsCancellationRequested)
            {
                AppLog.Write($"房间“{_roomName}”连接结束。");
                await Dispatcher.InvokeAsync(() => RoomSubtitle.Text = "连接已断开");
            }
        }
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
            var request = await WebSocketChannel.ReadRequestAsync(stream, cancellationToken);
            if (request is null) return;
            var channel = await WebSocketChannel.ServerAcceptAsync(stream, request, cancellationToken);
            if (channel is null)
            {
                AppLog.Write($"拒绝 {client.Client.RemoteEndPoint}：未通过 WebSocket 升级。");
                return;
            }
            var firstFrame = await channel.ReadMessageAsync(cancellationToken);
            if (firstFrame is null || !firstFrame.Value.IsText) return;
            var hello = JsonSerializer.Deserialize<RoomPacket>(firstFrame.Value.Text!);
            if (hello is null || hello.Type is not ("join" or "validate") || hello.Room != _roomName)
            {
                AppLog.Write($"拒绝 {client.Client.RemoteEndPoint} 的请求：房间名不匹配或请求格式无效。");
                await WritePacketAsync(channel, new RoomPacket { Type = "error", Text = "房间名错误或聊天室不存在。" }, cancellationToken);
                return;
            }
            if (hello.Password != _password)
            {
                AppLog.Write($"拒绝 {client.Client.RemoteEndPoint} 的加入请求：密码错误。");
                await WritePacketAsync(channel, new RoomPacket { Type = "error", Text = "密码错误" }, cancellationToken);
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
                await WritePacketAsync(channel, new RoomPacket { Type = "error", Text = denial }, cancellationToken);
                return;
            }
            if (hello.Type == "validate")
            {
                AppLog.Write($"加入预验证成功：{peerName} ({peerIp})。");
                await WritePacketAsync(channel, new RoomPacket { Type = "joined", Room = _roomName, Text = "验证成功" }, cancellationToken);
                return;
            }
            peer = new Peer(client, channel, peerName, peerIp);
            lock (_peerLock) _peers.Add(peer);
            await WritePacketAsync(channel, new RoomPacket { Type = "joined", Room = _roomName, Text = "已加入聊天室" }, cancellationToken);
            Peer[] existing;
            lock (_peerLock) existing = _peers.Where(p => !ReferenceEquals(p, peer)).ToArray();
            await WritePacketAsync(channel, new RoomPacket { Type = "member-joined", Sender = _userName, SenderIp = GetLocalPrivateIpv4(), SenderRole = "创建者" }, cancellationToken);
            foreach (var existingPeer in existing)
                await WritePacketAsync(channel, new RoomPacket { Type = "member-joined", Sender = existingPeer.Name, SenderIp = existingPeer.Ip, SenderRole = RoleOf(existingPeer) }, cancellationToken);
            var peerRole = RoleOf(peer);
            await BroadcastAsync(new RoomPacket { Type = "member-joined", Sender = peer.Name, SenderIp = peer.Ip, SenderRole = peerRole }, cancellationToken);
            await Dispatcher.InvokeAsync(() => _members.Add(new RoomMember(peer.Name, peer.Ip, peerRole)));
            var joinNotice = new RoomPacket { Type = "system", Room = _roomName, Text = $"{peer.Name} · {peer.Ip} 加入了聊天室。" };
            await BroadcastAsync(joinNotice, cancellationToken);
            await Dispatcher.InvokeAsync(() => AddPacket(joinNotice));
            AppLog.Write($"{peer.Name} ({peer.Ip}) 加入聊天室“{_roomName}”，角色 {peerRole}。");
            if (peerRole == "管理员")
                await WritePacketAsync(channel, new RoomPacket { Type = "role-changed", Sender = peer.Name, SenderIp = peer.Ip, SenderRole = peerRole, Text = peerRole }, cancellationToken);
            bool initiallyMuted;
            lock (_peerLock) initiallyMuted = _mutedNames.Contains(peer.Name) || _mutedIps.Contains(peer.Ip);
            if (initiallyMuted)
                await WritePacketAsync(channel, new RoomPacket { Type = "mute-state", Text = "muted" }, cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await channel.ReadMessageAsync(cancellationToken);
                if (frame is null) break;
                var packet = JsonSerializer.Deserialize<RoomPacket>(frame.Value.Text!);
                if (packet is null) continue;
                if (packet.Type == "command")
                {
                    if (RoleOf(peer) != "管理员")
                    {
                        await WritePacketAsync(channel, new RoomPacket { Type = "system", Text = "只有房主或管理员可以执行管理指令。" }, cancellationToken);
                        continue;
                    }
                    var commandText = packet.Text ?? string.Empty;
                    AppLog.Write($"管理员 {peer.Name} ({peer.Ip}) 请求执行指令：{commandText}。");
                    _ = Dispatcher.InvokeAsync((Action)(() => _ = RunOwnerCommandAsync(commandText, false)));
                    continue;
                }
                if (packet.Type == "rustdesk-start")
                {
                    packet.Room = _roomName;
                    packet.From = peer.Name;
                    packet.Sender = peer.Name;
                    packet.SenderIp = peer.Ip;
                    var target = packet.Target ?? string.Empty;
                    if (string.Equals(target, _userName, StringComparison.OrdinalIgnoreCase))
                        await Dispatcher.InvokeAsync(() => HandleRustDeskStart(packet));
                    else
                    {
                        Peer? destination;
                        lock (_peerLock) destination = _peers.FirstOrDefault(p => p.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
                        if (destination is null)
                            await WritePacketAsync(channel, new RoomPacket { Type = "system", Text = $"远程协助目标“{target}”不在线。" }, cancellationToken);
                        else
                            await WritePeerAsync(destination, packet, cancellationToken);
                    }
                    continue;
                }
                if (packet.Type == "rustdesk-id")
                {
                    packet.Room = _roomName;
                    packet.From = peer.Name;
                    packet.Sender = peer.Name;
                    packet.SenderIp = peer.Ip;
                    var idTarget = packet.Target ?? string.Empty;
                    if (string.Equals(idTarget, _userName, StringComparison.OrdinalIgnoreCase))
                        await Dispatcher.InvokeAsync(() => HandleRustDeskId(packet));
                    else
                    {
                        Peer? idDestination;
                        lock (_peerLock) idDestination = _peers.FirstOrDefault(p => p.Name.Equals(idTarget, StringComparison.OrdinalIgnoreCase));
                        if (idDestination is not null)
                            await WritePeerAsync(idDestination, packet, cancellationToken);
                    }
                    continue;
                }
                if (packet.Type == "clipboard")
                {
                    packet.Room = _roomName;
                    packet.From = peer.Name;
                    packet.Sender = peer.Name;
                    packet.SenderIp = peer.Ip;
                    var clipTarget = packet.Target ?? string.Empty;
                    if (string.Equals(clipTarget, _userName, StringComparison.OrdinalIgnoreCase))
                        await Dispatcher.InvokeAsync(() => ApplyRemoteClipboard(packet));
                    else
                    {
                        Peer? clipDestination;
                        lock (_peerLock) clipDestination = _peers.FirstOrDefault(p => p.Name.Equals(clipTarget, StringComparison.OrdinalIgnoreCase));
                        if (clipDestination is not null)
                            await WritePeerAsync(clipDestination, packet, cancellationToken);
                    }
                    continue;
                }
                if (packet.Type == "remote-key")
                {
                    packet.Room = _roomName;
                    packet.From = peer.Name;
                    packet.Sender = peer.Name;
                    var keyTarget = packet.Target ?? string.Empty;
                    if (string.Equals(keyTarget, _userName, StringComparison.OrdinalIgnoreCase))
                        InjectRemoteKey(packet);
                    else
                    {
                        Peer? keyDestination;
                        lock (_peerLock) keyDestination = _peers.FirstOrDefault(p => p.Name.Equals(keyTarget, StringComparison.OrdinalIgnoreCase));
                        if (keyDestination is not null)
                            await WritePeerAsync(keyDestination, packet, cancellationToken);
                    }
                    continue;
                }
                if (packet.Type is not ("text" or "attachment-chunk")) continue;
                bool muted;
                lock (_peerLock) muted = _mutedNames.Contains(peer.Name) || _mutedIps.Contains(peer.Ip);
                if (muted)
                {
                    await WritePacketAsync(channel, new RoomPacket { Type = "system", Text = "你当前已被房主禁言。" }, cancellationToken);
                    continue;
                }
                packet.Room = _roomName;
                packet.Sender = peer.Name;
                packet.SenderRole = RoleOf(peer);
                packet.SenderIp = peer.Ip;
                await BroadcastAsync(packet, cancellationToken);
                if (packet.Type == "attachment-chunk")
                {
                    var complete = AcceptAttachmentChunk(packet);
                    if (complete is not null)
                    {
                        Dispatcher.Invoke(() => AddPacket(complete.Value.Packet, complete.Value.Data));
                        AppLog.Write($"收到成员附件：{peer.Name} ({peer.Ip})，{complete.Value.Packet.FileName}，{complete.Value.Data.LongLength:N0} 字节。");
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
                foreach (var transfer in _incomingAttachments.Where(entry => entry.Value.SenderIp == peer.Ip).Select(entry => entry.Key).ToArray())
                    _incomingAttachments.TryRemove(transfer, out _);
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
        try { json = JsonSerializer.Serialize(packet); }
        catch (ArgumentException ex)
        {
            AppLog.Write($"房间数据序列化失败，已跳过该数据包：{ex.Message}");
            await Dispatcher.InvokeAsync(() => AddMessage("系统", "数据包过大，未能发送。请缩小消息或附件后重试。", null, null));
            return;
        }
        Peer[] peers;
        lock (_peerLock) peers = _peers.ToArray();
        foreach (var peer in peers)
        {
            try { await peer.Channel.SendTextAsync(json, cancellationToken); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private static async Task WritePacketAsync(WebSocketChannel channel, RoomPacket packet, CancellationToken cancellationToken)
    {
        await channel.SendTextAsync(JsonSerializer.Serialize(packet), cancellationToken);
    }

    private async Task WritePeerAsync(Peer peer, RoomPacket packet, CancellationToken cancellationToken)
    {
        await peer.Channel.SendTextAsync(JsonSerializer.Serialize(packet), cancellationToken);
    }

    // ======================= 远程协助（RustDesk） =======================

    /// <summary>发送只投递给指定成员的定向数据包。</summary>
    private async Task SendTargetedAsync(string target, RoomPacket packet)
    {
        packet.Room = _roomName;
        packet.Target = target;
        packet.From = _userName;
        if (_upstreamStream is not null)
        {
            await WriteUpstreamPacketAsync(packet, _shutdown.Token);
            return;
        }
        Peer? peer;
        lock (_peerLock) peer = _peers.FirstOrDefault(p => p.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
        if (peer is not null) await WritePeerAsync(peer, packet, _shutdown.Token);
    }

    private async void RequestRustDesk_Click(object sender, RoutedEventArgs e)
    {
        if (MembersList.SelectedItem is not RoomMember member) return;
        if (member.Name.Equals(_userName, StringComparison.OrdinalIgnoreCase)) return;
        if (!RustDeskLauncher.IsAvailable)
        {
            AddMessage("系统", "未找到 RustDesk，请确认程序目录下存在 Tools\\rustdesk.exe。", null, null);
            return;
        }
        _remotePeerName = member.Name;
        _pendingConnectIp = member.Ip;
        AddMessage("系统", $"已向 {member.Name} 发起远程协助，正在获取连接信息（约 3 秒）…", null, null);
        AppLog.Write($"发起远程协助：{member.Name}（{member.Ip}），等待对端回传 RustDesk ID 以选路。");
        try
        {
            await SendTargetedAsync(member.Name, new RoomPacket { Type = "rustdesk-start" });
        }
        catch (Exception ex)
        {
            AppLog.Write($"通知对方启动 RustDesk 失败：{ex.Message}");
        }
        // 对端 6 秒未回传 ID（旧版本或获取失败）则按原方式 IP 直连启动，不阻塞协助。
        StartRustDeskFallbackTimer();
    }

    private void StartRustDeskFallbackTimer()
    {
        var timer = _rustdeskFallbackTimer;
        if (timer is null)
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                ConnectRustDeskByIp("对端未回传 ID");
            };
            _rustdeskFallbackTimer = timer;
        }
        timer.Stop();
        timer.Start();
    }

    /// <summary>收到对端回传的 RustDesk ID 后选路：直连端口探测通则 IP 直连，否则改走 ID 中继。</summary>
    private void HandleRustDeskId(RoomPacket packet)
    {
        var from = packet.From ?? packet.Sender;
        if (!string.Equals(from, _remotePeerName, StringComparison.OrdinalIgnoreCase)) return;
        if (_pendingConnectIp is null) return;
        _rustdeskFallbackTimer?.Stop();
        _ = ConnectRustDeskAsync((packet.Text ?? string.Empty).Trim());
    }

    private async Task ConnectRustDeskAsync(string id)
    {
        var ip = _pendingConnectIp;
        var target = _remotePeerName;
        if (ip is null || target is null) return;
        var directReachable = await ProbeTcpAsync(ip, RustDeskDirectPort, TimeSpan.FromSeconds(2));
        bool started;
        string route;
        if (directReachable)
        {
            route = $"IP 直连 {ip}:{RustDeskDirectPort}";
            started = RustDeskLauncher.Start($"--connect \"{ip}\"");
        }
        else if (id.Length > 0)
        {
            route = $"ID 中继 {id}（直连端口 {ip}:{RustDeskDirectPort} 不可达）";
            started = RustDeskLauncher.Start($"--connect \"{id}\"");
        }
        else
        {
            route = $"IP 直连 {ip}（备用）";
            started = RustDeskLauncher.Start($"--connect \"{ip}\"");
        }
        AppLog.Write($"RustDesk 连接 {target}：{route}，启动{(started ? "成功" : "失败")}。");
        await Dispatcher.InvokeAsync(() => AddMessage("系统", started
            ? $"RustDesk 已启动（{route}），对方确认后即可控制。"
            : "未找到 RustDesk，请确认程序目录下存在 Tools\\rustdesk.exe。", null, null));
    }

    /// <summary>未取得对端 ID 时的回退：保持原有 IP 直连方式启动。</summary>
    private void ConnectRustDeskByIp(string reason)
    {
        var ip = _pendingConnectIp;
        var target = _remotePeerName;
        if (ip is null || target is null) return;
        var started = RustDeskLauncher.Start($"--connect \"{ip}\"");
        AppLog.Write($"RustDesk 连接 {target}（{reason}），按 IP 直连 {ip} 启动{(started ? "成功" : "失败")}。");
        AddMessage("系统", started
            ? $"已启动 RustDesk 连接 {target}（{ip}，{reason}，未走选路），对方确认后即可控制。"
            : "未找到 RustDesk，请确认程序目录下存在 Tools\\rustdesk.exe。", null, null);
    }

    /// <summary>探测对端 RustDesk 直连端口是否可达（决定走 IP 直连还是 ID 中继）。</summary>
    private static async Task<bool> ProbeTcpAsync(string host, int port, TimeSpan timeout)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeout);
            await client.ConnectAsync(host, port, cts.Token);
            return client.Connected;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { return false; }
    }

    private void HandleRustDeskStart(RoomPacket packet)
    {
        var requester = packet.From ?? packet.Sender ?? "对方";
        _remotePeerName = packet.From ?? packet.Sender;
        var started = RustDeskLauncher.Start();
        AddMessage("系统", started
            ? $"{requester} 请求远程协助，已启动 RustDesk，请在 RustDesk 窗口中确认连接。"
            : $"{requester} 请求远程协助，但本机未找到 RustDesk。", null, null);
        AppLog.Write($"{requester} 请求远程协助，RustDesk 启动{(started ? "成功" : "失败")}。");
        if (started) _ = ReplyRustDeskIdAsync(requester);
    }

    /// <summary>被控端回传本机 RustDesk ID，供发起端优先选路（IP 直连不可达时走 ID 中继）。</summary>
    private async Task ReplyRustDeskIdAsync(string requester)
    {
        try
        {
            var id = await RustDeskLauncher.TryGetIdAsync(TimeSpan.FromSeconds(6), _shutdown.Token);
            if (string.IsNullOrWhiteSpace(id))
            {
                AppLog.Write("未能获取本机 RustDesk ID，发起端将按 IP 直连连接。");
                return;
            }
            await SendTargetedAsync(requester, new RoomPacket { Type = "rustdesk-id", Text = id });
            AppLog.Write($"已向 {requester} 回传本机 RustDesk ID。");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            AppLog.Write($"回传 RustDesk ID 失败：{ex.Message}");
        }
    }

    // ======================= 快捷键转发（SharpHook，方案来自 GitHub 开源） =======================

    private async void ShortcutToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_remotePeerName))
        {
            AddMessage("系统", "请先在成员列表中选择对象并发起远程协助，再开启快捷键转发。", null, null);
            ShortcutToggle.IsChecked = false;
            return;
        }
        _shortcutRelay ??= new RemoteShortcutRelay();
        await _shortcutRelay.StartAsync(SendRemoteKeyAsync, ReleaseLocalHotkeys, _shutdown.Token);
        AppLog.Write($"快捷键转发已开启，目标：{_remotePeerName}。");
        AddMessage("系统", $"快捷键转发已开启：带修饰键的组合将发送给 {_remotePeerName}，本机不响应；按住 Ctrl+Alt 半秒可释放本机热键。", null, null);
    }

    private void ShortcutToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_shortcutRelay is not { IsActive: true }) return;
        _shortcutRelay.Stop();
        if (_releaseByHotkey)
        {
            _releaseByHotkey = false;
            AppLog.Write("已通过按住 Ctrl+Alt 释放本机热键，快捷键转发关闭。");
            AddMessage("系统", "已按住 Ctrl+Alt 释放本机热键：转发已关闭，本机恢复响应；如需继续转发请重新开启开关。", null, null);
            return;
        }
        AppLog.Write("快捷键转发已关闭。");
        AddMessage("系统", "快捷键转发已关闭，本机恢复响应。", null, null);
    }

    /// <summary>按住 Ctrl+Alt 半秒的释放回调（来自钩子线程，切换到 UI 线程后取消勾选）。</summary>
    private void ReleaseLocalHotkeys()
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (ShortcutToggle.IsChecked != true) return;
            _releaseByHotkey = true;
            ShortcutToggle.IsChecked = false;
        });
    }

    private Task SendRemoteKeyAsync(int key, bool isUp, CancellationToken token)
    {
        var target = _remotePeerName;
        if (string.IsNullOrWhiteSpace(target)) return Task.CompletedTask;
        return SendTargetedAsync(target, new RoomPacket { Type = "remote-key", Key = key, InputType = isUp ? 1 : 0 });
    }

    /// <summary>把远端转发来的按键注入本机（观看端为被控端时执行）。</summary>
    private void InjectRemoteKey(RoomPacket packet)
    {
        try
        {
            var key = (KeyCode)packet.Key;
            if (packet.InputType == 1) _keyInjector.SimulateKeyRelease(key);
            else _keyInjector.SimulateKeyPress(key);
        }
        catch (Exception ex) { AppLog.Write($"注入远端按键失败：键码 {packet.Key}，{ex.Message}"); }
    }

    // ======================= 跨设备剪贴板（点对点，仅远程协助对象） =======================

    private void ClipboardSyncToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_remotePeerName))
        {
            AddMessage("系统", "请先在成员列表中选择对象并发起远程协助，再开启剪贴板同步。", null, null);
            ClipboardSyncToggle.IsChecked = false;
            return;
        }
        _clipboardBaseline = ReadClipboardText();
        _clipboardTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _clipboardTimer.Tick += ClipboardTimer_Tick;
        _clipboardTimer.Start();
        AppLog.Write($"跨设备剪贴板同步已开启，目标：{_remotePeerName}。");
        AddMessage("系统", $"剪贴板同步已开启：复制的文字将发送给 {_remotePeerName}，对方开启同步后即可直接粘贴。", null, null);
    }

    private void ClipboardSyncToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_clipboardTimer is null) return;
        _clipboardTimer.Stop();
        _clipboardTimer = null;
        AppLog.Write("跨设备剪贴板同步已关闭。");
        AddMessage("系统", "剪贴板同步已关闭。", null, null);
    }

    /// <summary>轮询本机剪贴板，内容变化时点对点发送给远程协助对象。</summary>
    private async void ClipboardTimer_Tick(object? sender, EventArgs e)
    {
        var text = ReadClipboardText();
        if (text == _clipboardBaseline) return;
        _clipboardBaseline = text;
        if (text.Length == 0) return;
        if (text.Length > MaxClipboardChars)
        {
            AppLog.Write($"剪贴板文本过长（{text.Length:N0} 字符，上限 {MaxClipboardChars:N0}），已跳过同步。");
            return;
        }
        var target = _remotePeerName;
        if (string.IsNullOrWhiteSpace(target)) return;
        try
        {
            await SendTargetedAsync(target, new RoomPacket { Type = "clipboard", Text = text });
            AppLog.Write($"剪贴板文本已发送给 {target}（{text.Length:N0} 字符）。");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            AppLog.Write($"剪贴板同步失败：{ex.Message}");
        }
    }

    /// <summary>接收远程协助对象发来的剪贴板文本并写入本机（需本机也开启同步）。</summary>
    private void ApplyRemoteClipboard(RoomPacket packet)
    {
        if (ClipboardSyncToggle.IsChecked != true) return;
        var sender = packet.From ?? packet.Sender;
        if (string.IsNullOrWhiteSpace(_remotePeerName) ||
            !string.Equals(sender, _remotePeerName, StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Write($"已忽略非远程协助对象发来的剪贴板数据：{sender ?? "未知"}。");
            return;
        }
        var text = packet.Text;
        if (string.IsNullOrEmpty(text) || text.Length > MaxClipboardChars) return;
        if (text == _clipboardBaseline) return;
        _clipboardBaseline = text;
        try
        {
            Clipboard.SetText(text);
            AppLog.Write($"已接收 {sender} 的剪贴板文本（{text.Length:N0} 字符），可直接粘贴。");
        }
        catch (Exception ex) { AppLog.Write($"写入本机剪贴板失败：{ex.Message}"); }
    }

    private static string ReadClipboardText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty; }
        catch (Exception) { return string.Empty; }
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var text = MessageInput.Text.Trim();
        if (text.Length == 0) return;
        if (_upstreamStream is not null && _isMuted)
        {
            AppLog.Write($"已拦截禁言访客 {_userName} 的消息发送操作。");
            AddMessage("系统", "您已被房主禁言，暂时无法发送消息或附件。", null, null);
            return;
        }
        if (text.StartsWith('/'))
        {
            MessageInput.Clear();
            if (_upstreamStream is not null)
            {
                if (_isAdmin) await SendAdminCommandAsync(text);
                else AddMessage("系统", "管理指令仅房主或管理员可用。", null, null);
            }
            else
                await RunOwnerCommandAsync(text);
            return;
        }
        var packet = new RoomPacket { Type = "text", Room = _roomName, Sender = _userName, SenderRole = _listener is null ? "访客" : "创建者", SenderIp = GetLocalPrivateIpv4(), Text = text };
        if (_quotedSender is not null) packet.Quote = $"{_quotedSender}：{_quotedPreview}";
        AppLog.Write($"{_roomName} · {_userName}: {text}");
        MessageInput.Clear();
        ClearQuote();
        if (_upstreamStream is not null) await WriteUpstreamPacketAsync(packet, _shutdown.Token);
        else { AddPacket(packet); await BroadcastAsync(packet, _shutdown.Token); }
    }

    private async Task SendAdminCommandAsync(string command)
    {
        if (_upstreamStream is null) { await RunOwnerCommandAsync(command); return; }
        await WriteUpstreamPacketAsync(new RoomPacket { Type = "command", Room = _roomName, Sender = _userName, SenderIp = GetLocalPrivateIpv4(), Text = command }, _shutdown.Token);
        AppLog.Write($"管理员 {_userName} 已向房主发送指令：{command}。");
        AddMessage("系统", $"已发送管理指令：{command}", null, null);
    }

    private async Task RunOwnerCommandAsync(string raw, bool isOwner = true)
    {
        var parts = raw.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1 && parts[0].Equals("/help", StringComparison.OrdinalIgnoreCase))
        {
            AddMessage("系统", isOwner
                ? "用法：/mute 用户名或IP · /kick 用户名或IP · /ban 用户名或IP · /ipban IP · /unban 用户名或IP · /unipban IP · /admin 用户名或IP · /unadmin 用户名或IP"
                : "管理员可用：/mute 用户名或IP · /kick 用户名或IP · /ban 用户名或IP · /ipban IP · /unban 用户名或IP · /unipban IP", null, null);
            return;
        }
        if (parts.Length != 2 || parts[1].Length == 0)
        {
            AddMessage("系统", "指令格式：/指令 用户名或IP。输入 /help 查看可用指令。", null, null);
            return;
        }

        var command = parts[0].ToLowerInvariant();
        var target = parts[1].Trim();
        if (!isOwner && command is "/admin" or "/unadmin")
        {
            AddMessage("系统", "只有房主可以设置或取消管理员。", null, null);
            return;
        }
        AppLog.Write($"{(isOwner ? "房主" : "管理员")}命令请求：{command} {target}。");
        if (command is "/ipban" or "/unipban" && !IPAddress.TryParse(target, out _))
        {
            AddMessage("系统", $"{command} 的目标必须是有效 IP 地址。", null, null);
            return;
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
                case "/admin":
                    if (IPAddress.TryParse(target, out _)) _adminIps.Add(target);
                    else _adminNames.Add(target);
                    break;
                case "/unadmin":
                    if (IPAddress.TryParse(target, out _)) _adminIps.Remove(target);
                    else _adminNames.Remove(target);
                    break;
                default:
                    AddMessage("系统", "未知指令。可用：/mute、/kick、/ban、/ipban、/unban、/unipban、/admin、/unadmin、/help", null, null);
                    return;
            }

            if (command is "/kick" or "/ban" or "/ipban")
            {
                foreach (var peer in matches)
                {
                    _peers.Remove(peer);
                    _adminNames.Remove(peer.Name);
                    _adminIps.Remove(peer.Ip);
                }
            }
        }

        if (command is "/admin" or "/unadmin")
        {
            var role = command == "/admin" ? "管理员" : "访客";
            foreach (var peer in matches)
            {
                SetMemberRole(peer.Name, peer.Ip, role);
                await BroadcastAsync(new RoomPacket { Type = "role-changed", Sender = peer.Name, SenderIp = peer.Ip, SenderRole = role, Text = role }, _shutdown.Token);
            }
            AddMessage("系统", matches.Length > 0
                ? (command == "/admin" ? $"已将 {target} 设为管理员。" : $"已取消 {target} 的管理员权限。")
                : $"已记录 {target} 的角色设置（该成员上线后生效）。", null, null);
            AppLog.Write($"管理员设置变更：{target} → {role}，在线匹配 {matches.Length} 人。");
            return;
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
                try { await WritePacketAsync(peer.Channel, new RoomPacket { Type = "mute-state", Text = muted ? "muted" : "unmuted" }, _shutdown.Token); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
                { AppLog.Write($"无法更新 {peer.Name} 的禁言状态：{ex.Message}"); }
            }
        }
        foreach (var peer in matches.Where(p => removalNotice is not null))
        {
            try { await WritePacketAsync(peer.Channel, new RoomPacket { Type = "removed", Text = removalNotice }, _shutdown.Token); }
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
        AddMessage("系统", report, null, null);
        if (command == "/mute")
        {
            await BroadcastAsync(new RoomPacket { Type = "system", Room = _roomName, Text = $"房主更新了 {target} 的发言权限。" }, _shutdown.Token);
        }
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
    }

    private void SetMemberRole(string name, string ip, string role)
    {
        for (var index = 0; index < _members.Count; index++)
        {
            if (!_members[index].Name.Equals(name, StringComparison.OrdinalIgnoreCase) || _members[index].Ip != ip) continue;
            _members[index] = new RoomMember(name, ip, role);
            return;
        }
    }

    private string RoleOf(Peer peer)
    {
        lock (_peerLock)
            return _adminNames.Contains(peer.Name) || _adminIps.Contains(peer.Ip) ? "管理员" : "访客";
    }

    private void MembersList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (MembersList.SelectedItem is null) { e.Handled = true; return; }
        if (MembersList.ContextMenu is null) return;
        var isHost = _listener is not null;
        RemoteMenuItem.Visibility = MembersList.SelectedItem is RoomMember selected &&
                                    !selected.Name.Equals(_userName, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
        foreach (var item in MembersList.ContextMenu.Items.OfType<MenuItem>().Where(i => i.Tag is string))
        {
            var tag = (string)item.Tag;
            var ownerOnly = tag is "/admin" or "/unadmin";
            item.Visibility = ownerOnly
                ? (isHost ? Visibility.Visible : Visibility.Collapsed)
                : (isHost || _isAdmin ? Visibility.Visible : Visibility.Collapsed);
        }
    }

    private void MembersList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        var item = ItemsControl.ContainerFromElement(MembersList, source) as ListBoxItem;
        if (item is not null) item.IsSelected = true;
    }

    private async void CopyMemberName_Click(object sender, RoutedEventArgs e)
    {
        if (MembersList.SelectedItem is RoomMember member) await CopyToClipboardAsync(member.Name);
    }

    private async void CopyMemberIp_Click(object sender, RoutedEventArgs e)
    {
        if (MembersList.SelectedItem is RoomMember member) await CopyToClipboardAsync(member.Ip);
    }

    private async Task CopyToClipboardAsync(string value)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                Clipboard.SetText(value);
                AddMessage("系统", "已复制到剪贴板。", null, null);
                return;
            }
            catch (ExternalException ex) when (attempt < 5)
            {
                AppLog.Write($"剪贴板暂时不可用，正在重试（{attempt + 1}/5）：{ex.Message}");
                await Task.Delay(80);
            }
            catch (ExternalException ex)
            {
                AppLog.Write($"复制到剪贴板失败：{ex}");
                AddMessage("系统", "剪贴板正被其他程序占用，复制失败，请稍后重试。", null, null);
            }
            catch (InvalidOperationException ex)
            {
                AppLog.Write($"复制到剪贴板失败：{ex}");
                AddMessage("系统", "无法访问剪贴板，请稍后重试。", null, null);
                return;
            }
        }
    }

    private async void MemberCommand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string command } || MembersList.SelectedItem is not RoomMember member) return;
        if (_listener is not null) await RunOwnerCommandAsync($"{command} {member.Ip}");
        else if (_isAdmin) await SendAdminCommandAsync($"{command} {member.Ip}");
        else AddMessage("系统", "只有房主或管理员可以管理成员。", null, null);
    }

    private void ShowBanList_Click(object sender, RoutedEventArgs e)
    {
        if (_listener is null) { AddMessage("系统", "只有房主可以查看封禁列表。", null, null); return; }
        BanRecord[] records;
        lock (_peerLock)
        {
            records = _bannedNames.Select(n => new BanRecord(n, "用户封禁"))
                .Concat(_bannedIps.Select(ip => new BanRecord(ip, _ipBannedIps.Contains(ip) ? "IP 封禁" : "IP 封禁")))
                .ToArray();
        }
        new BanListWindow(records, record => _ = RunOwnerCommandAsync($"{(record.Kind == "用户封禁" ? "/unban" : "/unipban")} {record.Target}")) { Owner = OwnerWindow }.ShowDialog();
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
    private async void Folder_Click(object sender, RoutedEventArgs e) => await PickFolderAttachmentAsync();

    private const string FolderMime = "application/x-lanchat-folder";

    private bool EnsureCanSendAttachment()
    {
        if (_upstreamStream is not null && _isMuted)
        {
            AppLog.Write($"已拦截禁言访客 {_userName} 的附件发送操作。");
            AddMessage("系统", "您已被房主禁言，暂时无法发送消息或附件。", null, null);
            return false;
        }
        return true;
    }

    private async Task PickAttachmentAsync(bool imageOnly)
    {
        if (!EnsureCanSendAttachment()) return;
        var dialog = new OpenFileDialog
        {
            Title = imageOnly ? "选择要发送的图片" : "选择要发送的文件",
            Filter = imageOnly ? "图片文件|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp" : "所有文件|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog(OwnerWindow) != true) return;
        var info = new FileInfo(dialog.FileName);
        if (info.Length > MaxAttachmentBytes)
        {
            MessageBox.Show(OwnerWindow, "单个附件最大为 200 MB。", "文件过大", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await SendAttachmentAsync(dialog.FileName, info.Name, imageOnly ? "image" : "file");
    }

    private async Task PickFolderAttachmentAsync()
    {
        if (!EnsureCanSendAttachment()) return;
        var dialog = new OpenFolderDialog { Title = "选择要发送的文件夹" };
        if (dialog.ShowDialog(OwnerWindow) != true) return;
        var sourceDir = dialog.FolderName;
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir)) return;
        var folderName = new DirectoryInfo(sourceDir).Name;
        var zipPath = Path.Combine(Path.GetTempPath(), $"lanchat_folder_{Guid.NewGuid():N}.zip");
        try
        {
            AppLog.Write($"开始打包文件夹：{sourceDir}");
            ShowTransfer($"正在打包文件夹：{folderName}", 0);
            await Task.Run(() => ZipFile.CreateFromDirectory(sourceDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true));
            var zipInfo = new FileInfo(zipPath);
            if (zipInfo.Length > MaxAttachmentBytes)
            {
                MessageBox.Show(OwnerWindow, $"文件夹打包后为 {zipInfo.Length / 1024.0 / 1024.0:F1} MB，超过 200 MB 上限，无法发送。", "文件夹过大", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            // zip 内含“文件夹名/”根目录，接收端解压即可还原原目录结构。
            await SendAttachmentAsync(zipPath, $"{folderName}.zip", "folder");
            AppLog.Write($"文件夹打包发送完成：{folderName}，{zipInfo.Length:N0} 字节。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            AppLog.Write($"文件夹打包失败：{sourceDir} · {ex}");
            MessageBox.Show(OwnerWindow, $"无法打包文件夹：{ex.Message}", "发送失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            HideTransfer();
            try { File.Delete(zipPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task SendAttachmentAsync(string filePath, string displayName, string attachmentType)
    {
        var mimeType = attachmentType switch
        {
            "image" => GetImageMime(Path.GetExtension(displayName)),
            "folder" => FolderMime,
            _ => "application/octet-stream"
        };
        try
        {
            var transferId = Guid.NewGuid().ToString("N");
            // 采用流式分块读取，避免一次性把整个文件读入内存造成峰值占用。
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                AttachmentChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var totalBytes = stream.Length;
            if (totalBytes <= 0)
            {
                MessageBox.Show(OwnerWindow, "该文件为空，无法发送。", "文件为空", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var chunkCount = (int)((totalBytes + AttachmentChunkBytes - 1) / AttachmentChunkBytes);
            AppLog.Write($"开始分块发送附件：{displayName}，{totalBytes:N0} 字节，{chunkCount} 个数据块。");
            var buffer = new byte[AttachmentChunkBytes];
            byte[]? assembled = null;
            ShowTransfer($"正在发送：{displayName}", 0);
            for (var index = 0; index < chunkCount; index++)
            {
                var length = (int)Math.Min(AttachmentChunkBytes, totalBytes - (long)index * AttachmentChunkBytes);
                var read = await stream.ReadAsync(buffer.AsMemory(0, length), _shutdown.Token);
                if (read != length) throw new IOException("文件在发送过程中被修改，请重新发送。");
                var packet = new RoomPacket
                {
                    Type = "attachment-chunk", AttachmentType = attachmentType, TransferId = transferId,
                    ChunkIndex = index, ChunkCount = chunkCount, TotalBytes = totalBytes, Room = _roomName, Sender = _userName,
                    SenderRole = _listener is null ? "访客" : "创建者", SenderIp = GetLocalPrivateIpv4(),
                    FileName = displayName, MimeType = mimeType,
                    Data = Convert.ToBase64String(buffer, 0, read)
                };
                if (_upstreamStream is not null) await WriteUpstreamPacketAsync(packet, _shutdown.Token);
                else
                {
                    await BroadcastAsync(packet, _shutdown.Token);
                    var complete = AcceptAttachmentChunk(packet, false);
                    if (complete is not null) assembled = complete.Value.Data;
                }
                ShowTransfer($"正在发送：{displayName}", (index + 1) * 100.0 / chunkCount);
            }
            if (_upstreamStream is null && assembled is not null)
                AddPacket(new RoomPacket { Type = attachmentType, Sender = _userName, SenderRole = "创建者", SenderIp = GetLocalPrivateIpv4(), FileName = displayName, MimeType = mimeType }, assembled);
            AppLog.Write($"附件发送成功：{displayName}，{totalBytes:N0} 字节，房间“{_roomName}”。");
        }
        catch (IOException ex)
        {
            AppLog.Write($"附件读取或发送失败：{filePath} · {ex}");
            MessageBox.Show(OwnerWindow, $"无法读取或发送文件：{ex.Message}", "发送失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException or ArgumentException)
        {
            AppLog.Write($"附件分块发送失败：{filePath} · {ex}");
            if (!_shutdown.IsCancellationRequested)
                MessageBox.Show(OwnerWindow, $"附件发送失败：{ex.Message}", "发送失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            HideTransfer();
        }
    }

    private async Task WriteUpstreamPacketAsync(RoomPacket packet, CancellationToken cancellationToken)
    {
        if (_upstreamStream is null) return;
        await _upstreamWriteLock.WaitAsync(cancellationToken);
        try { await WritePacketAsync(_upstreamChannel!, packet, cancellationToken); }
        finally { _upstreamWriteLock.Release(); }
    }

    private (RoomPacket Packet, byte[] Data)? AcceptAttachmentChunk(RoomPacket packet, bool reportProgress = true)
    {
        if (string.IsNullOrWhiteSpace(packet.TransferId) || string.IsNullOrWhiteSpace(packet.SenderIp) || string.IsNullOrEmpty(packet.Data) ||
            packet.ChunkCount is < 1 or > 1024 || packet.ChunkIndex < 0 || packet.ChunkIndex >= packet.ChunkCount ||
            packet.TotalBytes is < 1 or > MaxAttachmentBytes || packet.ChunkCount != (packet.TotalBytes + AttachmentChunkBytes - 1) / AttachmentChunkBytes ||
            packet.AttachmentType is not ("file" or "image" or "folder")) return null;
        var transferKey = $"{packet.SenderIp}:{packet.TransferId}";
        if (!_incomingAttachments.TryGetValue(transferKey, out var accumulator))
        {
            accumulator = new AttachmentAccumulator(packet);
            if (!_incomingAttachments.TryAdd(transferKey, accumulator) && !_incomingAttachments.TryGetValue(transferKey, out accumulator)) return null;
        }
        if (accumulator.SenderIp != packet.SenderIp) return null;
        var completeData = accumulator.Add(packet);
        if (completeData is null)
        {
            if (reportProgress)
            {
                var percent = accumulator.Progress;
                Dispatcher.BeginInvoke(new Action(() => ShowTransfer($"正在接收：{packet.FileName}", percent)));
            }
            return null;
        }
        _incomingAttachments.TryRemove(transferKey, out _);
        var completePacket = new RoomPacket
        {
            Type = packet.AttachmentType, Room = packet.Room, Sender = packet.Sender, SenderRole = packet.SenderRole,
            SenderIp = packet.SenderIp, FileName = packet.FileName, MimeType = packet.MimeType
        };
        return (completePacket, completeData);
    }

    private void BuildEmojiPanel()
    {
        foreach (var emoji in Emojis)
        {
            var button = new Button
            {
                Content = emoji,
                Width = 34,
                Height = 34,
                MinHeight = 0,
                Padding = new Thickness(0),
                Margin = new Thickness(1),
                FontSize = 16,
                FontWeight = FontWeights.Normal,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderBrush = System.Windows.Media.Brushes.Transparent,
                ToolTip = emoji
            };
            button.Click += EmojiButton_Click;
            EmojiPanel.Children.Add(button);
        }
    }

    private void EmojiToggle_Checked(object sender, RoutedEventArgs e) => EmojiPopup.IsOpen = true;
    private void EmojiToggle_Unchecked(object sender, RoutedEventArgs e) => EmojiPopup.IsOpen = false;
    private void EmojiPopup_Closed(object? sender, EventArgs e) => EmojiToggle.IsChecked = false;

    private void EmojiButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Content: string emoji }) return;
        var index = MessageInput.CaretIndex;
        MessageInput.Text = MessageInput.Text.Insert(index, emoji);
        MessageInput.CaretIndex = index + emoji.Length;
        MessageInput.Focus();
    }

    private void QuoteMessage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChatMessage message || message.IsSystem) return;
        _quotedSender = message.Sender.Split('·')[0].Trim();
        _quotedPreview = message.Attachment is null ? message.Text : $"附件：{message.Attachment}";
        if (_quotedPreview.Length > 60) _quotedPreview = _quotedPreview[..60] + "…";
        QuoteText.Text = $"引用 {_quotedSender}：{_quotedPreview}";
        QuoteBar.Visibility = Visibility.Visible;
        MessageInput.Focus();
    }

    private async void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChatMessage message) return;
        await CopyToClipboardAsync(message.Attachment is null ? message.Text : $"{message.Text} {message.Attachment}");
    }

    private void CancelQuote_Click(object sender, RoutedEventArgs e) => ClearQuote();

    private void ClearQuote()
    {
        _quotedSender = null;
        _quotedPreview = null;
        QuoteBar.Visibility = Visibility.Collapsed;
        QuoteText.Text = string.Empty;
    }

    private DateTime _lastTransferRender = DateTime.MinValue;

    /// <summary>进度条按 100~200ms 节流刷新（150ms），避免高频重绘占用系统资源；满进度立即显示。</summary>
    private void ShowTransfer(string label, double percent)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        var now = DateTime.UtcNow;
        if (clamped < 100 && (now - _lastTransferRender).TotalMilliseconds < 150)
        {
            // 节流窗口内仅更新文案，进度条留待下个窗口统一重绘。
            TransferLabel.Text = label;
            return;
        }
        _lastTransferRender = now;
        TransferPanel.Visibility = Visibility.Visible;
        TransferLabel.Text = label;
        TransferProgress.Value = clamped;
        TransferPercent.Text = $"{clamped:0}%";
    }

    private void HideTransfer()
    {
        TransferPanel.Visibility = Visibility.Collapsed;
        TransferProgress.Value = 0;
        TransferPercent.Text = "0%";
    }

    private void DownloadFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择默认下载文件夹",
            InitialDirectory = Directory.Exists(AppSettings.DownloadFolder) ? AppSettings.DownloadFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(OwnerWindow) != true) return;
        AppSettings.DownloadFolder = dialog.FolderName;
        AppLog.Write($"下载默认目录已更改为：{dialog.FolderName}。");
        RoomSubtitle.Text = $"默认下载位置：{AppSettings.DownloadFolder}";
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChatMessage message || message.Data is null || message.Attachment is null) return;
        try
        {
            var bytes = message.Data;
            if (message.MimeType == FolderMime)
            {
                // 文件夹包：选择目标父目录，解压还原出原目录结构。
                var folderDialog = new OpenFolderDialog
                {
                    Title = "选择文件夹的保存位置（将还原原目录结构）",
                    InitialDirectory = Directory.Exists(AppSettings.DownloadFolder) ? AppSettings.DownloadFolder : null
                };
                if (folderDialog.ShowDialog(OwnerWindow) != true) return;
                var targetParent = folderDialog.FolderName;
                var tempZip = Path.Combine(Path.GetTempPath(), $"lanchat_recv_{Guid.NewGuid():N}.zip");
                try
                {
                    await File.WriteAllBytesAsync(tempZip, bytes, _shutdown.Token);
                    await Task.Run(() => ZipFile.ExtractToDirectory(tempZip, targetParent, overwriteFiles: true), _shutdown.Token);
                    var restoredName = Path.GetFileNameWithoutExtension(message.Attachment);
                    var restoredPath = Path.Combine(targetParent, restoredName);
                    AddMessage("系统", $"文件夹已还原到：{restoredPath}", null, null);
                    AppLog.Write($"文件夹包“{message.Attachment}”已解压还原到 {restoredPath}。");
                }
                finally
                {
                    try { File.Delete(tempZip); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
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
            if (dialog.ShowDialog(OwnerWindow) != true) return;
            Directory.CreateDirectory(Path.GetDirectoryName(dialog.FileName)!);
            File.WriteAllBytes(dialog.FileName, bytes);
            AddMessage("系统", $"文件已保存到：{dialog.FileName}", null, null);
            AppLog.Write($"附件“{message.Attachment}”已保存到 {dialog.FileName}。");
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(OwnerWindow, $"无法保存附件：{ex.Message}", "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void MessageInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            Send_Click(sender, new RoutedEventArgs());
        }
        await Task.CompletedTask;
    }

    private void AddPacket(RoomPacket packet, byte[]? attachmentData = null)
    {
        BitmapImage? thumbnail = null;
        if (packet.Type == "image" && attachmentData is not null)
        {
            try
            {
                using var memory = new MemoryStream(attachmentData);
                thumbnail = new BitmapImage();
                thumbnail.BeginInit();
                thumbnail.CacheOption = BitmapCacheOption.OnLoad;
                thumbnail.DecodePixelWidth = 640;
                thumbnail.StreamSource = memory;
                thumbnail.EndInit();
                thumbnail.Freeze();
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException or IOException) { }
        }

        var text = packet.Type switch
        {
            "file" => $"发送了文件：{packet.FileName}",
            "image" => $"发送了图片：{packet.FileName}",
            "folder" => $"发送了文件夹：{packet.FileName}",
            "system" => packet.Text ?? string.Empty,
            _ => packet.Text ?? string.Empty
        };
        var sender = packet.Sender ?? "访客";
        if (packet.Type is "text" or "file" or "image" or "folder")
            sender += $" · {packet.SenderRole ?? "访客"}";
        if (!string.IsNullOrWhiteSpace(packet.SenderIp)) sender += $" · {packet.SenderIp}";
        var isMine = packet.Type is "text" or "file" or "image" or "folder" && packet.Sender == _userName;
        AddMessage(sender, text, packet.Type is "file" or "image" or "folder" ? packet.FileName : null, thumbnail, attachmentData, packet.MimeType, isMine, packet.Type == "system", packet.Quote);
        if (packet.Type is "file" or "image" or "folder")
        {
            HideTransfer();
            AppLog.Write($"附件进入收件列表：{packet.Type} · {packet.FileName ?? "(未命名)"} · 来自 {sender}。");
        }
        MessageScroll.ScrollToEnd();
    }

    private void AddMessage(string sender, string text, string? attachment, BitmapImage? thumbnail, byte[]? data = null, string? mimeType = null, bool isMine = false, bool isSystem = false, string? quote = null)
    {
        var displayText = attachment is null ? text : text;
        _messages.Add(new ChatMessage(sender, DateTime.Now.ToString("HH:mm"), displayText, attachment, thumbnail, data, mimeType, isMine, isSystem || sender == "系统")
        {
            Quote = string.IsNullOrWhiteSpace(quote) ? null : quote
        });
        // 仅保留最近的消息，避免长时间挂机导致内存持续增长（附件数据随之释放）。
        while (_messages.Count > MaxRetainedMessages) _messages.RemoveAt(0);
        MessageScroll.ScrollToEnd();
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

    /// <summary>房主主动关闭房间：广播通知后释放资源，并请求宿主切回主页。</summary>
    public async Task LeaveRoomAsync()
    {
        if (_closingStarted) return;
        _closingStarted = true;
        _statusTimer.Stop();
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
        TeardownNetwork();
        LeaveRequested?.Invoke();
    }

    /// <summary>连接已被对端关闭时直接释放资源并返回主页。</summary>
    private void ReturnToHome()
    {
        _closingStarted = true;
        TeardownNetwork();
        LeaveRequested?.Invoke();
    }

    /// <summary>释放全部网络与消息资源。</summary>
    private void TeardownNetwork()
    {
        _statusTimer.Stop();
        try { _shortcutRelay?.Dispose(); } catch (Exception) { }
        _shortcutRelay = null;
        _clipboardTimer?.Stop();
        _clipboardTimer = null;
        try { _shutdown.Cancel(); } catch (ObjectDisposedException) { }
        try { _listener?.Stop(); } catch (SocketException) { }
        _discovery?.Dispose();
        _upstream?.Dispose();
        lock (_peerLock)
        {
            foreach (var peer in _peers) peer.Client.Dispose();
            _peers.Clear();
        }
        _incomingAttachments.Clear();
        _messages.Clear();
        _members.Clear();
    }

    /// <summary>宿主窗口关闭时调用，释放资源（不涉及界面切换）。</summary>
    public void Dispose()
    {
        if (_closingStarted)
        {
            TeardownNetwork();
            return;
        }
        _closingStarted = true;
        if (_listener is not null)
        {
            try { _ = BroadcastAsync(new RoomPacket { Type = "host-closed", Room = _roomName, Text = "主机已关闭连接" }, _shutdown.Token); } catch (Exception) { }
        }
        TeardownNetwork();
        GC.SuppressFinalize(this);
    }

    private sealed record Peer(TcpClient Client, WebSocketChannel Channel, string Name, string Ip)
    {
    }

    private sealed record RoomMember(string Name, string Ip, string Role)
    {
        public string Details => $"{Role} · {Ip}";
        public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Substring(0, 1).ToUpperInvariant();
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
        public string? Quote { get; set; }
        public string? FileName { get; set; }
        public string? MimeType { get; set; }
        public string? Data { get; set; }
        public string? AttachmentType { get; set; }
        public string? TransferId { get; set; }
        public int ChunkIndex { get; set; }
        public int ChunkCount { get; set; }
        public long TotalBytes { get; set; }
        public int Port { get; set; }
        public bool PasswordRequired { get; set; }
        public string? Name { get; set; }
        public string? HostAddress { get; set; }
        public string? Target { get; set; }
        public string? From { get; set; }
        public string? SessionId { get; set; }
        public int InputType { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Button { get; set; }
        public int Delta { get; set; }
        public int Key { get; set; }
    }

    private sealed record ChatMessage(string Sender, string Time, string Text, string? Attachment, BitmapImage? Thumbnail, byte[]? Data, string? MimeType, bool IsMine, bool IsSystem)
    {
        public string? Quote { get; init; }
        public string AttachmentLabel => Attachment is null ? string.Empty : $"📎 {Attachment}";
        public Visibility ThumbnailVisibility => Thumbnail is null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility DownloadVisibility => Attachment is null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility QuoteVisibility => string.IsNullOrWhiteSpace(Quote) ? Visibility.Collapsed : Visibility.Visible;
    }

    private sealed class AttachmentAccumulator
    {
        private readonly byte[] _data;
        private readonly bool[] _receivedChunks;
        private int _received;
        public string? SenderIp { get; }
        public double Progress => _receivedChunks.Length == 0 ? 0 : _received * 100.0 / _receivedChunks.Length;

        public AttachmentAccumulator(RoomPacket firstPacket)
        {
            SenderIp = firstPacket.SenderIp;
            _data = new byte[checked((int)firstPacket.TotalBytes)];
            _receivedChunks = new bool[firstPacket.ChunkCount];
        }

        public byte[]? Add(RoomPacket packet)
        {
            if (packet.ChunkCount != _receivedChunks.Length || packet.TotalBytes != _data.Length || packet.ChunkIndex < 0 || packet.ChunkIndex >= _receivedChunks.Length) return null;
            byte[] chunk;
            try { chunk = Convert.FromBase64String(packet.Data!); }
            catch (FormatException) { return null; }
            var offset = packet.ChunkIndex * AttachmentChunkBytes;
            var expectedLength = Math.Min(AttachmentChunkBytes, _data.Length - offset);
            if (chunk.Length != expectedLength || _receivedChunks[packet.ChunkIndex]) return null;
            Buffer.BlockCopy(chunk, 0, _data, offset, chunk.Length);
            _receivedChunks[packet.ChunkIndex] = true;
            _received++;
            return _received == _receivedChunks.Length ? _data : null;
        }
    }
}

internal sealed record BanRecord(string Target, string Kind);
