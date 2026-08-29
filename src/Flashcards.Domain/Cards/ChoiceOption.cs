using Flashcards.Domain.Common;

namespace Flashcards.Domain.Cards;

/// <summary>
/// One selectable option on a <see cref="CardType.MultipleChoice"/> card.
/// <para>
/// An option carries text, a picture, or both — the designer's four answer slots each accept a
/// typed answer, a dropped image, or a captioned image. What it may not be is empty, and that is
/// the one rule the guards below exist to keep.
/// </para>
/// </summary>
public sealed class ChoiceOption : Entity
{
    public const int MaxTextLength = 1_000;

    private ChoiceOption(Guid id, int ordinal, string text, bool isCorrect, Guid? mediaId) : base(id)
    {
        Ordinal = ordinal;
        Text = text;
        IsCorrect = isCorrect;
        MediaId = mediaId;
    }

    public int Ordinal { get; internal set; }

    /// <summary>The answer text. Empty is allowed only when <see cref="MediaId"/> supplies the answer instead.</summary>
    public string Text { get; private set; }

    public bool IsCorrect { get; private set; }

    /// <summary>The picture shown for this option, if the slot had an image dropped on it.</summary>
    public Guid? MediaId { get; private set; }

    public bool HasImage => MediaId is not null;

    public bool IsBlank => MediaId is null && string.IsNullOrWhiteSpace(Text);

    public static ChoiceOption Create(int ordinal, string text, bool isCorrect, Guid? mediaId = null)
        => new(Guid.CreateVersion7(), ordinal, Normalize(text, mediaId), isCorrect, mediaId);

    public static ChoiceOption Rehydrate(Guid id, int ordinal, string text, bool isCorrect, Guid? mediaId)
        => new(id, ordinal, text, isCorrect, mediaId);

    public void Update(string text, bool isCorrect, Guid? mediaId)
    {
        Text = Normalize(text, mediaId);
        IsCorrect = isCorrect;
        MediaId = mediaId;
    }

    /// <summary>
    /// Text is required only when there is no picture to stand in for it. Both together is fine —
    /// that is a captioned image — and neither is what <see cref="IsBlank"/> reports on.
    /// </summary>
    private static string Normalize(string? text, Guid? mediaId)
        => mediaId is null
            ? Guard.AgainstTooLong(Guard.AgainstNullOrWhiteSpace(text ?? string.Empty), MaxTextLength)
            : Guard.AgainstTooLong((text ?? string.Empty).Trim(), MaxTextLength);
}
