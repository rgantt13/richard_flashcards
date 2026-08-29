using Flashcards.Desktop.ViewModels.StudySetup;
using Flashcards.Desktop.ViewModels.Shared;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Contracts;
using Flashcards.Application.Quiz.Commands;
using Flashcards.Application.Cards.Queries;
using Flashcards.Application.Quiz.Queries;
using Flashcards.Application.Stats.Queries;
using Flashcards.Application.Subjects.Queries;
using Flashcards.Domain.Cards;

namespace Flashcards.Desktop.ViewModels.Study;

public enum QuizStage
{
    Setup,
    Question,
    Answer,
    Finished,
}

/// <summary>
/// Quiz mode. Pick subjects, then work a queue of cards: see the question, reveal, mark yourself.
/// <para>
/// The queue lives in memory for the sitting and nothing outlives it — there is no schedule, so
/// studying is something you do when you want to rather than something the app asks for. A card
/// answered wrong goes to the back of the queue so it comes round again before you finish.
/// </para>
/// </summary>
public sealed partial class QuizViewModel(IDispatcher dispatcher) : ViewModelBase
{
    private readonly Queue<Guid> _queue = new();
    private readonly Stopwatch _timer = new();
    private bool _suspendCardReload;

    public ObservableCollection<ChoiceEditorViewModel> CurrentChoices { get; } = [];

    [ObservableProperty]
    private QuizStage _stage = QuizStage.Setup;

    [ObservableProperty]
    private QuizCard? _current;

    [ObservableProperty]
    private bool _shuffleChoices = true;

    [ObservableProperty]
    private int _reviewedCount;

    [ObservableProperty]
    private int _correctCount;

    [ObservableProperty]
    private int _remainingCount;

    [ObservableProperty]
    private bool? _lastAnswerCorrect;

    /// <summary>The current card's lifetime record, shown alongside the answer.</summary>
    [ObservableProperty]
    private CardStats? _currentCardStats;

    // ---- session builder --------------------------------------------------
    // The setup screen is a tiered drill-down: the library at the top, then subjects, then the
    // cards inside them. Each tier both reports how you are doing and narrows what the custom
    // session will contain, because those are the same decision.

    /// <summary>The whole library — the panel header.</summary>
    [ObservableProperty]
    private OverallStats? _overall;

    public ObservableCollection<SubjectPickViewModel> SubjectPicks { get; } = [];

    public ObservableCollection<CardPickViewModel> CardPicks { get; } = [];

    /// <summary>
    /// The subject whose figures the subject tier is showing. Distinct from being included, the
    /// same way <see cref="FocusedCard"/> is: looking at how a subject is going should not change
    /// what the next session will contain.
    /// </summary>
    [ObservableProperty]
    private SubjectPickViewModel? _focusedSubject;

    /// <summary>The card whose figures the card tier is showing. Distinct from being included.</summary>
    [ObservableProperty]
    private CardPickViewModel? _focusedCard;

    [ObservableProperty]
    private CardStats? _focusedCardStats;

    /// <summary>How many cards the Random and Suggested modes draw. Custom uses your picks instead.</summary>
    [ObservableProperty]
    private int _quickCount = 20;

    // ---- aggregate over the current subject selection ----------------------

    private IEnumerable<SubjectPickViewModel> IncludedSubjects => SubjectPicks.Where(s => s.IsIncluded);

    public PracticeStats SelectionPractice => IncludedSubjects
        .Select(s => s.Practice)
        .Aggregate(PracticeStats.Empty, (running, next) =>
            new PracticeStats(running.Answered + next.Answered, running.Correct + next.Correct));

    public int SelectedSubjectCount => IncludedSubjects.Count();

    public bool HasSubjectSelection => SelectedSubjectCount > 0;

    /// <summary>Cards ticked for the custom session, or the whole filtered list when none are.</summary>
    private IReadOnlyList<CardPickViewModel> CustomSelection
    {
        get
        {
            var ticked = CardPicks.Where(c => c.IsIncluded).ToList();

            // Ticking nothing means "all of them" rather than "none of them": having chosen the
            // subjects, the obvious next step is to study what is in them.
            return ticked.Count > 0 ? ticked : [.. CardPicks];
        }
    }

    public int CustomCardCount => CustomSelection.Count;

    public bool CanStartCustom => CustomCardCount > 0;

    /// <summary>Label on the Custom button, so it says what pressing it will actually do.</summary>
    public string CustomSummary => CustomCardCount switch
    {
        0 => "nothing selected",
        1 => "1 card",
        _ => $"{CustomCardCount} cards",
    };

    public bool IsSetup => Stage == QuizStage.Setup;

    public bool IsStudying => Stage is QuizStage.Question or QuizStage.Answer;

    public bool IsAnswerVisible => Stage == QuizStage.Answer;

    /// <summary>
    /// Set while the user has stepped back to look at the question again after revealing.
    /// <para>
    /// Deliberately a flag on the current card rather than a history stack. Going back means
    /// "show me what I was just asked", and it stops at the card in hand — a session is a queue you
    /// work forwards through, and being able to walk back into cards already graded would make the
    /// counts and the queue disagree about where you are.
    /// </para>
    /// </summary>
    [ObservableProperty]
    private bool _isReviewingQuestion;

    /// <summary>
    /// Whether revealing swaps the question out for the answer.
    /// <para>
    /// Only for card types where the two are separate pieces of content. A cloze card answers
    /// itself by filling its own blanks, and a multiple-choice card by marking the right option —
    /// for those the question is exactly where the answer appears, so swapping it away would hide
    /// the thing being answered.
    /// </para>
    /// </summary>
    private bool SwapsOnReveal => Current?.CardType is CardType.Standard or CardType.Freeform;

    /// <summary>Whether the question side is on screen.</summary>
    public bool ShowsQuestion => !SwapsOnReveal || Stage != QuizStage.Answer || IsReviewingQuestion;

    /// <summary>Whether the answer side is on screen.</summary>
    public bool ShowsAnswer => Stage == QuizStage.Answer && !IsReviewingQuestion;

    /// <summary>Cloze blanks stay covered until the answer is actually the thing being shown.</summary>
    public bool HideClozeAnswers => Stage != QuizStage.Answer || IsReviewingQuestion;

    /// <summary>Whether there is a question to step back to — nothing was swapped away otherwise.</summary>
    public bool CanReviewQuestion => Stage == QuizStage.Answer && SwapsOnReveal;

    public bool IsFinished => Stage == QuizStage.Finished;

    public bool IsMultipleChoice => Current?.CardType == CardType.MultipleChoice;

    public bool IsCloze => Current?.CardType == CardType.Cloze;

    public bool HasSelection => CurrentChoices.Any(c => c.IsSelected);

    public string ProgressLabel => $"{ReviewedCount} done  ·  {RemainingCount} left";

    public string AccuracyLabel => ReviewedCount == 0 ? "-" : $"{CorrectCount * 100.0 / ReviewedCount:0}%";

    public override Task ActivateAsync() => RunAsync(RefreshAsync);

    /// <summary>
    /// Reloads the library and subject figures, preserving whatever was selected. Runs on every
    /// visit, so answers recorded in a previous sitting show up on the way in.
    /// </summary>
    private async Task RefreshAsync()
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
    /// Includes or drops every subject at once. Held under <see cref="_suspendCardReload"/> so that
    /// pressing "All" over twenty subjects runs one card query rather than twenty.
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

    /// <summary>
    /// Fills the card tier from whatever subjects are currently included, so the cards on offer
    /// are always a subset of what the tier above is reporting on.
    /// </summary>
    private async Task LoadCardsForSelectionAsync()
    {
        var subjectIds = IncludedSubjects.Select(s => s.Id).ToArray();
        var ticked = CardPicks.Where(c => c.IsIncluded).Select(c => c.Id).ToHashSet();
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
                var pick = new CardPickViewModel(card) { IsIncluded = ticked.Contains(card.Id) };
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
        OnPropertyChanged(nameof(CustomCardCount));
        OnPropertyChanged(nameof(CanStartCustom));
        OnPropertyChanged(nameof(CustomSummary));
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

    /// <summary>
    /// Runs a drill-down load, reporting failures rather than swallowing them.
    /// <para>
    /// Deliberately not <c>RunAsync</c>. These fire from property-changed handlers that are
    /// themselves reached from inside the activation refresh, which is already running under
    /// <c>RunAsync</c> — and its busy guard would drop the nested call silently, leaving the panel
    /// showing figures for the wrong subject.
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

    /// <summary>
    /// Exactly the cards picked above. Order is randomised on start regardless, so assembling a
    /// set never means committing to the order you assembled it in.
    /// </summary>
    [RelayCommand]
    private Task CustomStudy() => StartAsync(new QuizOptions
    {
        CardIds = [.. CustomSelection.Select(c => c.Id)],
        MaxCards = Math.Max(CustomCardCount, 1),
        HardestFirst = false,
        ShuffleChoices = ShuffleChoices,
    },
    emptyMessage: "Pick at least one subject to study from.");

    /// <summary>A straight random draw from the whole library, ignoring the selection above.</summary>
    [RelayCommand]
    private Task RandomStudy() => StartAsync(new QuizOptions
    {
        MaxCards = QuickCount,
        HardestFirst = false,
        ShuffleChoices = ShuffleChoices,
    },
    emptyMessage: "There are no cards to study yet.");

    /// <summary>
    /// Weighted by how often you get each card wrong, with never-answered cards leading — the
    /// cards you have most to gain from, without anything being scheduled for you.
    /// </summary>
    [RelayCommand]
    private Task SuggestedStudy() => StartAsync(new QuizOptions
    {
        MaxCards = QuickCount,
        HardestFirst = true,
        ShuffleChoices = ShuffleChoices,
    },
    emptyMessage: "There are no cards to study yet.");

    private Task StartAsync(QuizOptions options, string emptyMessage) => RunAsync(async () =>
    {
        var session = await dispatcher.QueryAsync(new StartQuizSessionQuery(options));

        _queue.Clear();

        // The queue is shuffled here rather than in SQL for the custom mode: the store returns a
        // hand-picked set in whatever order it found them, and "the order I ticked them" is not an
        // order anybody wants to be quizzed in.
        var ids = session.CardIds.ToArray();
        Random.Shared.Shuffle(ids);

        foreach (var id in ids)
        {
            _queue.Enqueue(id);
        }

        ReviewedCount = 0;
        CorrectCount = 0;
        RemainingCount = _queue.Count;

        if (_queue.Count == 0)
        {
            ErrorMessage = emptyMessage;
            return;
        }

        ErrorMessage = null;
        await AdvanceAsync();
    });

    private async Task AdvanceAsync()
    {
        LastAnswerCorrect = null;

        // Going back is scoped to the card in hand, so a new card always starts on its question.
        IsReviewingQuestion = false;

        // Going back is scoped to the card in hand, so a new card starts on its question side.
        IsReviewingQuestion = false;
        CurrentChoices.Clear();

        if (!_queue.TryDequeue(out var cardId))
        {
            Current = null;
            Stage = QuizStage.Finished;
            _timer.Reset();
            return;
        }

        RemainingCount = _queue.Count;

        var card = await dispatcher.QueryAsync(new GetQuizCardQuery(cardId, ShuffleChoices));

        if (card is null)
        {
            // Deleted mid-session; skip it.
            await AdvanceAsync();
            return;
        }

        Current = card;

        foreach (var choice in card.Choices)
        {
            CurrentChoices.Add(ChoiceEditorViewModel.FromDto(choice));
        }

        CurrentCardStats = card.Stats;

        Stage = QuizStage.Question;
        _timer.Restart();
    }

    [RelayCommand]
    private void Reveal()
    {
        if (Stage != QuizStage.Question)
        {
            return;
        }

        if (IsMultipleChoice && CurrentChoices.Count > 0)
        {
            // Multiple choice marks itself, so the answer side can pre-select the right button.
            LastAnswerCorrect = CurrentChoices.All(c => c.IsSelected == c.IsCorrect);
        }

        IsReviewingQuestion = false;
        Stage = QuizStage.Answer;
    }

    [RelayCommand]
    private void ToggleChoice(ChoiceEditorViewModel? choice)
    {
        if (choice is null || Stage != QuizStage.Question)
        {
            return;
        }

        if (Current is { IsMultiSelect: false })
        {
            foreach (var other in CurrentChoices.Where(c => !ReferenceEquals(c, choice)))
            {
                other.IsSelected = false;
            }
        }

        choice.IsSelected = !choice.IsSelected;
        OnPropertyChanged(nameof(HasSelection));
    }

    /// <summary>
    /// Records the answer as right or wrong and moves on.
    /// <para>
    /// This replaced the four-point grade. Nothing is rescheduled, so there is nothing for a
    /// finer scale to influence — and a card answered wrong simply goes to the back of the queue
    /// so it comes round again in the same sitting, which is the only "repeat" behaviour left.
    /// </para>
    /// </summary>
    [RelayCommand]
    private Task AnswerAsync(string? wasCorrect) => RunAsync(async () =>
    {
        if (Current is null || Stage != QuizStage.Answer || !bool.TryParse(wasCorrect, out var correct))
        {
            return;
        }

        _timer.Stop();

        var result = await dispatcher.SendAsync(
            new RecordAnswerCommand(Current.Id, correct, _timer.Elapsed));

        ReviewedCount++;

        if (correct)
        {
            CorrectCount++;
        }
        else
        {
            // Seen again before the session ends — the one thing worth keeping from repetition.
            _queue.Enqueue(Current.Id);
        }

        var lifetime = result.Stats.Practice;

        StatusMessage = correct
            ? $"Correct · {lifetime.Correct}/{lifetime.Answered} on this card"
            : $"Missed · {lifetime.Correct}/{lifetime.Answered} on this card";

        RemainingCount = _queue.Count;

        await AdvanceAsync();
    });

    [RelayCommand]
    private void EndSession()
    {
        _queue.Clear();
        Current = null;
        RemainingCount = 0;
        Stage = QuizStage.Finished;
    }

    [RelayCommand]
    private void BackToSetup()
    {
        _queue.Clear();
        Current = null;
        Stage = QuizStage.Setup;
    }

    partial void OnStageChanged(QuizStage value)
    {
        OnPropertyChanged(nameof(IsSetup));
        OnPropertyChanged(nameof(IsStudying));
        OnPropertyChanged(nameof(IsAnswerVisible));
        OnPropertyChanged(nameof(IsFinished));
        RaiseFaceChanged();
    }

    partial void OnCurrentChanged(QuizCard? value)
    {
        OnPropertyChanged(nameof(IsMultipleChoice));
        OnPropertyChanged(nameof(IsCloze));
        OnPropertyChanged(nameof(HasSelection));

        // SwapsOnReveal reads the card's type, so which face is showing depends on this too.
        RaiseFaceChanged();
    }

    partial void OnIsReviewingQuestionChanged(bool value) => RaiseFaceChanged();

    private void RaiseFaceChanged()
    {
        OnPropertyChanged(nameof(ShowsQuestion));
        OnPropertyChanged(nameof(ShowsAnswer));
        OnPropertyChanged(nameof(HideClozeAnswers));
        OnPropertyChanged(nameof(CanReviewQuestion));
    }

    /// <summary>Steps back to the question, and forward to the answer again.</summary>
    [RelayCommand]
    private void ToggleQuestionReview()
    {
        if (Stage == QuizStage.Answer)
        {
            IsReviewingQuestion = !IsReviewingQuestion;
        }
    }

    partial void OnReviewedCountChanged(int value)
    {
        OnPropertyChanged(nameof(ProgressLabel));
        OnPropertyChanged(nameof(AccuracyLabel));
    }

    partial void OnRemainingCountChanged(int value) => OnPropertyChanged(nameof(ProgressLabel));

    partial void OnCorrectCountChanged(int value) => OnPropertyChanged(nameof(AccuracyLabel));
}
