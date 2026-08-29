using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Application.Contracts;
using Flashcards.Application.Subjects;

namespace Flashcards.Application.Stats.Queries;

/// <summary>
/// The whole library at a glance — the header panel on the manage screen.
/// </summary>
public sealed record GetOverallStatsQuery : IQuery<OverallStats>;

internal sealed class GetOverallStatsHandler(IStatsReadStore store)
    : IQueryHandler<GetOverallStatsQuery, OverallStats>
{
    public Task<OverallStats> HandleAsync(GetOverallStatsQuery query, CancellationToken cancellationToken)
        => store.GetOverallStatsAsync(cancellationToken);
}

/// <summary>
/// Every subject with its tally. Returns them all rather than one at a time: the subject panel
/// needs the list to choose from anyway, and picking a different subject should not cost a query.
/// </summary>
public sealed record GetSubjectStatsQuery : IQuery<IReadOnlyList<SubjectStats>>;

internal sealed class GetSubjectStatsHandler(ISubjectReadStore store)
    : IQueryHandler<GetSubjectStatsQuery, IReadOnlyList<SubjectStats>>
{
    public async Task<IReadOnlyList<SubjectStats>> HandleAsync(GetSubjectStatsQuery query, CancellationToken cancellationToken)
    {
        var stats = await store.GetSubjectStatsAsync(cancellationToken);

        // Nested rather than alphabetical: the study panel indents by Depth, and a child has to
        // follow its parent for that indent to read as containment.
        return SubjectOrdering.InTreeOrder(stats, s => s.Id, s => s.ParentId, s => s.Name);
    }
}

/// <summary>One card's record. Unlike subjects there can be thousands, so this is fetched on demand.</summary>
public sealed record GetCardStatsQuery(Guid CardId) : IQuery<CardStats>;

internal sealed class GetCardStatsHandler(IStatsReadStore store)
    : IQueryHandler<GetCardStatsQuery, CardStats>
{
    public Task<CardStats> HandleAsync(GetCardStatsQuery query, CancellationToken cancellationToken)
        => store.GetCardStatsAsync(query.CardId, cancellationToken);
}
