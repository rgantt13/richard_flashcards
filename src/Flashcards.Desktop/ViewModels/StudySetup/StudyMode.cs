using Flashcards.Application.Contracts;

namespace Flashcards.Desktop.ViewModels.StudySetup;

/// <summary>
/// The ways to start a sitting.
/// <para>
/// Each is a different answer to "what should I work on", not a different difficulty. Nothing here
/// schedules anything — picking a mode picks an emphasis, and you can ignore all of them and hand
/// pick with <see cref="Custom"/>.
/// </para>
/// </summary>
public enum StudyMode
{
    Custom,
    Random,
    Suggested,
    Fresh,
    RecentlyMissed,
    SpeedDrill,
    Marathon,
}

/// <summary>
/// Everything the mode-selection panel needs to draw one tile, and everything the prep panel needs
/// to know about what it is preparing.
/// <para>
/// Kept as data rather than as a switch spread across the view models: which options a mode offers
/// is the main thing that differs between them, and having that stated once in a table is what
/// stops the prep panel and the tile disagreeing about what a mode does.
/// </para>
/// </summary>
public sealed record StudyModeCard(
    StudyMode Mode,
    string Title,
    string Blurb,
    QuizDraw Draw,
    string AccentKey,
    string IconKey)
{
    /// <summary>Whether the prep screen shows the subject and card tiers so you can hand-pick.</summary>
    public bool PicksCards { get; init; }

    /// <summary>Whether the prep screen offers a card count. Marathon deliberately does not.</summary>
    public bool HasCardCount { get; init; } = true;

    /// <summary>Whether this mode arrives with the clock already running.</summary>
    public bool PrefersTimed { get; init; }

    /// <summary>Whether this mode arrives restricted to card types the app can mark itself.</summary>
    public bool PrefersAutoGraded { get; init; }

    /// <summary>The whole catalogue, in the order the tiles are laid out.</summary>
    public static IReadOnlyList<StudyModeCard> All { get; } =
    [
        new(StudyMode.Custom, "Custom",
            "Choose subjects and cards by hand. The only mode that shows you the whole library first.",
            QuizDraw.Random, "SemiColorPrimary", "IconTune") { PicksCards = true },

        new(StudyMode.Random, "Random",
            "An even draw from everything you have. No emphasis, no ranking — just cards.",
            QuizDraw.Random, "#94A3B8", "IconShuffle"),

        new(StudyMode.Suggested, "Suggested",
            "Ranked by how often you get each card wrong, with cards you have never answered leading.",
            QuizDraw.HardestFirst, "SemiColorSuccess", "IconTarget"),

        new(StudyMode.Fresh, "Fresh cards",
            "Only cards you have never answered. The clean first pass over a deck you have just written.",
            QuizDraw.Untouched, "#14B8A6", "IconStar"),

        new(StudyMode.RecentlyMissed, "Recently missed",
            "Cards whose most recent answer was wrong, newest first. Not your worst cards ever — the ones you got wrong last.",
            QuizDraw.RecentlyMissed, "SemiColorWarning", "IconReplay"),

        new(StudyMode.SpeedDrill, "Speed drill",
            "A short timer on every question, and only cards the app can mark for you, so nothing waits on you grading yourself.",
            QuizDraw.Random, "SemiColorDanger", "IconTimer")
        {
            PrefersTimed = true,
            PrefersAutoGraded = true,
        },

        new(StudyMode.Marathon, "Marathon",
            "Everything in one sitting, with no cap on how many cards. For the night before.",
            QuizDraw.Random, "#7A5AF8", "IconLayers") { HasCardCount = false },
    ];

    public static StudyModeCard For(StudyMode mode) => All.First(m => m.Mode == mode);
}
