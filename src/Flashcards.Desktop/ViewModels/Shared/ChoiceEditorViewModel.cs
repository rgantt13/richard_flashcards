using CommunityToolkit.Mvvm.ComponentModel;
using Flashcards.Application.Contracts;
using Flashcards.Domain.Cards;

namespace Flashcards.Desktop.ViewModels.Shared;

/// <summary>
/// One multiple-choice answer slot.
/// <para>
/// The MC designer lays out four of these regardless of how many are filled in — an empty slot is
/// a placeholder on the artboard, not a saved option. <see cref="IsBlank"/> is what the editor
/// filters on when it turns slots into a command.
/// </para>
/// </summary>
public sealed partial class ChoiceEditorViewModel : ObservableObject
{
    public Guid Id { get; init; } = Guid.Empty;

    /// <summary>"A" through "D" — the slot's badge on the artboard.</summary>
    [ObservableProperty]
    private string _label = "A";

    /// <summary>
    /// Designer selection: this slot is the one the inspector is editing. Distinct from
    /// <see cref="IsSelected"/>, which is the learner ticking it during a quiz — the same view
    /// model serves both screens and the two states are unrelated.
    /// </summary>
    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private bool _isCorrect;

    [ObservableProperty]
    private Guid? _mediaId;

    [ObservableProperty]
    private string? _altText;

    /// <summary>Set during quiz mode when the user picks this option.</summary>
    [ObservableProperty]
    private bool _isSelected;

    public bool HasImage => MediaId is not null;

    /// <summary>An untouched slot: no words, no picture. These never reach the database.</summary>
    public bool IsBlank => MediaId is null && string.IsNullOrWhiteSpace(Text);

    public static ChoiceEditorViewModel FromDto(ChoiceDto dto)
        => new() { Id = dto.Id, Text = dto.Text, IsCorrect = dto.IsCorrect, MediaId = dto.MediaId };

    public ChoiceDto ToDto(int ordinal) => new(Id, ordinal, Text ?? string.Empty, IsCorrect, MediaId);

    /// <summary>
    /// The slot's picture as a one-element block list, so the answer tile can render it through
    /// the same presenter the rest of the app uses. Empty when the slot holds only words.
    /// </summary>
    public IReadOnlyList<ContentBlockDto> ImagePreview => MediaId is { } id
        ?
        [
            new ContentBlockDto(Guid.Empty, CardFace.Answer, 0, ContentKind.Image, null, null,
                id, ImageStretch.Uniform, 150, AltText),
        ]
        : [];

    partial void OnTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsBlank));
    }

    partial void OnAltTextChanged(string? value) => OnPropertyChanged(nameof(ImagePreview));

    partial void OnMediaIdChanged(Guid? value)
    {
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(IsBlank));
        OnPropertyChanged(nameof(ImagePreview));
    }
}
