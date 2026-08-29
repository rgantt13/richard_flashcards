namespace Flashcards.Domain.Cards;

/// <summary>
/// The format of a single content block. A face is an ordered list of these, which is
/// what lets one question mix prose, a code snippet and a screenshot.
/// Persisted as INTEGER — never renumber.
/// </summary>
public enum ContentKind
{
    PlainText = 0,
    Markdown = 1,
    Code = 2,
    Image = 3,

    /// <summary>
    /// Freehand ink. The strokes live in the block's text, serialised by <see cref="InkSerializer"/>;
    /// only freeform cards produce these.
    /// </summary>
    Drawing = 4,
}
