using Flashcards.Desktop.ViewModels.Shared;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Cards.Queries;
using Flashcards.Application.Contracts;
using Flashcards.Application.Subjects.Queries;
using Flashcards.Application.Transfer;

namespace Flashcards.Desktop.ViewModels.Manage;

/// <summary>Which way the deck is moving. The picker is the same screen either way.</summary>
public enum TransferDirection
{
    Export,
    Import,
}

/// <summary>
/// Choosing what goes into a deck file, or what comes out of one.
/// <para>
/// Two tiers, subjects then cards, the same arrangement the Study panel uses to assemble a custom
/// session — because it is the same question. The tiers are linked the same way too: ticking
/// subjects decides which cards are on offer below, and every card that appears there arrives
/// ticked — untick what you do not want.
/// </para>
/// <para>
/// This view model only <em>picks</em>. It never reads or writes a file and never runs the import:
/// it hands back what was ticked and the management panel does the rest. That is what lets one
/// screen serve both directions without growing a second personality.
/// </para>
/// </summary>
public sealed partial class DeckTransferViewModel(IDispatcher dispatcher) : ViewModelBase
{
    /// <summary>Raised when the dialog should close. True means the user committed.</summary>
    public event EventHandler<bool>? Closed;

    /// <summary>Every card in the file, before the subject tier narrows it. Import only.</summary>
    private readonly List<TransferCardViewModel> _deckCards = [];

    /// <summary>Child-name lookup over the file's own tree, so a ticked subject can widen to its subtree.</summary>
    private readonly Dictionary<string, List<string>> _deckChildren = new(StringComparer.CurrentCultureIgnoreCase);

    public TransferDirection Direction { get; private set; } = TransferDirection.Export;

    public bool IsImport => Direction == TransferDirection.Import;

    public bool IsExport => Direction == TransferDirection.Export;

    public ObservableCollection<TransferSubjectViewModel> SubjectPicks { get; } = [];

    public ObservableCollection<TransferCardViewModel> CardPicks { get; } = [];

    /// <summary>What to do about a card the library already has. Import only.</summary>
    [ObservableProperty]
    private DeckImportMode _mode = DeckImportMode.Skip;

    /// <summary>The file being read, for the header. Import only.</summary>
    [ObservableProperty]
    private string? _sourceName;

    public string Title => IsImport ? "Import a deck" : "Export a deck";

    public string Intro => IsImport
        ? "Tick what you want to bring in. A card brings the subjects it is filed under with it, whether or not you tick them."
        : "Tick what you want to write to the file. A card takes the subjects it is filed under with it, and each of those takes its parents.";

    public string ConfirmText => IsImport ? "Import" : "Export";

    // ---- the selection, in the shapes the two operations want it -----------

    private IEnumerable<TransferSubjectViewModel> IncludedSubjects => SubjectPicks.Where(s => s.IsIncluded);

    /// <summary>The cards that are ticked, and only those — the Study panel's rule.</summary>
    private IReadOnlyList<TransferCardViewModel> Selection => [.. CardPicks.Where(c => c.IsIncluded)];

    public IReadOnlyCollection<Guid> SelectedSubjectIds => [.. IncludedSubjects.Select(s => s.Id).OfType<Guid>()];

    public IReadOnlyCollection<string> SelectedSubjectNames => [.. IncludedSubjects.Select(s => s.Name)];

    public IReadOnlyCollection<Guid> SelectedCardIds => [.. Selection.Select(c => c.Id)];

    public int SelectedSubjectCount => IncludedSubjects.Count();

    public int SelectedCardCount => Selection.Count;

    public string SubjectSummary => SelectedSubjectCount == 1 ? "1 subject" : $"{SelectedSubjectCount} subjects";

    public string CardSummary => SelectedCardCount == 1 ? "1 card" : $"{SelectedCardCount} cards";

    /// <summary>A deck of subjects and no cards is legitimate — it is a tree you are handing over.</summary>
    public bool CanConfirm => SelectedCardCount > 0 || SelectedSubjectCount > 0;

    // ---- loading -----------------------------------------------------------

    /// <summary>
    /// Fills the picker from the library. <paramref name="scopeSubjectId"/> is whatever the manage
    /// panel had selected, so pressing Export while looking at one subject opens with that subject
    /// ticked rather than with the whole library ticked.
    /// </summary>
    public async Task LoadForExportAsync(Guid? scopeSubjectId = null)
    {
        Direction = TransferDirection.Export;

        var subjects = await dispatcher.QueryAsync(new GetSubjectsQuery());

        SubjectPicks.Clear();

        foreach (var subject in subjects)
        {
            var pick = new TransferSubjectViewModel
            {
                Id = subject.Id,
                Name = subject.Name,
                ColorHex = subject.ColorHex,
                Depth = subject.Depth,
                CardCount = subject.TotalCardCount,
                IsIncluded = scopeSubjectId is null || subject.Id == scopeSubjectId,
            };

            pick.PropertyChanged += OnSubjectPickChanged;
            SubjectPicks.Add(pick);
        }

        await LoadCardsForSelectionAsync();
    }

    /// <summary>
    /// Fills the picker from a file that has already been parsed. Rows that name something the
    /// library already has are annotated rather than hidden — you may well want to import them
    /// anyway, and the conflict setting below decides what that does.
    /// </summary>
    public async Task LoadForImportAsync(DeckDocument deck, string? sourceName = null)
    {
        Direction = TransferDirection.Import;
        SourceName = sourceName;

        var existingSubjects = (await dispatcher.QueryAsync(new GetSubjectsQuery()))
            .Select(s => s.Name)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);

        var existingCards = (await dispatcher.QueryAsync(new SearchFlashcardsQuery(new FlashcardSearchCriteria
        {
            PageSize = 1000,
        }))).Items.Select(c => c.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);

        _deckChildren.Clear();

        foreach (var subject in deck.Subjects.Where(s => s.Parent is not null))
        {
            if (!_deckChildren.TryGetValue(subject.Parent!, out var children))
            {
                _deckChildren[subject.Parent!] = children = [];
            }

            children.Add(subject.Name);
        }

        var depths = DepthsOf(deck);
        var tally = deck.Cards
            .SelectMany(c => c.Subjects)
            .GroupBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.CurrentCultureIgnoreCase);

        SubjectPicks.Clear();

        foreach (var subject in deck.Subjects)
        {
            var pick = new TransferSubjectViewModel
            {
                Name = subject.Name,
                ColorHex = subject.ColorHex,
                ParentName = subject.Parent,
                Depth = depths.GetValueOrDefault(subject.Name, 1),
                CardCount = tally.GetValueOrDefault(subject.Name),
                Note = existingSubjects.Contains(subject.Name) ? "already here" : null,
            };

            pick.PropertyChanged += OnSubjectPickChanged;
            SubjectPicks.Add(pick);
        }

        _deckCards.Clear();

        foreach (var card in deck.Cards)
        {
            _deckCards.Add(new TransferCardViewModel
            {
                Id = card.Id,
                Name = card.Name,
                CardType = card.CardType,
                TagNames = card.Subjects,
                Tags = [.. card.Subjects.Select(name => new TransferTag(
                    name,
                    deck.Subjects.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.CurrentCultureIgnoreCase))?.ColorHex))],
                Note = existingCards.Contains(card.Name) ? "already here" : null,
            });
        }

        await LoadCardsForSelectionAsync();
    }

    /// <summary>
    /// How deep each subject sits according to the file's own parent links. Capped by the visited
    /// set rather than by the depth limit, because a hand-edited deck can describe a cycle and
    /// this walk has to finish either way.
    /// </summary>
    private static Dictionary<string, int> DepthsOf(DeckDocument deck)
    {
        var parents = new Dictionary<string, string?>(StringComparer.CurrentCultureIgnoreCase);

        foreach (var subject in deck.Subjects)
        {
            parents[subject.Name] = subject.Parent;
        }

        var depths = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);

        foreach (var subject in deck.Subjects)
        {
            var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            var depth = 1;
            var walk = subject.Name;

            while (seen.Add(walk) && parents.TryGetValue(walk, out var parent) && parent is not null)
            {
                depth++;
                walk = parent;
            }

            depths[subject.Name] = depth;
        }

        return depths;
    }

    // ---- the link between the two tiers ------------------------------------

    private bool _suspendCardReload;

    private void OnSubjectPickChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TransferSubjectViewModel.IsIncluded) || _suspendCardReload)
        {
            return;
        }

        RaiseSelectionChanged();
        _ = LoadCardsForSelectionAsync();
    }

    /// <summary>
    /// Refills the card tier from whatever subjects are ticked, so what is on offer below is
    /// always a subset of what is chosen above. On the way out that is a search; on the way in it
    /// is a filter over the cards the file already handed us.
    /// <para>
    /// Everything it offers starts ticked, and changing the subjects re-ticks the lot: the subject
    /// tier is the coarse choice and this is the fine one, so the fine one begins by agreeing.
    /// </para>
    /// </summary>
    private async Task LoadCardsForSelectionAsync()
    {
        foreach (var stale in CardPicks)
        {
            stale.PropertyChanged -= OnCardPickChanged;
        }

        CardPicks.Clear();

        foreach (var card in await CardsInScopeAsync())
        {
            card.IsIncluded = true;
            card.PropertyChanged += OnCardPickChanged;
            CardPicks.Add(card);
        }

        RaiseSelectionChanged();
    }

    private async Task<IReadOnlyList<TransferCardViewModel>> CardsInScopeAsync()
    {
        if (IsImport)
        {
            // Widened to the file's own subtrees, so ticking a parent offers the cards filed under
            // its children — the same reading the library gives a selected subject.
            var scope = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

            foreach (var subject in IncludedSubjects)
            {
                Widen(subject.Name, scope);
            }

            return [.. _deckCards.Where(c => c.TagNames.Any(scope.Contains))];
        }

        var subjectIds = SelectedSubjectIds;

        if (subjectIds.Count == 0)
        {
            return [];
        }

        var results = await dispatcher.QueryAsync(new SearchFlashcardsQuery(new FlashcardSearchCriteria
        {
            // The read store widens these to their descendants, so ticking a parent offers
            // everything underneath it.
            SubjectIds = subjectIds,
            SortBy = FlashcardSortField.Name,
            SortDescending = false,
            PageSize = 1000,
        }));

        return
        [
            .. results.Items.Select(card => new TransferCardViewModel
            {
                Id = card.Id,
                Name = card.Name,
                CardType = card.CardType,
                TagNames = [.. card.Subjects.Select(s => s.Name)],
                Tags = [.. card.Subjects.Where(s => !s.IsInherited).Select(s => new TransferTag(s.Name, s.ColorHex))],
            }),
        ];
    }

    private void Widen(string name, HashSet<string> into)
    {
        if (!into.Add(name) || !_deckChildren.TryGetValue(name, out var children))
        {
            return;
        }

        foreach (var child in children)
        {
            Widen(child, into);
        }
    }

    private void OnCardPickChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TransferCardViewModel.IsIncluded))
        {
            RaiseSelectionChanged();
        }
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedSubjectCount));
        OnPropertyChanged(nameof(SelectedCardCount));
        OnPropertyChanged(nameof(SubjectSummary));
        OnPropertyChanged(nameof(CardSummary));
        OnPropertyChanged(nameof(CanConfirm));
    }

    // ---- commands ----------------------------------------------------------

    /// <summary>
    /// Ticks or clears every subject at once, held so that pressing All over twenty subjects runs
    /// one card query rather than twenty.
    /// </summary>
    [RelayCommand]
    private async Task SetAllSubjectsAsync(string? included)
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
        await LoadCardsForSelectionAsync();
    }

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

    [RelayCommand]
    private void Confirm() => Closed?.Invoke(this, true);

    [RelayCommand]
    private void Cancel() => Closed?.Invoke(this, false);

    partial void OnModeChanged(DeckImportMode value) => OnPropertyChanged(nameof(ModeExplanation));

    /// <summary>Spelled out under the choice, because "replace" is the one that loses something.</summary>
    public string ModeExplanation => Mode == DeckImportMode.Replace
        ? "A card already in your library is overwritten by the one in the file. Its answer history is kept."
        : "A card already in your library is left exactly as it is.";
}
