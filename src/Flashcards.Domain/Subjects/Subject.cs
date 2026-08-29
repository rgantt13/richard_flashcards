using Flashcards.Domain.Common;

namespace Flashcards.Domain.Subjects;

/// <summary>
/// A study subject ("SQL Server internals", "Spanish verbs"). Cards belong to exactly one.
/// Quiz mode lets you pick one or more subjects to draw from.
/// <para>
/// Subjects behave as <em>tags</em>: there is no screen for managing them. One comes into
/// existence the first time somebody types its name into the card designer, and retires when the
/// last card wearing it stops wearing it.
/// </para>
/// </summary>
public sealed class Subject : Entity, IAggregateRoot
{
    public const int MaxNameLength = 120;

    /// <summary>Tint options for the subject chip. A tag picks one from its own name — see <see cref="ColorFor"/>.</summary>
    private static readonly string[] TagPalette =
        ["#4C9AFF", "#7A5AF8", "#22C55E", "#F59E0B", "#EF4444", "#14B8A6", "#EC4899", "#94A3B8"];

    private Subject(Guid id, string name, string? colorHex, string? description, DateTimeOffset createdUtc, Guid? parentId)
        : base(id)
    {
        Name = name;
        ColorHex = colorHex;
        Description = description;
        CreatedUtc = createdUtc;
        ParentId = parentId;
    }

    public string Name { get; private set; }

    /// <summary>Optional "#RRGGBB" used to tint the subject chip in the UI.</summary>
    public string? ColorHex { get; private set; }

    public string? Description { get; private set; }

    public DateTimeOffset CreatedUtc { get; }

    /// <summary>
    /// The subject this one sits under, or null for a top-level subject.
    /// <para>
    /// This single nullable column is the entire hierarchy. Nothing about a card changes when a
    /// subject moves — see <see cref="SubjectHierarchy"/> for why ancestry is derived at query
    /// time rather than copied onto the cards that wear the tag.
    /// </para>
    /// </summary>
    public Guid? ParentId { get; private set; }

    public static Subject Create(string name, string? colorHex = null, string? description = null, Guid? parentId = null)
        => new(
            Guid.CreateVersion7(),
            Guard.AgainstTooLong(Guard.AgainstNullOrWhiteSpace(name), MaxNameLength),
            NormalizeColor(colorHex),
            description?.Trim(),
            DateTimeOffset.UtcNow,
            parentId);

    /// <summary>
    /// Mints a tag from a name alone. The colour is derived from the name so the same tag is the
    /// same colour on every machine and across every run, without anyone having to pick one.
    /// </summary>
    public static Subject CreateTag(string name, Guid? parentId = null)
    {
        var clean = Guard.AgainstTooLong(Guard.AgainstNullOrWhiteSpace(name), MaxNameLength);

        return new(Guid.CreateVersion7(), clean, ColorFor(clean), null, DateTimeOffset.UtcNow, parentId);
    }

    /// <summary>
    /// Folds the name into a palette index by hand rather than with <c>string.GetHashCode</c>,
    /// which is salted per-process on .NET Core — the same tag would otherwise change colour every
    /// time the app restarted.
    /// </summary>
    private static string ColorFor(string name)
    {
        var hash = 0;

        foreach (var c in name.ToUpperInvariant())
        {
            hash = ((hash * 31) + c) & 0x7FFFFFFF;
        }

        return TagPalette[hash % TagPalette.Length];
    }

    /// <summary>Rehydrate from persistence. Bypasses creation rules on purpose — the row was valid when it was written.</summary>
    public static Subject Rehydrate(
        Guid id,
        string name,
        string? colorHex,
        string? description,
        DateTimeOffset createdUtc,
        Guid? parentId = null)
        => new(id, name, colorHex, description, createdUtc, parentId);

    public void Rename(string name)
        => Name = Guard.AgainstTooLong(Guard.AgainstNullOrWhiteSpace(name), MaxNameLength);

    /// <summary>
    /// Re-parents this subject. Whether the move is legal — no cycle, no branch pushed past the
    /// depth limit — is <see cref="SubjectHierarchy"/>'s call, because answering it needs the
    /// whole tree and a single subject cannot see one. Callers validate there first.
    /// </summary>
    public void MoveTo(Guid? parentId)
    {
        if (parentId == Id)
        {
            throw new DomainException("A subject cannot be placed inside itself.");
        }

        ParentId = parentId;
    }

    public void SetColor(string? colorHex) => ColorHex = NormalizeColor(colorHex);

    public void SetDescription(string? description) => Description = description?.Trim();

    private static string? NormalizeColor(string? colorHex)
    {
        if (string.IsNullOrWhiteSpace(colorHex))
        {
            return null;
        }

        var value = colorHex.Trim();
        if (!value.StartsWith('#'))
        {
            value = "#" + value;
        }

        return value.Length is 7 or 9 ? value.ToUpperInvariant() : null;
    }
}
