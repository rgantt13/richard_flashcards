using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Flashcards.Domain.Cards;

namespace Flashcards.Desktop.Controls.Shared;

/// <summary>
/// The freehand layer of a designed card: renders <see cref="InkStroke"/>s and captures new ones.
/// <para>
/// It works directly in card-canvas coordinates. The designer hosts it inside a
/// <c>Viewbox</c> sized to <see cref="CardCanvas"/>, so Avalonia has already mapped pointer
/// positions out of screen space by the time they arrive here — which is what keeps a stroke drawn
/// in a small window landing in the same place when the card is studied in a large one.
/// </para>
/// <para>
/// The surface mutates the bound stroke collection itself rather than raising events for a view
/// model to handle. Pointer plumbing is view concern; keeping it here is the same split that keeps
/// <c>DragEventArgs</c> out of the view models.
/// </para>
/// </summary>
public sealed class InkSurface : Control
{
    public static readonly StyledProperty<IList<InkStroke>?> StrokesProperty =
        AvaloniaProperty.Register<InkSurface, IList<InkStroke>?>(nameof(Strokes));

    /// <summary>Pen down starts a stroke. When false, presses fall through to whatever is beneath.</summary>
    public static readonly StyledProperty<bool> IsDrawingProperty =
        AvaloniaProperty.Register<InkSurface, bool>(nameof(IsDrawing));

    /// <summary>Pen down removes strokes it touches instead of adding one.</summary>
    public static readonly StyledProperty<bool> IsErasingProperty =
        AvaloniaProperty.Register<InkSurface, bool>(nameof(IsErasing));

    public static readonly StyledProperty<string> StrokeColorProperty =
        AvaloniaProperty.Register<InkSurface, string>(nameof(StrokeColor), "#4C9AFF");

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<InkSurface, double>(nameof(StrokeThickness), 3d);

    /// <summary>How close the eraser has to pass to a stroke to take it, in canvas units.</summary>
    public static readonly StyledProperty<double> EraserRadiusProperty =
        AvaloniaProperty.Register<InkSurface, double>(nameof(EraserRadius), 12d);

    private readonly List<InkPoint> _live = [];
    private INotifyCollectionChanged? _observed;
    private bool _capturing;

    static InkSurface()
    {
        // Any of these changing alters what is on screen.
        AffectsRender<InkSurface>(StrokesProperty, StrokeColorProperty, StrokeThicknessProperty);
    }

    public IList<InkStroke>? Strokes
    {
        get => GetValue(StrokesProperty);
        set => SetValue(StrokesProperty, value);
    }

    public bool IsDrawing
    {
        get => GetValue(IsDrawingProperty);
        set => SetValue(IsDrawingProperty, value);
    }

    public bool IsErasing
    {
        get => GetValue(IsErasingProperty);
        set => SetValue(IsErasingProperty, value);
    }

    public string StrokeColor
    {
        get => GetValue(StrokeColorProperty);
        set => SetValue(StrokeColorProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public double EraserRadius
    {
        get => GetValue(EraserRadiusProperty);
        set => SetValue(EraserRadiusProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == StrokesProperty)
        {
            // Swapping faces swaps the collection; follow the new one so strokes added or erased
            // through it repaint without the view model having to poke us.
            Detach();

            if (change.GetNewValue<IList<InkStroke>?>() is INotifyCollectionChanged incc)
            {
                _observed = incc;
                incc.CollectionChanged += OnStrokesChanged;
            }

            InvalidateVisual();
        }
    }

    private void Detach()
    {
        if (_observed is not null)
        {
            _observed.CollectionChanged -= OnStrokesChanged;
            _observed = null;
        }
    }

    private void OnStrokesChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (Strokes is null || (!IsDrawing && !IsErasing))
        {
            return;
        }

        var point = ToInk(e.GetPosition(this));

        if (IsErasing)
        {
            Erase(point);
        }
        else
        {
            _live.Clear();
            _live.Add(point);
        }

        _capturing = true;
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!_capturing || Strokes is null)
        {
            return;
        }

        var point = ToInk(e.GetPosition(this));

        if (IsErasing)
        {
            Erase(point);
        }
        else
        {
            // Skip samples the pointer barely moved through: at high report rates they add
            // hundreds of points per stroke and change nothing visible.
            if (_live.Count == 0 || Distance(_live[^1], point) >= 1.5)
            {
                _live.Add(point);
            }
        }

        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (!_capturing)
        {
            return;
        }

        _capturing = false;
        e.Pointer.Capture(null);

        if (Strokes is not null && !IsErasing && _live.Count > 0)
        {
            Strokes.Add(new InkStroke(StrokeColor, StrokeThickness, [.. _live]));
        }

        _live.Clear();
        e.Handled = true;
        InvalidateVisual();
    }

    /// <summary>Removes every stroke within <see cref="EraserRadius"/> of the point.</summary>
    private void Erase(InkPoint point)
    {
        if (Strokes is null)
        {
            return;
        }

        // Backwards so removing does not disturb the indices still to be checked.
        for (var i = Strokes.Count - 1; i >= 0; i--)
        {
            if (Strokes[i].DistanceTo(point) <= EraserRadius)
            {
                Strokes.RemoveAt(i);
            }
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // A transparent fill makes the surface hit-testable. Without it the control has nothing to
        // hit and would never see a pointer press.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        if (Strokes is not null)
        {
            foreach (var stroke in Strokes)
            {
                Draw(context, stroke.ColorHex, stroke.Thickness, stroke.Points);
            }
        }

        // The stroke still under the pointer, so drawing feels immediate rather than appearing
        // only once the pen lifts.
        if (_live.Count > 0)
        {
            Draw(context, StrokeColor, StrokeThickness, _live);
        }
    }

    private static void Draw(DrawingContext context, string colorHex, double thickness, IReadOnlyList<InkPoint> points)
    {
        if (points.Count == 0)
        {
            return;
        }

        var brush = new SolidColorBrush(Color.TryParse(colorHex, out var color) ? color : Colors.Black);

        // Round caps and joins: a polyline with mitred corners looks like a saw at low sample rates.
        var pen = new Pen(brush, thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

        if (points.Count == 1)
        {
            // A tap is a dot, which a zero-length line would not draw.
            context.DrawEllipse(brush, null, new Point(points[0].X, points[0].Y), thickness / 2, thickness / 2);
            return;
        }

        var geometry = new StreamGeometry();

        using (var sink = geometry.Open())
        {
            sink.BeginFigure(new Point(points[0].X, points[0].Y), isFilled: false);

            for (var i = 1; i < points.Count; i++)
            {
                sink.LineTo(new Point(points[i].X, points[i].Y));
            }

            sink.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, pen, geometry);
    }

    private static InkPoint ToInk(Point point) => new(point.X, point.Y);

    private static double Distance(InkPoint a, InkPoint b)
        => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
}
