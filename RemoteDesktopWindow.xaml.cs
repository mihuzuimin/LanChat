using System.Windows;
using System.IO;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Threading;
using System.Collections.Generic;

namespace LanChat;

public partial class RemoteDesktopWindow : Window
{
    private readonly Action<RemoteControlInput> _sendInput;
    private readonly Action _closed;
    private readonly DispatcherTimer _mouseMoveTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private bool _closeNotified;
    private bool _keyboardCaptureEnabled = true;
    private int _frameGeneration;
    private readonly HashSet<int> _pressedKeys = [];
    private readonly HashSet<int> _pressedButtons = [];
    private int _pendingX;
    private int _pendingY;
    private bool _mousePositionDirty;

    public RemoteDesktopWindow(string targetName, Action<RemoteControlInput> sendInput, Action closed)
    {
        InitializeComponent();
        TitleText.Text = $"正在控制：{targetName}";
        _sendInput = sendInput;
        _closed = closed;
        _mouseMoveTimer.Tick += (_, _) =>
        {
            if (!_mousePositionDirty)
            {
                _mouseMoveTimer.Stop();
                return;
            }
            _mousePositionDirty = false;
            _sendInput(new RemoteControlInput("mouse-move", _pendingX, _pendingY, 0, 0, 0, false));
        };
        Loaded += (_, _) => ScreenImage.Focus();
    }

    public void SetFrame(byte[] jpeg)
    {
        var generation = Interlocked.Increment(ref _frameGeneration);
        _ = Task.Run(() => DecodeFrame(jpeg)).ContinueWith(task =>
        {
            if (task.IsFaulted)
            {
                AppLog.Write($"远程桌面帧解码失败：{task.Exception?.GetBaseException().Message}");
                return;
            }
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (generation != Volatile.Read(ref _frameGeneration) || !IsLoaded) return;
                ScreenImage.Source = task.Result;
                WaitText.Visibility = Visibility.Collapsed;
            });
        }, TaskScheduler.Default);
    }

    private static BitmapSource DecodeFrame(byte[] jpeg)
    {
        using var stream = new MemoryStream(jpeg, false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void ScreenImage_MouseMove(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(ScreenImage);
        if (_pressedButtons.Count > 0 && (point.X < 0 || point.Y < 0 || point.X > ScreenImage.ActualWidth || point.Y > ScreenImage.ActualHeight))
        {
            ReleasePressedMouseButtons();
            if (ScreenImage.IsMouseCaptured) ScreenImage.ReleaseMouseCapture();
            return;
        }
        UpdatePointerPosition(point);
    }

    private void ScreenImage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        ScreenImage.Focus();
        _keyboardCaptureEnabled = true;
        InputCaptureStatus.Text = "键盘已锁定到远端 · Ctrl+Alt 释放键盘 · 窗口失焦时自动释放";
        var (x, y) = UpdatePointerPosition(e.GetPosition(ScreenImage));
        var button = MapButton(e.ChangedButton);
        if (button >= 0 && _pressedButtons.Add(button))
        {
            ScreenImage.CaptureMouse();
            _sendInput(new RemoteControlInput("mouse-down", x, y, button, 0, 0, true));
        }
        e.Handled = true;
    }

    private void ScreenImage_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var (x, y) = UpdatePointerPosition(e.GetPosition(ScreenImage));
        var button = MapButton(e.ChangedButton);
        if (button >= 0 && _pressedButtons.Remove(button))
            _sendInput(new RemoteControlInput("mouse-up", x, y, button, 0, 0, false));
        if (_pressedButtons.Count == 0 && ScreenImage.IsMouseCaptured) ScreenImage.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void ScreenImage_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var (x, y) = UpdatePointerPosition(e.GetPosition(ScreenImage));
        _sendInput(new RemoteControlInput("mouse-wheel", x, y, 0, e.Delta, 0, false));
        e.Handled = true;
    }

    private (int X, int Y) UpdatePointerPosition(Point point)
    {
        var (x, y) = GetNormalizedPoint(point);
        if (x != _pendingX || y != _pendingY)
        {
            _pendingX = x;
            _pendingY = y;
            _mousePositionDirty = true;
            if (!_mouseMoveTimer.IsEnabled) _mouseMoveTimer.Start();
        }
        return (_pendingX, _pendingY);
    }

    private void ScreenImage_LostMouseCapture(object sender, MouseEventArgs e)
    {
        ReleasePressedMouseButtons();
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsWithinScreenImage(e.OriginalSource as DependencyObject)) return;
        ReleasePressedInputs();
        _keyboardCaptureEnabled = false;
        InputCaptureStatus.Text = "本地窗口控件已获得输入 · 点击远程画面可重新锁定键盘";
    }

    private bool IsWithinScreenImage(DependencyObject? source)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, ScreenImage)) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized) return;
        ReleasePressedInputs();
        _keyboardCaptureEnabled = false;
        InputCaptureStatus.Text = "窗口已最小化，远端键鼠输入已释放";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.ImeProcessed or Key.DeadCharProcessed) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;
        if (!_keyboardCaptureEnabled) { e.Handled = true; return; }

        // Ctrl+Alt is the explicit escape chord. Release any keys already held
        // remotely before detaching so neither modifier can remain stuck.
        var ctrlDown = vk is 0x11 or 0xA2 or 0xA3 || _pressedKeys.Overlaps([0x11, 0xA2, 0xA3]);
        var altDown = vk is 0x12 or 0xA4 or 0xA5 || _pressedKeys.Overlaps([0x12, 0xA4, 0xA5]);
        if (ctrlDown && altDown)
        {
            ReleasePressedKeys();
            _keyboardCaptureEnabled = false;
            InputCaptureStatus.Text = "键盘输入已释放 · 点击远程画面可重新锁定";
            e.Handled = true;
            return;
        }
        if (_pressedKeys.Add(vk)) _sendInput(new RemoteControlInput("key", 0, 0, 0, 0, vk, true));
        e.Handled = true;
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;
        if (_pressedKeys.Remove(vk)) _sendInput(new RemoteControlInput("key", 0, 0, 0, 0, vk, false));
        e.Handled = true;
    }

    private (int X, int Y) GetNormalizedPoint(Point point)
    {
        if (ScreenImage.Source is not BitmapSource source || ScreenImage.ActualWidth <= 0 || ScreenImage.ActualHeight <= 0) return (0, 0);
        var scale = Math.Min(ScreenImage.ActualWidth / source.PixelWidth, ScreenImage.ActualHeight / source.PixelHeight);
        var drawnWidth = source.PixelWidth * scale;
        var drawnHeight = source.PixelHeight * scale;
        var offsetX = (ScreenImage.ActualWidth - drawnWidth) / 2;
        var offsetY = (ScreenImage.ActualHeight - drawnHeight) / 2;
        var x = Math.Clamp((point.X - offsetX) / Math.Max(1, drawnWidth), 0, 1);
        var y = Math.Clamp((point.Y - offsetY) / Math.Max(1, drawnHeight), 0, 1);
        return ((int)(x * 65535), (int)(y * 65535));
    }

    private static int MapButton(MouseButton button) => button switch
    {
        MouseButton.Left => 0,
        MouseButton.Right => 1,
        MouseButton.Middle => 2,
        _ => -1
    };

    private void End_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        ReleasePressedInputs();
        _keyboardCaptureEnabled = false;
        InputCaptureStatus.Text = "窗口已失焦，键盘输入已释放 · 点击远程画面可重新锁定";
    }

    private void ReleasePressedInputs()
    {
        ReleasePressedKeys();
        ReleasePressedMouseButtons();
        if (ScreenImage.IsMouseCaptured) ScreenImage.ReleaseMouseCapture();
    }

    private void ReleasePressedMouseButtons()
    {
        foreach (var button in _pressedButtons.ToArray())
            _sendInput(new RemoteControlInput("mouse-up", _pendingX, _pendingY, button, 0, 0, false));
        _pressedButtons.Clear();
    }

    private void ReleasePressedKeys()
    {
        foreach (var vk in _pressedKeys)
            _sendInput(new RemoteControlInput("key", 0, 0, 0, 0, vk, false));
        _pressedKeys.Clear();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _mouseMoveTimer.Stop();
        ReleasePressedInputs();
        if (_closeNotified) return;
        _closeNotified = true;
        _closed();
    }
}
