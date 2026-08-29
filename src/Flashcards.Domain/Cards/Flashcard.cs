using Flashcards.Domain.Cards.Validation;
using Flashcards.Domain.Common;

namespace Flashcards.Domain.Cards;

/// <summary>
/// The aggregate root. A card owns its content blocks and its multiple-choice options —
/// nothing outside the aggregate may hold a reference to them or mutate them directly.
/// Review scheduling lives in a separate aggregate (<c>ReviewState</c>) because it changes
/// on a completely different cadence: content changes rarely, scheduling changes every review.
/// </summary>
public sealed class Flashcard : Entity, IAggregateRoot
{
    public const int MaxNameLength = 200;
    public const int MaxBlocksPerFace = 12;
    public const int MaxChoices = 8;

    private readonly List<ContentBlock> _blocks;
    private readonly List<ChoiceOption> _choices;

    private HashSet<Guid> _subjectIds;

    private Flashcard(
        Guid id,
        HashSet<Guid> subjectIds,
        string name,
        CardType cardType,
        string? notes,
        bool isSuspended,
        DateTimeOffset createdUtc,
        DateTimeOffset updatedUtc,
        List<ContentBlock> blocks,
        List<ChoiceOption> choices)
        : base(id)
    {
        _subjectIds = subjectIds;
        Name = name;
        CardType = cardType;
        Notes = notes;
        IsSuspended = isSuspended;
        CreatedUtc = createdUtc;
        UpdatedUtc = updatedUtc;
        _blocks = blocks;
        _choices = choices;
    }

    /// <summary>
    /// Every subject tag this card wears. A card must have at least one; beyond that there is no
    /// limit, and no ordering — a set, not a list, so tagging something twice is a no-op rather
    /// than a duplicate row.
    /// </summary>
    public IReadOnlyCollection<Guid> SubjectIds => _subjectIds;

    /// <summary>Short human label used by the management panel's search box.</summary>
    public string Name { get; private set; }

    public CardType CardType { get; private set; }

    /// <summary>Free-form study notes shown after grading. Never part of the question.</summary>
    public string? Notes { get; private set; }

    /// <summary>Suspended cards stay in the library but are skipped by quiz mode.</summary>
    public bool IsSuspended { get; private set; }

    public DateTimeOffset CreatedUtc { get; }

    public DateTimeOffset UpdatedUtc { get; private set; }

    public IReadOnlyList<ContentBlock> Blocks => _blocks;

    public IReadOnlyList<ChoiceOption> Choices => _choices;

    public IEnumerable<ContentBlock> QuestionBlocks => _blocks.Where(b => b.Face == CardFace.Question).OrderBy(b => b.Ordinal);

    public IEnumerable<ContentBlock> AnswerBlocks => _blocks.Where(b => b.Face == CardFace.Answer).OrderBy(b => b.Ordinal);

    public static Flashcard Create(IEnumerable<Guid> subjectIds, string name, CardType cardType, string? notes = null)
    {
        var tags = Normalize(subjectIds);

        var now = DateTimeOffset.UtcNow;

        return new Flashcard(
            Guid.CreateVersion7(),
            tags,
            Guard.AgainstTooLong(Guard.AgainstNullOrWhiteSpace(name), MaxNameLength),
            cardType,
            notes?.Trim(),
            isSuspended: false,
            now,
            now,
            blocks: [],
            choices: []);
    }

    public static Flashcard Rehydrate(
        Guid id,
        IEnumerable<Guid> subjectIds,
        string name,
        CardType cardType,
        string? notes,
        bool isSuspended,
        DateTimeOffset createdUtc,
        DateTimeOffset updatedUtc,
        IEnumerable<ContentBlock> blocks,
        IEnumerable<ChoiceOption> choices)
        => new(id, [.. subjectIds], name, cardType, notes, isSuspended, createdUtc, updatedUtc,
            [.. blocks.OrderBy(b => b.Face).ThenBy(b => b.Ordinal)],
            [.. choices.OrderBy(c => c.Ordinal)]);

    // ---- content -------------------------------------------------------

    public ContentBlock AddTextBlock(CardFace face, ContentKind kind, string text, string? language = null)
    {
        EnsureRoomOn(face);
        var block = ContentBlock.CreateText(face, NextOrdinal(face), kind, text, language);
        _blocks.Add(block);
        Touch();
        return block;
    }

    public ContentBlock AddImageBlock(
        CardFace face,
        Guid mediaId,
        ImageStretch stretch = ImageStretch.Uniform,
        double? maxHeight = 420d,
        string? altText = null)
    {
        EnsureRoomOn(face);
        var block = ContentBlock.CreateImage(face, NextOrdinal(face), mediaId, stretch, maxHeight, altText);
        _blocks.Add(block);
        Touch();
        return block;
    }

    public void RemoveBlock(Guid blockId)
    {
        var block = _blocks.SingleOrDefault(b => b.Id == blockId)
            ?? throw new DomainException("Block not found on this card.");

        _blocks.Remove(block);
        Compact(block.Face);
        Touch();
    }

    /// <summary>Moves a block up or down within its face. <paramref name="delta"/> is -1 or +1.</summary>
    public void MoveBlock(Guid blockId, int delta)
    {
        var block = _blocks.SingleOrDefault(b => b.Id == blockId)
            ?? throw new DomainException("Block not found on this card.");

        var ordered = _blocks.Where(b => b.Face == block.Face).OrderBy(b => b.Ordinal).ToList();
        var currentIndex = ordered.IndexOf(block);
        var targetIndex = currentIndex + Math.Sign(delta);

        if (targetIndex < 0 || targetIndex >= ordered.Count)
        {
            return;
        }

        ordered.RemoveAt(currentIndex);
        ordered.Insert(targetIndex, block);

        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Ordinal = i;
        }

        Touch();
    }

    public void ReplaceBlocks(IEnumerable<ContentBlock> blocks)
    {
        _blocks.Clear();
        _blocks.AddRange(blocks);
        Compact(CardFace.Question);
        Compact(CardFace.Answer);
        Touch();
    }

    // ---- multiple choice ------------------------------------------------

    public void ReplaceChoices(IEnumerable<ChoiceOption> choices)
    {
        var list = choices.ToList();

        if (list.Count > MaxChoices)
        {
            throw new DomainException($"A card may have at most {MaxChoices} choices.");
        }

        for (var i = 0; i < list.Count; i++)
        {
            list[i].Ordinal = i;
        }

        _choices.Clear();
        _choices.AddRange(list);
        Touch();
    }

    /// <summary>True when more than one option is marked correct — the UI then shows checkboxes rather than radios.</summary>
    public bool IsMultiSelect => _choices.Count(c => c.IsCorrect) > 1;

    // ---- cloze -----------------------------------------------------------

    /// <summary>Every blank across all question-side text blocks, numbered in reading order.</summary>
    public IReadOnlyList<ClozeBlank> ClozeBlanks
    {
        get
        {
            if (CardType != CardType.Cloze)
            {
                return [];
            }

            var all = new List<ClozeBlank>();

            foreach (var block in QuestionBlocks.Where(b => !b.IsImage))
            {
                foreach (var blank in ClozeParser.Parse(block.Text))
                {
                    all.Add(blank with { Index = all.Count + 1 });
                }
            }

            return all;
        }
    }

    // ---- metadata --------------------------------------------------------

    public void Rename(string name)
    {
        Name = Guard.AgainstTooLong(Guard.AgainstNullOrWhiteSpace(name), MaxNameLength);
        Touch();
    }

    /// <summary>Replaces the card's tags wholesale. At least one must survive.</summary>
    public void SetSubjects(IEnumerable<Guid> subjectIds)
    {
        var tags = Normalize(subjectIds);

        if (tags.SetEquals(_subjectIds))
        {
            return;
        }

        _subjectIds = tags;
        Touch();
    }

    public void AddSubject(Guid subjectId)
    {
        if (subjectId == Guid.Empty || !_subjectIds.Add(subjectId))
        {
            return;
        }

        Touch();
    }

    /// <summary>Removing the last tag is refused — an untagged card would be unreachable.</summary>
    public void RemoveSubject(Guid subjectId)
    {
        if (!_subjectIds.Contains(subjectId))
        {
            return;
        }

        if (_subjectIds.Count == 1)
        {
            throw new DomainException("A card needs at least one subject tag.");
        }

        _subjectIds.Remove(subjectId);
        Touch();
    }

    private static HashSet<Guid> Normalize(IEnumerable<Guid> subjectIds)
    {
        var tags = subjectIds is null ? [] : new HashSet<Guid>(subjectIds.Where(id => id != Guid.Empty));

        if (tags.Count == 0)
        {
            throw new DomainException("A card needs at least one subject tag.");
        }

        return tags;
    }

    public void ChangeType(CardType cardType)
    {
        if (CardType == cardType)
        {
            return;
        }

        CardType = cardType;

        if (cardType != CardType.MultipleChoice)
        {
            _choices.Clear();
        }

        Touch();
    }

    public void SetNotes(string? notes)
    {
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        Touch();
    }

    public void SetSuspended(bool suspended)
    {
        IsSuspended = suspended;
        Touch();
    }

    /// <summary>
    /// The check run before a card is saved. Collects every problem rather than throwing on the
    /// first, so the editor can show them all at once.
    /// <para>
    /// The rules themselves live in <see cref="FlashcardRules"/>: there is one per card type, they
    /// are the part of a card most likely to change, and they need nothing the aggregate does not
    /// already expose. This stays the way in, so callers never see that they moved.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Validate() => FlashcardRules.Check(this);


    private void EnsureRoomOn(CardFace face)
    {
        if (_blocks.Count(b => b.Face == face) >= MaxBlocksPerFace)
        {
            throw new DomainException($"A card face may hold at most {MaxBlocksPerFace} blocks.");
        }
    }

    private int NextOrdinal(CardFace face)
        => _blocks.Where(b => b.Face == face).Select(b => b.Ordinal + 1).DefaultIfEmpty(0).Max();

    private void Compact(CardFace face)
    {
        var ordered = _blocks.Where(b => b.Face == face).OrderBy(b => b.Ordinal).ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Ordinal = i;
        }
    }

    private void Touch() => UpdatedUtc = DateTimeOffset.UtcNow;
}
