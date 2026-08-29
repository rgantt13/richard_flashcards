using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using Flashcards.Application.Contracts;

namespace Flashcards.Desktop.ViewModels.StudySetup;

/// <summary>
/// One subject on the Study panel: its record and whether it is in the session being built.
/// <para>
/// Selection and statistics are the same row deliberately. Choosing what to study and seeing how
/// you do at it are the same decision, so splitting them across two controls made you look in two
/// places to answer one question.
/// </para>
/// </summary>
public sealed partial class SubjectPickViewModel(SubjectStats stats) : ObservableObject
{
    public SubjectStats Stats { get; } = stats;

    public Guid Id => Stats.Id;

    public string Name => Stats.Name;

    public string? ColorHex => Stats.ColorHex;

    public PracticeStats Practice => Stats.Practice;

    /// <summary>Cards in this subject's whole subtree — what studying it would actually draw from.</summary>
    public int CardCount => Stats.CardCount;

    public int CardsUntouched => Stats.CardsUntouched;

    /// <summary>1 for a top-level subject. Capped at five, so the deepest indent is four steps in.</summary>
    public int Depth => Stats.Depth;

    public bool HasChildren => Stats.HasChildren;

    /// <summary>
    /// The left margin that turns depth into nesting. One tab per level, as an actual
    /// <see cref="Thickness"/> rather than leading spaces in the name, so trimming a long subject
    /// name still leaves the indent intact.
    /// </summary>
    public Thickness Indent => new((Depth - 1) * 16, 0, 0, 0);

    /// <summary>
    /// Cards wearing this exact tag rather than the subtree total, shown only where the two differ.
    /// A parent reading "12 cards · 2 here" is saying where the other ten actually live.
    /// </summary>
    public int DirectCardCount => Stats.DirectCardCount;

    public bool ShowsDirectCount => HasChildren && DirectCardCount != CardCount;

    /// <summary>Pluralised, because this reads at headline size beside the subject's name.</summary>
    public string CardCountLabel => CardCount == 1 ? "1 card" : $"{CardCount} cards";

    /// <summary>In the custom session being assembled.</summary>
    [ObservableProperty]
    private bool _isIncluded;
}

/// <summary>One card on the Study panel: its record and whether it is in the custom session.</summary>
public sealed partial class CardPickViewModel(FlashcardSummary card) : ObservableObject
{
    public FlashcardSummary Card { get; } = card;

    public Guid Id => Card.Id;

    public string Name => Card.Name;

    public PracticeStats Practice => Card.Practice;

    public IReadOnlyList<SubjectRef> Subjects => Card.Subjects;

    public bool IsUntouched => Card.IsUntouched;

    [ObservableProperty]
    private bool _isIncluded;
}
