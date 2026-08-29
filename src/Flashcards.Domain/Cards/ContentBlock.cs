using Flashcards.Domain.Common;

namespace Flashcards.Domain.Cards;

/// <summary>
/// One piece of content on one side of a card.
/// <para>
/// This is the model that makes "multiple question and answer formats" work: a face is an
/// ordered list of blocks, so a single question can be a Markdown paragraph, then a C# code
/// snippet, then a pasted screenshot. Each block carries only the fields its kind needs.
/// </para>
/// </summary>
public sealed class ContentBlock : Entity
{
    public const int MaxTextLength = 20_000;

    private ContentBlock(
        Guid id,
        CardFace face,
        int ordinal,
        ContentKind kind,
        string? text,
        string? language,
        Guid? mediaId,
        ImageStretch stretch,
        double? maxHeight,
        string? altText,
        BlockBounds? bounds)
        : base(id)
    {
        Face = face;
        Ordinal = ordinal;
        Kind = kind;
        Text = text;
        Language = language;
        MediaId = mediaId;
        Stretch = stretch;
        MaxHeight = maxHeight;
        AltText = altText;
        Bounds = bounds;
    }

    public CardFace Face { get; internal set; }

    /// <summary>Zero-based position within its face. The card keeps these dense and gap-free.</summary>
    public int Ordinal { get; internal set; }

    public ContentKind Kind { get; private set; }

    /// <summary>Body text for PlainText / Markdown / Code blocks. Null for Image blocks.</summary>
    public string? Text { get; private set; }

    /// <summary>Highlighter language token for Code blocks ("csharp", "sql", "json"...). Null otherwise.</summary>
    public string? Language { get; private set; }

    /// <summary>Points at a row in the media store for Image blocks. Null otherwise.</summary>
    public Guid? MediaId { get; private set; }

    /// <summary>How an Image block fills its slot. Ignored by text blocks.</summary>
    public ImageStretch Stretch { get; private set; }

    /// <summary>Optional cap in device-independent pixels so one screenshot cannot swallow the card.</summary>
    public double? MaxHeight { get; private set; }

    /// <summary>Description shown if the image file is missing, and used for accessibility.</summary>
    public string? AltText { get; private set; }

    /// <summary>
    /// Absolute placement on a freeform card's canvas, or null when the block is laid out in flow.
    /// See <see cref="BlockBounds"/>.
    /// </summary>
    public BlockBounds? Bounds { get; private set; }

    public bool IsImage => Kind == ContentKind.Image;

    public bool IsDrawing => Kind == ContentKind.Drawing;

    /// <summary>The ink on a Drawing block. Empty for every other kind.</summary>
    public IReadOnlyList<InkStroke> Strokes => IsDrawing ? InkSerializer.Parse(Text) : [];

    /// <summary>A drawing whose strokes have all been erased carries no content.</summary>
    public bool IsBlankDrawing => IsDrawing && string.IsNullOrWhiteSpace(Text);

    /// <summary>Creates the ink layer for one face of a freeform card.</summary>
    public static ContentBlock CreateDrawing(CardFace face, int ordinal, IEnumerable<InkStroke> strokes)
        => new(
            Guid.CreateVersion7(),
            face,
            ordinal,
            ContentKind.Drawing,
            InkSerializer.Serialize(strokes),
            language: null,
            mediaId: null,
            stretch: ImageStretch.Uniform,
            maxHeight: null,
            altText: null,
            // The ink layer always spans the whole canvas; strokes carry their own coordinates.
            bounds: new BlockBounds(0, 0, CardCanvas.Width, CardCanvas.Height));

    /// <summary>Moves or resizes the block on the canvas, clamped so it cannot be lost off-edge.</summary>
    public void PlaceAt(BlockBounds bounds) => Bounds = bounds.ClampToCanvas();

    /// <summary>Returns the block to flow layout.</summary>
    public void ClearPlacement() => Bounds = null;

    public void ReplaceStrokes(IEnumerable<InkStroke> strokes)
    {
        if (!IsDrawing)
        {
            throw new DomainException("Only a drawing block holds ink.");
        }

        Text = InkSerializer.Serialize(strokes);
    }

    public static ContentBlock CreateText(CardFace face, int ordinal, ContentKind kind, string text, string? language = null)
    {
        if (kind == ContentKind.Image)
        {
            throw new DomainException("Use ContentBlock.CreateImage() for image blocks.");
        }

        var body = Guard.AgainstTooLong(Guard.AgainstNullOrWhiteSpace(text), MaxTextLength);

        return new ContentBlock(
            Guid.CreateVersion7(),
            face,
            ordinal,
            kind,
            body,
            kind == ContentKind.Code ? NormalizeLanguage(language) : null,
            mediaId: null,
            stretch: ImageStretch.Uniform,
            maxHeight: null,
            altText: null,
            bounds: null);
    }

    public static ContentBlock CreateImage(
        CardFace face,
        int ordinal,
        Guid mediaId,
        ImageStretch stretch = ImageStretch.Uniform,
        double? maxHeight = 420d,
        string? altText = null)
    {
        if (mediaId == Guid.Empty)
        {
            throw new DomainException("An image block needs a media reference.");
        }

        if (maxHeight is <= 0)
        {
            throw new DomainException("Image max height must be positive.");
        }

        return new ContentBlock(
            Guid.CreateVersion7(),
            face,
            ordinal,
            ContentKind.Image,
            text: null,
            language: null,
            mediaId,
            stretch,
            maxHeight,
            altText?.Trim(),
            bounds: null);
    }

    public static ContentBlock Rehydrate(
        Guid id,
        CardFace face,
        int ordinal,
        ContentKind kind,
        string? text,
        string? language,
        Guid? mediaId,
        ImageStretch stretch,
        double? maxHeight,
        string? altText,
        BlockBounds? bounds = null)
        => new(id, face, ordinal, kind, text, language, mediaId, stretch, maxHeight, altText, bounds);

    public void UpdateText(string text, string? language = null)
    {
        if (Kind is ContentKind.Image or ContentKind.Drawing)
        {
            throw new DomainException("Cannot set text on an image or drawing block.");
        }

        Text = Guard.AgainstTooLong(Guard.AgainstNullOrWhiteSpace(text), MaxTextLength);
        Language = Kind == ContentKind.Code ? NormalizeLanguage(language) : null;
    }

    public void ChangeKind(ContentKind kind, string? language = null)
    {
        if (Kind == ContentKind.Image || kind == ContentKind.Image)
        {
            throw new DomainException("Image blocks cannot be converted to or from text blocks. Delete and re-add.");
        }

        Kind = kind;
        Language = kind == ContentKind.Code ? NormalizeLanguage(language) : null;
    }

    public void UpdateImageLayout(ImageStretch stretch, double? maxHeight, string? altText)
    {
        if (Kind != ContentKind.Image)
        {
            throw new DomainException("Only image blocks have layout settings.");
        }

        if (maxHeight is <= 0)
        {
            throw new DomainException("Image max height must be positive.");
        }

        Stretch = stretch;
        MaxHeight = maxHeight;
        AltText = altText?.Trim();
    }

    private static string? NormalizeLanguage(string? language)
        => string.IsNullOrWhiteSpace(language) ? "plaintext" : language.Trim().ToLowerInvariant();
}
