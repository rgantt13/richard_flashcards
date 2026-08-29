using System.Text;
using System.Text.RegularExpressions;

namespace Flashcards.Domain.Cards;

/// <summary>A single fill-in-the-blank extracted from cloze markup.</summary>
/// <param name="Index">1-based blank number, in source order.</param>
/// <param name="Answer">The hidden text.</param>
/// <param name="Hint">Optional hint after a "::" separator.</param>
public readonly record struct ClozeBlank(int Index, string Answer, string? Hint);

/// <summary>
/// Parses Anki-style cloze markup: <c>The capital of France is {{Paris}}.</c>
/// A hint can follow a double colon: <c>{{Paris::a city}}</c>.
/// </summary>
public static partial class ClozeParser
{
    // Non-greedy so two blanks on one line do not merge into one.
    [GeneratedRegex(@"\{\{(?<body>.+?)\}\}", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex BlankPattern();

    public static bool HasBlanks(string? text) => !string.IsNullOrEmpty(text) && BlankPattern().IsMatch(text);

    public static IReadOnlyList<ClozeBlank> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var blanks = new List<ClozeBlank>();
        var index = 0;

        foreach (Match match in BlankPattern().Matches(text))
        {
            index++;
            var body = match.Groups["body"].Value;
            var separator = body.IndexOf("::", StringComparison.Ordinal);

            blanks.Add(separator >= 0
                ? new ClozeBlank(index, body[..separator].Trim(), body[(separator + 2)..].Trim())
                : new ClozeBlank(index, body.Trim(), null));
        }

        return blanks;
    }

    /// <summary>
    /// Renders the prompt: every blank becomes an underscore run, or "[hint]" when one was supplied.
    /// If <paramref name="revealIndex"/> is supplied, that one blank is filled in instead.
    /// </summary>
    public static string RenderPrompt(string? text, int? revealIndex = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var index = 0;

        return BlankPattern().Replace(text, match =>
        {
            index++;
            var body = match.Groups["body"].Value;
            var separator = body.IndexOf("::", StringComparison.Ordinal);
            var answer = separator >= 0 ? body[..separator].Trim() : body.Trim();
            var hint = separator >= 0 ? body[(separator + 2)..].Trim() : null;

            if (revealIndex == index)
            {
                return answer;
            }

            return string.IsNullOrEmpty(hint)
                ? new string('_', Math.Clamp(answer.Length, 3, 16))
                : $"[{hint}]";
        });
    }

    /// <summary>Renders the fully solved text with every blank filled in.</summary>
    public static string RenderSolution(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return BlankPattern().Replace(text, match =>
        {
            var body = match.Groups["body"].Value;
            var separator = body.IndexOf("::", StringComparison.Ordinal);
            return separator >= 0 ? body[..separator].Trim() : body.Trim();
        });
    }

    /// <summary>Wraps a selection in cloze markers — used by the "Make blank" button in the editor.</summary>
    public static string Wrap(string text, int selectionStart, int selectionLength, string? hint = null)
    {
        if (selectionLength <= 0 || selectionStart < 0 || selectionStart + selectionLength > text.Length)
        {
            return text;
        }

        var selected = text.Substring(selectionStart, selectionLength);
        var replacement = string.IsNullOrWhiteSpace(hint) ? $"{{{{{selected}}}}}" : $"{{{{{selected}::{hint.Trim()}}}}}";

        return new StringBuilder(text)
            .Remove(selectionStart, selectionLength)
            .Insert(selectionStart, replacement)
            .ToString();
    }
}
