using System.Windows;

namespace LanChat;

public partial class UpdateWindow : Window
{
    private readonly UpdateOffer _offer;
    private UpdateManifest? _manifest;
    private bool _updating;

    public UpdateWindow(UpdateOffer offer)
    {
        InitializeComponent();
        _offer = offer;
        LatestText.Text = $"新版本 {offer.VersionText}";
        CurrentText.Text = $"当前版本 {UpdateService.CurrentVersionText} · 来源 {offer.Address}:{UpdateService.UpdatePort}";
        Closed += (_, _) => { if (!_updating) AppLog.Write("用户关闭了更新提示。"); };
        Loaded += async (_, _) => await PrepareAsync();
    }

    /// <summary>拉取 version.json 清单；forceUpdate=true 时立即强制自动更新，不提供跳过选项。</summary>
    private async Task PrepareAsync()
    {
        StatusText.Text = "正在读取更新说明…";
        _manifest = await UpdateService.FetchManifestAsync(_offer, CancellationToken.None);
        if (_manifest is null)
        {
            StatusText.Text = "未获取到更新说明，可直接下载更新包。";
            return;
        }
        if (!string.IsNullOrWhiteSpace(_manifest.Notes))
        {
            NotesText.Text = _manifest.Notes;
            NotesText.Visibility = Visibility.Visible;
        }
        CurrentText.Text += $" · {_manifest.Size / 1024.0 / 1024.0:F1} MB";

        if (_manifest.ForceUpdate)
        {
            // 强制更新：隐藏跳过/退出选项，直接自动下载安装。
            ExitButton.Visibility = Visibility.Collapsed;
            UpdateButton.Visibility = Visibility.Collapsed;
            StatusText.Text = "该版本为强制更新，正在自动下载并安装…";
            AppLog.Write($"检测到强制更新 {_manifest.VersionText}，开始自动更新。");
            await RunUpdateAsync();
        }
        else
        {
            StatusText.Text = "将自动下载并重启完成更新。";
        }
    }

    private async void Update_Click(object sender, RoutedEventArgs e) => await RunUpdateAsync();

    private async Task RunUpdateAsync()
    {
        if (_updating) return;
        _updating = true;
        UpdateButton.IsEnabled = false;
        ExitButton.IsEnabled = false;
        StatusText.Text = "正在下载更新包…";
        try
        {
            var progress = new Progress<double>(value =>
            {
                DownloadProgress.Value = value;
                StatusText.Text = $"正在下载更新包… {value:F0}%";
            });
            var package = await UpdateService.DownloadAsync(_offer, _manifest, progress, CancellationToken.None);
            DownloadProgress.Value = 100;
            StatusText.Text = "校验通过，正在重启并安装…";
            await Task.Delay(400);
            UpdateService.ApplyAndRestart(package);
        }
        catch (Exception ex)
        {
            _updating = false;
            UpdateButton.IsEnabled = true;
            ExitButton.IsEnabled = true;
            ExitButton.Visibility = Visibility.Visible;
            StatusText.Text = $"更新失败：{ex.Message}";
            AppLog.Write($"更新失败：{ex}");
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
