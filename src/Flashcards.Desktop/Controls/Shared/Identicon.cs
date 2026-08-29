using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Flashcards.Desktop.Controls.Shared;

/// <summary>Which family of mark to draw. See <see cref="Identicon"/> for why there are two.</summary>
public enum IdenticonVariant
{
    /// <summary>A squared-off, vertically mirrored block grid — the GitHub-style mark.</summary>
    Subject,

    /// <summary>A round badge of dots with rotational symmetry.</summary>
    Card,
}

/// <summary>
/// A generated identity mark, in the spirit of a default GitHub avatar: the same seed always draws
/// the same picture, so a subject or a card becomes recognisable by shape before you read its name.
/// <para>
/// The two variants are deliberately unlike each other rather than recoloured versions of one
/// design. A subject is a rounded square of solid blocks mirrored down its vertical axis; a card is
/// a circle of dots with 180 degree rotational symmetry. Silhouette alone tells you which kind of
/// thing you are looking at, which matters on the Study panel where both appear a few centimetres
/// apart.
/// </para>
/// <para>
/// This derives from <see cref="Control"/> and draws itself, rather than being a
/// <c>TemplatedControl</c> like <see cref="SubjectChip"/>. There is no arrangement of child
/// controls to express here — the output is a pattern computed from a hash, and expressing 25 cells
/// as 25 bound borders would cost a lot of visual tree to produce one small square.
/// </para>
/// </summary>
public class Identicon : Control
{
    /// <summary>
    /// What the pattern is computed from. Use something stable and unique — an id.
    /// <para>
    /// Typed as <see cref="object"/> rather than <see cref="string"/> so that binding a
    /// <see cref="Guid"/> straight from a view model is exact rather than relying on the binding
    /// layer to convert it. A conversion that quietly failed would leave every mark drawn from the
    /// same null seed, which looks like a design decision rather than a bug.
    /// </para>
    /// </summary>
    public static readonly StyledProperty<object?> SeedProperty =
        AvaloniaProperty.Register<Identicon, object?>(nameof(Seed));

    /// <summary>
    /// Optional "#RRGGBB" to draw in. Subjects pass their own identity colour so the mark matches
    /// the chip beside it; leaving it unset derives a hue from the seed instead.
    /// </summary>
    public static readonly StyledProperty<string?> AccentHexProperty =
        AvaloniaProperty.Register<Identicon, string?>(nameof(AccentHex));

    public static readonly StyledProperty<IdenticonVariant> VariantProperty =
        AvaloniaProperty.Register<Identicon, IdenticonVariant>(nameof(Variant));

    static Identicon()
    {
        AffectsRender<Identicon>(SeedProperty, AccentHexProperty, VariantProperty);

        // An identicon is an icon: it wants an intrinsic size, not the stretch a Control defaults
        // to. Callers override both wherever they want it bigger.
        WidthProperty.OverrideDefaultValue<Identicon>(32d);
        HeightProperty.OverrideDefaultValue<Identicon>(32d);
    }

    public object? Seed
    {
        get => GetValue(SeedProperty);
        set => SetValue(SeedProperty, value);
    }

    public string? AccentHex
    {
        get => GetValue(AccentHexProperty);
        set => SetValue(AccentHexProperty, value);
    }

    public IdenticonVariant Variant
    {
        get => GetValue(VariantProperty);
        set => SetValue(VariantProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);

        if (size <= 0)
        {
            return;
        }

        // Centre the mark in whatever box the layout gave us, so a non-square slot still draws a
        // square (or round) identicon rather than a stretched one.
        var origin = new Point((Bounds.Width - size) / 2, (Bounds.Height - size) / 2);
        var (strong, soft, wash) = Palette();

        // One stream of bits per identicon, started from the seed hash. Every decision below pulls
        // from it in a fixed order, which is what makes the picture reproducible.
        var state = Hash(Seed);

        if (Variant == IdenticonVariant.Subject)
        {
            RenderSubject(context, origin, size, strong, soft, wash, ref state);
        }
        else
        {
            RenderCard(context, origin, size, strong, soft, wash, ref state);
        }
    }

    /// <summary>
    /// Five columns of five, mirrored about the centre column. Only the left three are drawn from
    /// the bit stream; columns 3 and 4 copy columns 1 and 0. The mirroring is what stops a random
    /// grid from looking like noise — symmetry is what the eye remembers.
    /// </summary>
    private static void RenderSubject(
        DrawingContext context,
        Point origin,
        double size,
        Color strong,
        Color soft,
        Color wash,
        ref uint state)
    {
        const int Grid = 5;

        var plate = new Rect(origin, new Size(size, size));
        context.DrawRectangle(new SolidColorBrush(wash), null, new RoundedRect(plate, size * 0.26));

        var inset = size * 0.15;
        var cell = (size - (inset * 2)) / Grid;

        // Cells are drawn a hair over one cell wide so neighbours in a run join into one shape
        // instead of showing hairline seams where their edges meet.
        var draw = cell + 0.5;

        for (var column = 0; column < 3; column++)
        {
            for (var row = 0; row < Grid; row++)
            {
                var bits = Next(ref state);

                // Roughly half the cells fill. The centre column is biased sparser: when it fills
                // as often as the rest, mirroring puts a heavy spine down every mark.
                var filled = column == 2 ? (bits & 3) == 0 : (bits & 1) == 0;

                if (!filled)
                {
                    continue;
                }

                var brush = new SolidColorBrush((bits & 0x10) == 0 ? strong : soft);

                Fill(column, row);

                if (column < 2)
                {
                    Fill(Grid - 1 - column, row);
                }

                void Fill(int atColumn, int atRow) => context.FillRectangle(
                    brush,
                    new Rect(
                        origin.X + inset + (atColumn * cell),
                        origin.Y + inset + (atRow * cell),
                        draw,
                        draw));
            }
        }
    }

    /// <summary>
    /// A disc of dots on a four-by-four lattice, each dot paired with the one diagonally opposite
    /// it through the centre. Rotational rather than mirror symmetry, so it reads as a different
    /// species of mark from the subject grid even at a glance.
    /// </summary>
    private static void RenderCard(
        DrawingContext context,
        Point origin,
        double size,
        Color strong,
        Color soft,
        Color wash,
        ref uint state)
    {
        const int Grid = 4;

        var centre = new Point(origin.X + (size / 2), origin.Y + (size / 2));
        var radius = (size / 2) - (size * 0.03);

        context.DrawEllipse(
            new SolidColorBrush(wash),
            new Pen(new SolidColorBrush(soft), size * 0.05),
            centre,
            radius,
            radius);

        // The lattice is inscribed in the disc rather than filling the box, so no dot lands on or
        // outside the ring.
        var span = size * 0.58;
        var cell = span / Grid;
        var left = centre.X - (span / 2) + (cell / 2);
        var top = centre.Y - (span / 2) + (cell / 2);

        // Half the lattice decides the whole of it: cell (row, column) sets its opposite number.
        for (var index = 0; index < Grid * Grid / 2; index++)
        {
            var bits = Next(ref state);

            if ((bits & 3) == 0)
            {
                continue;
            }

            var row = index / Grid;
            var column = index % Grid;

            // Three sizes of dot, so a mark has some internal rhythm rather than being a uniform
            // stipple.
            var dot = cell * ((bits & 0x10) == 0 ? 0.40 : (bits & 0x20) == 0 ? 0.30 : 0.20);
            var brush = new SolidColorBrush((bits & 0x40) == 0 ? strong : soft);

            Dot(row, column);
            Dot(Grid - 1 - row, Grid - 1 - column);

            void Dot(int atRow, int atColumn) => context.DrawEllipse(
                brush,
                null,
                new Point(left + (atColumn * cell), top + (atRow * cell)),
                dot,
                dot);
        }
    }

    /// <summary>
    /// The three tones a mark is drawn in: the accent itself, a translucent version of it, and a
    /// near-transparent plate behind. Working in alpha rather than in fixed light and dark shades
    /// is what lets one palette sit correctly on either theme — the surface underneath shows
    /// through and does the adapting.
    /// </summary>
    private (Color Strong, Color Soft, Color Wash) Palette()
    {
        Color accent;

        if (AccentHex is { } hex && !string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var parsed))
        {
            accent = parsed;
        }
        else
        {
            // No identity colour to borrow — cards are in this case — so the hue comes from the
            // seed as well. Saturation and lightness are fixed, which keeps every generated colour
            // in the same register instead of ranging from pastel to fluorescent.
            accent = FromHsl(Hash(Seed) % 360u / 360.0, 0.55, 0.62);
        }

        return (
            accent,
            Color.FromArgb(0x8C, accent.R, accent.G, accent.B),
            Color.FromArgb(0x24, accent.R, accent.G, accent.B));
    }

    /// <summary>
    /// FNV-1a over the seed. Hand-rolled for the same reason <c>Subject.ColorFor</c> is:
    /// <c>string.GetHashCode</c> is salted per process on .NET Core, so every restart would deal
    /// every subject and card a brand new face.
    /// </summary>
    private static uint Hash(object? seed)
    {
        unchecked
        {
            var hash = 2166136261u;

            foreach (var c in seed?.ToString() ?? string.Empty)
            {
                hash ^= c;
                hash *= 16777619u;
            }

            // xorshift needs a non-zero state, and an empty seed would otherwise draw nothing.
            return hash == 0 ? 0x9E3779B9u : hash;
        }
    }

    /// <summary>xorshift32: enough bits, in sequence, from one seed. Not a security primitive.</summary>
    private static uint Next(ref uint state)
    {
        unchecked
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        var chroma = (1 - Math.Abs((2 * lightness) - 1)) * saturation;
        var sector = hue * 6;
        var second = chroma * (1 - Math.Abs((sector % 2) - 1));
        var offset = lightness - (chroma / 2);

        var (r, g, b) = (int)sector switch
        {
            0 => (chroma, second, 0d),
            1 => (second, chroma, 0d),
            2 => (0d, chroma, second),
            3 => (0d, second, chroma),
            4 => (second, 0d, chroma),
            _ => (chroma, 0d, second),
        };

        return Color.FromRgb(Channel(r + offset), Channel(g + offset), Channel(b + offset));

        static byte Channel(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
    }
}
