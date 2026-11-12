using System.Net;
using System.Net.Sockets;
using System.Windows;

namespace LanChat;

public partial class RoomSetupWindow : Window
{
    public string RoomName => RoomNameInput.Text.Trim();
    public int Port { get; private set; }
    public string Password => PasswordInput.Password;
    public bool IsPublic => PublicOption.IsChecked == true;

    public RoomSetupWindow() => InitializeComponent();

    private void Pin_Checked(object sender, RoutedEventArgs e) => Topmost = true;
    private void Pin_Unchecked(object sender, RoutedEventArgs e) => Topmost = false;
    private void CloseWindow_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        if (string.IsNullOrWhiteSpace(RoomName)) { ErrorText.Text = "请输入房间名。"; RoomNameInput.Focus(); return; }
        if (!int.TryParse(PortInput.Text.Trim(), out var port) || port is < 1024 or > 65535)
        { ErrorText.Text = "端口必须是 1024 到 65535 之间的整数。"; PortInput.Focus(); return; }

        try
        {
            var probe = new TcpListener(IPAddress.Any, port);
            probe.Start();
            probe.Stop();
        }
        catch (SocketException)
        { ErrorText.Text = "该端口当前不可用，请输入其他端口。"; PortInput.Focus(); return; }

        Port = port;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
