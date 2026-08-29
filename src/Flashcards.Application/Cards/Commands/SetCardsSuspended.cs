using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Abstractions.Persistence;

namespace Flashcards.Application.Cards.Commands;

public sealed record SetCardsSuspendedCommand(IReadOnlyCollection<Guid> Ids, bool Suspended) : ICommand<int>;

internal sealed class SetCardsSuspendedHandler(IFlashcardRepository cards, IUnitOfWork unitOfWork)
    : ICommandHandler<SetCardsSuspendedCommand, int>
{
    public Task<int> HandleAsync(SetCardsSuspendedCommand command, CancellationToken cancellationToken)
        => unitOfWork.ExecuteAsync(async ct =>
        {
            var changed = 0;

            foreach (var id in command.Ids)
            {
                if (await cards.GetAsync(id, ct) is not { } card)
                {
                    continue;
                }

                card.SetSuspended(command.Suspended);
                await cards.UpdateAsync(card, ct);
                changed++;
            }

            return changed;
        }, cancellationToken);
}
