namespace Flashcards.Domain.Cards;


/// <summary>
/// Mirrors Avalonia's Stretch enum so the Domain does not depend on the UI framework.
/// The Desktop layer maps this 1:1.
/// </summary>
public enum ImageStretch
{
    /// <summary>Native pixel size.</summary>
    None = 0,

    /// <summary>Fill the box, ignoring aspect ratio.</summary>
    Fill = 1,

    /// <summary>Fit inside the box, preserving aspect ratio. The sane default.</summary>
    Uniform = 2,

    /// <summary>Cover the box, preserving aspect ratio, cropping the overflow.</summary>
    UniformToFill = 3,
}
