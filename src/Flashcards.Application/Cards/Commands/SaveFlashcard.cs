using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Application.Contracts;
using Flashcards.Domain.Cards;

namespace Flashcards.Application.Cards.Commands;

/// <summary>
/// Creates a card when <see cref="Id"/> is null, updates it otherwise.
/// <para>
/// One command for both because the designer is the same surface either way — the create and edit
/// flows are the same view bound to the same view model, so making the application layer mirror
/// that keeps the two from drifting apart.
/// </para>
/// <para>
/// The subject arrives as a <em>name</em>, not an id. Subjects are tags you type into the designer,
/// so resolving (or minting) the tag happens here, inside the same transaction that writes the
/// card — a half-created tag pointing at no card is not a state this can land in.
/// </para>
/// </summary>
public sealed record SaveFlashcardCommand : ICommand<Guid>
{
    public Guid? Id { get; init; }

    /// <summary>
    /// Every tag the card should wear. At least one is required; they are resolved (and minted
    /// if new) inside the same transaction that writes the card.
    /// </summary>
    public required IReadOnlyList<string> SubjectNames { get; init; }

    public required string Name { get; init; }

    public required CardType CardType { get; init; }

    public string? Notes { get; init; }

    public bool IsSuspended { get; init; }

    public required IReadOnlyList<ContentBlockDto> Blocks { get; init; }

    public IReadOnlyList<ChoiceDto> Choices { get; init; } = [];
}

/// <summary>Structural rules that do not need the database. Aggregate rules stay on the aggregate.</summary>
public sealed class SaveFlashcardValidator : IValidator<SaveFlashcardCommand>
{
    public IEnumerable<string> Validate(SaveFlashcardCommand request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            yield return "Give the card a name so you can find it later.";
        }

        if (request.SubjectNames is null || !request.SubjectNames.Any(n => !string.IsNullOrWhiteSpace(n)))
        {
            yield return "Give the card at least one subject tag.";
        }

        if (request.Blocks.Count == 0)
        {
            yield return "Add at least one content block.";
        }

        foreach (var block in request.Blocks)
        {
            if (block.Kind == ContentKind.Image && block.MediaId is null)
            {
                yield return "An image block has no image attached.";
            }

            // A drawing with no strokes left is legitimate — it is an empty ink layer, and the
            // designer drops it before saving. Only text kinds are required to carry text.
            if (block.Kind is not (ContentKind.Image or ContentKind.Drawing)
                && string.IsNullOrWhiteSpace(block.Text))
            {
                yield return $"A {block.Kind} block on the {block.Face} side is empty.";
            }
        }
    }
}

internal sealed class SaveFlashcardHandler(
    IFlashcardRepository cards,
    ISubjectRepository subjects,
    IUnitOfWork unitOfWork)
    : ICommandHandler<SaveFlashcardCommand, Guid>
{
    // The write itself lives in CardWriter, shared with the deck importer: bringing a card in from
    // a file is a save whose conflict has already been decided, and the two should map blocks onto
    // the aggregate the same way rather than each keeping a copy of the rules.
    public Task<Guid> HandleAsync(SaveFlashcardCommand command, CancellationToken cancellationToken)
        => unitOfWork.ExecuteAsync(ct => CardWriter.SaveAsync(cards, subjects, command, ct), cancellationToken);
}
