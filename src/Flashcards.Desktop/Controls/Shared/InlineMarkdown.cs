using System.Text.RegularExpressions;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Flashcards.Desktop.Controls.Shared;

/// <summary>
/// A deliberately small Markdown-to-<see cref="Inline"/> renderer covering the subset a flashcard
/// actually uses: bold, italic, inline code, links-as-text, bullet lists, numbered lists and
/// headings. Block-level structure comes back as separate lines rather than nested containers.
/// <para>
/// Why not Markdown.Avalonia: this is about eighty lines, has no version to keep in step with
/// Avalonia, and renders into the same TextBlock the plain-text path uses, so styling stays
/// consistent. Swap it out the day you need tables.
/// </para>
/// </summary>
public static partial class InlineMarkdown
{
    [GeneratedRegex(@"(\*\*(?<b>[^*]+)\*\*)|(__(?<b2>[^_]+)__)|(\*(?<i>[^*]+)\*)|(_(?<i2>[^_]+)_)|(`(?<c>[^`]+)`)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SpanPattern();

    /// <summary>Renders one document into a flat list of inlines, newlines included.</summary>
    public static InlineCollection Render(string? markdown, double fontSize)
    {
        var inlines = new InlineCollection();

        if (string.IsNullOrWhiteSpace(markdown))
        {
            return inlines;
        }

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];

            if (index > 0)
            {
                inlines.Add(new LineBreak());
            }

            var trimmed = line.TrimStart();

            // Headings: "### Text"
            var hashes = trimmed.TakeWhile(c => c == '#').Count();

            if (hashes is > 0 and <= 4 && trimmed.Length > hashes && trimmed[hashes] == ' ')
            {
                AppendSpans(inlines, trimmed[(hashes + 1)..], fontSize + (5 - hashes) * 1.5, FontWeight.SemiBold);
                continue;
            }

            // Bullets: "- text" / "* text"
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                inlines.Add(new Run("  •  ") { FontWeight = FontWeight.Bold });
                AppendSpans(inlines, trimmed[2..], fontSize, FontWeight.Normal);
                continue;
            }

            // Numbered: "1. text"
            var numbered = NumberedPattern().Match(trimmed);

            if (numbered.Success)
            {
                inlines.Add(new Run($"  {numbered.Groups["n"].Value}.  ") { FontWeight = FontWeight.Bold });
                AppendSpans(inlines, trimmed[numbered.Length..], fontSize, FontWeight.Normal);
                continue;
            }

            // Blockquote: "> text"
            if (trimmed.StartsWith("> ", StringComparison.Ordinal))
            {
                inlines.Add(new Run("  │  ") { Foreground = Brushes.Gray });
                AppendSpans(inlines, trimmed[2..], fontSize, FontWeight.Normal, italicAll: true);
                continue;
            }

            AppendSpans(inlines, line, fontSize, FontWeight.Normal);
        }

        return inlines;
    }

    [GeneratedRegex(@"^(?<n>\d+)\.\s+", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedPattern();

    private static void AppendSpans(InlineCollection inlines, string text, double fontSize, FontWeight weight, bool italicAll = false)
    {
        var position = 0;

        foreach (Match match in SpanPattern().Matches(text))
        {
            if (match.Index > position)
            {
                inlines.Add(Plain(text[position..match.Index], fontSize, weight, italicAll));
            }

            if (match.Groups["b"].Success || match.Groups["b2"].Success)
            {
                var value = match.Groups["b"].Success ? match.Groups["b"].Value : match.Groups["b2"].Value;
                inlines.Add(new Run(value) { FontWeight = FontWeight.Bold, FontSize = fontSize });
            }
            else if (match.Groups["i"].Success || match.Groups["i2"].Success)
            {
                var value = match.Groups["i"].Success ? match.Groups["i"].Value : match.Groups["i2"].Value;
                inlines.Add(new Run(value) { FontStyle = FontStyle.Italic, FontSize = fontSize, FontWeight = weight });
            }
            else if (match.Groups["c"].Success)
            {
                inlines.Add(new Run(match.Groups["c"].Value)
                {
                    FontFamily = CodeTheme.MonoFont,
                    FontSize = fontSize - 1,
                    Foreground = CodeTheme.InlineCode,
                });
            }

            position = match.Index + match.Length;
        }

        if (position < text.Length)
        {
            inlines.Add(Plain(text[position..], fontSize, weight, italicAll));
        }
    }

    private static Run Plain(string text, double fontSize, FontWeight weight, bool italic) => new(text)
    {
        FontSize = fontSize,
        FontWeight = weight,
        FontStyle = italic ? FontStyle.Italic : FontStyle.Normal,
    };
}
