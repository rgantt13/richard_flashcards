using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Application.Contracts;
using Flashcards.Application.Subjects;

namespace Flashcards.Application.Transfer;

/// <summary>
/// Gathers the chosen subjects and cards into a <see cref="DeckDocument"/>.
/// <para>
/// The selection is taken as a floor, not a ceiling. Two things are pulled in whether or not they
/// were ticked: every ancestor of a chosen subject, and every subject a chosen card is tagged
/// with. Without the first the tree arrives with holes in it; without the second a card arrives
/// with a tag that does not exist on the far side, and a card with no subject is not a state the
/// domain allows.
/// </para>
/// </summary>
public sealed record BuildDeckExportQuery(
    IReadOnlyCollection<Guid> SubjectIds,
    IReadOnlyCollection<Guid> CardIds) : IQuery<DeckDocument>;

internal sealed class BuildDeckExportHandler(IFlashcardReadStore cardStore, ISubjectReadStore subjectStore, IMediaStore media)
    : IQueryHandler<BuildDeckExportQuery, DeckDocument>
{
    public async Task<DeckDocument> HandleAsync(BuildDeckExportQuery query, CancellationToken cancellationToken)
    {
        var allSubjects = await subjectStore.GetSubjectSummariesAsync(cancellationToken);
        var byId = allSubjects.ToDictionary(s => s.Id);

        var cards = new List<DeckCard>();
        var wanted = new HashSet<Guid>(query.SubjectIds);

        foreach (var cardId in query.CardIds.Distinct())
        {
            if (await cardStore.GetDetailAsync(cardId, cancellationToken) is not { } detail)
            {
                // Deleted between the picker being filled and the export running. Silently
                // dropping it is right: the alternative is failing a whole export over one row.
                continue;
            }

            // Only the tags actually applied. The inherited ones come back on import from
            // wherever those tags end up sitting in the destination's tree.
            var tags = detail.Subjects.Where(s => !s.IsInherited).ToList();

            foreach (var tag in tags)
            {
                wanted.Add(tag.Id);
            }

            cards.Add(new DeckCard(
                detail.Id,
                detail.Name,
                detail.CardType,
                detail.Notes,
                detail.IsSuspended,
                [.. tags.Select(t => t.Name)],
                detail.Blocks,
                detail.Choices));
        }

        // Ancestors last, over the full set, so a card's tag drags its own chain in too.
        foreach (var id in wanted.ToList())
        {
            for (var walk = byId.GetValueOrDefault(id)?.ParentId; walk is { } parent; walk = byId.GetValueOrDefault(parent)?.ParentId)
            {
                if (!wanted.Add(parent))
                {
                    break;
                }
            }
        }

        // Tree order, so the file reads top-down and the importer meets every parent before the
        // children that name it.
        var subjects = SubjectOrdering
            .InTreeOrder([.. allSubjects.Where(s => wanted.Contains(s.Id))], s => s.Id, s => s.ParentId, s => s.Name)
            .Select(s => new DeckSubject(
                s.Name,
                // A parent left out of the selection cannot happen after the walk above, but if it
                // somehow does, the subject exports as top level rather than pointing at nothing.
                s.ParentId is { } p && wanted.Contains(p) ? byId[p].Name : null,
                s.ColorHex,
                s.Description))
            .ToList();

        return new DeckDocument
        {
            Subjects = subjects,
            Cards = cards,
            Media = await CollectMediaAsync(cards, cancellationToken),
        };
    }

    /// <summary>
    /// Every image the exported cards point at, once each. Media is content-addressed, so the
    /// same screenshot on ten cards is already one id and this loop needs no extra deduplication
    /// beyond the set.
    /// </summary>
    private async Task<List<DeckMedia>> CollectMediaAsync(List<DeckCard> cards, CancellationToken cancellationToken)
    {
        var ids = cards
            .SelectMany(c => c.Blocks.Select(b => b.MediaId).Concat(c.Choices.Select(ch => ch.MediaId)))
            .OfType<Guid>()
            .ToHashSet();

        var bundle = new List<DeckMedia>(ids.Count);

        foreach (var id in ids)
        {
            var descriptor = await media.DescribeAsync(id, cancellationToken);
            var bytes = await media.LoadAsync(id, cancellationToken);

            // A missing file is not worth failing the export over. The block that referenced it
            // still travels; the importer drops the reference and says so.
            if (descriptor is not null && bytes is { Length: > 0 })
            {
                bundle.Add(new DeckMedia(id, descriptor.FileName, descriptor.MimeType, bytes));
            }
        }

        return bundle;
    }
}
