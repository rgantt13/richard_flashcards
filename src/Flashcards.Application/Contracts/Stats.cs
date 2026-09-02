namespace Flashcards.Application.Contracts;

// The answer history, at three levels of zoom: everything, one subject, one card. They share
// a shape because they are the same numbers — see PracticeStats.

/// <summary>
/// A tally of answers over some scope — everything, one subject, or one card. The three stats
/// panels on the manage screen are the same numbers at three levels of zoom, so they share a shape.
/// </summary>
public sealed record PracticeStats(int Answered, int Correct)
{
    public int Wrong => Answered - Correct;

    /// <summary>Fraction correct in 0..1. Zero answers reads as zero rather than dividing by it.</summary>
    public double Accuracy => Answered == 0 ? 0 : Correct / (double)Answered;

    /// <summary>Whether there is anything to report. Drives "not practised yet" placeholders.</summary>
    public bool HasHistory => Answered > 0;

    public static PracticeStats Empty { get; } = new(0, 0);
}

/// <summary>Everything the header panel shows: the whole library at a glance.</summary>
public sealed record OverallStats(
    PracticeStats Practice,
    int TotalCards,
    int SubjectCount,
    /// <summary>Cards that have been answered at least once — the rest are untouched.</summary>
    int CardsPractised,
    DateTimeOffset? LastAnsweredUtc)
{
    public int CardsUntouched => Math.Max(TotalCards - CardsPractised, 0);

    /// <summary>
    /// Just today's answers, counted from local midnight.
    /// <para>
    /// Carried on the same record rather than fetched separately because it comes off the same
    /// table in the same round trip, and because everywhere that wants "how am I doing" wants both
    /// the lifetime figure and the one for the session you are actually in.
    /// </para>
    /// </summary>
    public PracticeStats Today { get; init; } = PracticeStats.Empty;

    /// <summary>Whether anything has been answered since midnight. Drives the sidebar's placeholder.</summary>
    public bool StudiedToday => Today.Answered > 0;
}

/// <summary>
/// One subject's record, for the subject panel.
/// <para>
/// Every figure here is rolled up over the subject's whole subtree, because that is what selecting
/// it studies: picking "SQL" picks everything under MSSQL and SQLite too. A card tagged with both a
/// parent and one of its children is counted once, not twice.
/// </para>
/// </summary>
public sealed record SubjectStats(
    Guid Id,
    string Name,
    string? ColorHex,
    PracticeStats Practice,
    int CardCount,
    int CardsPractised,
    Guid? ParentId = null,
    /// <summary>1 for a top-level subject. Drives the indent on the study panel's subject grid.</summary>
    int Depth = 1,
    /// <summary>Cards wearing this exact tag, excluding descendants — shown as a secondary figure.</summary>
    int DirectCardCount = 0)
{
    public int CardsUntouched => Math.Max(CardCount - CardsPractised, 0);

    /// <summary>Whether this subject groups others, which is what earns it a twisty in a tree view.</summary>
    public bool HasChildren { get; init; }
}

/// <summary>One card's record, for the card panel and the quiz's answer side.</summary>
public sealed record CardStats(
    Guid CardId,
    PracticeStats Practice,
    DateTimeOffset? LastAnsweredUtc,
    /// <summary>Mean time on screen before answering. Null until the card has been answered once.</summary>
    double? AverageSeconds)
{
    public static CardStats Empty(Guid cardId) => new(cardId, PracticeStats.Empty, null, null);
}
