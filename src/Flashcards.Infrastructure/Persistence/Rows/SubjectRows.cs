using Flashcards.Domain.Subjects;

namespace Flashcards.Infrastructure.Persistence.Rows;

// Dapper materialization targets for the subject tables. See CardRows.cs for why these are
// plain classes with a parameterless constructor rather than positional records.

internal sealed class SubjectRow
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string? ColorHex { get; init; }
    public string? Description { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public Guid? ParentId { get; init; }

    public Subject ToDomain() => Subject.Rehydrate(Id, Name, ColorHex, Description, CreatedUtc, ParentId);
}

internal sealed class SubjectSummaryRow
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string? ColorHex { get; init; }
    public string? Description { get; init; }
    public int CardCount { get; init; }
    public Guid? ParentId { get; init; }
    public int Depth { get; init; }
    public int TotalCardCount { get; init; }
}
