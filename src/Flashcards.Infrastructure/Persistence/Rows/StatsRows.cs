namespace Flashcards.Infrastructure.Persistence.Rows;

// Dapper materialization targets for the answer-history aggregates. See CardRows.cs for why
// these are plain classes with a parameterless constructor rather than positional records.

internal sealed class OverallStatsRow
{
    public int Answered { get; init; }
    public int Correct { get; init; }
    public int TotalCards { get; init; }
    public int SubjectCount { get; init; }
    public int CardsPractised { get; init; }
    public DateTimeOffset? LastAnsweredUtc { get; init; }
    public int AnsweredToday { get; init; }
    public int CorrectToday { get; init; }
}

internal sealed class SubjectStatsRow
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string? ColorHex { get; init; }
    public int CardCount { get; init; }
    public int Answered { get; init; }
    public int Correct { get; init; }
    public int CardsPractised { get; init; }
    public Guid? ParentId { get; init; }
    public int Depth { get; init; }
    public int DirectCardCount { get; init; }

    /// <summary>SQLite has no boolean type; EXISTS yields 0 or 1 as an INTEGER.</summary>
    public int HasChildren { get; init; }
}

internal sealed class CardStatsRow
{
    public int Answered { get; init; }
    public int Correct { get; init; }
    public DateTimeOffset? LastAnsweredUtc { get; init; }
    public int AnsweredToday { get; init; }
    public int CorrectToday { get; init; }
    public double? AverageSeconds { get; init; }
}

internal sealed class DailyActivityRow
{
    /// <summary>"YYYY-MM-DD" as SQLite's date() produces it, parsed by the caller.</summary>
    public string Day { get; init; } = "";
    public int Answered { get; init; }
    public int Correct { get; init; }
}
