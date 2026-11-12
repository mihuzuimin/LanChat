using System.Windows;

namespace LanChat;

public partial class UserGuideWindow : Window
{
    public UserGuideWindow() => InitializeComponent();
    private void Pin_Checked(object sender, RoutedEventArgs e) => Topmost = true;
    private void Pin_Unchecked(object sender, RoutedEventArgs e) => Topmost = false;
    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
