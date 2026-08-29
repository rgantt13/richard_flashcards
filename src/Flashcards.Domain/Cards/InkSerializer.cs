using System.Globalization;
using System.Text;

namespace Flashcards.Domain.Cards;

/// <summary>
/// Turns ink strokes into the string stored in a Drawing block's text column, and back.
/// <para>
/// The format is deliberately plain rather than JSON — a drawing is mostly coordinates, and the
/// overhead of property names on every point adds up fast:
/// </para>
/// <code>
/// #4C9AFF:2.5:10,20 11.5,22 13,25|#EF4444:4:80,90 82,95
/// </code>
/// <para>
/// Strokes separated by <c>|</c>; colour, thickness and points separated by <c>:</c>; points by
/// spaces and their pair by a comma. Everything is written with the invariant culture, because a
/// decimal comma from a European locale would collide with the coordinate separator and silently
/// corrupt every drawing.
/// </para>
/// </summary>
public static class InkSerializer
{
    private const char StrokeSeparator = '|';
    private const char FieldSeparator = ':';
    private const char PointSeparator = ' ';
    private const char CoordinateSeparator = ',';

    /// <summary>Two decimal places is well under one screen pixel at any sane zoom, and halves the size.</summary>
    private const string Precision = "0.##";

    public static string Serialize(IEnumerable<InkStroke> strokes)
    {
        var builder = new StringBuilder();

        foreach (var stroke in strokes)
        {
            if (stroke.IsEmpty)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(StrokeSeparator);
            }

            builder.Append(stroke.ColorHex)
                   .Append(FieldSeparator)
                   .Append(stroke.Thickness.ToString(Precision, CultureInfo.InvariantCulture))
                   .Append(FieldSeparator);

            for (var i = 0; i < stroke.Points.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(PointSeparator);
                }

                builder.Append(stroke.Points[i].X.ToString(Precision, CultureInfo.InvariantCulture))
                       .Append(CoordinateSeparator)
                       .Append(stroke.Points[i].Y.ToString(Precision, CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Parses the stored form. Malformed strokes are skipped rather than thrown on: a drawing that
    /// half-loads is a better outcome for the user than a card that will not open at all.
    /// </summary>
    public static IReadOnlyList<InkStroke> Parse(string? serialized)
    {
        if (string.IsNullOrWhiteSpace(serialized))
        {
            return [];
        }

        var strokes = new List<InkStroke>();

        foreach (var raw in serialized.Split(StrokeSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = raw.Split(FieldSeparator);

            if (fields.Length != 3
                || !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var thickness))
            {
                continue;
            }

            var points = new List<InkPoint>();

            foreach (var pair in fields[2].Split(PointSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split(CoordinateSeparator);

                if (parts.Length == 2
                    && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                {
                    points.Add(new InkPoint(x, y));
                }
            }

            if (points.Count > 0)
            {
                strokes.Add(new InkStroke(fields[0], thickness, points));
            }
        }

        return strokes;
    }

    /// <summary>
    /// Removes every stroke passing within <paramref name="radius"/> of <paramref name="point"/>.
    /// This is the eraser. Returns the surviving strokes.
    /// </summary>
    public static IReadOnlyList<InkStroke> Erase(IEnumerable<InkStroke> strokes, InkPoint point, double radius)
        => [.. strokes.Where(s => s.DistanceTo(point) > radius)];
}
