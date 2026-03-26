using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using System.Text.RegularExpressions;

namespace carton.GUI.Helpers;

public class EmojiTextHelper
{
    // Match common emojis, symbols, and flag sequences
    private static readonly Regex EmojiRegex = new Regex(
        @"(\uD83C[\uDDE6-\uDDFF]){2}|[\uD83C-\uD83E][\uDC00-\uDFFF]|[\u2600-\u27BF]\uFE0F?", 
        RegexOptions.Compiled);

    public static readonly AttachedProperty<string> TextProperty =
        AvaloniaProperty.RegisterAttached<EmojiTextHelper, TextBlock, string>("Text");

    public static readonly AttachedProperty<string> PrefixProperty =
        AvaloniaProperty.RegisterAttached<EmojiTextHelper, TextBlock, string>("Prefix");

    static EmojiTextHelper()
    {
        TextProperty.Changed.AddClassHandler<TextBlock>(OnTextChanged);
        PrefixProperty.Changed.AddClassHandler<TextBlock>(OnTextChanged);
    }

    public static string GetText(AvaloniaObject element) => element.GetValue(TextProperty);
    public static void SetText(AvaloniaObject element, string value) => element.SetValue(TextProperty, value);

    public static string GetPrefix(AvaloniaObject element) => element.GetValue(PrefixProperty);
    public static void SetPrefix(AvaloniaObject element, string value) => element.SetValue(PrefixProperty, value);

    private static void OnTextChanged(TextBlock textBlock, AvaloniaPropertyChangedEventArgs e)
    {
        var text = GetText(textBlock);
        var prefix = GetPrefix(textBlock);

        string fullText = (prefix ?? "") + (text ?? "");

        // Clear the plain Text value before rebuilding content so Text and Inlines
        // do not render together during prefix/text update races.
        textBlock.Text = string.Empty;

        if (textBlock.Inlines != null)
        {
            textBlock.Inlines.Clear();
        }
        else
        {
            textBlock.Inlines = new InlineCollection();
        }

        if (string.IsNullOrEmpty(fullText))
        {
            textBlock.Text = string.Empty;
            return;
        }

        var matches = EmojiRegex.Matches(fullText);
        if (matches.Count == 0)
        {
            textBlock.Text = fullText;
            return;
        }

        // Use the bundled Twemoji font or system emoji font
        var emojiFont = new FontFamily("avares://carton/Assets/Fonts#Twemoji COLRv0");

        int lastIndex = 0;
        foreach (Match match in matches)
        {
            if (match.Index > lastIndex)
            {
                textBlock.Inlines.Add(new Run { Text = fullText.Substring(lastIndex, match.Index - lastIndex) });
            }

            // Force FontWeight.Normal for emojis to preserve color
            textBlock.Inlines.Add(new Run
            {
                Text = match.Value,
                FontFamily = emojiFont,
                FontWeight = FontWeight.Normal,
                FontStyle = FontStyle.Normal
            });

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < fullText.Length)
        {
            textBlock.Inlines.Add(new Run { Text = fullText.Substring(lastIndex) });
        }
    }
}
