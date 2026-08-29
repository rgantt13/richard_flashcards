using Flashcards.Application.Contracts;
using Flashcards.Domain.Practice;

namespace Flashcards.Application.Abstractions.Persistence;

/// <summary>
/// The answer history. Append-only by design: there is no scheduling state to keep in step any
/// more, just a record of what happened, which the statistics are aggregated from.
/// </summary>
public interface IReviewLogRepository
{
    Task AppendAsync(ReviewRecord record, CancellationToken cancellationToken);

    /// <summary>Forgets a card's history. Used when the user asks to reset its statistics.</summary>
    Task<int> ClearAsync(IReadOnlyCollection<Guid> cardIds, CancellationToken cancellationToken);
}

/// <summary>
/// Read-side access to the answer history: the whole library at a glance, and one card's record.
/// Both are pure aggregates over the review log and touch nothing else.
/// </summary>
public interface IStatsReadStore
{
    Task<OverallStats> GetOverallStatsAsync(CancellationToken cancellationToken);

    Task<CardStats> GetCardStatsAsync(Guid cardId, CancellationToken cancellationToken);
}
