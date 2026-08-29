using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Application.Contracts;

namespace Flashcards.Application.Cards.Queries;

public sealed record SearchFlashcardsQuery(FlashcardSearchCriteria Criteria) : IQuery<PagedResult<FlashcardSummary>>;

internal sealed class SearchFlashcardsHandler(IFlashcardReadStore store)
    : IQueryHandler<SearchFlashcardsQuery, PagedResult<FlashcardSummary>>
{
    public Task<PagedResult<FlashcardSummary>> HandleAsync(SearchFlashcardsQuery query, CancellationToken cancellationToken)
        => store.SearchAsync(query.Criteria, cancellationToken);
}
