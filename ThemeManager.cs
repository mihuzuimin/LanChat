using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LanChat;

/// <summary>浅色 / 深色主题切换：替换 Application 资源中的调色板，DynamicResource 会自动刷新。</summary>
internal static class ThemeManager
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

    public static bool IsDark { get; private set; }

    public static void Apply(bool dark)
    {
        var app = Application.Current;
        if (app is null) return;
        var key = dark ? "DarkPalette" : "LightPalette";
        if (!app.Resources.Contains(key) || app.Resources[key] is not ResourceDictionary palette) return;
        foreach (var paletteKey in palette.Keys)
        {
            app.Resources[paletteKey] = palette[paletteKey];
        }
        IsDark = dark;
        foreach (Window window in app.Windows) ApplyWindowChrome(window);
    }

    /// <summary>让系统标题栏跟随主题，避免深色界面上出现白色标题条。</summary>
    public static void ApplyWindowChrome(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var enabled = IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
