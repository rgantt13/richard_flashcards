using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Flashcards.Desktop.ViewModels.Subjects;

namespace Flashcards.Desktop.Controls.Subjects;

/// <summary>Where a dragged subject was dropped. Null <see cref="NewParentId"/> means the top level.</summary>
public sealed record SubjectMove(Guid Id, Guid? NewParentId);

/// <summary>
/// A flat, indented, drag-to-re-file list of subjects.
/// <para>
/// Extracted from the manage panel once the create modal needed the same thing. Duplicating it
/// would have meant two copies of the pointer-threshold and drop-target logic drifting apart, and
/// that logic is the part most likely to need fixing.
/// </para>
/// <para>
/// The tree is drawn as a flat list whose rows carry their own depth rather than as nested
/// containers, which is what makes the drop unambiguous: every row is a sibling in one items panel,
/// so a drop lands on exactly one of them with no question of whether the pointer is over a child
/// or over the box holding it.
/// </para>
/// <para>
/// This control decides <em>where</em> something was dropped and nothing else. Whether the move is
/// legal — no cycles, nothing past the depth limit — belongs to whoever handles
/// <see cref="MoveCommand"/>, because only they know if the move is a live edit or a staged one.
/// </para>
/// </summary>
public partial class SubjectTreeView : UserControl
{
    /// <summary>Identifies our own payload, so a drag from outside the app is ignored.</summary>
    private const string SubjectFormat = "flashcards/subject-id";

    /// <summary>
    /// How far the pointer must travel before a press becomes a drag.
    /// <para>
    /// Without a threshold every click on a row would start one, and a click is also how you select
    /// a row — the two gestures begin identically and are only told apart by whether it moves.
    /// </para>
    /// </summary>
    private const double DragThreshold = 4;

    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<SubjectTreeView, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<SubjectTreeView, object?>(
            nameof(SelectedItem), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Executed with a <see cref="SubjectMove"/> when a row is dropped somewhere new.</summary>
    public static readonly StyledProperty<ICommand?> MoveCommandProperty =
        AvaloniaProperty.Register<SubjectTreeView, ICommand?>(nameof(MoveCommand));

    /// <summary>Shows the per-row rename and delete buttons. Off for a read-only placement tree.</summary>
    public static readonly StyledProperty<bool> ShowActionsProperty =
        AvaloniaProperty.Register<SubjectTreeView, bool>(nameof(ShowActions));

    public static readonly StyledProperty<ICommand?> RenameCommandProperty =
        AvaloniaProperty.Register<SubjectTreeView, ICommand?>(nameof(RenameCommand));

    public static readonly StyledProperty<ICommand?> DeleteCommandProperty =
        AvaloniaProperty.Register<SubjectTreeView, ICommand?>(nameof(DeleteCommand));

    private Point _pressOrigin;
    private SubjectNodeViewModel? _pressed;
    private SubjectNodeViewModel? _highlighted;

    public SubjectTreeView()
    {
        InitializeComponent();

        Rows.AddHandler(PointerPressedEvent, OnPointerPressedTunnel, RoutingStrategies.Tunnel);
        Rows.PointerMoved += OnRowsPointerMoved;
        Rows.AddHandler(PointerReleasedEvent, (_, _) => _pressed = null, RoutingStrategies.Tunnel);

        Rows.AddHandler(DragDrop.DragOverEvent, OnRowsDragOver);
        Rows.AddHandler(DragDrop.DropEvent, OnRowsDrop);
        Rows.AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearHighlight());
    }

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public ICommand? MoveCommand
    {
        get => GetValue(MoveCommandProperty);
        set => SetValue(MoveCommandProperty, value);
    }

    public bool ShowActions
    {
        get => GetValue(ShowActionsProperty);
        set => SetValue(ShowActionsProperty, value);
    }

    public ICommand? RenameCommand
    {
        get => GetValue(RenameCommandProperty);
        set => SetValue(RenameCommandProperty, value);
    }

    public ICommand? DeleteCommand
    {
        get => GetValue(DeleteCommandProperty);
        set => SetValue(DeleteCommandProperty, value);
    }

    private void OnPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        // Only arm on the row itself. Pressing a row action is a click on a button that happens to
        // sit on a row, and turning that into a drag would make both unusable.
        if (e.Source is Control source && source.FindAncestorOfType<Button>() is not null)
        {
            _pressed = null;
            return;
        }

        var node = NodeUnder(e.Source as Control);

        _pressed = node is { CanDrag: true } ? node : null;
        _pressOrigin = e.GetPosition(Rows);
    }

    private async void OnRowsPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is null || !e.GetCurrentPoint(Rows).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var moved = e.GetPosition(Rows) - _pressOrigin;

        if (Math.Abs(moved.X) < DragThreshold && Math.Abs(moved.Y) < DragThreshold)
        {
            return;
        }

        var dragged = _pressed;
        _pressed = null;

        var payload = new DataObject();
        payload.Set(SubjectFormat, dragged.Id.ToString());

        dragged.IsDragging = true;

        try
        {
            await DragDrop.DoDragDrop(e, payload, DragDropEffects.Move);
        }
        finally
        {
            // DoDragDrop returns once the gesture ends however it ended — dropped, cancelled, or
            // released over nothing — so this is the one place guaranteed to run.
            dragged.IsDragging = false;
            ClearHighlight();
        }
    }

    private void OnRowsDragOver(object? sender, DragEventArgs e)
    {
        var dragged = DraggedNode(e);

        if (dragged is null)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        var target = NodeUnder(e.Source as Control);

        // Dropping onto empty space below the rows promotes the subject to the top level, which is
        // the only way out of a branch when there is no other branch to put it in.
        e.DragEffects = DragDropEffects.Move;

        Highlight(target is not null && target.Id != dragged.Id ? target : null);
    }

    private void OnRowsDrop(object? sender, DragEventArgs e)
    {
        var dragged = DraggedNode(e);

        ClearHighlight();

        if (dragged is null)
        {
            return;
        }

        var target = NodeUnder(e.Source as Control);

        if (target?.Id == dragged.Id)
        {
            return;
        }

        var move = new SubjectMove(dragged.Id, target?.Id);

        if (MoveCommand is { } command && command.CanExecute(move))
        {
            command.Execute(move);
        }
    }

    private SubjectNodeViewModel? DraggedNode(DragEventArgs e)
    {
        if (e.Data.Get(SubjectFormat) is not string raw || !Guid.TryParse(raw, out var id))
        {
            return null;
        }

        return ItemsSource?.OfType<SubjectNodeViewModel>().FirstOrDefault(n => n.Id == id);
    }

    /// <summary>Walks up from whatever was hit to the row's own view model.</summary>
    private static SubjectNodeViewModel? NodeUnder(Control? source)
    {
        for (var current = source; current is not null; current = current.Parent as Control)
        {
            if (current.DataContext is SubjectNodeViewModel node)
            {
                return node;
            }
        }

        return null;
    }

    private void Highlight(SubjectNodeViewModel? node)
    {
        if (ReferenceEquals(_highlighted, node))
        {
            return;
        }

        ClearHighlight();

        if (node is not null)
        {
            node.IsDropTarget = true;
            _highlighted = node;
        }
    }

    private void ClearHighlight()
    {
        if (_highlighted is not null)
        {
            _highlighted.IsDropTarget = false;
            _highlighted = null;
        }
    }
}
