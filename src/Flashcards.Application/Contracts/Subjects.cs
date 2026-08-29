namespace Flashcards.Application.Contracts;

// Subjects as the screens see them — a tag on a card, and a node in the tree.

/// <summary>One subject tag, in the shape a chip needs to render it.</summary>
/// <summary>
/// One subject as it appears on a card.
/// <para>
/// <paramref name="IsInherited"/> separates the tag the user actually applied from the ancestors
/// that come with it. A card tagged "MSSQL" also wears "SQL" and "Databases", but only MSSQL was
/// chosen — and only MSSQL can be removed. Views render the inherited ones more quietly, and the
/// designer refuses to let you take one off.
/// </para>
/// </summary>
public sealed record SubjectRef(Guid Id, string Name, string? ColorHex, bool IsInherited = false);

public sealed record SubjectSummary(
    Guid Id,
    string Name,
    string? ColorHex,
    string? Description,
    /// <summary>Cards wearing this exact tag — not counting the ones under its children.</summary>
    int CardCount,
    Guid? ParentId = null,
    /// <summary>1 for a top-level subject, up to <see cref="Flashcards.Domain.Subjects.SubjectHierarchy.MaxDepth"/>.</summary>
    int Depth = 1,
    /// <summary>Distinct cards in this subject's whole subtree — what selecting it would study.</summary>
    int TotalCardCount = 0);
