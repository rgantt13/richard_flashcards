using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Application.Subjects.Commands;

namespace Flashcards.Application.Cards.Commands;

/// <summary>
/// Replaces the tag set on a batch of cards. Takes subject <em>names</em> rather than ids because
/// the manage panel offers the same type-or-pick tag box the designer does — a tag may not exist
/// yet. At least one name is required; a card with no tags would be unreachable.
/// </summary>
public sealed record RetagCardsCommand(IReadOnlyCollection<Guid> Ids, IReadOnlyList<string> SubjectNames) : ICommand<int>;

internal sealed class RetagCardsHandler(
    IFlashcardRepository cards,
    ISubjectRepository subjects,
    IUnitOfWork unitOfWork)
    : ICommandHandler<RetagCardsCommand, int>
{
    public Task<int> HandleAsync(RetagCardsCommand command, CancellationToken cancellationToken)
        => unitOfWork.ExecuteAsync(async ct =>
        {
            var subjectIds = await EnsureSubjectHandler.ResolveManyAsync(subjects, command.SubjectNames, ct);

            var moved = 0;

            foreach (var id in command.Ids)
            {
                if (await cards.GetAsync(id, ct) is not { } card)
                {
                    continue;
                }

                card.SetSubjects(subjectIds);
                await cards.UpdateAsync(card, ct);
                moved++;
            }

            // Retagging no longer retires the tag it left behind — see DeleteFlashcards.

            return moved;
        }, cancellationToken);
}
