using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Flashcards.Application.Contracts;

namespace Flashcards.Desktop.Controls.Shared;

/// <summary>
/// A year of answering, one square per day — the contribution-graph arrangement, because it is the
/// one picture that answers "am I still turning up" without being read.
/// <para>
/// Weeks run down the columns and along the rows, so a column is a week and a row is a weekday.
/// That is what makes the shape legible at a glance: a person who only studies at weekends grows
/// two solid lines, and a fortnight off is a blank stripe you cannot miss. The week starts on
/// whichever day the machine's culture says it does.
/// </para>
/// <para>
/// Drawn rather than composed, for the same reason <see cref="Identicon"/> is: 371 bound
/// <c>Border</c>s would be a lot of visual tree to express something that is a loop over a list.
/// The hit test in <see cref="OnPointerMoved"/> is the price of that, and it is one subtraction.
/// </para>
/// </summary>
public class StudyHeatmap : Control
{
    /// <summary>The days to draw, densely filled — see <see cref="ActivityHistory"/>.</summary>
    public static readonly StyledProperty<ActivityHistory?> HistoryProperty =
        AvaloniaProperty.Register<StudyHeatmap, ActivityHistory?>(nameof(History));

    /// <summary>
    /// The colour a busy day is drawn in. Quieter days are the same hue at lower opacity, so the
    /// ramp reads as one colour getting stronger rather than as four unrelated shades.
    /// </summary>
    public static readonly StyledProperty<IBrush?> AccentProperty =
        AvaloniaProperty.Register<StudyHeatmap, IBrush?>(nameof(Accent));

    /// <summary>The square for a day with nothing on it.</summary>
    public static readonly StyledProperty<IBrush?> EmptyBrushProperty =
        AvaloniaProperty.Register<StudyHeatmap, IBrush?>(nameof(EmptyBrush));

    /// <summary>Month and weekday captions.</summary>
    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<StudyHeatmap, IBrush?>(nameof(LabelBrush));

    /// <summary>
    /// What the square under the pointer says, for a caption beside the graph.
    /// <para>
    /// A caption rather than a tooltip. A tooltip over a grid of eleven-pixel squares is a game of
    /// keeping still, and it hides the neighbours you are comparing against — which is the whole
    /// reason to look at a square in the first place.
    /// </para>
    /// </summary>
    public static readonly DirectProperty<StudyHeatmap, string?> HoverCaptionProperty =
        AvaloniaProperty.RegisterDirect<StudyHeatmap, string?>(
            nameof(HoverCaption), o => o.HoverCaption);

    private const double Cell = 11;
    private const double Gap = 3;
    private const double Step = Cell + Gap;
    private const double WeekdayGutter = 26;
    private const double MonthStrip = 16;
    private const double LabelSize = 9;

    private DateOnly? _gridStart;
    private int _columns;
    private DateOnly? _hovered;
    private string? _hoverCaption;

    static StudyHeatmap()
    {
        AffectsMeasure<StudyHeatmap>(HistoryProperty);
        AffectsRender<StudyHeatmap>(HistoryProperty, AccentProperty, EmptyBrushProperty, LabelBrushProperty);
    }

    public ActivityHistory? History
    {
        get => GetValue(HistoryProperty);
        set => SetValue(HistoryProperty, value);
    }

    public IBrush? Accent
    {
        get => GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public IBrush? EmptyBrush
    {
        get => GetValue(EmptyBrushProperty);
        set => SetValue(EmptyBrushProperty, value);
    }

    public IBrush? LabelBrush
    {
        get => GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public string? HoverCaption
    {
        get => _hoverCaption;
        private set => SetAndRaise(HoverCaptionProperty, ref _hoverCaption, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Layout();

        return new Size(WeekdayGutter + (_columns * Step), MonthStrip + (7 * Step));
    }

    public override void Render(DrawingContext context)
    {
        if (History is not { Days.Count: > 0 } history || _gridStart is not { } start)
        {
            return;
        }

        var accent = (Accent as ISolidColorBrush)?.Color ?? Colors.SteelBlue;
        var empty = EmptyBrush ?? new SolidColorBrush(Color.FromArgb(28, 128, 128, 128));
        var labels = LabelBrush ?? new SolidColorBrush(Color.FromArgb(140, 128, 128, 128));
        var typeface = new Typeface(FontFamily.Default);

        // Everything scales against the busiest day rather than a fixed count. A person answering
        // ten cards a night and one answering two hundred should both see a full-looking year;
        // the alternative is a graph that is either always pale or always saturated.
        var busiest = Math.Max(history.BusiestDay, 1);

        // The empty squares are drawn for the whole grid first, so the leading and trailing days
        // that fall outside the window still read as part of the calendar rather than as a ragged
        // edge.
        for (var column = 0; column < _columns; column++)
        {
            for (var row = 0; row < 7; row++)
            {
                context.DrawRectangle(empty, null, Square(column, row), 2, 2);
            }
        }

        foreach (var day in history.Days)
        {
            var offset = day.Day.DayNumber - start.DayNumber;

            if (offset < 0)
            {
                continue;
            }

            if (day.Answered > 0)
            {
                context.DrawRectangle(
                    new SolidColorBrush(accent, Intensity(day.Answered, busiest)),
                    null,
                    Square(offset / 7, offset % 7),
                    2,
                    2);
            }
        }

        // A ring rather than a fill, so the square you are reading keeps the colour you are
        // reading it for.
        if (_hovered is { } hovered)
        {
            var offset = hovered.DayNumber - start.DayNumber;

            if (offset >= 0)
            {
                var ring = Square(offset / 7, offset % 7).Inflate(1.5);

                context.DrawRectangle(null, new Pen(new SolidColorBrush(accent), 1.5), ring, 3, 3);
            }
        }

        DrawMonthLabels(context, start, labels, typeface);
        DrawWeekdayLabels(context, start, labels, typeface);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        Describe(DayAt(e.GetPosition(this)));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);

        Describe(null);
    }

    /// <summary>Updates the caption and the ring, and only when the square changes.</summary>
    private void Describe(DailyActivity? day)
    {
        if (day?.Day == _hovered)
        {
            return;
        }

        _hovered = day?.Day;
        HoverCaption = Caption(day);
        InvalidateVisual();
    }

    /// <summary>
    /// Works out where the grid begins and how wide it is. The first column has to start on the
    /// week's first day or every row would mean a different weekday as the year goes on, which is
    /// the one thing this arrangement is for.
    /// </summary>
    private void Layout()
    {
        if (History is not { Days.Count: > 0 } history)
        {
            _gridStart = null;
            _columns = 0;

            return;
        }

        var first = history.Days[0].Day;
        var last = history.Days[^1].Day;

        _gridStart = first.AddDays(-WeekdayIndex(first));
        _columns = ((last.DayNumber - _gridStart.Value.DayNumber) / 7) + 1;
    }

    private static Rect Square(int column, int row)
        => new(WeekdayGutter + (column * Step), MonthStrip + (row * Step), Cell, Cell);

    /// <summary>
    /// Four visible steps rather than a continuous ramp. A smooth gradient looks better in
    /// isolation and reads worse in practice: the eye compares squares, and comparing them needs
    /// them to fall into bands.
    /// </summary>
    private static double Intensity(int answered, int busiest)
    {
        var share = answered / (double)busiest;

        return share switch
        {
            <= 0.25 => 0.32,
            <= 0.5 => 0.55,
            <= 0.75 => 0.78,
            _ => 1.0,
        };
    }

    /// <summary>Position in the week, counted from whatever day the culture starts it on.</summary>
    private static int WeekdayIndex(DateOnly day)
    {
        var start = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;

        return (((int)day.DayOfWeek - start) + 7) % 7;
    }

    private void DrawMonthLabels(DrawingContext context, DateOnly start, IBrush brush, Typeface typeface)
    {
        var lastLabelled = -1;

        for (var column = 0; column < _columns; column++)
        {
            var day = start.AddDays(column * 7);

            // Labelled on the first column that contains the month, and only when there is room
            // for the caption before the next one — otherwise January and February collide.
            if (day.Month == lastLabelled || (column > 0 && day.Day > 7))
            {
                continue;
            }

            lastLabelled = day.Month;

            context.DrawText(
                Caption(CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedMonthName(day.Month), brush, typeface),
                new Point(WeekdayGutter + (column * Step), 1));
        }
    }

    /// <summary>
    /// Every other row is captioned. Labelling all seven turns the gutter into a wall of text for
    /// information the arrangement already carries.
    /// </summary>
    private void DrawWeekdayLabels(DrawingContext context, DateOnly start, IBrush brush, Typeface typeface)
    {
        for (var row = 1; row < 7; row += 2)
        {
            var name = CultureInfo.CurrentCulture.DateTimeFormat
                .GetShortestDayName(start.AddDays(row).DayOfWeek);

            context.DrawText(
                Caption(name, brush, typeface),
                new Point(0, MonthStrip + (row * Step) + 1));
        }
    }

    private static FormattedText Caption(string text, IBrush brush, Typeface typeface)
        => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, LabelSize, brush);

    private DailyActivity? DayAt(Point point)
    {
        if (History is not { Days.Count: > 0 } history || _gridStart is not { } start)
        {
            return null;
        }

        var column = (int)Math.Floor((point.X - WeekdayGutter) / Step);
        var row = (int)Math.Floor((point.Y - MonthStrip) / Step);

        if (column < 0 || column >= _columns || row is < 0 or > 6)
        {
            return null;
        }

        var day = start.AddDays((column * 7) + row);

        // The grid is a rectangle and the window is not: the first column runs back before the
        // window starts and the last runs past today. Those squares belong to no day.
        return history.Days.FirstOrDefault(d => d.Day == day);
    }

    private static string? Caption(DailyActivity? day) => day switch
    {
        null => null,
        { Answered: 0 } => $"{day.Day:ddd d MMM} — nothing answered",
        { Answered: 1 } => $"{day.Day:ddd d MMM} — 1 answer, {day.Correct} right",
        _ => $"{day.Day:ddd d MMM} — {day.Answered} answers, {day.Correct} right",
    };
}
