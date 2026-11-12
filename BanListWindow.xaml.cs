using System.Collections.ObjectModel;
using System.Windows;

namespace LanChat;

public partial class BanListWindow : Window
{
    private readonly Action<BanRecord> _unban;
    private readonly ObservableCollection<BanRecord> _records;

    internal BanListWindow(IEnumerable<BanRecord> records, Action<BanRecord> unban)
    {
        InitializeComponent();
        _unban = unban;
        _records = new ObservableCollection<BanRecord>(records);
        BansList.ItemsSource = _records;
        EmptyHint.Visibility = _records.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Pin_Checked(object sender, RoutedEventArgs e) => Topmost = true;
    private void Pin_Unchecked(object sender, RoutedEventArgs e) => Topmost = false;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void BansList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var selected = BansList.SelectedItem as BanRecord;
        UnbanUserButton.IsEnabled = selected?.Kind == "用户封禁";
        UnbanIpButton.IsEnabled = selected?.Kind == "IP 封禁";
    }

    private void UnbanUser_Click(object sender, RoutedEventArgs e) => UnbanSelected();
    private void UnbanIp_Click(object sender, RoutedEventArgs e) => UnbanSelected();

    private void UnbanSelected()
    {
        if (BansList.SelectedItem is not BanRecord record) return;
        _unban(record);
        _records.Remove(record);
        EmptyHint.Visibility = _records.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }
}
