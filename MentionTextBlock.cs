using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace LanChat;

/// <summary>把消息文本里的 @用户名 渲染成蓝色，其余保持普通文本。</summary>
public sealed class MentionTextBlock : TextBlock
{
    private static readonly Regex MentionPattern = new(@"@[\p{L}\p{N}_\-\.]+", RegexOptions.Compiled);
    private static readonly Brush DefaultMentionBrush = CreateBrush();
    private static readonly HashSet<string> KnownNames = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object KnownLock = new();

    /// <summary>只有房间内存在的用户名才高亮。</summary>
    public static void SetKnownNames(IEnumerable<string> names)
    {
        lock (KnownLock)
        {
            KnownNames.Clear();
            foreach (var name in names)
                if (!string.IsNullOrWhiteSpace(name)) KnownNames.Add(name);
        }
    }

    private static bool IsKnownName(string name)
    {
        lock (KnownLock) return KnownNames.Contains(name);
    }

    public static readonly DependencyProperty SourceTextProperty = DependencyProperty.Register(
        nameof(SourceText), typeof(string), typeof(MentionTextBlock),
        new PropertyMetadata(string.Empty, OnSourceTextChanged));

    public string SourceText
    {
        get => (string)GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    public Brush MentionBrush { get; set; } = DefaultMentionBrush;

    private static Brush CreateBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF));
        brush.Freeze();
        return brush;
    }

    private static void OnSourceTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MentionTextBlock)d).Rebuild();

    private void Rebuild()
    {
        Inlines.Clear();
        var text = SourceText;
        if (string.IsNullOrEmpty(text)) return;
        var position = 0;
        foreach (Match match in MentionPattern.Matches(text))
        {
            if (match.Index > position) Inlines.Add(new Run(text[position..match.Index]));
            var token = match.Value;
            var name = token[1..];
            if (IsKnownName(name)) Inlines.Add(new Run(token) { Foreground = MentionBrush });
            else Inlines.Add(new Run(token));
            position = match.Index + match.Length;
        }
        if (position < text.Length) Inlines.Add(new Run(text[position..]));
    }
}
