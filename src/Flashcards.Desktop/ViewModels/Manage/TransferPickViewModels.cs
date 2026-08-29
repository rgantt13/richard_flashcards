using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using Flashcards.Domain.Cards;

namespace Flashcards.Desktop.ViewModels.Manage;

/// <summary>One subject tag as it appears on a card row, flattened to what a chip needs.</summary>
public sealed record TransferTag(string Name, string? ColorHex);

/// <summary>
/// One subject in the import/export picker.
/// <para>
/// Deliberately not <see cref="SubjectPickViewModel"/>. That one wraps a <c>SubjectStats</c> and is
/// all about how you are doing at the subject, which is exactly the question this screen is not
/// asking; and half of these rows come out of a <em>file</em>, where there are no statistics, no
/// ids and nothing to look them up with.
/// </para>
/// </summary>
public sealed partial class TransferSubjectViewModel : ObservableObject
{
    /// <summary>Null for a subject read out of a file — it does not exist in this library yet.</summary>
    public Guid? Id { get; init; }

    public required string Name { get; init; }

    public string? ColorHex { get; init; }

    /// <summary>1 at the top level, as everywhere else. Drives the indent.</summary>
    public int Depth { get; init; } = 1;

    public string? ParentName { get; init; }

    /// <summary>Cards this subject accounts for — its whole subtree on the way out, the file's own tally on the way in.</summary>
    public int CardCount { get; init; }

    /// <summary>Set on an import row that names something already in the library.</summary>
    public string? Note { get; init; }

    public bool HasNote => Note is not null;

    /// <summary>
    /// What the identicon hashes. A real subject seeds from its id so its mark matches the one on
    /// every other panel; a subject that only exists in a file has to fall back to its name.
    /// </summary>
    public object Seed => Id.HasValue ? Id.Value : Name;

    /// <summary>One step per level, as an actual margin so a trimmed name keeps its nesting.</summary>
    public Thickness Indent => new((Depth - 1) * 16, 0, 0, 0);

    [ObservableProperty]
    private bool _isIncluded = true;
}

/// <summary>One card in the import/export picker.</summary>
public sealed partial class TransferCardViewModel : ObservableObject
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public CardType CardType { get; init; }

    public IReadOnlyList<TransferTag> Tags { get; init; } = [];

    /// <summary>The subject names this card is filed under, used to match it against the tier above.</summary>
    public IReadOnlyList<string> TagNames { get; init; } = [];

    /// <summary>Set on an import row that would collide with a card already here.</summary>
    public string? Note { get; init; }

    public bool HasNote => Note is not null;

    [ObservableProperty]
    private bool _isIncluded;
}
