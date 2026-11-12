using System.Windows;

namespace LanChat;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        VersionText.Text = $"当前版本 {UpdateService.CurrentVersionText} · 检查耗时 5 秒";
        AutoCheckBox.IsChecked = AppSettings.AutoCheckUpdate;
        DarkModeBox.IsChecked = AppSettings.DarkMode;
    }

    private void DarkMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var dark = DarkModeBox.IsChecked == true;
        ThemeManager.Apply(dark);
        AppSettings.DarkMode = dark;
        StatusText.Text = dark ? "已切换到深色模式。" : "已切换到浅色模式。";
    }

    private void Pin_Checked(object sender, RoutedEventArgs e) => Topmost = true;

    private void Pin_Unchecked(object sender, RoutedEventArgs e) => Topmost = false;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.AutoCheckUpdate = AutoCheckBox.IsChecked == true;
        AppSettings.SaveNow();
        StatusText.Text = "设置已写入。";
        AppLog.Write($"设置已保存：启动时自动检查更新 = {AppSettings.AutoCheckUpdate}。");
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        StatusText.Text = "正在局域网内搜索新版本（5 秒）…";
        UpdateOffer? offer;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            offer = await UpdateService.DiscoverAsync(TimeSpan.FromSeconds(5), timeout.Token);
        }
        CheckButton.IsEnabled = true;
        if (offer is null)
        {
            StatusText.Text = $"已是最新版本（{UpdateService.CurrentVersionText}）。";
            return;
        }
        StatusText.Text = $"发现新版本 {offer.VersionText}（{offer.Address}）。";
        new UpdateWindow(offer) { Owner = this }.ShowDialog();
    }
}
