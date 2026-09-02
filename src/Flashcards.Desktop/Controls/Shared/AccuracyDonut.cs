using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Flashcards.Desktop.Controls.Shared;

/// <summary>
/// The whole record as one figure: a ring split where the answers split, the percentage in the
/// hole, and the counts standing beside the segments they belong to.
/// <para>
/// This is the headline reading on a selection card, where <see cref="AccuracyBar"/> is the reading
/// in a list row. They are different shapes on purpose. A bar is a good comparator — twenty of them
/// stacked in a column can be scanned down, which is exactly what the rows want — and a poor
/// headline: at the top of a card it is a stripe of colour with no obvious place to put a number.
/// A ring is the opposite. It is hopeless in a list, and it is the best single-value shape there
/// is, because it has a hole in the middle and a circumference to hang labels off.
/// </para>
/// <para>
/// The counts are drawn here rather than tabled underneath the card. A row of four figures under a
/// chart makes the reader carry a number from the table up to the picture to find out which part
/// of it that number describes; putting "843 right" against the green arc answers that before the
/// question is asked.
/// </para>
/// </summary>
public class AccuracyDonut : Control
{
    public static readonly StyledProperty<int> CorrectProperty =
        AvaloniaProperty.Register<AccuracyDonut, int>(nameof(Correct));

    public static readonly StyledProperty<int> WrongProperty =
        AvaloniaProperty.Register<AccuracyDonut, int>(nameof(Wrong));

    public static readonly StyledProperty<IBrush?> CorrectBrushProperty =
        AvaloniaProperty.Register<AccuracyDonut, IBrush?>(nameof(CorrectBrush));

    public static readonly StyledProperty<IBrush?> WrongBrushProperty =
        AvaloniaProperty.Register<AccuracyDonut, IBrush?>(nameof(WrongBrush));

    /// <summary>The ring behind the segments, which is all you see before anything is answered.</summary>
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<AccuracyDonut, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<AccuracyDonut, IBrush?>(nameof(TextBrush));

    /// <summary>Across the outside of the ring. The control is wider than this to leave room for the labels.</summary>
    public static readonly StyledProperty<double> RingDiameterProperty =
        AvaloniaProperty.Register<AccuracyDonut, double>(nameof(RingDiameter), 118d);

    public static readonly StyledProperty<double> RingThicknessProperty =
        AvaloniaProperty.Register<AccuracyDonut, double>(nameof(RingThickness), 11d);

    /// <summary>A gap between the two segments, in degrees, so they read as two rather than one.</summary>
    private const double SegmentGap = 3;

    /// <summary>How far past the outside of the ring a count sits.</summary>
    private const double LabelReach = 12;

    private const double LabelGap = 5;

    static AccuracyDonut()
    {
        AffectsRender<AccuracyDonut>(
            CorrectProperty,
            WrongProperty,
            CorrectBrushProperty,
            WrongBrushProperty,
            TrackBrushProperty,
            TextBrushProperty,
            RingDiameterProperty,
            RingThicknessProperty);

        // Wide enough that a count sitting due east or due west of the ring still has room for its
        // word, and tall enough for one sitting at the bottom.
        WidthProperty.OverrideDefaultValue<AccuracyDonut>(272d);
        HeightProperty.OverrideDefaultValue<AccuracyDonut>(176d);
    }

    public int Correct
    {
        get => GetValue(CorrectProperty);
        set => SetValue(CorrectProperty, value);
    }

    public int Wrong
    {
        get => GetValue(WrongProperty);
        set => SetValue(WrongProperty, value);
    }

    public IBrush? CorrectBrush
    {
        get => GetValue(CorrectBrushProperty);
        set => SetValue(CorrectBrushProperty, value);
    }

    public IBrush? WrongBrush
    {
        get => GetValue(WrongBrushProperty);
        set => SetValue(WrongBrushProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? TextBrush
    {
        get => GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public double RingDiameter
    {
        get => GetValue(RingDiameterProperty);
        set => SetValue(RingDiameterProperty, value);
    }

    public double RingThickness
    {
        get => GetValue(RingThicknessProperty);
        set => SetValue(RingThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var ring = Math.Min(RingDiameter, Math.Min(Bounds.Width, Bounds.Height));

        if (ring <= 0)
        {
            return;
        }

        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var thickness = Math.Min(RingThickness, ring / 3);
        var radius = (ring - thickness) / 2;

        var track = TrackBrush ?? new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));
        var text = TextBrush ?? Brushes.Gray;

        context.DrawEllipse(null, new Pen(track, thickness), centre, radius, radius);

        var answered = Correct + Wrong;

        if (answered > 0)
        {
            // The whole ring is the answers, split where the record splits them. Starting at twelve
            // o'clock and going clockwise means the green arc grows the way a reader expects a
            // score to grow.
            var correctSweep = 360.0 * Correct / answered;

            DrawSegment(context, centre, radius, thickness, CorrectBrush, -90, correctSweep);
            DrawSegment(context, centre, radius, thickness, WrongBrush, -90 + correctSweep, 360 - correctSweep);

            // Each count stands off the middle of its own arc, so which colour it belongs to needs
            // no explaining. A segment too thin to have a middle worth pointing at is left unlabelled
            // rather than labelled misleadingly.
            DrawCount(context, centre, radius, thickness, Correct, "right", CorrectBrush ?? text, -90 + (correctSweep / 2), correctSweep);
            DrawCount(context, centre, radius, thickness, Wrong, "wrong", WrongBrush ?? text, -90 + correctSweep + ((360 - correctSweep) / 2), 360 - correctSweep);
        }

        DrawCentre(context, centre, ring, answered, text);
    }

    /// <summary>
    /// One arc. A segment that would be the whole circle is drawn as a circle instead: an arc from
    /// a point back to itself is degenerate, and renders as nothing at all.
    /// </summary>
    private static void DrawSegment(
        DrawingContext context,
        Point centre,
        double radius,
        double thickness,
        IBrush? brush,
        double startDegrees,
        double sweepDegrees)
    {
        if (brush is null || sweepDegrees <= 0)
        {
            return;
        }

        var pen = new Pen(brush, thickness) { LineCap = PenLineCap.Flat };

        if (sweepDegrees >= 359.9)
        {
            context.DrawEllipse(null, pen, centre, radius, radius);

            return;
        }

        // The gap is taken out of the segment rather than added between them, so the two always
        // add up to the circle and neither is inflated by the spacing.
        var inset = Math.Min(SegmentGap, sweepDegrees / 3);
        var from = startDegrees + (inset / 2);
        var sweep = sweepDegrees - inset;

        var geometry = new StreamGeometry();

        using (var path = geometry.Open())
        {
            path.BeginFigure(PointOn(centre, radius, from), false);
            path.ArcTo(
                PointOn(centre, radius, from + sweep),
                new Size(radius, radius),
                0,
                sweep > 180,
                SweepDirection.Clockwise);
            path.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);
    }

    /// <summary>
    /// A count and its word, centred on a point outside the ring at the middle of its segment. The
    /// pair is nudged back inside the control if the angle would hang it over an edge, so a
    /// lopsided split still reads rather than clipping.
    /// </summary>
    private void DrawCount(
        DrawingContext context,
        Point centre,
        double radius,
        double thickness,
        int count,
        string word,
        IBrush brush,
        double atDegrees,
        double sweepDegrees)
    {
        if (count == 0 || sweepDegrees < 12)
        {
            return;
        }

        var figure = Text(count.ToString("N0", CultureInfo.CurrentCulture), 13.5, FontWeight.Bold, brush);
        var caption = Text(word, 11.5, FontWeight.Normal, brush);

        var width = figure.Width + LabelGap + caption.Width;
        var height = Math.Max(figure.Height, caption.Height);

        var radians = atDegrees * Math.PI / 180;
        var anchor = PointOn(centre, radius + (thickness / 2) + LabelReach, atDegrees);

        // The box grows away from the ring rather than being centred on the anchor: centred, a
        // label pointing east would have half its width lying back across the arc it is labelling.
        // The lean is proportional, so one pointing due north stays centred and one due east does
        // not.
        static double Lean(double direction) => 0.5 - (0.5 * Math.Clamp(direction * 2, -1, 1));

        var left = anchor.X - (width * Lean(Math.Cos(radians)));
        var top = anchor.Y - (height * Lean(Math.Sin(radians)));

        left = Math.Clamp(left, 2, Math.Max(Bounds.Width - width - 2, 2));
        top = Math.Clamp(top, 2, Math.Max(Bounds.Height - height - 2, 2));

        context.DrawText(figure, new Point(left, top));

        using (context.PushOpacity(0.7))
        {
            context.DrawText(caption, new Point(left + figure.Width + LabelGap, top + (figure.Height - caption.Height)));
        }
    }

    /// <summary>
    /// The percentage, and under it what it is a percentage of. The count is the honest half: a
    /// ring at 100% over three answers and one over three hundred are the same picture, and only
    /// the small line underneath tells them apart.
    /// </summary>
    private void DrawCentre(DrawingContext context, Point centre, double ring, int answered, IBrush text)
    {
        var headline = answered == 0
            ? "—"
            : ((double)Correct / answered).ToString("P0", CultureInfo.CurrentCulture);

        var figure = Text(headline, ring * 0.23, FontWeight.Bold, text);

        var caption = Text(
            answered switch
            {
                0 => "no answers",
                1 => "1 answer",
                _ => $"{answered:N0} answers",
            },
            ring * 0.095,
            FontWeight.Normal,
            text);

        var top = centre.Y - ((figure.Height + caption.Height) / 2);

        context.DrawText(figure, new Point(centre.X - (figure.Width / 2), top));

        using (context.PushOpacity(0.55))
        {
            context.DrawText(caption, new Point(centre.X - (caption.Width / 2), top + figure.Height));
        }
    }

    private static FormattedText Text(string text, double size, FontWeight weight, IBrush brush)
        => new(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, weight),
            size,
            brush);

    private static Point PointOn(Point centre, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;

        return new Point(
            centre.X + (radius * Math.Cos(radians)),
            centre.Y + (radius * Math.Sin(radians)));
    }
}
