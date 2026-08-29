namespace Flashcards.Domain.Cards.Validation;

/// <summary>
/// The rules a card has to satisfy before it can be saved.
/// <para>
/// These are deliberately <em>not</em> the same thing as the guards inside the aggregate. A guard —
/// <c>Rename("")</c>, <c>RemoveSubject</c> on the last tag — throws, because it is stopping the
/// object entering a state it must never be in. The rules here collect instead of throwing, because
/// a half-built card is a perfectly normal thing to be holding: the designer shows you every
/// problem at once and lets you keep working. Reporting the first and abandoning the rest would
/// turn writing a card into a guessing game.
/// </para>
/// <para>
/// They live outside <see cref="Flashcard"/> because there is one of them per card type and they
/// are the part most likely to change — adding a card type means adding a rule here, not editing
/// the aggregate. <see cref="Flashcard.Validate"/> stays the way in, so nothing outside the domain
/// has to know they moved.
/// </para>
/// </summary>
internal static class FlashcardRules
{
    public static IReadOnlyList<string> Check(Flashcard card) => [.. Failures(card)];

    private static IEnumerable<string> Failures(Flashcard card)
    {
        foreach (var failure in QuestionHasContent(card))
        {
            yield return failure;
        }

        foreach (var failure in ShapeMatchesType(card))
        {
            yield return failure;
        }

        foreach (var failure in EveryBlockCarriesSomething(card))
        {
            yield return failure;
        }
    }

    /// <summary>True of every card type: something has to be asked.</summary>
    private static IEnumerable<string> QuestionHasContent(Flashcard card)
    {
        if (!card.QuestionBlocks.Any())
        {
            yield return "The question side needs at least one block.";
        }
    }

    /// <summary>
    /// What each card type additionally requires. The four cases are the four ways a card can be
    /// answered, so this switch is the shape of the feature rather than an accident of layout.
    /// </summary>
    private static IEnumerable<string> ShapeMatchesType(Flashcard card) => card.CardType switch
    {
        CardType.Standard => StandardRules(card),
        CardType.MultipleChoice => MultipleChoiceRules(card),
        CardType.Cloze => ClozeRules(card),
        CardType.Freeform => FreeformRules(card),
        _ => [],
    };

    private static IEnumerable<string> StandardRules(Flashcard card)
    {
        if (!card.AnswerBlocks.Any())
        {
            yield return "A standard card needs at least one answer block.";
        }
    }

    private static IEnumerable<string> MultipleChoiceRules(Flashcard card)
    {
        if (card.Choices.Count < 2)
        {
            yield return "A multiple-choice card needs at least two options.";
        }

        if (!card.Choices.Any(c => c.IsCorrect))
        {
            yield return "Mark at least one option as correct.";
        }

        // A card where everything is correct tests nothing. Guarded on Count > 1 so that a card
        // still being built does not collect this on top of the "needs two options" line above.
        if (card.Choices.All(c => c.IsCorrect) && card.Choices.Count > 1)
        {
            yield return "At least one option must be incorrect, or there is nothing to test.";
        }

        if (card.Choices.Any(c => c.IsBlank))
        {
            yield return "Every answer slot needs either text or an image.";
        }
    }

    private static IEnumerable<string> ClozeRules(Flashcard card)
    {
        if (card.ClozeBlanks.Count == 0)
        {
            yield return "A cloze card needs at least one {{blank}} on the question side.";
        }
    }

    private static IEnumerable<string> FreeformRules(Flashcard card)
    {
        // Both faces are canvases the author arranges, so the only structural rule is that neither
        // side is blank. An ink layer with no strokes left on it does not count as content —
        // otherwise erasing a drawing would leave a card that looks empty but still validates.
        if (!card.AnswerBlocks.Any(b => !b.IsBlankDrawing))
        {
            yield return "The answer side of a designed card needs at least one element.";
        }

        if (!card.QuestionBlocks.Any(b => !b.IsBlankDrawing))
        {
            yield return "The question side of a designed card needs at least one element.";
        }
    }

    /// <summary>
    /// A block that carries nothing at all, whatever the card type. Text blocks are trimmed on the
    /// way in, so an empty one here means the author cleared it after adding it.
    /// </summary>
    private static IEnumerable<string> EveryBlockCarriesSomething(Flashcard card)
    {
        if (card.Blocks.Any(b => !b.IsImage && string.IsNullOrWhiteSpace(b.Text)))
        {
            yield return "One or more text blocks are empty.";
        }

        if (card.Blocks.Any(b => b.IsImage && b.MediaId is null))
        {
            yield return "One or more image blocks have no image attached.";
        }
    }
}
