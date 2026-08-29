using Flashcards.Domain.Cards;

namespace Flashcards.Application.Contracts;

// Asking the library a question, and the page of answers that comes back.

/// <summary>Filters for the management panel. Every field is optional; they AND together.</summary>
public sealed record FlashcardSearchCriteria
{
    /// <summary>Matched against card name and question text, case-insensitively.</summary>
    public string? Text { get; init; }

    public IReadOnlyCollection<Guid>? SubjectIds { get; init; }

    public CardType? CardType { get; init; }

    public bool? IsSuspended { get; init; }

    /// <summary>Only cards that have never been answered.</summary>
    public bool UntouchedOnly { get; init; }

    public FlashcardSortField SortBy { get; init; } = FlashcardSortField.UpdatedUtc;

    public bool SortDescending { get; init; } = true;

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 50;
}

public enum FlashcardSortField
{
    Name,
    SubjectName,
    UpdatedUtc,
    CreatedUtc,

    /// <summary>How often the card has been answered.</summary>
    TimesAnswered,

    /// <summary>Share answered correctly — ascending puts your weakest cards first.</summary>
    Accuracy,
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize)
{
    public int PageCount => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public static PagedResult<T> Empty(int pageSize) => new([], 0, 1, pageSize);
}
