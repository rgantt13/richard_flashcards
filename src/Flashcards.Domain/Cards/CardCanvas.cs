namespace Flashcards.Domain.Cards;

/// <summary>
/// The logical card the freeform designer draws on. Fixed so that a card is a stable artefact
/// rather than something whose layout depends on the window it happened to be authored in.
/// </summary>
public static class CardCanvas
{
    /// <summary>A 8:5 landscape card — close to an index card, and comfortable on a laptop screen.</summary>
    public const double Width = 960d;

    public const double Height = 600d;

    /// <summary>Below this an element is too small to grab, let alone read.</summary>
    public const double MinElementSize = 24d;

    public static double AspectRatio => Width / Height;
}
