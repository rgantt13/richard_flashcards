using CommunityToolkit.Mvvm.ComponentModel;
using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Contracts;
using Flashcards.Application.Stats.Queries;
using Flashcards.Desktop.ViewModels.Shared;

namespace Flashcards.Desktop.ViewModels.Statistics;

/// <summary>
/// How you are doing, and nothing else. No way to start a sitting from here on purpose: this is
/// the panel you open to look, and mixing "review your record" with "begin studying" is what made
/// the old study screen carry two jobs at once.
/// <para>
/// The three familiar tiers — the library, subjects, cards — are the same
/// <see cref="SubjectBrowserViewModel"/> the Custom prep screen uses, with its card tick boxes
/// turned off. Ticking a card here would mean nothing, because there is nothing to start.
/// </para>
/// </summary>
public sealed partial class StatisticsViewModel : ViewModelBase
{
    private readonly IDispatcher _dispatcher;

    public StatisticsViewModel(IDispatcher dispatcher)
    {
        _dispatcher = dispatcher;

        Browser = new SubjectBrowserViewModel(dispatcher) { ShowsSelection = false };
        Browser.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SubjectBrowserViewModel.ErrorMessage))
            {
                ErrorMessage = Browser.ErrorMessage;
            }
        };
    }

    public SubjectBrowserViewModel Browser { get; }

    /// <summary>The subjects that stand out — the tier that sits between the library and the list.</summary>
    [ObservableProperty]
    private SubjectHighlights _highlights = SubjectHighlights.Empty;

    /// <summary>
    /// A year of answering, by day. The one figure on this panel that is not a lifetime total:
    /// everything else here says how much and how well, and this says how often.
    /// </summary>
    [ObservableProperty]
    private ActivityHistory _activity = ActivityHistory.Empty;

    // ---- the streak counters, worded rather than formatted -----------------
    //
    // A bare number needs a caption to mean anything, and the caption changes with the number:
    // "1 days in a row" is the kind of thing that makes an app feel unfinished.

    public string CurrentStreakFigure => Figure(Activity.CurrentStreak);

    public string CurrentStreakDetail => Activity.CurrentStreak switch
    {
        0 => "nothing yesterday or today",
        1 => "day in a row",
        _ => "days in a row",
    };

    public string LongestStreakFigure => Figure(Activity.LongestStreak);

    public string LongestStreakDetail => Activity.LongestStreak switch
    {
        0 => "nothing yet",
        1 => "day, at your best",
        _ => "days, at your best",
    };

    public string DaysStudiedFigure => Figure(Activity.DaysStudied);

    public string DaysStudiedDetail => Activity switch
    {
        { Days.Count: 0 } => "no history yet",
        { DaysStudied: 0 } => $"nothing in the last {Activity.Days.Count}",
        _ => $"days of the last {Activity.Days.Count}",
    };

    public string YearAnswersFigure => Figure(Activity.Answered);

    public string YearAnswersDetail => Activity.Answered == 0
        ? "nothing answered this year"
        : $"answers, {Activity.Correct} of them right";

    private static string Figure(int value) => value == 0 ? "—" : value.ToString("N0");

    partial void OnActivityChanged(ActivityHistory value)
    {
        OnPropertyChanged(nameof(CurrentStreakFigure));
        OnPropertyChanged(nameof(CurrentStreakDetail));
        OnPropertyChanged(nameof(LongestStreakFigure));
        OnPropertyChanged(nameof(LongestStreakDetail));
        OnPropertyChanged(nameof(DaysStudiedFigure));
        OnPropertyChanged(nameof(DaysStudiedDetail));
        OnPropertyChanged(nameof(YearAnswersFigure));
        OnPropertyChanged(nameof(YearAnswersDetail));
    }

    public override Task ActivateAsync() => RunAsync(async () =>
    {
        await Browser.RefreshAsync();

        // Derived from the figures the browser has just loaded rather than queried again — see
        // SubjectHighlights for why one source beats two.
        Highlights = SubjectHighlights.From(await _dispatcher.QueryAsync(new GetSubjectStatsQuery()));

        // A year, plus enough of the week before it that the grid can start on a Monday.
        Activity = await _dispatcher.QueryAsync(new GetActivityHistoryQuery(371));
    });
}
