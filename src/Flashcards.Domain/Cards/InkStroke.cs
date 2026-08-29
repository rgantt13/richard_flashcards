
namespace Flashcards.Domain.Cards;

/// <summary>A point on the card canvas, in the same coordinate space as <see cref="BlockBounds"/>.</summary>
public readonly record struct InkPoint(double X, double Y);

/// <summary>
/// One continuous freehand stroke: everything between a pen going down and coming back up.
/// <para>
/// Strokes are the unit the eraser works in. Erasing a stroke removes the whole polyline rather
/// than punching a hole through it, which is how a "stroke eraser" behaves in most note-taking
/// apps and is the only sane option when the drawing is stored as vectors rather than pixels.
/// </para>
/// </summary>
public sealed record InkStroke(string ColorHex, double Thickness, IReadOnlyList<InkPoint> Points)
{
    /// <summary>A stroke needs two points to be a line; a single tap is a dot and still counts.</summary>
    public bool IsEmpty => Points.Count == 0;

    /// <summary>
    /// The shortest distance from <paramref name="point"/> to this stroke, used for hit-testing
    /// the eraser. Measured against each segment, not just the recorded points, so erasing works
    /// on a long straight stroke drawn quickly with few samples.
    /// </summary>
    public double DistanceTo(InkPoint point)
    {
        if (Points.Count == 0)
        {
            return double.MaxValue;
        }

        if (Points.Count == 1)
        {
            return Distance(Points[0], point);
        }

        var nearest = double.MaxValue;

        for (var i = 1; i < Points.Count; i++)
        {
            nearest = Math.Min(nearest, DistanceToSegment(Points[i - 1], Points[i], point));
        }

        return nearest;
    }

    private static double Distance(InkPoint a, InkPoint b)
        => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    /// <summary>Point-to-segment distance: project onto the segment, clamp to its ends, measure.</summary>
    private static double DistanceToSegment(InkPoint start, InkPoint end, InkPoint point)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = (dx * dx) + (dy * dy);

        if (lengthSquared <= double.Epsilon)
        {
            return Distance(start, point);
        }

        var t = (((point.X - start.X) * dx) + ((point.Y - start.Y) * dy)) / lengthSquared;
        t = Math.Clamp(t, 0, 1);

        return Distance(new InkPoint(start.X + (t * dx), start.Y + (t * dy)), point);
    }
}

