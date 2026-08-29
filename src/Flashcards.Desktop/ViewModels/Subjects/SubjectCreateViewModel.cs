using Flashcards.Desktop.Controls.Subjects;
using Flashcards.Desktop.ViewModels.Shared;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Contracts;
using Flashcards.Application.Subjects.Commands;
using Flashcards.Application.Subjects.Queries;
using Flashcards.Domain.Common;
using Flashcards.Domain.Subjects;

namespace Flashcards.Desktop.ViewModels.Subjects;

/// <summary>
/// The create-a-subject dialog: a name, and a tree you place it in by dragging.
/// <para>
/// The whole point is that placement is shown rather than described. The previous flow had a
/// "file under…" drop-down listing every subject as flat text, which asked you to hold the shape of
/// the tree in your head to answer a question that is entirely about shape. Here the new subject is
/// a real row in a real tree from the moment the dialog opens, and moving it is the same gesture as
/// moving anything else.
/// </para>
/// <para>
/// Nothing is written until Create is pressed. The pending row exists only in this view model, and
/// the existing rows are copies — they cannot be dragged, because a live edit made from inside an
/// unconfirmed dialog would be a surprise either way it went.
/// </para>
/// </summary>
public sealed partial class SubjectCreateViewModel : ObservableObject
{
    /// <summary>
    /// The id the pending subject answers to while it is being placed.
    /// <para>
    /// It needs one so it can be dragged and be a parent like any other row. A version-7 GUID would
    /// do, but a fixed sentinel makes it obvious in a debugger that this row is not a real subject,
    /// and it is never written anywhere — creation mints a fresh id.
    /// </para>
    /// </summary>
    private static readonly Guid PendingId = new("11111111-1111-1111-1111-111111111111");

    private readonly IDispatcher _dispatcher;
    private IReadOnlyList<SubjectSummary> _existing = [];

    public SubjectCreateViewModel(IDispatcher dispatcher, string? initialName)
    {
        _dispatcher = dispatcher;
        _name = initialName?.Trim() ?? string.Empty;
    }

    /// <summary>Raised when the dialog is finished with. True if a subject was actually created.</summary>
    public event EventHandler<bool>? Closed;

    /// <summary>The id of the subject that was created, for the caller to tag the card with.</summary>
    public string? CreatedName { get; private set; }

    /// <summary>Existing subjects plus the pending one, flattened in draw order.</summary>
    public ObservableCollection<SubjectNodeViewModel> Nodes { get; } = [];

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>The row for the subject being created. Held so its placement can be read back.</summary>
    private SubjectNodeViewModel? _pending;

    public string PlacementLabel
    {
        get
        {
            if (_pending?.ParentId is not { } parentId)
            {
                return "At the top level";
            }

            var parent = _existing.FirstOrDefault(s => s.Id == parentId);

            return parent is null ? "At the top level" : $"Inside \"{parent.Name}\"";
        }
    }

    public async Task LoadAsync()
    {
        _existing = await _dispatcher.QueryAsync(new GetSubjectsQuery());

        _pending = new SubjectNodeViewModel(new SubjectSummary(
            PendingId,
            DisplayName,
            // No identity colour yet — the real one is derived from the name when the subject is
            // minted, so anything chosen here would be a guess that changes on save.
            ColorHex: null,
            Description: null,
            CardCount: 0))
        {
            IsPending = true,
            CanDrag = true,
        };

        Rebuild();
    }

    /// <summary>What the pending row is labelled while the name box is empty.</summary>
    private string DisplayName => string.IsNullOrWhiteSpace(Name) ? "New subject" : Name.Trim();

    /// <summary>
    /// Rebuilds the flattened tree with the pending row in its current place.
    /// <para>
    /// Everything is rebuilt rather than patched because a move changes the depth of a whole branch,
    /// and re-deriving from <see cref="SubjectHierarchy"/> is the only version that cannot drift
    /// from what the write side will do with the same placement.
    /// </para>
    /// </summary>
    private void Rebuild()
    {
        if (_pending is null)
        {
            return;
        }

        var placements = _existing
            .Select(s => new SubjectPlacement(s.Id, s.ParentId, s.Name))
            .Append(new SubjectPlacement(PendingId, _pending.ParentId, DisplayName));

        var hierarchy = new SubjectHierarchy(placements);
        var byId = _existing.ToDictionary(s => s.Id);

        Nodes.Clear();

        foreach (var (subject, depth) in hierarchy.InTreeOrder())
        {
            if (subject.Id == PendingId)
            {
                _pending.Depth = depth;
                _pending.Name = DisplayName;
                Nodes.Add(_pending);
                continue;
            }

            if (!byId.TryGetValue(subject.Id, out var summary))
            {
                continue;
            }

            Nodes.Add(new SubjectNodeViewModel(summary)
            {
                // Existing subjects are drop targets, not cargo.
                CanDrag = false,
            });
        }

        OnPropertyChanged(nameof(PlacementLabel));
    }

    /// <summary>Re-files the pending row. The tree control hands us every drop; we ignore the rest.</summary>
    [RelayCommand]
    private void Move(SubjectMove? move)
    {
        if (move is null || _pending is null || move.Id != PendingId)
        {
            return;
        }

        var hierarchy = new SubjectHierarchy(
            _existing.Select(s => new SubjectPlacement(s.Id, s.ParentId, s.Name)));

        // The pending subject is always a leaf, so only the target's own depth constrains it —
        // exactly the check CreateSubjectCommand will run again on submit.
        if (!hierarchy.CanAddUnder(move.NewParentId, out var reason))
        {
            Error = reason;
            return;
        }

        Error = null;
        _pending.ParentId = move.NewParentId;

        Rebuild();
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var trimmed = Name.Trim();

        if (trimmed.Length == 0)
        {
            Error = "Give the subject a name.";
            return;
        }

        // Checked here for a friendly message, and again by the command against the database, which
        // is the version that actually holds if another panel added the same name meanwhile.
        if (_existing.FirstOrDefault(s => string.Equals(s.Name, trimmed, StringComparison.CurrentCultureIgnoreCase)) is { } clash)
        {
            Error = $"A subject called \"{clash.Name}\" already exists.";
            return;
        }

        IsBusy = true;

        try
        {
            await _dispatcher.SendAsync(new CreateSubjectCommand(trimmed, _pending?.ParentId));

            CreatedName = trimmed;
            Closed?.Invoke(this, true);
        }
        catch (DomainException exception)
        {
            Error = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => Closed?.Invoke(this, false);

    partial void OnNameChanged(string value)
    {
        Error = null;

        // The pending row follows the name box keystroke by keystroke — the tree is a preview, and
        // a preview that lagged the thing it previews would be worse than none.
        Rebuild();
    }
}
