using System.Text;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Flashcards.Desktop.ViewModels.Shared;

namespace Flashcards.Desktop.ViewModels.Manage;

/// <summary>
/// Builds the prompt you paste into whichever assistant you use, and hands it to the clipboard.
/// <para>
/// The app deliberately does not call a model. It has no API key, makes no network request and
/// keeps no account — "no server, no account, no sync" is true of this app and worth keeping true.
/// The cost of that is one paste; the benefit is that generating a deck works with whatever you
/// already have open, including something running on your own machine.
/// </para>
/// <para>
/// The template is an embedded asset rather than a string in this file, and it is the same file
/// <c>docs/generating-decks.md</c> points at — one copy, so the documentation and the dialog cannot
/// drift into disagreeing about the schema.
/// </para>
/// </summary>
public sealed partial class GenerateDeckViewModel : ViewModelBase
{
    private const string TemplateUri = "avares://Flashcards.Desktop/Assets/DeckPrompt.txt";

    private readonly string _template;

    /// <summary>Raised when the dialog should close.</summary>
    public event EventHandler? Closed;

    /// <summary>
    /// Set by the view. The clipboard hangs off the TopLevel, which only a Visual can reach, so
    /// the window supplies this the same way the card editor supplies its image providers.
    /// </summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    public GenerateDeckViewModel()
    {
        _template = LoadTemplate();
    }

    [ObservableProperty]
    private string? _subject;

    [ObservableProperty]
    private int _cardCount = 20;

    /// <summary>
    /// Optional extra steer — "focus on the syntax", "assume I already know the basics". Dropped
    /// from the prompt entirely when blank, rather than leaving an empty heading behind.
    /// </summary>
    [ObservableProperty]
    private string? _focus;

    public bool CanCopy => !string.IsNullOrWhiteSpace(Subject);

    /// <summary>
    /// The finished prompt, shown in full so you can see exactly what you are about to paste.
    /// <para>
    /// The focus token takes its own line in the template, so an empty one is swallowed along with
    /// that line rather than left behind as a gap in the middle of the header.
    /// </para>
    /// </summary>
    public string Prompt
    {
        get
        {
            var focus = string.IsNullOrWhiteSpace(Focus)
                ? string.Empty
                : $"ADDITIONAL FOCUS: {Focus.Trim()}";

            var filled = _template
                .Replace("<<SUBJECT>>", string.IsNullOrWhiteSpace(Subject) ? "(type a subject above)" : Subject.Trim())
                .Replace("<<COUNT>>", CardCount.ToString());

            return focus.Length == 0
                ? filled.Replace("<<FOCUS>>\r\n", string.Empty).Replace("<<FOCUS>>\n", string.Empty)
                : filled.Replace("<<FOCUS>>", focus);
        }
    }

    partial void OnSubjectChanged(string? value)
    {
        OnPropertyChanged(nameof(Prompt));
        OnPropertyChanged(nameof(CanCopy));
    }

    partial void OnCardCountChanged(int value) => OnPropertyChanged(nameof(Prompt));

    partial void OnFocusChanged(string? value) => OnPropertyChanged(nameof(Prompt));

    [RelayCommand]
    private Task CopyAsync() => RunAsync(async () =>
    {
        if (!CanCopy || CopyToClipboard is not { } copy)
        {
            return;
        }

        await copy(Prompt);
        // Short on purpose: the three steps are already spelled out beside this line, and
        // repeating them turns a confirmation into a wall.
        StatusMessage = "Copied to your clipboard.";
    });

    [RelayCommand]
    private void Close() => Closed?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Reads the template out of the assembly. A missing asset would mean a broken build rather
    /// than a broken install, but the dialog still says so rather than showing an empty box.
    /// </summary>
    private static string LoadTemplate()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(TemplateUri));
            using var reader = new StreamReader(stream, Encoding.UTF8);

            return reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is FileNotFoundException or UriFormatException)
        {
            return "The prompt template is missing from this build.";
        }
    }
}
