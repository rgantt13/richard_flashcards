using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Abstractions.Persistence;

namespace Flashcards.Application.Stats.Commands;

/// <summary>
/// Forgets every answer ever recorded, across the whole library. The cards themselves are
/// untouched — this is the statistics going back to zero, not a deletion.
/// <para>
/// Its own command rather than <see cref="ClearCardHistoryCommand"/> over every id: the ids would
/// have to be fetched first, chunked past SQLite's bound-parameter limit, and the result would
/// still be a worse way of saying <c>DELETE FROM review_log</c>.
/// </para>
/// </summary>
public sealed record ClearAllHistoryCommand : ICommand<int>;

internal sealed class ClearAllHistoryHandler(IReviewLogRepository log, IUnitOfWork unitOfWork)
    : ICommandHandler<ClearAllHistoryCommand, int>
{
    public Task<int> HandleAsync(ClearAllHistoryCommand command, CancellationToken cancellationToken)
        => unitOfWork.ExecuteAsync(log.ClearAllAsync, cancellationToken);
}
