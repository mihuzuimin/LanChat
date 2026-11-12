using System.Windows;

namespace LanChat;

public partial class RoomAccessNoticeWindow : Window
{
    public RoomAccessNoticeWindow(string message)
    {
        InitializeComponent();
        NoticeText.Text = message;
    }

    private void Pin_Checked(object sender, RoutedEventArgs e) => Topmost = true;
    private void Pin_Unchecked(object sender, RoutedEventArgs e) => Topmost = false;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
