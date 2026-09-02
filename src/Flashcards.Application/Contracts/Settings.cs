namespace Flashcards.Application.Contracts;

/// <summary>Which colour scheme the window should use.</summary>
public enum ThemePreference
{
    /// <summary>Follow whatever Windows is set to, and change with it.</summary>
    System = 0,

    Light = 1,

    Dark = 2,
}

/// <summary>
/// The handful of preferences that outlive a run.
/// <para>
/// Deliberately small, and deliberately not stored in the flashcards database. A copy of that file
/// is a copy of your cards — something you would send someone or restore from a backup — and your
/// choice of theme has no business travelling with it. These live in their own file beside it.
/// </para>
/// <para>
/// Every value has a default that is a working configuration, so a missing or half-written file
/// costs you your preferences rather than the app.
/// </para>
/// </summary>
public sealed record AppSettings
{
    public ThemePreference Theme { get; init; } = ThemePreference.Dark;

    /// <summary>What the study prep screen's card count starts at.</summary>
    public int DefaultCardCount { get; init; } = 20;

    /// <summary>Whether multiple-choice options are shuffled each time a card is shown.</summary>
    public bool ShuffleChoices { get; init; } = true;

    public static AppSettings Default { get; } = new();
}

/// <summary>
/// Where the library is, and whether that was chosen for this run.
/// <para>
/// The second half matters: an app pointed at a different folder looks exactly like an app that
/// has lost your cards. Saying so on the settings panel turns a mystery into a sentence.
/// </para>
/// </summary>
public sealed record DataLocation(string Path, bool IsOverridden, string OverrideVariable);
