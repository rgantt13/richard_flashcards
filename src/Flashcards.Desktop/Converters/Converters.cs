using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Flashcards.Domain.Cards;

namespace Flashcards.Desktop.Converters;

/// <summary>True when the bound enum equals the ConverterParameter. Drives radio buttons.</summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && parameter is not null
           && string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && parameter is not null
            ? Enum.Parse(Nullable.GetUnderlyingType(targetType) ?? targetType, parameter.ToString()!)
            : Avalonia.Data.BindingOperations.DoNothing;
}

/// <summary>True when the value is not null. Pass "invert" to flip it.</summary>
public sealed class NullToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasValue = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            _ => true,
        };

        return string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase) ? !hasValue : hasValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>True when a collection or count is non-empty. Pass "invert" to flip it.</summary>
public sealed class CountToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var any = value switch
        {
            null => false,
            int i => i > 0,
            System.Collections.ICollection c => c.Count > 0,
            System.Collections.IEnumerable e => e.GetEnumerator().MoveNext(),
            _ => false,
        };

        return string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase) ? !any : any;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps the domain's framework-free <see cref="ImageStretch"/> onto Avalonia's Stretch.</summary>
public sealed class StretchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ImageStretch stretch
            ? stretch switch
            {
                ImageStretch.None => Stretch.None,
                ImageStretch.Fill => Stretch.Fill,
                ImageStretch.UniformToFill => Stretch.UniformToFill,
                _ => Stretch.Uniform,
            }
            : Stretch.Uniform;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Stretch stretch
            ? stretch switch
            {
                Stretch.None => ImageStretch.None,
                Stretch.Fill => ImageStretch.Fill,
                Stretch.UniformToFill => ImageStretch.UniformToFill,
                _ => ImageStretch.Uniform,
            }
            : ImageStretch.Uniform;
}

/// <summary>"#4C9AFF" to a brush, with a neutral fallback so an unset colour still renders.</summary>
public sealed class ColorHexToBrushConverter : IValueConverter
{
    private static readonly IBrush Fallback = new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x8C));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var color))
        {
            return new SolidColorBrush(color);
        }

        return Fallback;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// "#4C9AFF" to a translucent brush of the same hue, for chip fills and borders.
/// <para>
/// The ConverterParameter is the alpha as a 0-1 string, defaulting to 0.18. Tinting rather than
/// filling is what lets one palette work on both light and dark themes: the subject keeps its
/// identity colour, but the label underneath stays the theme's normal foreground and so stays
/// readable either way.
/// </para>
/// </summary>
public sealed class ColorHexToTintBrushConverter : IValueConverter
{
    private static readonly IBrush Fallback = new SolidColorBrush(Color.FromArgb(0x2E, 0x7A, 0x7A, 0x8C));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string hex || string.IsNullOrWhiteSpace(hex) || !Color.TryParse(hex, out var color))
        {
            return Fallback;
        }

        var alpha = parameter is string raw
                    && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, 0, 1)
            : 0.18;

        return new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), color.R, color.G, color.B));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// A count to a star <see cref="GridLength"/>, so two columns can divide a track in the ratio of
/// the numbers bound to them. Zero yields a zero-width star column rather than collapsing to Auto.
/// </summary>
public sealed class CountToStarConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var share = value switch
        {
            int i => i,
            long l => l,
            double d => d,
            _ => 0d,
        };

        return new GridLength(Math.Max(share, 0), GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>"in 3 days" / "2 hours ago" / "due now" for a DateTimeOffset.</summary>
public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTimeOffset instant)
        {
            return "never studied";
        }

        var delta = instant - DateTimeOffset.UtcNow;
        var future = delta > TimeSpan.Zero;
        var magnitude = delta.Duration();

        var span = magnitude switch
        {
            { TotalMinutes: < 1 } => "moments",
            { TotalHours: < 1 } => $"{(int)magnitude.TotalMinutes} min",
            { TotalDays: < 1 } => $"{(int)magnitude.TotalHours} hr",
            { TotalDays: < 31 } => $"{(int)magnitude.TotalDays} day{(magnitude.TotalDays >= 2 ? "s" : string.Empty)}",
            { TotalDays: < 365 } => $"{magnitude.TotalDays / 30.4:0.#} mo",
            _ => $"{magnitude.TotalDays / 365:0.#} yr",
        };

        return future ? $"in {span}" : magnitude.TotalMinutes < 1 ? "due now" : $"{span} ago";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// The card type's full name, for the designer's type drop-down. Distinct from
/// <see cref="CardTypeGlyphConverter"/>, which is the terse form used on dense list rows.
/// </summary>
public sealed class CardTypeNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is CardType type
            ? type switch
            {
                CardType.MultipleChoice => "Multiple choice",
                CardType.Cloze => "Fill in the blank",
                CardType.Freeform => "Custom design",
                _ => "Question & answer",
            }
            : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>A short label for the card type, used on list rows.</summary>
public sealed class CardTypeGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is CardType type
            ? type switch
            {
                CardType.MultipleChoice => "choice",
                CardType.Cloze => "cloze",
                _ => "basic",
            }
            : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// A subject's depth to a left margin, so a flat list renders as a tree.
/// <para>
/// One step per level below the root, as a real <see cref="Thickness"/> rather than leading spaces
/// in the text: a name trimmed with an ellipsis keeps its indent, and the controls to the left of
/// the name stay on a straight edge. Depth is capped at five by the domain, so this never runs
/// further than four steps.
/// </para>
/// <para>
/// The ConverterParameter is the step in pixels, defaulting to 14.
/// </para>
/// </summary>
public sealed class DepthToIndentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var depth = value switch
        {
            int i => i,
            long l => (int)l,
            _ => 1,
        };

        var step = parameter is string raw
                   && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 14d;

        return new Avalonia.Thickness(Math.Max(depth - 1, 0) * step, 0, 0, 0);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Dims a subject chip the card only wears by inheritance.
/// <para>
/// A card tagged "MSSQL" also answers to "SQL" and "Databases", but it was never tagged with them —
/// they follow from where MSSQL sits. Rendering both at full strength would suggest the tree had
/// quietly edited the card, and would hide which of the chips can actually be taken off.
/// </para>
/// </summary>
public sealed class InheritedOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 0.5 : 1.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Explains the dimming on hover, since the visual difference alone does not say why.</summary>
public sealed class InheritedTipConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true
            ? "Inherited — this card is tagged with a subject that sits under this one"
            : "Tagged directly on this card";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// The label on the study session's back button, which is one control doing both directions:
/// it steps back to the question, and then forward to the answer again.
/// </summary>
public sealed class ReviewToggleLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "Answer" : "Back";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// The arrow on that same button. It points back to the question and then forward to the answer
/// again, so the direction has to turn round with the label — a fixed left arrow on a control that
/// takes you forward would be worse than no arrow at all.
/// </summary>
public sealed class ReviewToggleIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is true ? "IconChevronRight" : "IconChevronLeft";

        return Avalonia.Application.Current?.TryFindResource(key, out var geometry) == true ? geometry : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Fills the radio dot when a quiz choice is selected, and leaves it hollow otherwise.</summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    private static readonly IBrush On = new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? On : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Resolves a theme resource key held as a string — "SemiColorPrimary" — into the brush it names.
/// <para>
/// The study modes carry their accent as a key rather than a colour so the catalogue stays plain
/// data with no dependency on Avalonia, and so a mode tile follows the theme like everything else.
/// A key that does not resolve falls back to transparent rather than throwing: a missing stripe is
/// a cosmetic problem, and a panel that will not render is not.
/// </para>
/// </summary>
public sealed class ThemeBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || Avalonia.Application.Current is not { } app)
        {
            return Brushes.Transparent;
        }

        if (app.TryGetResource(key, app.ActualThemeVariant, out var found) && found is IBrush brush)
        {
            return brush;
        }

        // A literal "#RRGGBB" is accepted too. The theme's own tokens are preferred where one
        // fits, but the study modes need seven distinguishable hues and the theme does not supply
        // seven — and a key that silently resolves to nothing paints an invisible icon, which is
        // exactly the failure this fallback exists to make impossible.
        return Color.TryParse(key, out var colour) ? new SolidColorBrush(colour) : Brushes.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Resolves an icon resource key held as a string — "IconShuffle" — into the geometry it names.
/// <para>
/// The sibling of <see cref="ThemeBrushConverter"/>, and there for the same reason: the study mode
/// catalogue and the navigation list are plain data, so they carry the <em>name</em> of an icon
/// rather than a <c>Geometry</c> that would drag Avalonia into them. A key that does not resolve
/// draws nothing, which loses an icon rather than a panel.
/// </para>
/// </summary>
public sealed class IconGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string key ? Resolve(key) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    internal static Geometry? Resolve(string key)
        => Avalonia.Application.Current is { } app
           && app.TryGetResource(key, app.ActualThemeVariant, out var found)
            ? found as Geometry
            : null;
}

/// <summary>
/// The icon for a card type, for the designer's type drop-down.
/// <para>
/// A sibling of <see cref="CardTypeNameConverter"/> rather than another column on some table: the
/// mapping is four cases and belongs next to the one that supplies the names, so a new card type
/// is two lines in one file instead of a lookup somebody forgets to extend.
/// </para>
/// </summary>
public sealed class CardTypeIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is CardType type ? IconGeometryConverter.Resolve(KeyFor(type)) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static string KeyFor(CardType type) => type switch
    {
        CardType.MultipleChoice => "IconCardChoice",
        CardType.Cloze => "IconCardCloze",
        CardType.Freeform => "IconCardFreeform",
        _ => "IconCardStandard",
    };
}
