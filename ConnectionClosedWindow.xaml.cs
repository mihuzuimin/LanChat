using System.Windows;
using System.Windows.Threading;

namespace LanChat;

public partial class ConnectionClosedWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _seconds = 5;

    public ConnectionClosedWindow()
    {
        InitializeComponent();
        _timer.Tick += (_, _) =>
        {
            _seconds--;
            if (_seconds <= 0) { _timer.Stop(); Close(); }
            else CountdownText.Text = $"窗口将在 {_seconds} 秒后自动关闭。";
        };
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => _timer.Start();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Pin_Checked(object sender, RoutedEventArgs e) => Topmost = true;
    private void Pin_Unchecked(object sender, RoutedEventArgs e) => Topmost = false;
    protected override void OnClosed(EventArgs e) { _timer.Stop(); base.OnClosed(e); }
}
