using System.Windows;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace LanChat;

public partial class MainWindow : Window
{
    private string? _currentUsername;
    private LoggerWindow? _loggerWindow;
    private ChatRoomView? _roomView;

    private void Pin_Checked(object sender, RoutedEventArgs e) => Topmost = true;
    private void Pin_Unchecked(object sender, RoutedEventArgs e) => Topmost = false;

    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) =>
        {
            _roomView?.Dispose();
            _roomView = null;
            _loggerWindow?.Close();
        };
        // 启动后静默检查局域网更新（15 秒），发现更高版本才提示。
        Loaded += async (_, _) =>
        {
            if (!AppSettings.AutoCheckUpdate) return;
            var offer = await UpdateService.DiscoverAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
            if (offer is null) return;
            AppLog.Write($"启动静默检查发现新版本 {offer.VersionText}，提示强制更新。");
            new UpdateWindow(offer) { Owner = this }.ShowDialog();
        };
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        new SettingsWindow { Owner = this }.ShowDialog();
    }

    /// <summary>在当前窗口内显示聊天室页面。</summary>
    private void ShowRoom(ChatRoomView view)
    {
        _roomView?.Dispose();
        _roomView = view;
        view.LeaveRequested += () => { if (ReferenceEquals(_roomView, view)) LeaveRoom(); };
        RoomPage.Children.Clear();
        RoomPage.Children.Add(view);
        HomePage.Visibility = Visibility.Collapsed;
        RoomPage.Visibility = Visibility.Visible;
        Title = $"{view.RoomName} - 局域聊天室";
    }

    /// <summary>退出房间并回到主页面。</summary>
    private void LeaveRoom()
    {
        var view = _roomView;
        if (view is null) return;
        _roomView = null;
        RoomPage.Children.Clear();
        RoomPage.Visibility = Visibility.Collapsed;
        HomePage.Visibility = Visibility.Visible;
        Title = "局域聊天室";
        Topmost = false;
        view.Dispose();
        ActionHint.Text = "已退出聊天室，可重新加入或创建。";
        AppLog.Write("已返回主页面。");
    }

    private void LogToggle_Checked(object sender, RoutedEventArgs e)
    {
        AppLog.Write("运行状态：日志窗口已开启。");
        if (_loggerWindow is null)
        {
            _loggerWindow = new LoggerWindow();
            _loggerWindow.Closed += LoggerWindow_Closed;
        }
        if (!_loggerWindow.IsVisible) _loggerWindow.Show();
        _loggerWindow.Activate();
    }

    private void LogToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        AppLog.Write("运行状态：日志窗口已关闭。");
        var window = _loggerWindow;
        _loggerWindow = null;
        if (window is not null) window.Close();
    }

    private void LoggerWindow_Closed(object? sender, EventArgs e)
    {
        _loggerWindow = null;
        LogToggle.IsChecked = false;
    }

    private void UsernameButton_Click(object sender, RoutedEventArgs e)
    {
        PromptForUsername();
    }

    private bool EnsureUsername()
    {
        if (!string.IsNullOrWhiteSpace(_currentUsername)) return true;
        MessageBox.Show(this, "请设置一个用户名", "需要设置用户名", MessageBoxButton.OK, MessageBoxImage.Information);
        return PromptForUsername();
    }

    private bool PromptForUsername()
    {
        var dialog = new UsernameWindow { Owner = this, CurrentUsername = _currentUsername ?? string.Empty };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Username)) return false;
        _currentUsername = dialog.Username;
        UsernameButton.Content = _currentUsername;
        ActionHint.Text = $"当前用户名：{_currentUsername}";
        AppLog.Write($"用户名已设置为“{_currentUsername}”。");
        return true;
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        AppLog.Write("用户打开帮助窗口。");
        var help = new UserGuideWindow { Owner = this };
        help.ShowDialog();
    }

    private async void JoinRoom_Click(object sender, RoutedEventArgs e)
    {
        AppLog.Write("用户选择加入指定聊天室。");
        if (!EnsureUsername()) return;
        var dialog = new JoinRoomWindow(false) { Owner = this };
        if (dialog.ShowDialog() == true) await OpenJoinedRoomAsync(dialog);
    }

    private async void SearchRoom_Click(object sender, RoutedEventArgs e)
    {
        AppLog.Write("用户选择搜索局域网聊天室。");
        if (!EnsureUsername()) return;
        var dialog = new JoinRoomWindow(true) { Owner = this };
        if (dialog.ShowDialog() == true) await OpenJoinedRoomAsync(dialog);
    }

    private async Task OpenJoinedRoomAsync(JoinRoomWindow dialog)
    {
        try
        {
            await VerifyRoomCredentialsAsync(dialog);
            var room = new ChatRoomView(dialog.RoomName, dialog.Host, dialog.Port, dialog.Password,
                _currentUsername!);
            ShowRoom(room);
            ActionHint.Text = $"正在加入“{dialog.RoomName}”";
            AppLog.Write($"已加入聊天室“{dialog.RoomName}”。");
        }
        catch (InvalidDataException ex) when (ex.Message == "密码错误")
        {
            AppLog.Write($"加入聊天室“{dialog.RoomName}”失败：密码错误。");
            MessageBox.Show(this, "密码错误", "无法加入聊天室", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (InvalidDataException ex) when (IsRoomAccessNotice(ex.Message))
        {
            AppLog.Write($"加入聊天室“{dialog.RoomName}”被拒绝：{ex.Message}。");
            new RoomAccessNoticeWindow(ex.Message) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            AppLog.Write($"加入聊天室失败：{dialog.RoomName} · {ex}");
            MessageBox.Show(this, $"无法加入聊天室：{ex.Message}", "连接失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task VerifyRoomCredentialsAsync(JoinRoomWindow dialog)
    {
        AppLog.Write($"开始验证房间：{dialog.RoomName} @ {dialog.Host}:{dialog.Port}。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new TcpClient();
        await client.ConnectAsync(dialog.Host, dialog.Port, timeout.Token);
        var stream = client.GetStream();
        var channel = await WebSocketChannel.ClientHandshakeAsync(stream, timeout.Token);
        await channel.SendTextAsync(JsonSerializer.Serialize(new
        {
            Type = "validate",
            Room = dialog.RoomName,
            Password = dialog.Password,
            Sender = _currentUsername
        }), timeout.Token);
        var frame = await channel.ReadMessageAsync(timeout.Token);
        if (frame is null || !frame.Value.IsText || string.IsNullOrWhiteSpace(frame.Value.Text))
            throw new InvalidDataException("房间未返回加入确认，请检查房间名和网络连接。");
        using var response = JsonDocument.Parse(frame.Value.Text);
        var type = response.RootElement.TryGetProperty("Type", out var typeValue) ? typeValue.GetString() : null;
        if (type == "error")
        {
            var message = response.RootElement.TryGetProperty("Text", out var textValue) ? textValue.GetString() : null;
            throw new InvalidDataException(message ?? "无法加入聊天室。");
        }
        if (type != "joined") throw new InvalidDataException("未能确认加入聊天室，请检查房间信息。");
    }

    private static bool IsRoomAccessNotice(string message) =>
        message is "您已被踢出聊天室" or "您已被封禁" or "您的 IP 已被封禁";

    private void CreateRoom_Click(object sender, RoutedEventArgs e)
    {
        AppLog.Write("用户选择新建聊天室。");
        if (!EnsureUsername()) return;
        var setup = new RoomSetupWindow { Owner = this };
        if (setup.ShowDialog() != true) return;

        try
        {
            var room = new ChatRoomView(setup.RoomName, setup.Port, setup.Password, _currentUsername!, setup.IsPublic);
            ShowRoom(room);
            ActionHint.Text = $"聊天室“{setup.RoomName}”已创建 · 端口 {setup.Port}";
            AppLog.Write($"聊天室“{setup.RoomName}”创建成功。");
        }
        catch (Exception ex)
        {
            AppLog.Write($"创建聊天室失败：{setup.RoomName} · {ex}");
            MessageBox.Show(this, $"无法创建聊天室：{ex.Message}", "创建失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
