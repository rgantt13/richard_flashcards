using Dapper;
using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Domain.Practice;

namespace Flashcards.Infrastructure.Persistence.Repositories;

/// <summary>
/// The answer history. Append and forget — there is no update path, because a record of something
/// that happened does not change.
/// </summary>
internal sealed class ReviewLogRepository(DbSession session) : IReviewLogRepository
{
    public async Task AppendAsync(ReviewRecord record, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO review_log (card_id, reviewed_utc, was_correct, elapsed_ms)
            VALUES (@CardId, @ReviewedUtc, @WasCorrect, @ElapsedMs);
            """,
            new
            {
                record.CardId,
                record.ReviewedUtc,
                WasCorrect = record.WasCorrect ? 1 : 0,
                ElapsedMs = (long)record.Elapsed.TotalMilliseconds,
            },
            session.DbTransaction, cancellationToken: cancellationToken));
    }

    public async Task<int> ClearAsync(IReadOnlyCollection<Guid> cardIds, CancellationToken cancellationToken)
    {
        if (cardIds.Count == 0)
        {
            return 0;
        }

        var connection = await session.GetConnectionAsync(cancellationToken);

        // Dapper expands an IEnumerable parameter used with IN into (@Ids1, @Ids2, ...).
        // [T-SQL] Same behaviour as against SQL Server, but note SQLite's default limit of 999
        // bound parameters (32766 since 3.32) — chunk larger lists.
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM review_log WHERE card_id IN @Ids;",
            new { Ids = cardIds.ToArray() },
            session.DbTransaction, cancellationToken: cancellationToken));
    }

    public async Task<int> ClearAllAsync(CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);

        // No WHERE, and no id list to chunk past SQLite's bound-parameter limit — which is the
        // whole reason this is not the other method called with every id in the library.
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM review_log;",
            transaction: session.DbTransaction, cancellationToken: cancellationToken));
    }
}
