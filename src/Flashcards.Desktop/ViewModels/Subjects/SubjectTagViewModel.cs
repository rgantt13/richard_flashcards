using CommunityToolkit.Mvvm.ComponentModel;

namespace Flashcards.Desktop.ViewModels.Subjects;

/// <summary>
/// One subject tag sitting in the designer's tag box.
/// <para>
/// Holds a name rather than an id because a tag the user has just typed may not exist yet — it is
/// minted when the card is saved. The colour is looked up from the known subjects and is null for a
/// brand-new one, which is what makes an unsaved tag render as an uncoloured chip.
/// </para>
/// <para>
/// An <see cref="IsInherited"/> tag is not on the card at all: it is an ancestor of one that is.
/// Tagging a card "MSSQL" makes it a "SQL" card too, and showing that in the designer is the only
/// way the consequence of the tree is visible at the moment of tagging. It cannot be removed here,
/// because the way to remove it is to move the subject or drop the tag underneath it.
/// </para>
/// </summary>
public sealed partial class SubjectTagViewModel(string name, string? colorHex, bool isInherited = false) : ObservableObject
{
    public string Name { get; } = name;

    public bool IsInherited { get; } = isInherited;

    /// <summary>Only a tag the user actually applied offers a remove affordance.</summary>
    public bool IsRemovable => !IsInherited;

    [ObservableProperty]
    private string? _colorHex = colorHex;
}
