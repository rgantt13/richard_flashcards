using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Application.Contracts;

namespace Flashcards.Application.Quiz.Queries;

/// <summary>
/// Builds the queue of card ids for a sitting. A query, not a command: nothing is written — the
/// session lives in the view model until the first answer comes back.
/// </summary>
public sealed record StartQuizSessionQuery(QuizOptions Options) : IQuery<QuizSession>;

internal sealed class StartQuizSessionHandler(IQuizReadStore store)
    : IQueryHandler<StartQuizSessionQuery, QuizSession>
{
    public async Task<QuizSession> HandleAsync(StartQuizSessionQuery query, CancellationToken cancellationToken)
    {
        var options = query.Options;

        var ids = await store.GetQuizQueueAsync(
            options.SubjectIds,
            options.CardIds,
            options.MaxCards,
            options.HardestFirst,
            cancellationToken);

        return new QuizSession(ids);
    }
}
