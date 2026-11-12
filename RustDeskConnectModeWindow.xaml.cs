using System.Windows;

namespace LanChat;

public enum RustDeskConnectMode
{
    None,
    DirectIp,
    RustDeskId
}

public partial class RustDeskConnectModeWindow : Window
{
    public RustDeskConnectModeWindow(string targetName, string targetIp, string? rustDeskId)
    {
        InitializeComponent();
        TargetText.Text = $"对方：{targetName} · {targetIp}";
        var hasId = !string.IsNullOrWhiteSpace(rustDeskId);
        if (hasId)
        {
            IdHintText.Text = $"对方 RustDesk ID：{rustDeskId}";
            IdHintText.Visibility = Visibility.Visible;
        }
        else
        {
            IdButton.IsEnabled = false;
            IdHintText.Text = "对方暂未提供 RustDesk ID（对方需运行 RustDesk 并联网），只能使用 IP 直连。";
            IdHintText.Visibility = Visibility.Visible;
        }
    }

    public RustDeskConnectMode Mode { get; private set; } = RustDeskConnectMode.None;

    private void DirectIp_Click(object sender, RoutedEventArgs e)
    {
        Mode = RustDeskConnectMode.DirectIp;
        DialogResult = true;
    }

    private void Id_Click(object sender, RoutedEventArgs e)
    {
        Mode = RustDeskConnectMode.RustDeskId;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Mode = RustDeskConnectMode.None;
        DialogResult = false;
    }
}
