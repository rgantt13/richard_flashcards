using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Application.Contracts;

namespace Flashcards.Application.Subjects.Queries;

/// <summary>
/// Every subject with its card/due/new counts. Backs the tag autocomplete in the card designer
/// and the subject filters on the manage and study panels.
/// </summary>
public sealed record GetSubjectsQuery : IQuery<IReadOnlyList<SubjectSummary>>;

/// <summary>
/// The cards standing in the way of deleting this subject — those wearing it and nothing else,
/// where it has no parent for them to be promoted into. Empty means the delete will go through.
/// <para>
/// Asked before the confirmation is shown so that an impossible delete is refused outright rather
/// than confirmed and then rejected. <see cref="Commands.DeleteSubjectCommand"/> checks the same
/// thing again when it runs, because between the two the tree may have moved.
/// </para>
/// </summary>
public sealed record GetSubjectDeletionBlockersQuery(Guid Id) : IQuery<IReadOnlyList<string>>;

internal sealed class GetSubjectDeletionBlockersHandler(ISubjectRepository subjects)
    : IQueryHandler<GetSubjectDeletionBlockersQuery, IReadOnlyList<string>>
{
    public async Task<IReadOnlyList<string>> HandleAsync(
        GetSubjectDeletionBlockersQuery query,
        CancellationToken cancellationToken)
    {
        if (await subjects.GetAsync(query.Id, cancellationToken) is not { } subject)
        {
            return [];
        }

        return await subjects.FindCardsOrphanedByDeleteAsync(query.Id, subject.ParentId, cancellationToken);
    }
}

internal sealed class GetSubjectsHandler(ISubjectReadStore store)
    : IQueryHandler<GetSubjectsQuery, IReadOnlyList<SubjectSummary>>
{
    public async Task<IReadOnlyList<SubjectSummary>> HandleAsync(GetSubjectsQuery query, CancellationToken cancellationToken)
    {
        var summaries = await store.GetSubjectSummariesAsync(cancellationToken);

        // Tree order, so the manage panel's tree and the designer's parent picker can both render
        // straight down the list without re-arranging it themselves.
        return SubjectOrdering.InTreeOrder(summaries, s => s.Id, s => s.ParentId, s => s.Name);
    }
}
