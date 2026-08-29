using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Application.Contracts;
using Flashcards.Domain.Practice;

namespace Flashcards.Application.Quiz.Commands;

/// <summary>
/// Records how one card was answered.
/// <para>
/// This is all that is left of what used to be grading. Nothing is rescheduled, no interval is
/// computed and no ease factor is adjusted — the answer is written to the log and the card's
/// running tally is read back so the quiz can show it.
/// </para>
/// </summary>
public sealed record RecordAnswerCommand(Guid CardId, bool WasCorrect, TimeSpan Elapsed) : ICommand<AnswerResult>;

internal sealed class RecordAnswerHandler(
    IReviewLogRepository log,
    IStatsReadStore statsStore,
    IClock clock,
    IUnitOfWork unitOfWork)
    : ICommandHandler<RecordAnswerCommand, AnswerResult>
{
    public Task<AnswerResult> HandleAsync(RecordAnswerCommand command, CancellationToken cancellationToken)
        => unitOfWork.ExecuteAsync(async ct =>
        {
            await log.AppendAsync(
                ReviewRecord.Create(command.CardId, command.WasCorrect, command.Elapsed, clock.UtcNow),
                ct);

            // Read back inside the same transaction so the figure shown includes the answer just
            // given, rather than lagging one behind.
            var stats = await statsStore.GetCardStatsAsync(command.CardId, ct);

            return new AnswerResult(command.CardId, command.WasCorrect, stats);
        }, cancellationToken);
}
