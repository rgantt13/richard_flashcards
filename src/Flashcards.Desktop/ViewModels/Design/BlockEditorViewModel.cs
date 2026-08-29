using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Flashcards.Application.Contracts;
using Flashcards.Domain.Cards;

namespace Flashcards.Desktop.ViewModels.Design;

/// <summary>
/// One editable content block — an element on the designer's artboard. The editor holds two lists
/// of these, one per face, and each is edited in place on the artboard.
/// </summary>
public sealed partial class BlockEditorViewModel : ObservableObject
{
    public BlockEditorViewModel(CardFace face, ContentKind kind)
    {
        Face = face;
        _kind = kind;
        _language = kind == ContentKind.Code ? "csharp" : null;
    }

    public static BlockEditorViewModel FromDto(ContentBlockDto dto) => new(dto.Face, dto.Kind)
    {
        Id = dto.Id,
        Text = dto.Text ?? string.Empty,
        Language = dto.Language,
        MediaId = dto.MediaId,
        Stretch = dto.Stretch,
        MaxHeight = dto.MaxHeight ?? 420,
        AltText = dto.AltText,
        IsPlaced = dto.IsPlaced,
        X = dto.X ?? 0,
        Y = dto.Y ?? 0,
        Width = dto.Width ?? 240,
        Height = dto.Height ?? 120,
    };

    public Guid Id { get; init; } = Guid.Empty;

    public CardFace Face { get; }

    /// <summary>Drives the selection ring on the artboard, and which element the handles act on.</summary>
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private ContentKind _kind;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private string? _language;

    [ObservableProperty]
    private Guid? _mediaId;

    [ObservableProperty]
    private ImageStretch _stretch = ImageStretch.Uniform;

    [ObservableProperty]
    private double _maxHeight = 420;

    [ObservableProperty]
    private string? _altText;

    // ---- freeform placement ---------------------------------------------
    // Only meaningful when IsPlaced. Kept as plain doubles rather than a BlockBounds so the
    // canvas can bind Canvas.Left/Top/Width/Height straight at them while a drag is in flight.

    /// <summary>True when this element lives at a fixed spot on a designed card's canvas.</summary>
    [ObservableProperty]
    private bool _isPlaced;

    [ObservableProperty]
    private double _x;

    [ObservableProperty]
    private double _y;

    [ObservableProperty]
    private double _width = 240;

    [ObservableProperty]
    private double _height = 120;

    /// <summary>
    /// Placement expressed as a margin against a top-left aligned parent.
    /// <para>
    /// The canvas positions elements this way rather than with <c>Canvas.Left</c>/<c>Top</c>,
    /// because those are attached properties set on the generated container — and a compiled
    /// binding inside <c>ItemsControl.Styles</c> has no way to know the container's data type.
    /// A margin is set on the item template's own root, where the type is known.
    /// </para>
    /// </summary>
    public Thickness Placement => new(X, Y, 0, 0);

    /// <summary>Applies a move or resize, clamped by the domain so nothing leaves the card.</summary>
    public void Place(double x, double y, double width, double height)
    {
        var bounds = new BlockBounds(x, y, width, height).ClampToCanvas();

        IsPlaced = true;
        X = bounds.X;
        Y = bounds.Y;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    // TextBox exposes SelectionStart/SelectionEnd, not a length, and the two can arrive in
    // either order depending on which way the user dragged. Both are mirrored here so the
    // "Blank" button knows exactly what to wrap.
    [ObservableProperty]
    private int _selectionStart;

    [ObservableProperty]
    private int _selectionEnd;

    public int SelectionOffset => Math.Min(SelectionStart, SelectionEnd);

    public int SelectionLength => Math.Abs(SelectionEnd - SelectionStart);

    /// <summary>
    /// The last selection that actually covered something, kept so a command invoked from a
    /// button still knows what the user had highlighted. The live selection is not guaranteed to
    /// survive the click that runs the command; this is.
    /// <para>
    /// Cleared whenever the text changes, so it can never point into a string that no longer
    /// looks like the one the user was selecting in.
    /// </para>
    /// </summary>
    public int LastSelectionOffset { get; private set; }

    public int LastSelectionLength { get; private set; }

    private void CaptureSelection()
    {
        if (SelectionLength <= 0)
        {
            return;
        }

        LastSelectionOffset = SelectionOffset;
        LastSelectionLength = SelectionLength;
    }

    public bool IsImage => Kind == ContentKind.Image;

    public bool IsCode => Kind == ContentKind.Code;

    public bool IsTextual => Kind != ContentKind.Image;

    public bool IsEmpty => IsImage ? MediaId is null : string.IsNullOrWhiteSpace(Text);

    public string KindLabel => Kind switch
    {
        ContentKind.Markdown => "Markdown",
        ContentKind.Code => "Code",
        ContentKind.Image => "Image",
        _ => "Text",
    };

    /// <summary>Single-letter badge shown on the element's handle strip.</summary>
    public string KindGlyph => Kind switch
    {
        ContentKind.Markdown => "M",
        ContentKind.Code => "</>",
        ContentKind.Image => "IMG",
        _ => "T",
    };

    /// <summary>Monospace while editing code, the default face otherwise.</summary>
    public FontFamily EditorFont => IsCode
        ? new FontFamily("Cascadia Code,Cascadia Mono,Consolas,Menlo,monospace")
        : FontFamily.Default;

    public ContentBlockDto ToDto(int ordinal) => new(
        Id,
        Face,
        ordinal,
        Kind,
        Kind == ContentKind.Image ? null : Text,
        Kind == ContentKind.Code ? (Language ?? "plaintext") : null,
        Kind == ContentKind.Image ? MediaId : null,
        Stretch,
        Kind == ContentKind.Image ? MaxHeight : null,
        Kind == ContentKind.Image ? AltText : null,
        IsPlaced ? X : null,
        IsPlaced ? Y : null,
        IsPlaced ? Width : null,
        IsPlaced ? Height : null);

    /// <summary>
    /// This element on its own, so the artboard can render it with the same
    /// <c>RichContentPresenter</c> the study screen uses. Designing against the real renderer is
    /// what stops the canvas and the card from disagreeing about how something looks.
    /// </summary>
    public IReadOnlyList<ContentBlockDto> SelfPreview => [ToDto(0)];

    private void RefreshSelfPreview() => OnPropertyChanged(nameof(SelfPreview));

    partial void OnSelectionStartChanged(int value)
    {
        OnPropertyChanged(nameof(SelectionOffset));
        OnPropertyChanged(nameof(SelectionLength));
        CaptureSelection();
    }

    partial void OnSelectionEndChanged(int value)
    {
        OnPropertyChanged(nameof(SelectionOffset));
        OnPropertyChanged(nameof(SelectionLength));
        CaptureSelection();
    }

    partial void OnTextChanged(string value)
    {
        // A remembered offset into the previous text is meaningless once the text moves.
        LastSelectionOffset = 0;
        LastSelectionLength = 0;

        OnPropertyChanged(nameof(IsEmpty));
        RefreshSelfPreview();
    }

    partial void OnMediaIdChanged(Guid? value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        RefreshSelfPreview();
    }

    partial void OnXChanged(double value) => OnPropertyChanged(nameof(Placement));

    partial void OnYChanged(double value) => OnPropertyChanged(nameof(Placement));

    partial void OnLanguageChanged(string? value) => RefreshSelfPreview();

    partial void OnStretchChanged(ImageStretch value) => RefreshSelfPreview();

    partial void OnMaxHeightChanged(double value) => RefreshSelfPreview();

    partial void OnAltTextChanged(string? value) => RefreshSelfPreview();

    partial void OnKindChanged(ContentKind value)
    {
        Language = value == ContentKind.Code ? Language ?? "csharp" : null;

        OnPropertyChanged(nameof(IsImage));
        OnPropertyChanged(nameof(IsCode));
        OnPropertyChanged(nameof(IsTextual));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(KindGlyph));
        OnPropertyChanged(nameof(EditorFont));
        RefreshSelfPreview();
    }
}
