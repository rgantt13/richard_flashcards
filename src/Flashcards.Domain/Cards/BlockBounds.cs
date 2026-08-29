using Flashcards.Domain.Common;

namespace Flashcards.Domain.Cards;

/// <summary>
/// Where an element sits on a freeform card's canvas, and how big it is.
/// <para>
/// The coordinate space is the fixed logical card described by <see cref="CardCanvas"/>, not
/// screen pixels. That is what lets a card designed in a small window study correctly in a large
/// one: the whole canvas is scaled as a unit, so relative positions never drift.
/// </para>
/// <para>
/// A block with no bounds is laid out in flow — stacked under the previous one. That is how every
/// standard, multiple-choice and cloze card works, and why this is nullable on
/// <see cref="ContentBlock"/> rather than mandatory.
/// </para>
/// </summary>
public readonly record struct BlockBounds(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    /// <summary>
    /// Clamps the rectangle inside the canvas, keeping its size where possible. Moving an element
    /// past the edge parks it at the edge instead of letting it disappear.
    /// </summary>
    public BlockBounds ClampToCanvas()
    {
        var width = Math.Clamp(Width, CardCanvas.MinElementSize, CardCanvas.Width);
        var height = Math.Clamp(Height, CardCanvas.MinElementSize, CardCanvas.Height);

        return this with
        {
            X = Math.Clamp(X, 0, CardCanvas.Width - width),
            Y = Math.Clamp(Y, 0, CardCanvas.Height - height),
            Width = width,
            Height = height,
        };
    }

    public static BlockBounds Create(double x, double y, double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new DomainException("An element needs a positive width and height.");
        }

        return new BlockBounds(x, y, width, height).ClampToCanvas();
    }
}

