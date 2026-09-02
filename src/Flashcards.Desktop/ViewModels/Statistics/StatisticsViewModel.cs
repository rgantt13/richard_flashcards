using CommunityToolkit.Mvvm.ComponentModel;
using Flashcards.Application.Abstractions.Messaging;
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

        Browser = new SubjectBrowserViewModel(dispatcher) { ShowsCardSelection = false };
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

    public override Task ActivateAsync() => RunAsync(async () =>
    {
        await Browser.RefreshAsync();

        // Derived from the figures the browser has just loaded rather than queried again — see
        // SubjectHighlights for why one source beats two.
        Highlights = SubjectHighlights.From(await _dispatcher.QueryAsync(new GetSubjectStatsQuery()));
    });
}
