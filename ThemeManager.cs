using System.Windows;

namespace LanChat;

/// <summary>浅色 / 深色主题切换：替换 Application 资源中的调色板，DynamicResource 会自动刷新。</summary>
internal static class ThemeManager
{
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
    }
}
