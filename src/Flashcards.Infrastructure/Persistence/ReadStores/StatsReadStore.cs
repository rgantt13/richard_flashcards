using Dapper;
using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Application.Contracts;
using Flashcards.Infrastructure.Persistence.Rows;

namespace Flashcards.Infrastructure.Persistence.ReadStores;

/// <summary>
/// The read side of the answer history: the whole library at a glance, and one card's record.
/// <para>
/// Both are pure aggregates over <c>review_log</c> and touch no other table, which is what earns
/// them a file of their own — nothing here needs to know what a subject or a content block is.
/// </para>
/// </summary>
internal sealed class StatsReadStore(DbSession session) : IStatsReadStore
{
    public async Task<OverallStats> GetOverallStatsAsync(CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);

        // Every figure comes off review_log except the library counts. SUM(was_correct) works
        // because the column is constrained to 0/1 — cheaper than SUM(CASE WHEN ...) and, with the
        // CHECK constraint in place, exactly as safe.
        // Local midnight, expressed in UTC. Both halves matter: "today" is the user's day, not
        // Greenwich's, and the comparison below is lexicographic over round-trip "O" strings —
        // which only sorts correctly when every value carries the same +00:00 offset the stored
        // ones do. Converting before binding is what guarantees that.
        var since = new DateTimeOffset(DateTime.Today, DateTimeOffset.Now.Offset).ToUniversalTime();

        var row = await connection.QuerySingleAsync<OverallStatsRow>(new CommandDefinition(
            """
            SELECT (SELECT COUNT(*) FROM review_log)                        AS Answered,
                   (SELECT COALESCE(SUM(was_correct), 0) FROM review_log)   AS Correct,
                   (SELECT COUNT(*) FROM flashcards)                        AS TotalCards,
                   (SELECT COUNT(*) FROM subjects)                          AS SubjectCount,
                   (SELECT COUNT(DISTINCT card_id) FROM review_log)         AS CardsPractised,
                   (SELECT MAX(reviewed_utc) FROM review_log)               AS LastAnsweredUtc,
                   (SELECT COUNT(*) FROM review_log WHERE reviewed_utc >= @Since)
                                                                            AS AnsweredToday,
                   (SELECT COALESCE(SUM(was_correct), 0) FROM review_log WHERE reviewed_utc >= @Since)
                                                                            AS CorrectToday;
            """,
            new { Since = since },
            session.DbTransaction, cancellationToken: cancellationToken));

        return new OverallStats(
            new PracticeStats(row.Answered, row.Correct),
            row.TotalCards,
            row.SubjectCount,
            row.CardsPractised,
            row.LastAnsweredUtc)
        {
            Today = new PracticeStats(row.AnsweredToday, row.CorrectToday),
        };
    }

    public async Task<CardStats> GetCardStatsAsync(Guid cardId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);

        // AVG over an empty set is NULL in SQLite exactly as in T-SQL, which is what leaves
        // AverageSeconds null for a card nobody has answered yet.
        var row = await connection.QuerySingleAsync<CardStatsRow>(new CommandDefinition(
            """
            SELECT COUNT(*)                          AS Answered,
                   COALESCE(SUM(was_correct), 0)     AS Correct,
                   MAX(reviewed_utc)                 AS LastAnsweredUtc,
                   AVG(elapsed_ms) / 1000.0          AS AverageSeconds
            FROM   review_log
            WHERE  card_id = @CardId;
            """,
            new { CardId = cardId },
            session.DbTransaction, cancellationToken: cancellationToken));

        return new CardStats(
            cardId,
            new PracticeStats(row.Answered, row.Correct),
            row.LastAnsweredUtc,
            row.AverageSeconds);
    }
}
