using Flashcards.Domain.Cards;

namespace Flashcards.Application.Contracts;

// The shapes a card takes on its way to a screen. Nothing here is an aggregate: the read
// side builds these directly in SQL, and the designer hands them back the same way.

/// <summary>A row in the management panel's results grid.</summary>
public sealed record FlashcardSummary(
    Guid Id,
    string Name,
    IReadOnlyList<SubjectRef> Subjects,
    CardType CardType,
    bool IsSuspended,
    string QuestionPreview,
    int BlockCount,
    bool HasImages,
    DateTimeOffset UpdatedUtc,
    /// <summary>This card's answer tally, so the results grid can show how you do on each row.</summary>
    PracticeStats Practice)
{
    /// <summary>Never answered. Replaces the old "new card" idea, which meant "never scheduled".</summary>
    public bool IsUntouched => !Practice.HasHistory;

    /// <summary>The tint for the row's accent stripe: the first tag's colour, if it has one.</summary>
    public string? PrimaryColorHex => Subjects.Count > 0 ? Subjects[0].ColorHex : null;
}

/// <summary>Everything the editor needs to open a card, in one round trip.</summary>
public sealed record FlashcardDetail(
    Guid Id,
    IReadOnlyList<SubjectRef> Subjects,
    string Name,
    CardType CardType,
    string? Notes,
    bool IsSuspended,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    IReadOnlyList<ContentBlockDto> Blocks,
    IReadOnlyList<ChoiceDto> Choices);

/// <summary>
/// One element on a card. <paramref name="X"/>/<paramref name="Y"/>/<paramref name="Width"/>/
/// <paramref name="Height"/> are set only on designed (freeform) cards, where the author placed
/// the element on a canvas; everywhere else they are null and the element is laid out in flow.
/// </summary>
public sealed record ContentBlockDto(
    Guid Id,
    CardFace Face,
    int Ordinal,
    ContentKind Kind,
    string? Text,
    string? Language,
    Guid? MediaId,
    ImageStretch Stretch,
    double? MaxHeight,
    string? AltText,
    double? X = null,
    double? Y = null,
    double? Width = null,
    double? Height = null)
{
    /// <summary>True when this element was placed on a canvas rather than stacked.</summary>
    public bool IsPlaced => X is not null && Y is not null && Width is not null && Height is not null;
}

/// <summary>
/// One multiple-choice option. <paramref name="MediaId"/> is set when the answer slot holds a
/// picture; text may then be empty (image-only) or act as its caption.
/// </summary>
public sealed record ChoiceDto(Guid Id, int Ordinal, string Text, bool IsCorrect, Guid? MediaId = null);
