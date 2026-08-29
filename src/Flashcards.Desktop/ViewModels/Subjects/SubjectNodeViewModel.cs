using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using Flashcards.Application.Contracts;

namespace Flashcards.Desktop.ViewModels.Subjects;

/// <summary>
/// One row of the manage panel's subject tree.
/// <para>
/// The tree is rendered as a flat list whose rows carry their own depth, rather than as nested
/// containers. That is what makes drag-and-drop tractable: every row is a sibling in the same
/// items panel, so a drop lands on exactly one row and there is no ambiguity about whether the
/// pointer is over a child or the container holding it.
/// </para>
/// </summary>
public sealed partial class SubjectNodeViewModel(SubjectSummary subject) : ObservableObject
{
    public SubjectSummary Subject { get; } = subject;

    public Guid Id => Subject.Id;

    /// <summary>
    /// The name shown on the row. Settable so the create modal's pending row can follow the name
    /// box keystroke by keystroke; for a real subject it is just <c>Subject.Name</c>.
    /// </summary>
    [ObservableProperty]
    private string _name = subject.Name;

    public string? ColorHex => Subject.ColorHex;

    /// <summary>Where this row currently sits. Mutable so a drag can re-file it before it is saved.</summary>
    [ObservableProperty]
    private Guid? _parentId = subject.ParentId;

    /// <summary>1 at the top level. Recomputed by whoever owns the list after a move.</summary>
    [ObservableProperty]
    private int _depth = subject.Depth;

    /// <summary>
    /// A subject that does not exist yet — the row the create modal shows for the thing being
    /// added. It is the only row that modal lets you drag, and it is drawn as an outline so it
    /// never reads as something already saved.
    /// </summary>
    public bool IsPending { get; init; }

    /// <summary>
    /// Whether this row may be picked up. The manage tree lets you drag anything; the create modal
    /// only lets you drag the pending row, because moving a real subject there would be a live edit
    /// made from inside a dialog that has not been confirmed yet.
    /// </summary>
    public bool CanDrag { get; init; } = true;

    /// <summary>Cards wearing this exact subject.</summary>
    public int CardCount => Subject.CardCount;

    /// <summary>Cards in this subject and everything under it — what filtering by it shows.</summary>
    public int TotalCardCount => Subject.TotalCardCount;

    /// <summary>Shown only where the two differ, so a parent says where its cards actually live.</summary>
    public bool ShowsDirectCount => !IsPending && TotalCardCount != CardCount;

    /// <summary>Counts are meaningless for a subject that does not exist yet.</summary>
    public bool ShowsCounts => !IsPending;

    /// <summary>One step per level below the root. See <c>DepthToIndentConverter</c> for the reasoning.</summary>
    public Thickness Indent => new((Depth - 1) * 18, 0, 0, 0);

    partial void OnDepthChanged(int value) => OnPropertyChanged(nameof(Indent));

    /// <summary>
    /// Whether a drag is currently hovering over this row. Purely visual, and set by the view —
    /// the alternative, styling from a pseudo-class, cannot see which row the drag data would
    /// actually land on.
    /// </summary>
    [ObservableProperty]
    private bool _isDropTarget;

    /// <summary>True while this row is the one being dragged, so it can fade out under the pointer.</summary>
    [ObservableProperty]
    private bool _isDragging;
}
