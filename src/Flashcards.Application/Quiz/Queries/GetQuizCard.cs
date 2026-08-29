using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Application.Contracts;
using Flashcards.Domain.Cards;

namespace Flashcards.Application.Quiz.Queries;

public sealed record GetQuizCardQuery(Guid CardId, bool ShuffleChoices) : IQuery<QuizCard?>;

internal sealed class GetQuizCardHandler(IFlashcardReadStore cardStore, IStatsReadStore statsStore)
    : IQueryHandler<GetQuizCardQuery, QuizCard?>
{
    public async Task<QuizCard?> HandleAsync(GetQuizCardQuery query, CancellationToken cancellationToken)
    {
        var detail = await cardStore.GetDetailAsync(query.CardId, cancellationToken);

        if (detail is null)
        {
            return null;
        }

        var choices = detail.Choices.ToList();

        if (query.ShuffleChoices && choices.Count > 1)
        {
            // Random.Shared.Shuffle is an in-place Fisher-Yates over the list's backing array.
            // Ordinals are rewritten afterwards so the view can bind a stable display index.
            Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(choices));
            for (var i = 0; i < choices.Count; i++)
            {
                choices[i] = choices[i] with { Ordinal = i };
            }
        }

        var stats = await statsStore.GetCardStatsAsync(query.CardId, cancellationToken);

        return new QuizCard(
            detail.Id,
            detail.Name,
            detail.Subjects,
            detail.CardType,
            [.. detail.Blocks.Where(b => b.Face == CardFace.Question).OrderBy(b => b.Ordinal)],
            [.. detail.Blocks.Where(b => b.Face == CardFace.Answer).OrderBy(b => b.Ordinal)],
            choices,
            choices.Count(c => c.IsCorrect) > 1,
            detail.Notes,
            stats);
    }
}
