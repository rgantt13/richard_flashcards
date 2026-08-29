using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Domain.Common;
using Flashcards.Domain.Subjects;

namespace Flashcards.Application.Subjects.Commands;

/// <summary>
/// Creates a subject deliberately, as opposed to <see cref="EnsureSubjectCommand"/> minting one as
/// a side effect of tagging a card. Omitting <paramref name="ParentId"/> files it at the top level,
/// which is the default the designer offers.
/// </summary>
public sealed record CreateSubjectCommand(string Name, Guid? ParentId = null) : ICommand<Guid>;

/// <summary>Re-files a subject, with its whole subtree, under a new parent. Null means top level.</summary>
public sealed record MoveSubjectCommand(Guid Id, Guid? NewParentId) : ICommand<Unit>;

public sealed record RenameSubjectCommand(Guid Id, string Name) : ICommand<Unit>;

/// <summary>
/// Deletes one subject and promotes everything it held into its place — child subjects and cards
/// alike — so a branch moves up a level rather than being cut off.
/// <para>
/// Refused when the subject is top level and a card wears it and nothing else, because there is
/// nowhere to promote that card to and a card with no subject is not a state the domain allows.
/// </para>
/// </summary>
public sealed record DeleteSubjectCommand(Guid Id) : ICommand<Unit>;

/// <summary>
/// The write side of the subject tree.
/// <para>
/// Every one of these validates against a <see cref="SubjectHierarchy"/> built from the whole
/// subject table rather than against the single row being changed. The rules that matter — no
/// cycles, nothing pushed past the depth limit — are properties of the shape, and a subject in
/// isolation cannot see the shape it is part of. Subject tables are small, so loading all of them
/// per write is cheaper than the recursive queries the alternative would need.
/// </para>
/// </summary>
internal static class SubjectTreeRules
{
    internal static async Task<SubjectHierarchy> LoadAsync(ISubjectRepository subjects, CancellationToken ct)
    {
        var all = await subjects.GetAllAsync(ct);

        return new SubjectHierarchy(all.Select(s => new SubjectPlacement(s.Id, s.ParentId, s.Name)));
    }

    /// <summary>
    /// Trims and checks a proposed name. <paramref name="allowingId"/> is the subject being renamed,
    /// so that saving a subject under its existing name is not a clash with itself.
    /// </summary>
    internal static async Task<string> ValidateNameAsync(
        ISubjectRepository subjects,
        string? name,
        Guid? allowingId,
        CancellationToken ct)
    {
        var trimmed = (name ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            throw new DomainException("Give the subject a name.");
        }

        // Matched through the repository rather than in memory so the COLLATE NOCASE index does the
        // comparison — "sql server" and "SQL Server" are the same subject.
        var existing = await subjects.GetByNameAsync(trimmed, ct);

        if (existing is not null && existing.Id != allowingId)
        {
            throw new DomainException($"A subject called \"{existing.Name}\" already exists.");
        }

        return trimmed;
    }
}

internal sealed class CreateSubjectHandler(ISubjectRepository subjects, IUnitOfWork unitOfWork)
    : ICommandHandler<CreateSubjectCommand, Guid>
{
    public Task<Guid> HandleAsync(CreateSubjectCommand command, CancellationToken cancellationToken)
        => unitOfWork.ExecuteAsync(async ct =>
        {
            var name = await SubjectTreeRules.ValidateNameAsync(subjects, command.Name, null, ct);
            var hierarchy = await SubjectTreeRules.LoadAsync(subjects, ct);

            // A new subject is always a leaf, so only the parent's own depth constrains it.
            hierarchy.EnsureCanAddUnder(command.ParentId);

            var subject = Subject.CreateTag(name, command.ParentId);
            await subjects.AddAsync(subject, ct);

            return subject.Id;
        }, cancellationToken);
}

internal sealed class MoveSubjectHandler(ISubjectRepository subjects, IUnitOfWork unitOfWork)
    : ICommandHandler<MoveSubjectCommand, Unit>
{
    public Task<Unit> HandleAsync(MoveSubjectCommand command, CancellationToken cancellationToken)
        => unitOfWork.ExecuteAsync(async ct =>
        {
            var hierarchy = await SubjectTreeRules.LoadAsync(subjects, ct);

            // Rejects a cycle, a no-op move, and any move that would push a descendant past the
            // depth limit — the last of which depends on the height of the branch being dragged,
            // not just on where it lands.
            hierarchy.EnsureCanMove(command.Id, command.NewParentId);

            if (await subjects.GetAsync(command.Id, ct) is not { } subject)
            {
                throw new DomainException("That subject no longer exists.");
            }

            subject.MoveTo(command.NewParentId);
            await subjects.UpdateAsync(subject, ct);

            // Nothing else changes. Every card under this branch answers to its new ancestors from
            // the next query onwards, because ancestry is read from the tree, never stored.
            return Unit.Value;
        }, cancellationToken);
}

internal sealed class RenameSubjectHandler(ISubjectRepository subjects, IUnitOfWork unitOfWork)
    : ICommandHandler<RenameSubjectCommand, Unit>
{
    public Task<Unit> HandleAsync(RenameSubjectCommand command, CancellationToken cancellationToken)
        => unitOfWork.ExecuteAsync(async ct =>
        {
            if (await subjects.GetAsync(command.Id, ct) is not { } subject)
            {
                throw new DomainException("That subject no longer exists.");
            }

            subject.Rename(await SubjectTreeRules.ValidateNameAsync(subjects, command.Name, command.Id, ct));
            await subjects.UpdateAsync(subject, ct);

            return Unit.Value;
        }, cancellationToken);
}

internal sealed class DeleteSubjectHandler(ISubjectRepository subjects, IUnitOfWork unitOfWork)
    : ICommandHandler<DeleteSubjectCommand, Unit>
{
    public Task<Unit> HandleAsync(DeleteSubjectCommand command, CancellationToken cancellationToken)
        => unitOfWork.ExecuteAsync(async ct =>
        {
            if (await subjects.GetAsync(command.Id, ct) is not { } subject)
            {
                // Already gone. Deleting twice is not an error worth reporting.
                return Unit.Value;
            }

            // Everything the subject held is promoted to its own parent, so "delete this grouping"
            // keeps what it grouped and just removes one level. Deleting a top-level subject
            // promotes its children to the top, which is the same rule with a null parent — but
            // there is no such destination for its *cards*, and a card must always wear a subject.
            var stranded = await subjects.FindCardsOrphanedByDeleteAsync(command.Id, subject.ParentId, ct);

            if (stranded.Count > 0)
            {
                throw new DomainException(SubjectDeletion.Describe(subject.Name, stranded));
            }

            await subjects.DeleteAsync(command.Id, subject.ParentId, ct);

            return Unit.Value;
        }, cancellationToken);

}
