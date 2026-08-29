using Flashcards.Domain.Cards;

namespace Flashcards.Application.Contracts;

/// <summary>
/// What the user picked on the "start studying" screen.
/// <para>
/// There is no due/new/ahead split any more: nothing schedules cards, so every card in the chosen
/// subjects is equally eligible and the only questions left are which subjects and how many.
/// </para>
/// </summary>
public sealed record QuizOptions
{
    /// <summary>
    /// Narrows the draw to these subjects. Empty means the whole library, which is what the
    /// quick-start modes use.
    /// </summary>
    public IReadOnlyCollection<Guid> SubjectIds { get; init; } = [];

    /// <summary>
    /// An exact set of cards to study, hand-picked. When this is non-empty it wins outright —
    /// subjects are ignored, because the user has already said precisely what they want.
    /// </summary>
    public IReadOnlyCollection<Guid> CardIds { get; init; } = [];

    public int MaxCards { get; init; } = 20;

    /// <summary>
    /// Draw the cards answered wrong most often first, rather than at random. A way to lean on
    /// weak spots when you want to — not a routine the app imposes.
    /// </summary>
    public bool HardestFirst { get; init; }

    /// <summary>Shuffle multiple-choice options each time the card is shown.</summary>
    public bool ShuffleChoices { get; init; } = true;
}

/// <summary>The ordered queue of card ids for one sitting.</summary>
public sealed record QuizSession(IReadOnlyList<Guid> CardIds)
{
    public bool IsEmpty => CardIds.Count == 0;
}

/// <summary>One card, ready to render. Everything the quiz view needs, already resolved.</summary>
public sealed record QuizCard(
    Guid Id,
    string Name,
    IReadOnlyList<SubjectRef> Subjects,
    CardType CardType,
    IReadOnlyList<ContentBlockDto> QuestionBlocks,
    IReadOnlyList<ContentBlockDto> AnswerBlocks,
    IReadOnlyList<ChoiceDto> Choices,
    bool IsMultiSelect,
    string? Notes,
    /// <summary>This card's own running record, so the answer side can show how you tend to do on it.</summary>
    CardStats Stats);

/// <summary>What happened after an answer was recorded.</summary>
public sealed record AnswerResult(Guid CardId, bool WasCorrect, CardStats Stats);
