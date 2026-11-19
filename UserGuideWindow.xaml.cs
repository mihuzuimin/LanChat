using System.Windows;
using Microsoft.Win32;

namespace LanChat;

public partial class UserGuideWindow : Window
{
    public UserGuideWindow() => InitializeComponent();
    private void Pin_Checked(object sender, RoutedEventArgs e) => Topmost = true;
    private void Pin_Unchecked(object sender, RoutedEventArgs e) => Topmost = false;
    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ExportRustDeskSource_Click(object sender, RoutedEventArgs e) => ExportRustDeskFile(
        "rustdesk-1.5.3-corresponding-source.zip", "RustDesk 1.5.3 对应源码|rustdesk-1.5.3-corresponding-source.zip", RustDeskRuntime.ExportSource);

    private void ExportRustDeskLicense_Click(object sender, RoutedEventArgs e) => ExportRustDeskFile(
        "AGPL-3.0.txt", "GNU AGPL-3.0 许可证|AGPL-3.0.txt", RustDeskRuntime.ExportLicense);

    private void ExportRustDeskFile(string fileName, string filter, Action<string> exporter)
    {
        var dialog = new SaveFileDialog { FileName = fileName, Filter = filter, AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            exporter(dialog.FileName);
            MessageBox.Show(this, "文件已保存。", "RustDesk 开源资料", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppLog.Write($"导出 RustDesk 开源文件失败：{ex}");
            MessageBox.Show(this, $"保存失败：{ex.Message}", "RustDesk 开源资料", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
