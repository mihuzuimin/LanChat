using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace LanChat;

public partial class LoggerWindow : Window
{
    public ObservableCollection<string> Entries { get; } = new();

    public LoggerWindow()
    {
        InitializeComponent();
        DataContext = this;
        AppLog.EntryAdded += OnEntryAdded;
        foreach (var entry in AppLog.GetSnapshot()) Entries.Add(entry);
        if (EntriesList.Items.Count > 0) EntriesList.ScrollIntoView(EntriesList.Items[^1]);
    }

    private void OnEntryAdded(string entry)
    {
        Dispatcher.InvokeAsync(() =>
        {
            Entries.Add(entry);
            if (Entries.Count > 1000) Entries.RemoveAt(0);
            if (EntriesList.Items.Count > 0) EntriesList.ScrollIntoView(EntriesList.Items[^1]);
        });
    }

    private void Pin_Checked(object sender, RoutedEventArgs e) => Topmost = true;
    private void Pin_Unchecked(object sender, RoutedEventArgs e) => Topmost = false;
    private void Window_Closed(object? sender, EventArgs e) => AppLog.EntryAdded -= OnEntryAdded;

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存程序运行日志",
            FileName = $"LanChat-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            DefaultExt = ".txt",
            AddExtension = true,
            Filter = "文本文件 (*.txt)|*.txt",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, string.Join(Environment.NewLine, Entries), new UTF8Encoding(true));
            AppLog.Write($"用户手动导出日志到：{dialog.FileName}。");
            MessageBox.Show(this, $"日志已保存到：{dialog.FileName}", "保存完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"无法保存日志：{ex.Message}", "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
