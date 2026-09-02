using Flashcards.Domain.Cards;

namespace Flashcards.Application.Contracts;

/// <summary>
/// Which cards a mode draws, and in what order.
/// <para>
/// This replaced a <c>HardestFirst</c> flag once there were more than two answers to the question.
/// Nothing here schedules anything — every one of these is a way of ordering cards that are all
/// equally eligible, so choosing a mode is choosing an emphasis rather than being told what is due.
/// </para>
/// </summary>
public enum QuizDraw
{
    /// <summary>Shuffled. What Random and Custom use.</summary>
    Random = 0,

    /// <summary>
    /// Weighted by how often you get each card wrong, with never-answered cards leading — the
    /// cards you have most to gain from.
    /// </summary>
    HardestFirst = 1,

    /// <summary>Only cards that have never been answered.</summary>
    Untouched = 2,

    /// <summary>
    /// Only cards whose <em>most recent</em> answer was wrong, newest first. Deliberately not the
    /// same as <see cref="HardestFirst"/>: that ranks by a lifetime average, so a card you have
    /// finally learned still scores badly for a long time. This one is about what you fluffed
    /// last, which is the thing worth another look today.
    /// </summary>
    RecentlyMissed = 3,
}

/// <summary>
/// What the user picked on the "start studying" screen: which cards, how many, in what order.
/// <para>
/// Session behaviour that does not change <em>which</em> cards are drawn — the time limits, for
/// instance — deliberately does not live here. Those never reach the database, so putting them on
/// a query input would be describing something the handler cannot act on.
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

    public QuizDraw Draw { get; init; } = QuizDraw.Random;

    /// <summary>
    /// Restricts the draw to card types the app can mark for you — multiple choice and cloze.
    /// A standard or designed card is graded by the person answering it, which is exactly what a
    /// timed drill has no room for.
    /// </summary>
    public bool AutoGradedOnly { get; init; }

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
