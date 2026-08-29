using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Flashcards.Desktop.ViewModels.Design;

namespace Flashcards.Desktop.Views.Design;

/// <summary>
/// The freeform artboard's pointer work: placing an element where you click, and moving or
/// resizing one by dragging it.
/// <para>
/// This lives here rather than on <see cref="CardEditorView"/> because all of it is about
/// <c>DesignSurface</c>, which is this board's own control. The parent still owns the tunnelling
/// press handler — it has to, because clicking to select works on every board — and calls into the
/// two public methods below when the press concerns this one.
/// </para>
/// </summary>
public partial class DesignerFreeformBoard : UserControl
{
    /// <summary>Card units the pointer must travel before a press becomes a drag rather than a click.</summary>
    private const double DragThreshold = 3d;

    /// <summary>
    /// A drag's starting state. The element's geometry is snapshotted at the moment the pointer
    /// went down and every move is applied as an offset from it, so the element tracks the cursor
    /// exactly instead of accumulating rounding drift frame by frame.
    /// </summary>
    private sealed record DragState(
        BlockEditorViewModel Block,
        Point Origin,
        double StartX,
        double StartY,
        double StartWidth,
        double StartHeight,
        bool IsResize);

    /// <summary>
    /// A drag that has been set up but not started. Nothing is captured until the pointer actually
    /// travels — capturing on press would redirect every following pointer event to the canvas,
    /// which stops a TextBox ever taking focus and stops a Button ever seeing its release.
    /// </summary>
    private DragState? _pending;

    private DragState? _drag;

    public DesignerFreeformBoard()
    {
        InitializeComponent();

        // Move and resize are tracked on the artboard rather than on each element: the pointer
        // routinely leaves the element it is dragging, and capture has to live somewhere stable.
        DesignSurface.PointerMoved += OnDesignPointerMoved;
        DesignSurface.PointerReleased += OnDesignPointerReleased;
    }

    /// <summary>
    /// Places the armed element where the pointer went down, if that was on bare canvas. Returns
    /// whether it did, so the caller knows whether the press was consumed.
    /// </summary>
    public bool TryPlaceElement(CardEditorViewModel viewModel, PointerPressedEventArgs e)
    {
        if (viewModel.PendingElementKind is not { } kind || !DesignSurface.IsVisible)
        {
            return false;
        }

        var position = e.GetPosition(DesignSurface);

        if (position.X < 0 || position.Y < 0
            || position.X > DesignSurface.Bounds.Width
            || position.Y > DesignSurface.Bounds.Height)
        {
            return false;
        }

        viewModel.PlaceElementAt(kind, position.X, position.Y);
        return true;
    }

    /// <summary>Arms a move or resize for the pressed element. Nothing happens until it travels.</summary>
    public void BeginDrag(CardEditorViewModel viewModel, BlockEditorViewModel block, PointerPressedEventArgs e)
    {
        _pending = null;
        _drag = null;

        // Only the pointer tool moves things; the pen tools are busy drawing. And only elements
        // that are actually placed on a canvas can be dragged.
        if (!viewModel.IsFreeform || viewModel.ActiveTool != FreeformTool.Select || !block.IsPlaced)
        {
            return;
        }

        if (!e.GetCurrentPoint(DesignSurface).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var source = e.Source as Control;
        var onResizeGrip = HasTag(source, "resize");
        var onMoveHandle = HasTag(source, "move");

        // Presses that belong to something else: inside the text editor they place a caret, and on
        // a button they press the button. Only the grips override that, because they sit on top of
        // the element and exist purely to be dragged.
        if (!onResizeGrip && !onMoveHandle && (IsWithin<TextBox>(source) || IsWithin<Button>(source)))
        {
            return;
        }

        _pending = new DragState(
            block,
            e.GetPosition(DesignSurface),
            block.X,
            block.Y,
            block.Width,
            block.Height,
            IsResize: onResizeGrip);
    }

    /// <summary>Walks up from the pressed control looking for a marker tag.</summary>
    private static bool HasTag(Control? control, string tag)
    {
        while (control is not null)
        {
            if (control.Tag is string value && value == tag)
            {
                return true;
            }

            control = control.Parent as Control;
        }

        return false;
    }

    /// <summary>Whether the press landed inside a control of the given type.</summary>
    private static bool IsWithin<T>(Control? control) where T : Control
    {
        while (control is not null)
        {
            if (control is T)
            {
                return true;
            }

            control = control.Parent as Control;
        }

        return false;
    }

    private void OnDesignPointerMoved(object? sender, PointerEventArgs e)
    {
        // Promote a pending press into a real drag once the pointer has actually moved. Capture is
        // taken here rather than on press, so a plain click still reaches whatever it landed on.
        if (_drag is null && _pending is { } pending)
        {
            var moved = e.GetPosition(DesignSurface);

            if (Math.Abs(moved.X - pending.Origin.X) < DragThreshold
                && Math.Abs(moved.Y - pending.Origin.Y) < DragThreshold)
            {
                return;
            }

            _drag = pending;
            _pending = null;
            e.Pointer.Capture(DesignSurface);
        }

        if (_drag is not { } drag)
        {
            return;
        }

        // Positions come from the artboard, which the Viewbox has already mapped back into card
        // coordinates — so a delta here is a delta in the same units the element stores.
        var current = e.GetPosition(DesignSurface);
        var dx = current.X - drag.Origin.X;
        var dy = current.Y - drag.Origin.Y;

        if (drag.IsResize)
        {
            drag.Block.Place(drag.StartX, drag.StartY, drag.StartWidth + dx, drag.StartHeight + dy);
        }
        else
        {
            drag.Block.Place(drag.StartX + dx, drag.StartY + dy, drag.StartWidth, drag.StartHeight);
        }

        e.Handled = true;
    }

    private void OnDesignPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // A press that never travelled far enough leaves _pending set; clearing it here is what
        // makes it a plain click.
        _pending = null;

        if (_drag is null)
        {
            return;
        }

        _drag = null;
        e.Pointer.Capture(null);
    }
}
