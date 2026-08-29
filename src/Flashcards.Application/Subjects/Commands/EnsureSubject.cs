using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Domain.Common;
using Flashcards.Domain.Subjects;

namespace Flashcards.Application.Subjects.Commands;

/// <summary>
/// Resolves a typed subject name to its id, creating the subject if this is the first time anyone
/// has used that name.
/// <para>
/// Subjects are tags, not managed entities: there is no "create subject" screen, you just type one
/// into the designer. This is the command that makes that work, and it is the only way a subject
/// comes into existence.
/// </para>
/// </summary>
public sealed record EnsureSubjectCommand(string Name) : ICommand<Guid>;

internal sealed class EnsureSubjectHandler(ISubjectRepository subjects, IUnitOfWork unitOfWork)
    : ICommandHandler<EnsureSubjectCommand, Guid>
{
    public Task<Guid> HandleAsync(EnsureSubjectCommand command, CancellationToken cancellationToken)
        => unitOfWork.ExecuteAsync(ct => ResolveAsync(subjects, command.Name, ct), cancellationToken);

    /// <summary>
    /// Shared with <c>SaveFlashcardHandler</c> so that saving a card with a brand-new tag is one
    /// transaction rather than two. Callers are expected to already be inside a unit of work.
    /// </summary>
    internal static async Task<Guid> ResolveAsync(ISubjectRepository subjects, string name, CancellationToken cancellationToken)
    {
        var trimmed = (name ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            throw new DomainException("Give the card a subject tag.");
        }

        // The name column is COLLATE NOCASE, so "sql server" finds an existing "SQL Server"
        // rather than creating a near-duplicate tag that only differs by case.
        var existing = await subjects.GetByNameAsync(trimmed, cancellationToken);

        if (existing is not null)
        {
            return existing.Id;
        }

        var subject = Subject.CreateTag(trimmed);
        await subjects.AddAsync(subject, cancellationToken);

        return subject.Id;
    }

    /// <summary>
    /// Resolves a whole set of tag names in one go, minting the ones that are new. Blank entries
    /// are dropped and duplicates collapse — "SQL", "sql" and " SQL " are one tag, so the caller
    /// does not have to clean the list first.
    /// </summary>
    internal static async Task<List<Guid>> ResolveManyAsync(
        ISubjectRepository subjects,
        IEnumerable<string> names,
        CancellationToken cancellationToken)
    {
        var distinct = (names ?? [])
            .Select(n => (n ?? string.Empty).Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinct.Count == 0)
        {
            throw new DomainException("Give the card at least one subject tag.");
        }

        var ids = new List<Guid>(distinct.Count);

        foreach (var name in distinct)
        {
            ids.Add(await ResolveAsync(subjects, name, cancellationToken));
        }

        return ids;
    }
}
