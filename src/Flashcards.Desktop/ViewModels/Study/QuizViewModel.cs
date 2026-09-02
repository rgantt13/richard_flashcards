using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Contracts;
using Flashcards.Application.Quiz.Commands;
using Flashcards.Application.Quiz.Queries;
using Flashcards.Application.Settings.Queries;
using Flashcards.Desktop.ViewModels.Shared;
using Flashcards.Desktop.ViewModels.StudySetup;
using Flashcards.Domain.Cards;

namespace Flashcards.Desktop.ViewModels.Study;

public enum QuizStage
{
    /// <summary>The tiles. Which way of studying do you want.</summary>
    ModeSelect,

    /// <summary>The chosen mode's options, and the button that starts it.</summary>
    Prep,

    Question,
    Answer,
    Finished,
}

/// <summary>One entry in a duration drop-down. Zero means "no limit".</summary>
public sealed record TimeChoice(int Value, string Label);

/// <summary>
/// Study mode: pick how you want to work, set that mode's options, then work a queue of cards.
/// <para>
/// The screen used to open on one prep page carrying every option for every way of starting, most
/// of which did not apply to whichever button you were about to press. It now opens on the modes
/// themselves and each one leads to the options it actually has — which is why the subject and
/// card tiers only appear for Custom, the one mode where choosing cards <em>is</em> the point.
/// </para>
/// <para>
/// The queue lives in memory for the sitting and nothing outlives it. There is no schedule, so
/// studying is something you do when you want to rather than something the app asks for. A card
/// answered wrong goes to the back of the queue so it comes round again before you finish.
/// </para>
/// </summary>
public sealed partial class QuizViewModel : ViewModelBase
{
    private readonly IDispatcher _dispatcher;
    private readonly Queue<Guid> _queue = new();
    private readonly Stopwatch _timer = new();

    /// <summary>Drives both countdowns. One timer, because they always run together or not at all.</summary>
    private readonly Avalonia.Threading.DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>Guards the timeout path against re-entering while an answer is already being recorded.</summary>
    private bool _recording;

    public QuizViewModel(IDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Browser = new SubjectBrowserViewModel(dispatcher);
        Browser.SelectionChanged += (_, _) => OnPropertyChanged(nameof(CanStart));
        _clock.Tick += OnTick;

        // Both drop-downs open on "no limit"; a mode that prefers a clock overrides this when it
        // is chosen. Property initialisers have already run, so the lists are here to read.
        _sessionLimit = SessionLimits[0];
        _questionLimit = QuestionLimits[0];
    }

    /// <summary>The library, subjects and cards — shown on the Custom prep screen only.</summary>
    public SubjectBrowserViewModel Browser { get; }

    /// <summary>
    /// The mode offered on its own at the foot of the panel, and the one to press if you have no
    /// opinion: it needs no decisions and it picks the cards you are worst at.
    /// </summary>
    public StudyModeCard Suggested { get; } = StudyModeCard.For(StudyMode.Suggested);

    /// <summary>
    /// The rest, in catalogue order. Suggested is missing from the grid because it is not one of
    /// several equal choices any more — it is the default, and a default sitting among its
    /// alternatives is not being offered as one.
    /// </summary>
    public IReadOnlyList<StudyModeCard> Modes { get; } =
        [.. StudyModeCard.All.Where(m => m.Mode != StudyMode.Suggested)];

    [ObservableProperty]
    private QuizStage _stage = QuizStage.ModeSelect;

    [ObservableProperty]
    private StudyModeCard? _mode;

    [ObservableProperty]
    private QuizCard? _current;

    public ObservableCollection<ChoiceEditorViewModel> CurrentChoices { get; } = [];

    // ---- prep options ------------------------------------------------------

    /// <summary>How many cards the sitting draws. Marathon ignores it.</summary>
    [ObservableProperty]
    private int _cardCount = 20;

    [ObservableProperty]
    private bool _shuffleChoices = true;

    /// <summary>
    /// Restricts the draw to multiple-choice and cloze cards — the ones the app marks itself.
    /// A standard or designed card is graded by the person answering it, which a clock has no
    /// room for, so the timed modes arrive with this already on.
    /// </summary>
    [ObservableProperty]
    private bool _autoGradedOnly;

    public IReadOnlyList<TimeChoice> SessionLimits { get; } =
    [
        new(0, "no limit"), new(5, "5 minutes"), new(10, "10 minutes"), new(15, "15 minutes"),
        new(20, "20 minutes"), new(30, "30 minutes"), new(45, "45 minutes"), new(60, "1 hour"),
    ];

    public IReadOnlyList<TimeChoice> QuestionLimits { get; } =
    [
        new(0, "no limit"), new(10, "10 seconds"), new(15, "15 seconds"), new(20, "20 seconds"),
        new(30, "30 seconds"), new(45, "45 seconds"), new(60, "1 minute"),
    ];

    [ObservableProperty]
    private TimeChoice _sessionLimit;

    [ObservableProperty]
    private TimeChoice _questionLimit;

    // ---- countdowns --------------------------------------------------------

    [ObservableProperty]
    private TimeSpan? _sessionRemaining;

    [ObservableProperty]
    private TimeSpan? _questionRemaining;

    public bool HasSessionClock => SessionRemaining is not null;

    public bool HasQuestionClock => QuestionRemaining is not null;

    public string SessionClockLabel => Format(SessionRemaining);

    public string QuestionClockLabel => Format(QuestionRemaining);

    /// <summary>Turns the question countdown red over the last five seconds.</summary>
    public bool IsQuestionClockCritical => QuestionRemaining is { TotalSeconds: <= 5 };

    private static string Format(TimeSpan? value)
        => value is not { } t ? string.Empty : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    // ---- session progress --------------------------------------------------

    [ObservableProperty]
    private int _reviewedCount;

    [ObservableProperty]
    private int _correctCount;

    [ObservableProperty]
    private int _remainingCount;

    [ObservableProperty]
    private bool? _lastAnswerCorrect;

    [ObservableProperty]
    private CardStats? _currentCardStats;

    // ---- stage flags -------------------------------------------------------

    public bool IsChoosingMode => Stage == QuizStage.ModeSelect;

    public bool IsPreparing => Stage == QuizStage.Prep;

    public bool IsStudying => Stage is QuizStage.Question or QuizStage.Answer;

    public bool IsAnswerVisible => Stage == QuizStage.Answer;

    public bool IsFinished => Stage == QuizStage.Finished;

    /// <summary>Whether the prep screen shows the subject and card tiers. Custom only.</summary>
    public bool ShowsPickers => Mode?.PicksCards == true;

    public string PrepTitle => Mode?.Title ?? "Study";

    public string PrepBlurb => Mode?.Blurb ?? string.Empty;

    /// <summary>
    /// Whether there is anything to start. Only Custom can be empty in a way the user controls —
    /// the other modes either find cards or report that there are none once they run.
    /// </summary>
    public bool CanStart => !ShowsPickers || Browser.HasCardSelection;

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

    public bool ShowsQuestion => !SwapsOnReveal || Stage != QuizStage.Answer || IsReviewingQuestion;

    public bool ShowsAnswer => Stage == QuizStage.Answer && !IsReviewingQuestion;

    public bool HideClozeAnswers => Stage != QuizStage.Answer || IsReviewingQuestion;

    public bool CanReviewQuestion => Stage == QuizStage.Answer && SwapsOnReveal;

    public bool IsMultipleChoice => Current?.CardType == CardType.MultipleChoice;

    public bool IsCloze => Current?.CardType == CardType.Cloze;

    public bool HasSelection => CurrentChoices.Any(c => c.IsSelected);

    public string ProgressLabel => $"{ReviewedCount} done  ·  {RemainingCount} left";

    public string AccuracyLabel => ReviewedCount == 0 ? "-" : $"{CorrectCount * 100.0 / ReviewedCount:0}%";

    /// <summary>
    /// Only the mode tiles need loading on the way in. The browser is loaded when Custom is picked,
    /// so visiting the panel to press Random does not pay for a full library query.
    /// </summary>
    public override Task ActivateAsync() => Task.CompletedTask;

    // ---- choosing a mode ---------------------------------------------------

    [RelayCommand]
    private Task ChooseModeAsync(StudyModeCard? card) => RunAsync(async () =>
    {
        if (card is null)
        {
            return;
        }

        Mode = card;
        ErrorMessage = null;
        StatusMessage = null;

        // Your saved defaults are the starting point, read fresh so a change on the settings
        // panel takes effect on the next mode you pick rather than the next time you launch.
        var settings = await _dispatcher.QueryAsync(new GetSettingsQuery());

        CardCount = settings.DefaultCardCount;
        ShuffleChoices = settings.ShuffleChoices;

        // The mode's own preferences then override the two it has an opinion about. Neither is a
        // lock — every one of them is still a control on the screen you are about to see.
        AutoGradedOnly = card.PrefersAutoGraded;
        QuestionLimit = QuestionLimits.First(q => q.Value == (card.PrefersTimed ? 20 : 0));
        SessionLimit = SessionLimits[0];

        if (card.PicksCards)
        {
            await Browser.RefreshAsync();
        }

        Stage = QuizStage.Prep;
    });

    /// <summary>Back to the tiles, from prep or from the end of a sitting.</summary>
    [RelayCommand]
    private void BackToModes()
    {
        StopClock();
        _queue.Clear();
        Current = null;
        RemainingCount = 0;
        Mode = null;
        ErrorMessage = null;
        Stage = QuizStage.ModeSelect;
    }

    // ---- starting ----------------------------------------------------------

    [RelayCommand]
    private Task StartStudyAsync() => RunAsync(async () =>
    {
        if (Mode is not { } mode)
        {
            return;
        }

        var options = new QuizOptions
        {
            // Custom hands over an exact set; every other mode narrows by subject at most.
            CardIds = mode.PicksCards ? [.. Browser.Selection.Select(c => c.Id)] : [],
            MaxCards = mode.HasCardCount ? Math.Max(CardCount, 1) : int.MaxValue,
            Draw = mode.Draw,
            AutoGradedOnly = AutoGradedOnly,
            ShuffleChoices = ShuffleChoices,
        };

        var session = await _dispatcher.QueryAsync(new StartQuizSessionQuery(options));

        _queue.Clear();

        // Shuffled here rather than in SQL for the ordered modes' benefit: Suggested and Recently
        // missed mean their order, and the rest do not — so only the unordered ones are shuffled.
        var ids = session.CardIds.ToArray();

        if (mode.Draw == QuizDraw.Random)
        {
            Random.Shared.Shuffle(ids);
        }

        foreach (var id in ids)
        {
            _queue.Enqueue(id);
        }

        ReviewedCount = 0;
        CorrectCount = 0;
        RemainingCount = _queue.Count;

        if (_queue.Count == 0)
        {
            ErrorMessage = EmptyMessageFor(mode);
            return;
        }

        ErrorMessage = null;
        StartClock();
        await AdvanceAsync();
    });

    private static string EmptyMessageFor(StudyModeCard mode) => mode.Mode switch
    {
        StudyMode.Custom => "Pick at least one subject to study from.",
        StudyMode.Fresh => "Nothing left that you have not answered — try Suggested instead.",
        StudyMode.RecentlyMissed => "Nothing missed recently. That is the good outcome.",
        _ => "There are no cards to study yet.",
    };

    // ---- the clock ---------------------------------------------------------

    private void StartClock()
    {
        SessionRemaining = SessionLimit.Value > 0 ? TimeSpan.FromMinutes(SessionLimit.Value) : null;
        QuestionRemaining = null;

        if (SessionRemaining is not null || QuestionLimit.Value > 0)
        {
            _clock.Start();
        }

        RaiseClockChanged();
    }

    private void StopClock()
    {
        _clock.Stop();
        SessionRemaining = null;
        QuestionRemaining = null;
        RaiseClockChanged();
    }

    /// <summary>
    /// One tick drives both countdowns.
    /// <para>
    /// The question countdown only runs while the question is on screen. Once you reveal, the
    /// clock has done its job — what is left is reading the answer and grading yourself, and
    /// putting that on a timer would be timing the wrong thing.
    /// </para>
    /// </summary>
    private void OnTick(object? sender, EventArgs e)
    {
        if (!IsStudying)
        {
            return;
        }

        if (SessionRemaining is { } session)
        {
            SessionRemaining = session <= TimeSpan.FromSeconds(1) ? TimeSpan.Zero : session.Subtract(TimeSpan.FromSeconds(1));

            if (SessionRemaining == TimeSpan.Zero)
            {
                StatusMessage = "Time is up.";
                EndSession();
                return;
            }
        }

        if (Stage == QuizStage.Question && QuestionRemaining is { } question)
        {
            QuestionRemaining = question <= TimeSpan.FromSeconds(1) ? TimeSpan.Zero : question.Subtract(TimeSpan.FromSeconds(1));

            if (QuestionRemaining == TimeSpan.Zero && !_recording)
            {
                // Running out counts against you, and the card returns to the back of the queue
                // like any other wrong answer, so you meet it again before the sitting ends.
                StatusMessage = "Out of time — marked wrong.";
                _ = RecordAsync(correct: false);
                return;
            }
        }

        RaiseClockChanged();
    }

    private void RaiseClockChanged()
    {
        OnPropertyChanged(nameof(HasSessionClock));
        OnPropertyChanged(nameof(HasQuestionClock));
        OnPropertyChanged(nameof(SessionClockLabel));
        OnPropertyChanged(nameof(QuestionClockLabel));
        OnPropertyChanged(nameof(IsQuestionClockCritical));
    }

    // ---- working the queue -------------------------------------------------

    private async Task AdvanceAsync()
    {
        LastAnswerCorrect = null;

        // Going back is scoped to the card in hand, so a new card always starts on its question.
        IsReviewingQuestion = false;
        CurrentChoices.Clear();

        if (!_queue.TryDequeue(out var cardId))
        {
            Current = null;
            Stage = QuizStage.Finished;
            _timer.Reset();
            StopClock();
            return;
        }

        RemainingCount = _queue.Count;

        var card = await _dispatcher.QueryAsync(new GetQuizCardQuery(cardId, ShuffleChoices));

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

        // Every question gets the full allowance, however long the last one took.
        QuestionRemaining = QuestionLimit.Value > 0 ? TimeSpan.FromSeconds(QuestionLimit.Value) : null;
        RaiseClockChanged();
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
    private Task AnswerAsync(string? wasCorrect)
    {
        if (Stage != QuizStage.Answer || !bool.TryParse(wasCorrect, out var correct))
        {
            return Task.CompletedTask;
        }

        return RecordAsync(correct);
    }

    /// <summary>
    /// The one path that writes an answer, whether you gave it or the clock did.
    /// <para>
    /// Not wrapped in <c>RunAsync</c>: the timeout calls this from a timer tick, and the busy
    /// guard would silently drop it if a grading click were already in flight — leaving a session
    /// stuck on a card whose clock had already run out.
    /// </para>
    /// </summary>
    private async Task RecordAsync(bool correct)
    {
        if (Current is null || _recording)
        {
            return;
        }

        _recording = true;

        try
        {
            _timer.Stop();

            var result = await _dispatcher.SendAsync(new RecordAnswerCommand(Current.Id, correct, _timer.Elapsed));

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
                : $"{StatusMessage ?? "Missed"} · {lifetime.Correct}/{lifetime.Answered} on this card";

            RemainingCount = _queue.Count;

            await AdvanceAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            _recording = false;
        }
    }

    [RelayCommand]
    private void EndSession()
    {
        StopClock();
        _queue.Clear();
        Current = null;
        RemainingCount = 0;
        Stage = QuizStage.Finished;
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

    // ---- change notification -----------------------------------------------

    partial void OnStageChanged(QuizStage value)
    {
        OnPropertyChanged(nameof(IsChoosingMode));
        OnPropertyChanged(nameof(IsPreparing));
        OnPropertyChanged(nameof(IsStudying));
        OnPropertyChanged(nameof(IsAnswerVisible));
        OnPropertyChanged(nameof(IsFinished));
        RaiseFaceChanged();
    }

    partial void OnModeChanged(StudyModeCard? value)
    {
        OnPropertyChanged(nameof(ShowsPickers));
        OnPropertyChanged(nameof(PrepTitle));
        OnPropertyChanged(nameof(PrepBlurb));
        OnPropertyChanged(nameof(CanStart));
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

    partial void OnQuestionRemainingChanged(TimeSpan? value) => RaiseClockChanged();

    partial void OnSessionRemainingChanged(TimeSpan? value) => RaiseClockChanged();

    private void RaiseFaceChanged()
    {
        OnPropertyChanged(nameof(ShowsQuestion));
        OnPropertyChanged(nameof(ShowsAnswer));
        OnPropertyChanged(nameof(HideClozeAnswers));
        OnPropertyChanged(nameof(CanReviewQuestion));
    }

    partial void OnReviewedCountChanged(int value)
    {
        OnPropertyChanged(nameof(ProgressLabel));
        OnPropertyChanged(nameof(AccuracyLabel));
    }

    partial void OnRemainingCountChanged(int value) => OnPropertyChanged(nameof(ProgressLabel));

    partial void OnCorrectCountChanged(int value) => OnPropertyChanged(nameof(AccuracyLabel));
}
