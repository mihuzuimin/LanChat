using System.Windows;

namespace LanChat;

public partial class UsernameWindow : Window
{
    public string Username => UsernameInput.Text.Trim();
    public string CurrentUsername { get; set; } = string.Empty;

    private void Pin_Checked(object sender, RoutedEventArgs e) => Topmost = true;
    private void Pin_Unchecked(object sender, RoutedEventArgs e) => Topmost = false;
    private void CloseWindow_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    public UsernameWindow()
    {
        InitializeComponent();
        UsernameInput.Text = CurrentUsername;
        Loaded += (_, _) => UsernameInput.Focus();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Username))
        {
            ValidationText.Text = "请输入用户名后再确定。";
            ValidationText.Foreground = System.Windows.Media.Brushes.Firebrick;
            UsernameInput.Focus();
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
