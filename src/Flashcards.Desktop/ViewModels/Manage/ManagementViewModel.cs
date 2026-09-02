using Flashcards.Desktop.Controls.Subjects;
using Flashcards.Desktop.ViewModels.Subjects;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Cards.Commands;
using Flashcards.Application.Cards.Queries;
using Flashcards.Application.Contracts;
using Flashcards.Application.Stats.Commands;
using Flashcards.Application.Subjects;
using Flashcards.Application.Subjects.Commands;
using Flashcards.Application.Subjects.Queries;
using Flashcards.Application.Transfer;
using Flashcards.Desktop.Services;
using Flashcards.Desktop.ViewModels.Shared;
using Flashcards.Domain.Cards;
using Flashcards.Domain.Common;
using Flashcards.Domain.Subjects;

namespace Flashcards.Desktop.ViewModels.Manage;

/// <summary>
/// The management panel: search by name or subject, then act on the results.
/// Search is debounced — retyping a filter fires one query after the typing stops rather than one
/// per keystroke.
/// </summary>
public sealed partial class ManagementViewModel : ViewModelBase
{
    private readonly IDispatcher _dispatcher;
    private readonly IDialogService _dialogs;
    private readonly IDeckFileService _files;
    private CancellationTokenSource? _debounce;

    public ManagementViewModel(IDispatcher dispatcher, IDialogService dialogs, IDeckFileService files)
    {
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _files = files;
    }

    /// <summary>Raised when the user asks to edit a card; the shell switches panels.</summary>
    public event EventHandler<Guid>? EditRequested;

    public ObservableCollection<FlashcardSummary> Results { get; } = [];

    // ---- the subject tree ------------------------------------------------
    //
    // This replaced the searchable subject drop-down that used to sit in the filter bar. With
    // subjects arranged in a tree, one control has to answer two questions — where does a subject
    // sit, and what is in it — and splitting those across a drop-down and a separate tree would
    // have meant two places showing the same set and disagreeing about the order.

    /// <summary>The whole tree, flattened, in draw order. Rows carry their own depth.</summary>
    public ObservableCollection<SubjectNodeViewModel> SubjectNodes { get; } = [];

    /// <summary>The subject whose cards the grid is showing. Null means every card.</summary>
    [ObservableProperty]
    private SubjectNodeViewModel? _selectedSubject;

    /// <summary>Narrows the tree by name. A match keeps its ancestors so the branch still reads.</summary>
    [ObservableProperty]
    private string? _subjectSearch;

    /// <summary>Typed into the "new subject" box at the foot of the tree.</summary>
    [ObservableProperty]
    private string? _newSubjectName;

    /// <summary>Why the last subject edit was refused — duplicate name, too deep, a cycle.</summary>
    [ObservableProperty]
    private string? _subjectError;

    /// <summary>All subjects as loaded, before the search filter. The tree is rebuilt from this.</summary>
    private IReadOnlyList<SubjectSummary> _allSubjects = [];

    public string SubjectScopeLabel => SelectedSubject is null
        ? "All cards"
        : $"{SelectedSubject.Name} and everything under it";

    public IReadOnlyList<CardType?> CardTypeFilters { get; } =
        [null, CardType.Standard, CardType.MultipleChoice, CardType.Cloze, CardType.Freeform];

    public IReadOnlyList<FlashcardSortField> SortFields { get; } =
        [FlashcardSortField.UpdatedUtc, FlashcardSortField.Name, FlashcardSortField.SubjectName,
         FlashcardSortField.TimesAnswered, FlashcardSortField.Accuracy];

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    private CardType? _cardTypeFilter;

    /// <summary>Show only cards that have never been answered.</summary>
    [ObservableProperty]
    private bool _untouchedOnly;

    [ObservableProperty]
    private bool _includeSuspended = true;

    [ObservableProperty]
    private FlashcardSortField _sortBy = FlashcardSortField.UpdatedUtc;

    [ObservableProperty]
    private bool _sortDescending = true;

    [ObservableProperty]
    private FlashcardSummary? _selected;

    [ObservableProperty]
    private int _page = 1;

    [ObservableProperty]
    private int _pageCount = 1;

    [ObservableProperty]
    private int _totalCount;

    public string ResultSummary => TotalCount switch
    {
        0 => "No cards match.",
        1 => "1 card",
        _ => $"{TotalCount} cards  ·  page {Page} of {PageCount}",
    };

    public override async Task ActivateAsync()
    {
        await LoadSubjectsAsync();
        await SearchAsync();
    }

    /// <summary>Clears one card's answer history — the statistics equivalent of starting over.</summary>
    [RelayCommand]
    private Task ClearHistoryAsync(FlashcardSummary? card) => RunAsync(async () =>
    {
        if (card is null)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync(
                $"Forget the history for \"{card.Name}\"?",
                "Its answer counts and percentage go back to zero. The card itself is untouched.",
                confirmText: "Forget",
                destructive: false))
        {
            return;
        }

        await _dispatcher.SendAsync(new ClearCardHistoryCommand([card.Id]));

        StatusMessage = $"Cleared the history for \"{card.Name}\".";

        // The Study panel reloads its figures on activation, so it picks this up on the way in.
        await RunSearchAsync();
    });

    // ---- import and export ------------------------------------------------
    //
    // Both go through the same picker, and neither does anything until it comes back committed.
    // The panel keeps the file handling because that is the half the picker deliberately does not
    // know about: it picks, this reads and writes.

    /// <summary>
    /// Writes chosen subjects and cards to a deck file. Opens scoped to whatever subject the tree
    /// has selected, so "export this branch" is two clicks rather than a hunt through every tick.
    /// </summary>
    [RelayCommand]
    private Task ExportAsync() => RunAsync(async () =>
    {
        var picker = new DeckTransferViewModel(_dispatcher);
        await picker.LoadForExportAsync(SelectedSubject?.Id);

        if (!await _dialogs.TransferDeckAsync(picker))
        {
            return;
        }

        var deck = await _dispatcher.QueryAsync(
            new BuildDeckExportQuery(picker.SelectedSubjectIds, picker.SelectedCardIds));

        var saved = await _files.SaveAsync(SuggestedFileName(), DeckSerializer.Write(deck));

        if (saved is null)
        {
            return;
        }

        StatusMessage = $"Exported {deck.Summary} to {saved}.";
    });

    /// <summary>
    /// Opens the prompt builder for making a deck with an assistant.
    /// <para>
    /// It sits beside Import because that is where it lands: the prompt produces a file, and this
    /// panel is where a file becomes cards. The app itself never calls a model.
    /// </para>
    /// </summary>
    [RelayCommand]
    private Task GenerateDeckAsync() => RunAsync(() => _dialogs.GenerateDeckAsync(new GenerateDeckViewModel()));

    /// <summary>
    /// Reads a deck file and brings the chosen part of it in. The file is parsed before the picker
    /// opens, so "that is not a deck" is answered straight away rather than after a selection.
    /// </summary>
    [RelayCommand]
    private Task ImportAsync() => RunAsync(async () =>
    {
        if (await _files.OpenAsync() is not { } file)
        {
            return;
        }

        DeckDocument deck;

        try
        {
            deck = DeckSerializer.Read(file.Bytes);
        }
        catch (DeckFormatException exception)
        {
            await _dialogs.ShowErrorAsync($"Cannot read {file.Name}", exception.Message);
            return;
        }

        var picker = new DeckTransferViewModel(_dispatcher);
        await picker.LoadForImportAsync(deck, file.Name);

        if (!await _dialogs.TransferDeckAsync(picker))
        {
            return;
        }

        var result = await _dispatcher.SendAsync(new ImportDeckCommand(
            deck,
            picker.SelectedSubjectNames,
            picker.SelectedCardIds,
            picker.Mode));

        // The tree and the grid can both have gained rows.
        await LoadSubjectsAsync();
        await RunSearchAsync();

        StatusMessage = result.Summary;

        if (result.Warnings.Count > 0)
        {
            // A list to work from rather than a one-line complaint, so it goes in a dialog — the
            // same call the blocked-subject-delete message makes.
            await _dialogs.ShowErrorAsync(
                "Imported, with some cards left out",
                string.Join(Environment.NewLine + Environment.NewLine, result.Warnings));
        }
    });

    /// <summary>Names the file after the scope it was exported from, dated so two are distinguishable.</summary>
    private string SuggestedFileName()
    {
        var scope = SelectedSubject?.Name ?? "flashcards";
        var cleaned = new string([.. scope.Where(c => !Path.GetInvalidFileNameChars().Contains(c))]).Trim();

        if (cleaned.Length == 0)
        {
            cleaned = "flashcards";
        }

        return $"{cleaned} {DateTime.Now:yyyy-MM-dd}{DeckSerializer.FileExtension}";
    }

    private async Task LoadSubjectsAsync()
    {
        var previouslySelected = SelectedSubject?.Id;

        // Already in tree order — see SubjectOrdering, applied in the query handler so every panel
        // agrees about the arrangement.
        _allSubjects = await _dispatcher.QueryAsync(new GetSubjectsQuery());

        RebuildTree();

        // Hold the selection across a reload, so re-parenting a subject does not also lose your
        // place in the grid beside it.
        SelectedSubject = SubjectNodes.FirstOrDefault(n => n.Id == previouslySelected);
    }

    /// <summary>
    /// Rebuilds the flattened tree from <see cref="_allSubjects"/>, applying the search.
    /// <para>
    /// A search keeps the ancestors of every match even when they do not match themselves.
    /// Filtering them out would leave a child floating at an indent with nothing above it, which
    /// reads as a broken tree rather than as a filtered one.
    /// </para>
    /// </summary>
    private void RebuildTree()
    {
        SubjectNodes.Clear();

        var term = SubjectSearch?.Trim();
        IReadOnlyCollection<Guid>? visible = null;

        if (!string.IsNullOrEmpty(term))
        {
            var hierarchy = new SubjectHierarchy(
                _allSubjects.Select(s => new SubjectPlacement(s.Id, s.ParentId, s.Name)));

            var keep = new HashSet<Guid>();

            foreach (var match in _allSubjects.Where(s => s.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)))
            {
                keep.Add(match.Id);

                foreach (var ancestor in hierarchy.AncestorsOf(match.Id))
                {
                    keep.Add(ancestor);
                }
            }

            visible = keep;
        }

        foreach (var summary in _allSubjects)
        {
            if (visible is null || visible.Contains(summary.Id))
            {
                SubjectNodes.Add(new SubjectNodeViewModel(summary));
            }
        }
    }

    /// <summary>Clears the subject scope so the grid shows every card again.</summary>
    [RelayCommand]
    private void ClearSubjectFilter() => SelectedSubject = null;

    /// <summary>
    /// Re-files one subject under another — the drop half of the tree's drag and drop.
    /// <para>
    /// Every rule about whether the move is legal lives in the command, not here. The view can
    /// only refuse a drop it can see is wrong; the command refuses one that has become wrong since
    /// the tree was drawn, which is the version that actually holds.
    /// </para>
    /// </summary>
    [RelayCommand]
    private Task MoveSubjectAsync(SubjectMove? move) => RunAsync(async () =>
    {
        if (move is null)
        {
            return;
        }

        SubjectError = null;

        try
        {
            await _dispatcher.SendAsync(new MoveSubjectCommand(move.Id, move.NewParentId));
        }
        catch (DomainException exception)
        {
            // A rejected drag is an ordinary outcome — dropping a subject on its own child, or one
            // level too deep — so it is reported next to the tree rather than thrown at the user.
            SubjectError = exception.Message;
            return;
        }

        await LoadSubjectsAsync();
        await RunSearchAsync();
    });

    [RelayCommand]
    private Task CreateSubjectAsync() => RunAsync(async () =>
    {
        SubjectError = null;

        var name = (NewSubjectName ?? string.Empty).Trim();

        if (name.Length == 0)
        {
            SubjectError = "Give the subject a name.";
            return;
        }

        try
        {
            // Filed under whatever is selected, so building a branch is: pick the parent, type,
            // press. Nothing selected means a new top-level subject.
            await _dispatcher.SendAsync(new CreateSubjectCommand(name, SelectedSubject?.Id));
        }
        catch (DomainException exception)
        {
            SubjectError = exception.Message;
            return;
        }

        NewSubjectName = null;
        await LoadSubjectsAsync();
    });

    [RelayCommand]
    private Task RenameSubjectAsync(SubjectNodeViewModel? node) => RunAsync(async () =>
    {
        if (node is null)
        {
            return;
        }

        SubjectError = null;

        var name = await _dialogs.PromptAsync("Rename subject", "New name", node.Name);

        if (string.IsNullOrWhiteSpace(name) || name.Trim() == node.Name)
        {
            return;
        }

        try
        {
            await _dispatcher.SendAsync(new RenameSubjectCommand(node.Id, name));
        }
        catch (DomainException exception)
        {
            SubjectError = exception.Message;
            return;
        }

        await LoadSubjectsAsync();
        await RunSearchAsync();
    });

    [RelayCommand]
    private Task DeleteSubjectAsync(SubjectNodeViewModel? node) => RunAsync(async () =>
    {
        if (node is null)
        {
            return;
        }

        SubjectError = null;

        // Asked before the confirmation, so an impossible delete is refused outright instead of
        // being confirmed and then rejected — the earlier order let the prompt promise cards would
        // "move up to the top level" in exactly the case where there is nowhere for them to go.
        var blockers = await _dispatcher.QueryAsync(new GetSubjectDeletionBlockersQuery(node.Id));

        if (blockers.Count > 0)
        {
            await _dialogs.ShowErrorAsync(
                $"Cannot delete \"{node.Name}\"",
                SubjectDeletion.Describe(node.Name, blockers));
            return;
        }

        var children = _allSubjects.Count(s => s.ParentId == node.Id);
        var parent = _allSubjects.FirstOrDefault(s => s.Id == node.ParentId);

        // The confirmation spells out where everything goes, because "delete" reads as "and
        // everything in it" and that is the opposite of what this does.
        var moves = new List<string>();

        if (children > 0)
        {
            moves.Add(children == 1 ? "its 1 child subject" : $"its {children} child subjects");
        }

        if (parent is not null && node.CardCount > 0)
        {
            moves.Add(node.CardCount == 1 ? "1 card" : $"{node.CardCount} cards");
        }

        var detail = moves.Count > 0
            ? parent is null
                ? $"{Capitalise(string.Join(" and ", moves))} will move up to the top level."
                : $"{Capitalise(string.Join(" and ", moves))} will move up into \"{parent.Name}\"."
            : node.CardCount > 0
                // Top level with cards, and none of them blocked: every one wears something else
                // too, so they keep a subject without needing to be promoted anywhere.
                ? $"{node.CardCount} card(s) will drop this subject and keep their others."
                : "It has no cards and nothing under it.";

        if (!await _dialogs.ConfirmAsync($"Delete \"{node.Name}\"?", detail))
        {
            return;
        }

        try
        {
            await _dispatcher.SendAsync(new DeleteSubjectCommand(node.Id));
        }
        catch (DomainException exception)
        {
            // This one names the cards standing in the way, so it goes in a dialog rather than the
            // narrow strip beside the tree — it is a list to work from, not a one-line complaint.
            await _dialogs.ShowErrorAsync($"Cannot delete \"{node.Name}\"", exception.Message);
            return;
        }

        if (SelectedSubject?.Id == node.Id)
        {
            SelectedSubject = null;
        }

        await LoadSubjectsAsync();
        await RunSearchAsync();
    });

    /// <summary>Sentence-cases a fragment assembled from counted pieces ("2 cards" → "2 cards").</summary>
    private static string Capitalise(string value)
        => value.Length == 0 ? value : char.ToUpper(value[0], System.Globalization.CultureInfo.CurrentCulture) + value[1..];

    partial void OnSubjectSearchChanged(string? value) => RebuildTree();

    partial void OnSelectedSubjectChanged(SubjectNodeViewModel? value)
    {
        OnPropertyChanged(nameof(SubjectScopeLabel));
        Page = 1;
        _ = SearchAsync();
    }

    [RelayCommand]
    private Task SearchAsync() => RunAsync(RunSearchAsync);

    private async Task RunSearchAsync()
    {
        var criteria = new FlashcardSearchCriteria
        {
            Text = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
            // One subject, and the store widens it to its descendants — selecting "SQL" searches
            // MSSQL and SQLite too, which is the whole point of the tree.
            SubjectIds = SelectedSubject is { } scope ? [scope.Id] : null,
            CardType = CardTypeFilter,
            IsSuspended = IncludeSuspended ? null : false,
            UntouchedOnly = UntouchedOnly,
            SortBy = SortBy,
            SortDescending = SortDescending,
            Page = Math.Max(Page, 1),
            PageSize = 50,
        };

        var result = await _dispatcher.QueryAsync(new SearchFlashcardsQuery(criteria));

        Results.Clear();

        foreach (var item in result.Items)
        {
            Results.Add(item);
        }

        TotalCount = result.TotalCount;
        PageCount = Math.Max(result.PageCount, 1);
        OnPropertyChanged(nameof(ResultSummary));
    }

    /// <summary>Coalesces keystrokes into a single query 300 ms after the last one.</summary>
    private void QueueSearch()
    {
        _debounce?.Cancel();
        _debounce?.Dispose();
        _debounce = new CancellationTokenSource();

        var token = _debounce.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (!token.IsCancellationRequested)
                {
                    await SearchAsync();
                }
            });
        });
    }

    [RelayCommand]
    private Task NextPageAsync() => GoToPageAsync(Page + 1);

    [RelayCommand]
    private Task PreviousPageAsync() => GoToPageAsync(Page - 1);

    private Task GoToPageAsync(int page)
    {
        if (page < 1 || page > PageCount)
        {
            return Task.CompletedTask;
        }

        Page = page;
        return SearchAsync();
    }

    [RelayCommand]
    private void Edit(FlashcardSummary? card)
    {
        if (card is not null)
        {
            EditRequested?.Invoke(this, card.Id);
        }
    }

    [RelayCommand]
    private Task DeleteAsync(FlashcardSummary? card) => RunAsync(async () =>
    {
        if (card is null)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync($"Delete \"{card.Name}\"?", "The card and its review history are removed permanently."))
        {
            return;
        }

        await _dispatcher.SendAsync(new DeleteFlashcardsCommand([card.Id]));
        StatusMessage = $"Deleted \"{card.Name}\".";
        await RunSearchAsync();
    });

    [RelayCommand]
    private Task ToggleSuspendAsync(FlashcardSummary? card) => RunAsync(async () =>
    {
        if (card is null)
        {
            return;
        }

        await _dispatcher.SendAsync(new SetCardsSuspendedCommand([card.Id], !card.IsSuspended));
        StatusMessage = card.IsSuspended ? $"\"{card.Name}\" resumed." : $"\"{card.Name}\" suspended.";
        await RunSearchAsync();
    });

    [RelayCommand]
    private Task ClearFiltersAsync() => RunAsync(async () =>
    {
        SearchText = null;
        CardTypeFilter = null;
        UntouchedOnly = false;
        IncludeSuspended = true;
        SubjectSearch = null;
        Page = 1;

        SelectedSubject = null;

        await RunSearchAsync();
    });

    partial void OnSearchTextChanged(string? value)
    {
        Page = 1;
        QueueSearch();
    }

    partial void OnCardTypeFilterChanged(CardType? value) => _ = SearchAsync();

    partial void OnUntouchedOnlyChanged(bool value) => _ = SearchAsync();


    partial void OnIncludeSuspendedChanged(bool value) => _ = SearchAsync();

    partial void OnSortByChanged(FlashcardSortField value) => _ = SearchAsync();

    partial void OnSortDescendingChanged(bool value) => _ = SearchAsync();

    partial void OnPageChanged(int value) => OnPropertyChanged(nameof(ResultSummary));

    partial void OnTotalCountChanged(int value) => OnPropertyChanged(nameof(ResultSummary));
}
