using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;

namespace LanChat;

public partial class JoinRoomWindow : Window
{
    private const int DiscoveryPort = 47820;
    private readonly ObservableCollection<DiscoveredRoom> _rooms = new();
    private readonly bool _searchOnOpen;
    public string Host => HostInput.Text.Trim();
    public string RoomName => RoomNameInput.Text.Trim();
    public int Port { get; private set; }
    public string Password => PasswordInput.Password;

    public JoinRoomWindow(bool searchOnOpen)
    {
        InitializeComponent();
        _searchOnOpen = searchOnOpen;
        RoomsList.ItemsSource = _rooms;
        if (searchOnOpen)
        {
            DialogTitle.Text = "搜索已创建的聊天室";
            DialogSubtitle.Text = "搜索当前局域网中正在广播的房间，也可以手动输入地址。";
            Loaded += async (_, _) => await SearchRoomsAsync();
        }
    }

    private void Pin_Checked(object sender, RoutedEventArgs e) => Topmost = true;
    private void Pin_Unchecked(object sender, RoutedEventArgs e) => Topmost = false;
    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private async void SearchButton_Click(object sender, RoutedEventArgs e) => await SearchRoomsAsync();

    private async Task SearchRoomsAsync()
    {
        AppLog.Write("开始发送 UDP 局域网房间发现请求。");
        SearchButton.IsEnabled = false;
        SearchButton.Content = "搜索中…";
        _rooms.Clear();
        try
        {
            using var udp = new UdpClient(0) { EnableBroadcast = true };
            udp.Client.ReceiveTimeout = 3200;
            var request = JsonSerializer.SerializeToUtf8Bytes(new DiscoveryPacket { Type = "discover" });
            await udp.SendAsync(request, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
            var until = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < until)
            {
                using var receiveTimeout = new CancellationTokenSource(until - DateTime.UtcNow);
                UdpReceiveResult result;
                try { result = await udp.ReceiveAsync(receiveTimeout.Token); }
                catch (OperationCanceledException) { break; }
                var packet = JsonSerializer.Deserialize<DiscoveryPacket>(result.Buffer);
                if (packet?.Type != "room" || string.IsNullOrWhiteSpace(packet.Name) || packet.Port is < 1024 or > 65535) continue;
                var address = packet.HostAddress ?? result.RemoteEndPoint.Address.ToString();
                if (_rooms.Any(r => r.Host == address && r.Port == packet.Port && r.Name == packet.Name)) continue;
                _rooms.Add(new DiscoveredRoom(packet.Name, address, packet.Port, packet.PasswordRequired));
            }
            if (_rooms.Count == 0) DialogSubtitle.Text = "没有发现房间。请确认双方在同一网络并允许应用通过防火墙。也可手动输入地址。";
            else DialogSubtitle.Text = $"发现 {_rooms.Count} 个聊天室。选择房间后输入密码（如有）。";
            AppLog.Write($"UDP 房间搜索完成，发现 {_rooms.Count} 个公开房间。");
        }
        catch (SocketException ex) { DialogSubtitle.Text = $"局域网搜索失败：{ex.Message}"; AppLog.Write($"UDP 房间搜索失败：{ex}"); }
        finally { SearchButton.IsEnabled = true; SearchButton.Content = "搜索"; }
    }

    private void RoomsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (RoomsList.SelectedItem is not DiscoveredRoom room) return;
        AppLog.Write($"用户选择搜索结果：房间“{room.Name}”，{room.Host}:{room.Port}，{(room.PasswordRequired ? "需密码" : "无密码")}。");
        HostInput.Text = room.Host;
        PortInput.Text = room.Port.ToString();
        RoomNameInput.Text = room.Name;
    }

    private void Join_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        if (!IPAddress.TryParse(Host, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        { ErrorText.Text = "请输入有效的 IPv4 主机地址，或从搜索结果中选择房间。"; AppLog.Write("加入信息校验失败：IPv4 地址无效。"); return; }
        if (!int.TryParse(PortInput.Text.Trim(), out var port) || port is < 1024 or > 65535)
        { ErrorText.Text = "端口必须是 1024 到 65535 之间的整数。"; AppLog.Write("加入信息校验失败：端口无效。"); return; }
        if (string.IsNullOrWhiteSpace(RoomName))
        { ErrorText.Text = "请从搜索结果选择聊天室，或填写房间名。"; AppLog.Write("加入信息校验失败：房间名为空。"); return; }
        Port = port;
        AppLog.Write($"加入表单确认：房间“{RoomName}”，服务器 {Host}:{port}。");
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed class DiscoveryPacket
    {
        public string? Type { get; set; }
        public string? Name { get; set; }
        public int Port { get; set; }
        public string? HostAddress { get; set; }
        public bool PasswordRequired { get; set; }
    }

    private sealed record DiscoveredRoom(string Name, string Host, int Port, bool PasswordRequired)
    {
        public string Endpoint => $"{Host}:{Port}";
        public string LockLabel => PasswordRequired ? "需密码" : "开放";
    }
}
