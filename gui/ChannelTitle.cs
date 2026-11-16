using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DiscordSnapshot.Gui;

public sealed class ChannelTitle : TextBlock
{
    private static readonly FontFamily EmojiFont = new("Segoe UI Emoji");
    private static readonly Lazy<ZipArchive> EmojiAssets = new(() =>
        new ZipArchive(typeof(ChannelTitle).Assembly.GetManifestResourceStream("ArchiveWebUi/Twemoji.zip")!));
    private static readonly Dictionary<string, BitmapSource> EmojiImages = new(StringComparer.Ordinal);

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string),
        typeof(ChannelTitle), new PropertyMetadata("", (element, change) => ((ChannelTitle)element).SetTitle((string?)change.NewValue ?? "")));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    private void SetTitle(string title)
    {
        Inlines.Clear();
        AppendEmojiText(Inlines, title, FontSize);
    }

    internal static void AppendEmojiText(InlineCollection inlines, string title, double fontSize)
    {
        var plainStart = 0;
        var elements = StringInfo.GetTextElementEnumerator(title);
        while (elements.MoveNext())
        {
            var text = elements.GetTextElement();
            if (text.Contains('\uFE0E') || !Rune.TryGetRuneAt(text, 0, out var rune)) continue;
            var emojiVariation = text.Contains('\uFE0F') || text.Contains('\u20E3');
            var likelyEmoji = rune.Value is >= 0x1F000 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF or 0x231A or 0x231B or 0x2B50 or 0x2B55;
            if (!emojiVariation && !likelyEmoji && rune.Value is not (>= 0x2190 and <= 0x2BFF)) continue;
            var image = GetEmojiImage(text);
            if (image is null && !emojiVariation && !likelyEmoji) continue;

            var start = elements.ElementIndex;
            if (start > plainStart) inlines.Add(new Run(title[plainStart..start]));
            if (image is not null)
                inlines.Add(new InlineUIContainer(new Image
                    { Source = image, Width = fontSize + 1, Height = fontSize + 1, ToolTip = text })
                    { BaselineAlignment = BaselineAlignment.Center });
            else
                inlines.Add(new Run(text) { FontFamily = EmojiFont });
            plainStart = start + text.Length;
        }
        if (plainStart < title.Length) inlines.Add(new Run(title[plainStart..]));
    }

    private static BitmapSource? GetEmojiImage(string text)
    {
        var codePoints = new List<string>();
        foreach (var rune in text.EnumerateRunes()) codePoints.Add(rune.Value.ToString("x", CultureInfo.InvariantCulture));
        var name = string.Join('-', codePoints) + ".png";
        var entry = EmojiAssets.Value.GetEntry(name) ?? EmojiAssets.Value.GetEntry(name.Replace("-fe0f", "", StringComparison.Ordinal));
        if (entry is null) return null;
        if (EmojiImages.TryGetValue(entry.Name, out var cached)) return cached;

        using var stream = entry.Open();
        using var png = new MemoryStream((int)entry.Length);
        stream.CopyTo(png);
        png.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.StreamSource = png;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 36;
        bitmap.EndInit();
        bitmap.Freeze();
        EmojiImages.Add(entry.Name, bitmap);
        return bitmap;
    }
}
