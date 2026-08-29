using Avalonia;
using Avalonia.Controls.Primitives;

namespace Flashcards.Desktop.Controls.Shared;

/// <summary>
/// A right/wrong split rendered as one bar — the infographic primitive the Study panel uses at
/// every tier, from the whole library down to a single card.
/// <para>
/// It takes raw counts rather than a percentage so the two segments can be sized in proportion to
/// each other directly, and so a card answered 1 of 2 and one answered 50 of 100 are visibly the
/// same shape while their labels differ.
/// </para>
/// <para>
/// With nothing answered both segments collapse and the empty track shows through, which is the
/// honest rendering of "no data" — as opposed to a full red bar, which would read as 0% correct.
/// </para>
/// </summary>
public class AccuracyBar : TemplatedControl
{
    public static readonly StyledProperty<int> CorrectProperty =
        AvaloniaProperty.Register<AccuracyBar, int>(nameof(Correct));

    public static readonly StyledProperty<int> WrongProperty =
        AvaloniaProperty.Register<AccuracyBar, int>(nameof(Wrong));

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
}
