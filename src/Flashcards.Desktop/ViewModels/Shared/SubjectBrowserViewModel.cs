using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Cards.Queries;
using Flashcards.Application.Contracts;
using Flashcards.Application.Stats.Queries;
using Flashcards.Application.Subjects.Queries;

namespace Flashcards.Desktop.ViewModels.Shared;

/// <summary>
/// The library at three levels of zoom: everything, then subjects, then the cards inside them.
/// <para>
/// Lifted out of the study panel once the statistics panel needed the same three tiers. Both
/// screens ask the same questions — how am I doing on this subject, on this card — and the only
/// thing that differs is whether the rows can be ticked, which is what
/// <see cref="ShowsCardSelection"/> settles. Two copies of this would have been two places for the
/// tier-to-tier reloading to drift.
/// </para>
/// <para>
/// Composed into its owners rather than inherited from: a panel <em>has</em> a browser, and making
/// <c>QuizViewModel</c> a kind of browser would have put the session's stages and this screen's
/// selection on the same object again, which is what the split is undoing.
/// </para>
/// </summary>
public sealed partial class SubjectBrowserViewModel(IDispatcher dispatcher) : ObservableObject
{
    private bool _suspendCardReload;

    /// <summary>Raised whenever the ticked set changes, so an owner can re-evaluate its buttons.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>
    /// Whether the card tier offers tick boxes. False on the statistics panel, where picking a
    /// card means "show me this one's record" and a second, invisible meaning would be a trap.
    /// </summary>
    [ObservableProperty]
    private bool _showsCardSelection = true;

    /// <summary>Anything that went wrong loading a tier. Surfaced by whichever panel owns this.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>The whole library — the header tier.</summary>
    [ObservableProperty]
    private OverallStats? _overall;

    public ObservableCollection<SubjectPickViewModel> SubjectPicks { get; } = [];

    public ObservableCollection<CardPickViewModel> CardPicks { get; } = [];

    /// <summary>
    /// The subject whose figures the subject tier is showing. Distinct from being ticked: looking
    /// at how a subject is going should not change what the next session will contain.
    /// </summary>
    [ObservableProperty]
    private SubjectPickViewModel? _focusedSubject;

    [ObservableProperty]
    private CardPickViewModel? _focusedCard;

    [ObservableProperty]
    private CardStats? _focusedCardStats;

    // ---- aggregates over the current selection ----------------------------

    public IEnumerable<SubjectPickViewModel> IncludedSubjects => SubjectPicks.Where(s => s.IsIncluded);

    public PracticeStats SelectionPractice => IncludedSubjects
        .Select(s => s.Practice)
        .Aggregate(PracticeStats.Empty, (running, next) =>
            new PracticeStats(running.Answered + next.Answered, running.Correct + next.Correct));

    public int SelectedSubjectCount => IncludedSubjects.Count();

    public bool HasSubjectSelection => SelectedSubjectCount > 0;

    /// <summary>The cards that are ticked. Nothing ticked means nothing to study.</summary>
    public IReadOnlyList<CardPickViewModel> Selection => [.. CardPicks.Where(c => c.IsIncluded)];

    public int SelectedCardCount => Selection.Count;

    public bool HasCardSelection => SelectedCardCount > 0;

    /// <summary>Label for whatever button acts on the selection, so it says what pressing it does.</summary>
    public string SelectionSummary => SelectedCardCount switch
    {
        0 => "nothing selected",
        1 => "1 card",
        _ => $"{SelectedCardCount} cards",
    };

    // The two picker columns are headed differently depending on what ticking a row means. On the
    // statistics panel it narrows what you are looking at; on the study prep screen it decides what
    // you are about to be asked. Same control, two honest readings.

    public string SubjectListLabel => ShowsCardSelection ? "IN THIS SESSION" : "SHOWING CARDS FROM";

    public string CardListLabel => ShowsCardSelection ? "IN THIS SESSION" : "CARDS";

    public string CardListHint => ShowsCardSelection
        ? SelectionSummary
        : CardPicks.Count == 1 ? "1 card" : $"{CardPicks.Count} cards";

    partial void OnShowsCardSelectionChanged(bool value)
    {
        OnPropertyChanged(nameof(SubjectListLabel));
        OnPropertyChanged(nameof(CardListLabel));
        OnPropertyChanged(nameof(CardListHint));
    }

    // ---- loading -----------------------------------------------------------

    /// <summary>
    /// Reloads the library and subject figures, preserving whatever was ticked. Run on every visit,
    /// so answers recorded in a previous sitting show up on the way in.
    /// </summary>
    public async Task RefreshAsync()
    {
        Overall = await dispatcher.QueryAsync(new GetOverallStatsQuery());

        var chosen = SubjectPicks.Where(s => s.IsIncluded).Select(s => s.Id).ToHashSet();
        var focused = FocusedSubject?.Id;
        var subjects = await dispatcher.QueryAsync(new GetSubjectStatsQuery());

        foreach (var stale in SubjectPicks)
        {
            stale.PropertyChanged -= OnSubjectPickChanged;
        }

        SubjectPicks.Clear();

        foreach (var subject in subjects)
        {
            var pick = new SubjectPickViewModel(subject)
            {
                // Nothing is "due" any more, so a first visit simply offers every subject that has
                // cards in it — you pick what you feel like working on.
                IsIncluded = chosen.Count > 0 ? chosen.Contains(subject.Id) : subject.CardCount > 0,
            };

            pick.PropertyChanged += OnSubjectPickChanged;
            SubjectPicks.Add(pick);
        }

        // Keep looking at whatever was on screen before the refresh; fall back to the first subject
        // so the tier's readout is never blank while there is something to report.
        FocusedSubject = SubjectPicks.FirstOrDefault(s => s.Id == focused) ?? SubjectPicks.FirstOrDefault();

        await LoadCardsForSelectionAsync();
    }

    private void OnSubjectPickChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SubjectPickViewModel.IsIncluded) || _suspendCardReload)
        {
            return;
        }

        RaiseSelectionChanged();
        LoadSafely(LoadCardsForSelectionAsync);
    }

    /// <summary>
    /// Fills the card tier from whatever subjects are ticked, so the cards on offer are always a
    /// subset of what the tier above is reporting on — every one of them ticked.
    /// </summary>
    public async Task LoadCardsForSelectionAsync()
    {
        var subjectIds = IncludedSubjects.Select(s => s.Id).ToArray();
        var focused = FocusedCard?.Id;

        foreach (var stale in CardPicks)
        {
            stale.PropertyChanged -= OnCardPickChanged;
        }

        CardPicks.Clear();

        if (subjectIds.Length > 0)
        {
            var results = await dispatcher.QueryAsync(new SearchFlashcardsQuery(new FlashcardSearchCriteria
            {
                SubjectIds = subjectIds,
                SortBy = FlashcardSortField.Name,
                SortDescending = false,
                PageSize = 500,
            }));

            foreach (var card in results.Items)
            {
                // Every card in the chosen subjects starts ticked. Changing the subjects is the
                // coarse choice and this is the fine one, so the fine one begins by agreeing with
                // it — untick what you do not want rather than build the list up from nothing.
                var pick = new CardPickViewModel(card) { IsIncluded = true };
                pick.PropertyChanged += OnCardPickChanged;
                CardPicks.Add(pick);
            }
        }

        FocusedCard = CardPicks.FirstOrDefault(c => c.Id == focused) ?? CardPicks.FirstOrDefault();
        RaiseSelectionChanged();
    }

    private void OnCardPickChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CardPickViewModel.IsIncluded))
        {
            RaiseSelectionChanged();
        }
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectionPractice));
        OnPropertyChanged(nameof(SelectedSubjectCount));
        OnPropertyChanged(nameof(HasSubjectSelection));
        OnPropertyChanged(nameof(SelectedCardCount));
        OnPropertyChanged(nameof(HasCardSelection));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CardListHint));

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnFocusedCardChanged(CardPickViewModel? value)
    {
        if (value is null)
        {
            FocusedCardStats = null;
            return;
        }

        LoadSafely(async () => FocusedCardStats = await dispatcher.QueryAsync(new GetCardStatsQuery(value.Id)));
    }

    /// <summary>
    /// Runs a drill-down load, reporting failures rather than swallowing them.
    /// <para>
    /// Deliberately not wrapped in the panel's busy guard. These fire from property-changed
    /// handlers that are themselves reached from inside the activation refresh, and a busy guard
    /// would drop the nested call silently — leaving the panel showing figures for the wrong subject.
    /// </para>
    /// </summary>
    private async void LoadSafely(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    // ---- commands ----------------------------------------------------------

    /// <summary>
    /// Ticks or clears every subject at once. Held under a flag so that pressing "All" over twenty
    /// subjects runs one card query rather than twenty.
    /// </summary>
    [RelayCommand]
    private void SetAllSubjects(string? included)
    {
        if (!bool.TryParse(included, out var value))
        {
            return;
        }

        _suspendCardReload = true;

        try
        {
            foreach (var subject in SubjectPicks)
            {
                subject.IsIncluded = value;
            }
        }
        finally
        {
            _suspendCardReload = false;
        }

        RaiseSelectionChanged();
        LoadSafely(LoadCardsForSelectionAsync);
    }

    /// <summary>Ticks or clears every card currently on offer.</summary>
    [RelayCommand]
    private void SetAllCards(string? included)
    {
        if (!bool.TryParse(included, out var value))
        {
            return;
        }

        foreach (var card in CardPicks)
        {
            card.IsIncluded = value;
        }
    }
}
