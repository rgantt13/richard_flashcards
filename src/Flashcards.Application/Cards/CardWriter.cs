using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Application.Cards.Commands;
using Flashcards.Application.Contracts;
using Flashcards.Application.Subjects.Commands;
using Flashcards.Domain.Cards;
using Flashcards.Domain.Common;

namespace Flashcards.Application.Cards;

/// <summary>
/// Writing one card, from the DTO shape the outside world speaks to the aggregate the repository
/// stores.
/// <para>
/// Extracted from <see cref="SaveFlashcardHandler"/> once importing a deck needed the same thing.
/// An import is a save with the conflict already decided, so it should go down the same road —
/// two copies of the block-to-aggregate mapping would be two places for the freeform geometry or
/// the ink special case to drift apart.
/// </para>
/// </summary>
internal static class CardWriter
{
    /// <summary>
    /// Creates the card when <see cref="SaveFlashcardCommand.Id"/> is null and updates it
    /// otherwise, resolving (and minting) its subject tags on the way. Callers are expected to
    /// already be inside a unit of work.
    /// </summary>
    internal static async Task<Guid> SaveAsync(
        IFlashcardRepository cards,
        ISubjectRepository subjects,
        SaveFlashcardCommand command,
        CancellationToken cancellationToken)
    {
        var subjectIds = await EnsureSubjectHandler.ResolveManyAsync(subjects, command.SubjectNames, cancellationToken);

        var isNew = command.Id is null;

        var card = isNew
            ? Flashcard.Create(subjectIds, command.Name, command.CardType, command.Notes)
            : await cards.GetAsync(command.Id!.Value, cancellationToken)
              ?? throw new DomainException("That card no longer exists. It may have been deleted in another window.");

        if (await cards.ExistsWithNameAsync(subjectIds, command.Name, card.Id, cancellationToken))
        {
            throw new DomainException(
                $"Another card named \"{command.Name}\" already carries one of these tags.");
        }

        if (!isNew)
        {
            card.Rename(command.Name);
            card.SetSubjects(subjectIds);
            card.ChangeType(command.CardType);
            card.SetNotes(command.Notes);
            card.SetSuspended(command.IsSuspended);
        }

        card.ReplaceBlocks(command.Blocks.Select(ToBlock));

        card.ReplaceChoices(command.CardType == CardType.MultipleChoice
            ? command.Choices.Select(ToChoice)
            : []);

        var errors = card.Validate();
        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        if (isNew)
        {
            // No scheduling row to seed any more: a card is eligible to study the moment it
            // exists, and its history starts empty.
            await cards.AddAsync(card, cancellationToken);
        }
        else
        {
            await cards.UpdateAsync(card, cancellationToken);
        }

        return card.Id;
    }

    internal static ContentBlock ToBlock(ContentBlockDto dto)
    {
        var id = dto.Id == Guid.Empty ? Guid.CreateVersion7() : dto.Id;

        // Geometry travels for designed cards and is null everywhere else, which is what keeps a
        // flow-laid-out block flowing. Bounds are clamped to the canvas on the way in, so a value
        // that arrived from anywhere cannot place an element off the card.
        var bounds = dto.IsPlaced
            ? new BlockBounds(dto.X!.Value, dto.Y!.Value, dto.Width!.Value, dto.Height!.Value).ClampToCanvas()
            : (BlockBounds?)null;

        return dto.Kind switch
        {
            // Ink is stored verbatim: it is already in the serialised stroke form, and trimming
            // or re-encoding it here would only risk corrupting coordinates.
            ContentKind.Drawing => ContentBlock.Rehydrate(
                id, dto.Face, dto.Ordinal, ContentKind.Drawing, dto.Text, null,
                null, ImageStretch.Uniform, null, null, bounds),

            ContentKind.Image => ContentBlock.Rehydrate(
                id, dto.Face, dto.Ordinal, ContentKind.Image, null, null,
                dto.MediaId, dto.Stretch, dto.MaxHeight, dto.AltText, bounds),

            _ => ContentBlock.Rehydrate(
                id, dto.Face, dto.Ordinal, dto.Kind, dto.Text?.Trim(),
                dto.Kind == ContentKind.Code ? (dto.Language ?? "plaintext") : null,
                null, ImageStretch.Uniform, null, null, bounds),
        };
    }

    internal static ChoiceOption ToChoice(ChoiceDto dto)
        => ChoiceOption.Rehydrate(
            dto.Id == Guid.Empty ? Guid.CreateVersion7() : dto.Id,
            dto.Ordinal,
            (dto.Text ?? string.Empty).Trim(),
            dto.IsCorrect,
            dto.MediaId);
}
