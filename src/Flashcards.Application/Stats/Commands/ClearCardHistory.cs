using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Abstractions.Persistence;

namespace Flashcards.Application.Stats.Commands;

/// <summary>
/// Forgets a card's answer history, resetting its percentages to nothing.
/// <para>
/// The nearest thing to the old "reset schedule", but honest about what it does: there is no
/// schedule to reset, only a record to discard. The card itself is untouched.
/// </para>
/// </summary>
public sealed record ClearCardHistoryCommand(IReadOnlyCollection<Guid> CardIds) : ICommand<int>;

internal sealed class ClearCardHistoryHandler(IReviewLogRepository log, IUnitOfWork unitOfWork)
    : ICommandHandler<ClearCardHistoryCommand, int>
{
    public Task<int> HandleAsync(ClearCardHistoryCommand command, CancellationToken cancellationToken)
        => unitOfWork.ExecuteAsync(ct => log.ClearAsync(command.CardIds, ct), cancellationToken);
}
