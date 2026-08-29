using Flashcards.Application.Contracts;
using Flashcards.Domain.Cards;

namespace Flashcards.Application.Abstractions.Persistence;

/// <summary>
/// Write-side access to the <see cref="Flashcard"/> aggregate. Loads and saves whole aggregates —
/// there is no <c>GetBlocks()</c> here on purpose; blocks are reached through their card.
/// </summary>
public interface IFlashcardRepository
{
    Task<Flashcard?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task AddAsync(Flashcard card, CancellationToken cancellationToken);

    Task UpdateAsync(Flashcard card, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Whether another card with this name already wears any of these tags. Card names stay
    /// unique per tag, which with multi-tagging means "unique within every tag it is given".
    /// </summary>
    Task<bool> ExistsWithNameAsync(
        IReadOnlyCollection<Guid> subjectIds,
        string name,
        Guid? excludingId,
        CancellationToken cancellationToken);

    /// <summary>
    /// The id of the card with this name wearing any of these tags, or null when there is none.
    /// <para>
    /// The same rule as <see cref="ExistsWithNameAsync"/>, asked the other way round. Importing a
    /// deck needs to know <em>which</em> card a new one would collide with, so that "replace what
    /// is already here" is an option rather than only "refuse".
    /// </para>
    /// </summary>
    Task<Guid?> FindIdByNameAsync(
        IReadOnlyCollection<Guid> subjectIds,
        string name,
        CancellationToken cancellationToken);
}

/// <summary>
/// Read-side. Returns flat DTOs shaped for the screen that asks for them, built by SQL that joins
/// and aggregates directly. This is the half of CQRS that pays for itself: no ORM materialising an
/// object graph you are about to flatten anyway.
/// <para>
/// One interface per concern rather than one covering the whole read side. A handler after a
/// card's detail should not also be handed every way of counting a subject, and the four narrower
/// seams are what let the SQL behind them live in four files instead of one of six hundred lines.
/// See <see cref="ISubjectReadStore"/>, <see cref="IStatsReadStore"/> and <see cref="IQuizReadStore"/>.
/// </para>
/// </summary>
public interface IFlashcardReadStore
{
    Task<PagedResult<FlashcardSummary>> SearchAsync(
        FlashcardSearchCriteria criteria,
        CancellationToken cancellationToken);

    Task<FlashcardDetail?> GetDetailAsync(Guid id, CancellationToken cancellationToken);
}
