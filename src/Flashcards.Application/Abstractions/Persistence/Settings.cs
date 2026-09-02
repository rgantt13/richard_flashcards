using Flashcards.Application.Contracts;

namespace Flashcards.Application.Abstractions.Persistence;

/// <summary>
/// Where preferences are kept.
/// <para>
/// Separate from the repositories on purpose: those all hang off a <c>DbSession</c> and a unit of
/// work, and preferences are neither transactional nor part of any aggregate. Reading them must
/// also work before the database has been opened, because the theme has to be applied before a
/// window is shown.
/// </para>
/// </summary>
public interface ISettingsStore
{
    /// <summary>Reads what is stored, falling back to defaults for anything missing or unreadable.</summary>
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);

    /// <summary>Where the library lives, and whether that was overridden for this run.</summary>
    DataLocation Location { get; }
}
