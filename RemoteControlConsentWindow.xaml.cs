using System.Windows;

namespace LanChat;

public partial class RemoteControlConsentWindow : Window
{
    public RemoteControlConsentWindow(string requesterName, string requesterIp)
    {
        InitializeComponent();
        RequesterText.Text = $"{requesterName}（{requesterIp}）";
    }

    private void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Decline_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
