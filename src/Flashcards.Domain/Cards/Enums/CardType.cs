namespace Flashcards.Domain.Cards;

/// <summary>
/// How a card is answered and graded. Persisted as INTEGER — never renumber these.
/// </summary>
public enum CardType
{
    /// <summary>Question side, answer side, you grade yourself after flipping.</summary>
    Standard = 0,

    /// <summary>Question side plus a fixed list of choices. Auto-graded, then the answer side is shown as an explanation.</summary>
    MultipleChoice = 1,

    /// <summary>Question side contains {{blanks}}. You type or recall each blank, then reveal.</summary>
    Cloze = 2,

    /// <summary>
    /// A designed card. Each face is a canvas of freely positioned elements — text, images and
    /// freehand ink — rather than a vertical stack, so diagrams, labelled screenshots and
    /// hand-drawn working all sit where the author put them.
    /// </summary>
    Freeform = 3,
}

