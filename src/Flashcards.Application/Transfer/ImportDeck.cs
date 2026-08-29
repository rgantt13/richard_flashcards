using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Application.Cards;
using Flashcards.Application.Cards.Commands;
using Flashcards.Application.Contracts;
using Flashcards.Domain.Common;
using Flashcards.Domain.Subjects;

namespace Flashcards.Application.Transfer;

/// <summary>What to do about a card the library already has.</summary>
public enum DeckImportMode
{
    /// <summary>Leave the copy already here alone and move on.</summary>
    Skip,

    /// <summary>Overwrite it with the one from the file. Its answer history is untouched.</summary>
    Replace,
}

/// <summary>
/// Brings part of a deck into the library.
/// <para>
/// <paramref name="SubjectNames"/> and <paramref name="CardIds"/> are what the user ticked, and
/// they are a floor rather than a ceiling for the same reason as on the way out: a chosen card
/// drags in the subjects it is tagged with, and a chosen subject drags in its ancestors, because
/// neither can be recreated without them.
/// </para>
/// </summary>
public sealed record ImportDeckCommand(
    DeckDocument Deck,
    IReadOnlyCollection<string> SubjectNames,
    IReadOnlyCollection<Guid> CardIds,
    DeckImportMode Mode = DeckImportMode.Skip) : ICommand<DeckImportResult>;

/// <summary>
/// What the import did. <see cref="Warnings"/> is the part worth reading: a card that could not be
/// brought in is reported there rather than thrown, so one bad row does not cost you the other
/// forty-nine.
/// </summary>
public sealed record DeckImportResult(
    int SubjectsCreated,
    int CardsAdded,
    int CardsReplaced,
    int CardsSkipped,
    int Images,
    IReadOnlyList<string> Warnings)
{
    public string Summary
    {
        get
        {
            var parts = new List<string>();

            Add(CardsAdded, "card", "added");
            Add(CardsReplaced, "card", "replaced");
            Add(CardsSkipped, "card", "skipped");
            Add(SubjectsCreated, "subject", "created");

            // Only worth a mention when something actually landed. "1 image" beside a line saying
            // every card was skipped reads as though an image came in on its own.
            if (CardsAdded + CardsReplaced > 0)
            {
                Add(Images, "image", string.Empty);
            }

            return parts.Count == 0 ? "Nothing was imported." : string.Join("  ·  ", parts) + ".";

            void Add(int count, string noun, string verb)
            {
                if (count > 0)
                {
                    var plural = count == 1 ? noun : noun + "s";
                    parts.Add(verb.Length == 0 ? $"{count} {plural}" : $"{count} {plural} {verb}");
                }
            }
        }
    }
}

internal sealed class ImportDeckHandler(
    IFlashcardRepository cards,
    ISubjectRepository subjects,
    IMediaStore media,
    IUnitOfWork unitOfWork)
    : ICommandHandler<ImportDeckCommand, DeckImportResult>
{
    public Task<DeckImportResult> HandleAsync(ImportDeckCommand command, CancellationToken cancellationToken)
        => unitOfWork.ExecuteAsync(ct => RunAsync(command, ct), cancellationToken);

    private async Task<DeckImportResult> RunAsync(ImportDeckCommand command, CancellationToken ct)
    {
        var deck = command.Deck;
        var warnings = new List<string>();

        var chosenCards = deck.Cards.Where(c => command.CardIds.Contains(c.Id)).ToList();

        var needed = RequiredSubjectNames(deck, command.SubjectNames, chosenCards);
        var subjectsCreated = await EnsureSubjectsAsync(deck, needed, warnings, ct);

        var added = 0;
        var replaced = 0;
        var skipped = 0;

        // Which cards are actually going to be written, decided before anything is. The media
        // below is stored from this list rather than from the whole selection: importing a deck
        // whose cards you already have should not litter the store with images nothing points at.
        var plan = new List<(DeckCard Card, List<string> Tags, Guid? ExistingId)>();

        foreach (var card in chosenCards)
        {
            var tags = card.Subjects.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

            if (tags.Count == 0)
            {
                warnings.Add($"\"{card.Name}\" was skipped — it carries no subject.");
                skipped++;
                continue;
            }

            var existing = await FindExistingAsync(card, tags, ct);

            if (existing is not null && command.Mode == DeckImportMode.Skip)
            {
                skipped++;
                continue;
            }

            plan.Add((card, tags, existing));
        }

        var mediaMap = await StoreMediaAsync(deck, [.. plan.Select(p => p.Card)], ct);

        foreach (var (card, tags, existingId) in plan)
        {
            var save = new SaveFlashcardCommand
            {
                // Null creates a card; an id updates the one already here, which is what Replace means.
                Id = existingId,
                SubjectNames = tags,
                Name = card.Name,
                CardType = card.CardType,
                Notes = card.Notes,
                IsSuspended = card.IsSuspended,
                Blocks = [.. card.Blocks.Select(b => Rehome(b, mediaMap))],
                Choices = [.. card.Choices.Select(c => Rehome(c, mediaMap))],
            };

            // The validator normally runs in the dispatcher, ahead of the handler. Running it here
            // by hand is what turns a malformed card into a line in the report rather than an
            // exception that abandons the whole import.
            var errors = new SaveFlashcardValidator().Validate(save).ToList();

            if (errors.Count > 0)
            {
                warnings.Add($"\"{card.Name}\" was skipped — {string.Join(" ", errors)}");
                skipped++;
                continue;
            }

            try
            {
                await CardWriter.SaveAsync(cards, subjects, save, ct);
            }
            catch (Exception exception) when (exception is DomainException or ValidationException)
            {
                warnings.Add($"\"{card.Name}\" was skipped — {exception.Message}");
                skipped++;
                continue;
            }

            if (existingId is null)
            {
                added++;
            }
            else
            {
                replaced++;
            }
        }

        return new DeckImportResult(subjectsCreated, added, replaced, skipped, mediaMap.Count, warnings);
    }

    /// <summary>
    /// The subjects that have to exist afterwards: the ticked ones, the tags of every chosen card,
    /// and the ancestors of both as the file arranges them.
    /// </summary>
    private static HashSet<string> RequiredSubjectNames(
        DeckDocument deck,
        IReadOnlyCollection<string> chosen,
        List<DeckCard> chosenCards)
    {
        var parents = new Dictionary<string, string?>(StringComparer.CurrentCultureIgnoreCase);

        foreach (var subject in deck.Subjects)
        {
            parents[subject.Name] = subject.Parent;
        }

        var needed = new HashSet<string>(chosen, StringComparer.CurrentCultureIgnoreCase);

        foreach (var tag in chosenCards.SelectMany(c => c.Subjects))
        {
            needed.Add(tag);
        }

        foreach (var name in needed.ToList())
        {
            var walk = name;

            // Termination comes from the set, not from trusting the file: a hand-edited deck can
            // describe a cycle, and Add returning false is what stops this walking it forever.
            while (parents.TryGetValue(walk, out var parent) && parent is not null && needed.Add(parent))
            {
                walk = parent;
            }
        }

        return needed;
    }

    /// <summary>
    /// Creates whichever required subjects are missing, in the file's order — which is tree order,
    /// so a parent is always in place before the child that names it.
    /// </summary>
    private async Task<int> EnsureSubjectsAsync(
        DeckDocument deck,
        HashSet<string> needed,
        List<string> warnings,
        CancellationToken ct)
    {
        // Held locally and grown as we go: the depth rule is a property of the whole tree, and
        // re-reading it from the database after each insert would be a query per subject.
        var placements = (await subjects.GetAllAsync(ct))
            .Select(s => new SubjectPlacement(s.Id, s.ParentId, s.Name))
            .ToList();

        var byName = new Dictionary<string, SubjectPlacement>(StringComparer.CurrentCultureIgnoreCase);

        foreach (var placement in placements)
        {
            byName[placement.Name] = placement;
        }

        var created = 0;

        foreach (var entry in deck.Subjects.Where(s => needed.Contains(s.Name)))
        {
            // Already here. Its placement is left exactly as it is — an import adds to your
            // library, it does not rearrange the tree you built.
            if (byName.ContainsKey(entry.Name))
            {
                continue;
            }

            var parentId = entry.Parent is { } parent && byName.TryGetValue(parent, out var found)
                ? found.Id
                : (Guid?)null;

            if (parentId is not null && !new SubjectHierarchy(placements).CanAddUnder(parentId, out _))
            {
                warnings.Add(
                    $"\"{entry.Name}\" was filed at the top level — putting it under \"{entry.Parent}\" " +
                    $"would be more than {SubjectHierarchy.MaxDepth} levels deep.");

                parentId = null;
            }

            var subject = Subject.CreateTag(entry.Name, parentId);
            await subjects.AddAsync(subject, ct);

            var added = new SubjectPlacement(subject.Id, parentId, subject.Name);
            placements.Add(added);
            byName[added.Name] = added;
            created++;
        }

        return created;
    }

    /// <summary>
    /// Stores the deck's images and maps the ids inside the file onto the ids this library now
    /// uses. The store is content-addressed, so an image already here comes back as the id it
    /// already had rather than being written a second time.
    /// </summary>
    private async Task<Dictionary<Guid, Guid>> StoreMediaAsync(
        DeckDocument deck,
        List<DeckCard> chosenCards,
        CancellationToken ct)
    {
        var referenced = chosenCards
            .SelectMany(c => c.Blocks.Select(b => b.MediaId).Concat(c.Choices.Select(ch => ch.MediaId)))
            .OfType<Guid>()
            .ToHashSet();

        var map = new Dictionary<Guid, Guid>();

        foreach (var image in deck.Media)
        {
            if (referenced.Contains(image.Id) && image.Bytes.Length > 0)
            {
                map[image.Id] = (await media.SaveAsync(image.Bytes, image.FileName, ct)).Id;
            }
        }

        return map;
    }

    /// <summary>
    /// Re-points a block at this library's copy of its image, and clears its id so it is written
    /// as a new row rather than carrying one minted in somebody else's database.
    /// </summary>
    private static ContentBlockDto Rehome(ContentBlockDto block, Dictionary<Guid, Guid> media)
        => block with { Id = Guid.Empty, MediaId = MapMedia(block.MediaId, media) };

    private static ChoiceDto Rehome(ChoiceDto choice, Dictionary<Guid, Guid> media)
        => choice with { Id = Guid.Empty, MediaId = MapMedia(choice.MediaId, media) };

    /// <summary>
    /// An image reference the file did not carry the bytes for comes back null. The block is left
    /// standing rather than dropped — an image block with nothing attached fails validation and is
    /// reported by name, which is more use than a card that silently lost half its content.
    /// </summary>
    private static Guid? MapMedia(Guid? id, Dictionary<Guid, Guid> media)
        => id is { } value && media.TryGetValue(value, out var mapped) ? mapped : null;

    /// <summary>
    /// The card in this library that the imported one would collide with, if there is one.
    /// <para>
    /// Two ways to be the same card. Sharing an id means the deck came from here and is being
    /// brought back, which is the strongest signal there is. Otherwise it is the rule the library
    /// already enforces on itself: same name, sharing at least one tag.
    /// </para>
    /// </summary>
    private async Task<Guid?> FindExistingAsync(DeckCard card, List<string> tags, CancellationToken ct)
    {
        if (await cards.GetAsync(card.Id, ct) is not null)
        {
            return card.Id;
        }

        var subjectIds = new List<Guid>(tags.Count);

        foreach (var tag in tags)
        {
            if (await subjects.GetByNameAsync(tag.Trim(), ct) is { } subject)
            {
                subjectIds.Add(subject.Id);
            }
        }

        return subjectIds.Count == 0 ? null : await cards.FindIdByNameAsync(subjectIds, card.Name, ct);
    }
}
