using System.Text.RegularExpressions;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Flashcards.Desktop.Controls.Shared;

internal static class CodeTheme
{
    public static readonly FontFamily MonoFont = new("Cascadia Code,Cascadia Mono,Consolas,JetBrains Mono,Menlo,monospace");

    public static readonly IBrush Default = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
    public static readonly IBrush Keyword = new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6));
    public static readonly IBrush Type = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
    public static readonly IBrush StringLiteral = new SolidColorBrush(Color.FromRgb(0xCE, 0x91, 0x78));
    public static readonly IBrush Number = new SolidColorBrush(Color.FromRgb(0xB5, 0xCE, 0xA8));
    public static readonly IBrush Comment = new SolidColorBrush(Color.FromRgb(0x6A, 0x99, 0x55));
    public static readonly IBrush InlineCode = new SolidColorBrush(Color.FromRgb(0xE0, 0x93, 0xC7));
}

/// <summary>
/// A regex-based syntax highlighter for the handful of languages you are likely to put on a card.
/// <para>
/// It tokenises in one pass with a single alternation, so comments and strings win over keywords
/// that happen to appear inside them — the mistake every naive "replace each keyword" highlighter
/// makes. It is not a parser and does not pretend to be; if you outgrow it, AvaloniaEdit's
/// TextMate grammars are the drop-in replacement.
/// </para>
/// </summary>
public static partial class CodeHighlighter
{
    private static readonly Dictionary<string, HashSet<string>> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["csharp"] =
        [
            "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
            "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event",
            "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "get", "goto", "if",
            "implicit", "in", "init", "int", "interface", "internal", "is", "lock", "long", "namespace", "new",
            "null", "object", "operator", "out", "override", "params", "private", "protected", "public", "readonly",
            "record", "ref", "required", "return", "sbyte", "sealed", "set", "short", "sizeof", "stackalloc",
            "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong",
            "unchecked", "unsafe", "ushort", "using", "var", "virtual", "void", "volatile", "when", "where",
            "while", "with", "yield",
        ],
        ["sql"] =
        [
            "add", "all", "alter", "and", "as", "asc", "begin", "between", "by", "case", "cast", "check", "collate",
            "column", "commit", "conflict", "constraint", "create", "cross", "delete", "desc", "distinct", "do",
            "drop", "else", "end", "escape", "except", "exists", "foreign", "from", "full", "group", "having", "if",
            "in", "index", "inner", "insert", "intersect", "into", "is", "join", "key", "left", "like", "limit",
            "not", "null", "nulls", "offset", "on", "or", "order", "outer", "over", "partition", "pragma", "primary",
            "references", "returning", "right", "rollback", "select", "set", "table", "then", "transaction",
            "trigger", "union", "unique", "update", "using", "values", "view", "when", "where", "window", "with",
        ],
        ["javascript"] =
        [
            "async", "await", "break", "case", "catch", "class", "const", "continue", "default", "delete", "do",
            "else", "export", "extends", "false", "finally", "for", "from", "function", "if", "import", "in",
            "instanceof", "let", "new", "null", "of", "return", "static", "super", "switch", "this", "throw",
            "true", "try", "typeof", "undefined", "var", "void", "while", "yield",
        ],
        ["python"] =
        [
            "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif", "else",
            "except", "False", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "None",
            "nonlocal", "not", "or", "pass", "raise", "return", "True", "try", "while", "with", "yield",
        ],
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs"] = "csharp", ["c#"] = "csharp", ["dotnet"] = "csharp",
        ["tsql"] = "sql", ["sqlite"] = "sql", ["mssql"] = "sql", ["postgres"] = "sql",
        ["js"] = "javascript", ["ts"] = "javascript", ["typescript"] = "javascript", ["json"] = "javascript",
        ["py"] = "python",
    };

    public static IReadOnlyList<string> SupportedLanguages { get; } =
        ["plaintext", "csharp", "sql", "javascript", "python"];

    // One alternation, evaluated left to right: comments and strings are matched before the
    // identifier rule ever sees their contents.
    [GeneratedRegex(
        @"(?<comment>--[^\n]*|//[^\n]*|\#[^\n]*|/\*.*?\*/)" +
        @"|(?<string>@?""(?:[^""\\]|\\.|"""")*""|'(?:[^'\\]|\\.|'')*')" +
        @"|(?<number>\b\d+(\.\d+)?\b)" +
        @"|(?<word>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    public static InlineCollection Highlight(string? code, string? language, double fontSize)
    {
        var inlines = new InlineCollection();

        if (string.IsNullOrEmpty(code))
        {
            return inlines;
        }

        var resolved = Resolve(language);

        if (resolved is null || !Keywords.TryGetValue(resolved, out var keywords))
        {
            inlines.Add(Token(code, CodeTheme.Default, fontSize));
            return inlines;
        }

        var position = 0;

        foreach (Match match in TokenPattern().Matches(code))
        {
            if (match.Index > position)
            {
                inlines.Add(Token(code[position..match.Index], CodeTheme.Default, fontSize));
            }

            var brush = CodeTheme.Default;

            if (match.Groups["comment"].Success)
            {
                brush = CodeTheme.Comment;
            }
            else if (match.Groups["string"].Success)
            {
                brush = CodeTheme.StringLiteral;
            }
            else if (match.Groups["number"].Success)
            {
                brush = CodeTheme.Number;
            }
            else if (match.Groups["word"].Success)
            {
                var word = match.Value;

                brush = keywords.Contains(word) ? CodeTheme.Keyword
                    // Heuristic: PascalCase identifiers in C#-like languages are almost always types.
                    : resolved != "sql" && char.IsUpper(word[0]) ? CodeTheme.Type
                    : CodeTheme.Default;
            }

            inlines.Add(Token(match.Value, brush, fontSize));
            position = match.Index + match.Length;
        }

        if (position < code.Length)
        {
            inlines.Add(Token(code[position..], CodeTheme.Default, fontSize));
        }

        return inlines;
    }

    private static string? Resolve(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var key = language.Trim().ToLowerInvariant();

        return Aliases.TryGetValue(key, out var alias) ? alias : key;
    }

    private static Run Token(string text, IBrush brush, double fontSize) => new(text)
    {
        Foreground = brush,
        FontFamily = CodeTheme.MonoFont,
        FontSize = fontSize,
    };
}
