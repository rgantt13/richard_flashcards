using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Abstractions.Persistence;

namespace Flashcards.Application.Cards.Commands;

public sealed record DeleteFlashcardsCommand(IReadOnlyCollection<Guid> Ids) : ICommand<int>;

internal sealed class DeleteFlashcardsHandler(
    IFlashcardRepository cards,
    ISubjectRepository subjects,
    IMediaStore media,
    IUnitOfWork unitOfWork)
    : ICommandHandler<DeleteFlashcardsCommand, int>
{
    public Task<int> HandleAsync(DeleteFlashcardsCommand command, CancellationToken cancellationToken)
        => unitOfWork.ExecuteAsync(async ct =>
        {
            var deleted = 0;

            foreach (var id in command.Ids)
            {
                await cards.DeleteAsync(id, ct);
                deleted++;
            }

            // Blocks, choices, review state and the log all disappear via ON DELETE CASCADE.
            // Image files are not covered by the foreign keys, so sweep the orphans here.
            await media.CollectGarbageAsync(ct);

            // Subjects used to be retired here once the last card wearing one was gone. They are a
            // curated tree now, so they outlive their cards: a parent that only groups its children
            // has no cards of its own by definition, and would have been swept away on first use.
            // Deleting a subject is an explicit act on the manage panel.

            return deleted;
        }, cancellationToken);
}
