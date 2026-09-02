using Flashcards.Application.Contracts;

namespace Flashcards.Application.Abstractions.Persistence;

/// <summary>
/// Assembling one sitting's queue of cards.
/// <para>
/// One method, but deliberately not filed with the management grid's search: nothing here is
/// filtered out by when it was last seen, the ordering is about how badly you do on a card rather
/// than about sorting a list, and what comes back is a queue of ids rather than anything a screen
/// binds to.
/// </para>
/// </summary>
public interface IQuizReadStore
{
    /// <summary>
    /// Cards to work through this sitting. Ordering is either random or weakest-first; nothing is
    /// filtered out on grounds of when it was last seen, because nothing is scheduled.
    /// <para>
    /// Scope narrows in order of how specific it is: an explicit <paramref name="cardIds"/> set
    /// wins, then <paramref name="subjectIds"/>, and with neither the whole library is eligible.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<Guid>> GetQuizQueueAsync(QuizOptions options, CancellationToken cancellationToken);
}
