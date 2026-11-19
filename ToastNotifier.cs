using System.Drawing;
using System.Windows.Forms;

namespace LanChat;

/// <summary>被 @ 时通过 Windows 通知区域弹出提示。</summary>
internal static class ToastNotifier
{
    private static NotifyIcon? _icon;
    private static System.Threading.Timer? _hideTimer;
    private static readonly object Sync = new();

    public static void ShowMention(string sender)
    {
        try
        {
            lock (Sync)
            {
                EnsureIcon();
                _icon!.BalloonTipIcon = ToolTipIcon.Info;
                _icon.BalloonTipTitle = "LanChat";
                _icon.BalloonTipText = $"您被 {sender} 艾特了";
                _icon.ShowBalloonTip(6000);
                _hideTimer?.Dispose();
                _hideTimer = new System.Threading.Timer(_ => HideIcon(), null, TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"弹出被艾特通知失败：{ex.Message}");
        }
    }

    private static void EnsureIcon()
    {
        if (_icon is not null) return;
        var icon = SystemIcons.Information;
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path)) icon = Icon.ExtractAssociatedIcon(path) ?? icon;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.FileNotFoundException) { }
        _icon = new NotifyIcon
        {
            Icon = icon,
            Text = "LanChat",
            Visible = true
        };
    }

    private static void HideIcon()
    {
        lock (Sync)
        {
            _icon?.Dispose();
            _icon = null;
            _hideTimer?.Dispose();
            _hideTimer = null;
        }
    }
}
