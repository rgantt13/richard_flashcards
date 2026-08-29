using Flashcards.Application.Contracts;
using Flashcards.Domain.Subjects;

namespace Flashcards.Application.Abstractions.Persistence;

/// <summary>
/// Write-side access to subjects.
/// <para>
/// This grew when subjects became a tree. They used to be pure tags — minted by typing a name,
/// retired automatically once nothing wore them — so the interface needed no update and no
/// single-delete. A tree is something you curate: it is renamed, re-parented and deleted
/// deliberately from the manage panel, and a subject that exists only to group its children has
/// no cards of its own and must survive anyway.
/// </para>
/// </summary>
public interface ISubjectRepository
{
    Task<Subject?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<Subject?> GetByNameAsync(string name, CancellationToken cancellationToken);

    Task<IReadOnlyList<Subject>> GetAllAsync(CancellationToken cancellationToken);

    Task AddAsync(Subject subject, CancellationToken cancellationToken);

    /// <summary>Persists a rename or a re-parent.</summary>
    Task UpdateAsync(Subject subject, CancellationToken cancellationToken);

    /// <summary>
    /// Removes one subject, promoting everything it held up one level: its child subjects are
    /// re-pointed at <paramref name="promoteTo"/> — the deleted subject's own parent — and so are
    /// the cards that wore it.
    /// <para>
    /// Cards used to be left to the join table's cascade, which quietly untagged them. That was
    /// wrong: a card must always wear at least one subject, and deleting a grouping should move
    /// what it grouped rather than destroy its membership.
    /// </para>
    /// <para>
    /// A null <paramref name="promoteTo"/> means the subject was top level and there is nowhere to
    /// promote cards to. Callers must have established that no card depends on this subject alone
    /// before then — see <see cref="FindCardsOrphanedByDeleteAsync"/>.
    /// </para>
    /// </summary>
    Task DeleteAsync(Guid id, Guid? promoteTo, CancellationToken cancellationToken);

    /// <summary>
    /// The names of cards that would be left with no subject at all if this one were deleted.
    /// <para>
    /// Only ever non-empty for a top-level subject: anywhere else the cards are promoted to the
    /// parent and keep a tag. A card that also wears some other subject is not at risk and is not
    /// listed, which is what makes the resulting message accurate — it names the cards that
    /// genuinely need attention rather than every card in the subject.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<string>> FindCardsOrphanedByDeleteAsync(
        Guid id,
        Guid? promoteTo,
        CancellationToken cancellationToken);
}

/// <summary>
/// Read-side access to the subject tree.
/// <para>
/// Every figure here is rolled up over a subject's whole subtree, because that is what selecting
/// it studies. Ancestry is derived rather than stored — see <see cref="SubjectHierarchy"/> — so a
/// subject that moves takes its subtree's numbers with it without a row being rewritten.
/// </para>
/// </summary>
public interface ISubjectReadStore
{
    Task<IReadOnlyList<SubjectSummary>> GetSubjectSummariesAsync(CancellationToken cancellationToken);

    /// <summary>Every subject's tally, so the subject panel can offer a list and its numbers together.</summary>
    Task<IReadOnlyList<SubjectStats>> GetSubjectStatsAsync(CancellationToken cancellationToken);
}
