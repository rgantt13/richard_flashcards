using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Application.Contracts;

namespace Flashcards.Application.Cards.Queries;

public sealed record GetFlashcardDetailQuery(Guid Id) : IQuery<FlashcardDetail?>;

internal sealed class GetFlashcardDetailHandler(IFlashcardReadStore store)
    : IQueryHandler<GetFlashcardDetailQuery, FlashcardDetail?>
{
    public Task<FlashcardDetail?> HandleAsync(GetFlashcardDetailQuery query, CancellationToken cancellationToken)
        => store.GetDetailAsync(query.Id, cancellationToken);
}
