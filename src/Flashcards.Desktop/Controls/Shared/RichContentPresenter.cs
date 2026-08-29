using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Flashcards.Application.Contracts;
using Flashcards.Desktop.Services;
using Flashcards.Domain.Cards;
using Microsoft.Extensions.DependencyInjection;

namespace Flashcards.Desktop.Controls.Shared;

/// <summary>
/// Renders an ordered list of <see cref="ContentBlockDto"/> — the thing that makes "several
/// formats on one card" visible. Each block becomes the control its kind calls for:
/// plain text and Markdown become a wrapping TextBlock, code becomes a highlighted monospace
/// surface with a language tag, and an image becomes an <see cref="Image"/> honouring the
/// stretch and height the card chose.
/// <para>
/// [WPF] The equivalent would be an ItemsControl with a DataTemplateSelector. Avalonia has
/// <c>DataTemplates</c> with <c>DataType</c> matching, but the blocks are one CLR type
/// discriminated by an enum, so building the tree in code is both shorter and easier to follow
/// than five templates plus a converter.
/// </para>
/// </summary>
public sealed class RichContentPresenter : Decorator
{
    public static readonly StyledProperty<IEnumerable<ContentBlockDto>?> BlocksProperty =
        AvaloniaProperty.Register<RichContentPresenter, IEnumerable<ContentBlockDto>?>(nameof(Blocks));

    /// <summary>When true, cloze markers are rendered as blanks rather than shown literally.</summary>
    public static readonly StyledProperty<bool> HideClozeAnswersProperty =
        AvaloniaProperty.Register<RichContentPresenter, bool>(nameof(HideClozeAnswers));

    public static readonly StyledProperty<double> ContentFontSizeProperty =
        AvaloniaProperty.Register<RichContentPresenter, double>(nameof(ContentFontSize), 15d);

    static RichContentPresenter()
    {
        // Any of these changing means the whole subtree is stale.
        BlocksProperty.Changed.AddClassHandler<RichContentPresenter>((c, _) => c.Rebuild());
        HideClozeAnswersProperty.Changed.AddClassHandler<RichContentPresenter>((c, _) => c.Rebuild());
        ContentFontSizeProperty.Changed.AddClassHandler<RichContentPresenter>((c, _) => c.Rebuild());
    }

    public IEnumerable<ContentBlockDto>? Blocks
    {
        get => GetValue(BlocksProperty);
        set => SetValue(BlocksProperty, value);
    }

    public bool HideClozeAnswers
    {
        get => GetValue(HideClozeAnswersProperty);
        set => SetValue(HideClozeAnswersProperty, value);
    }

    public double ContentFontSize
    {
        get => GetValue(ContentFontSizeProperty);
        set => SetValue(ContentFontSizeProperty, value);
    }

    private void Rebuild()
    {
        var blocks = (Blocks ?? []).OrderBy(b => b.Ordinal).ToList();

        // Decorator, not ContentControl, on purpose: a control derived from ContentControl needs a
        // ControlTheme registered for its concrete type or it silently renders nothing — the most
        // common "my custom control is invisible" bug in Avalonia. Decorator just hosts its Child.
        Child = blocks.Any(b => b.IsPlaced)
            ? BuildDesignedFace(blocks)
            : BuildFlow(blocks);
    }

    private Control BuildFlow(IReadOnlyList<ContentBlockDto> blocks)
    {
        var panel = new StackPanel { Spacing = 12 };

        foreach (var block in blocks)
        {
            if (Build(block) is { } control)
            {
                panel.Children.Add(control);
            }
        }

        return panel;
    }

    /// <summary>
    /// One face of a designed card: elements at the coordinates the author left them, with the ink
    /// layer on top.
    /// <para>
    /// The whole thing is built at the card's fixed logical size and then scaled by a Viewbox, so
    /// studying a card at any window size reproduces the layout exactly rather than reflowing it.
    /// That is the same arrangement the designer uses, which is why the two agree.
    /// </para>
    /// </summary>
    private Control BuildDesignedFace(IReadOnlyList<ContentBlockDto> blocks)
    {
        var surface = new Panel
        {
            Width = CardCanvas.Width,
            Height = CardCanvas.Height,
        };

        foreach (var block in blocks.Where(b => b.Kind != ContentKind.Drawing))
        {
            if (Build(block) is not { } control)
            {
                continue;
            }

            // An unplaced element on an otherwise designed face still has to go somewhere; the
            // top-left corner is the least surprising fallback.
            surface.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(block.X ?? 0, block.Y ?? 0, 0, 0),
                Width = block.Width ?? CardCanvas.Width,
                Height = block.Height ?? CardCanvas.Height,
                // Content that outgrew its box is cropped rather than allowed to overlap whatever
                // the author placed next to it.
                ClipToBounds = true,
                Child = control,
            });
        }

        foreach (var drawing in blocks.Where(b => b.Kind == ContentKind.Drawing))
        {
            surface.Children.Add(new InkSurface
            {
                Strokes = [.. InkSerializer.Parse(drawing.Text)],
                // Purely presentational here: nothing on the study screen draws or erases.
                IsHitTestVisible = false,
            });
        }

        return new Viewbox
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            Child = surface,
        };
    }

    private Control? Build(ContentBlockDto block) => block.Kind switch
    {
        // Ink is not an element — it is painted as a layer by BuildDesignedFace. Returning null
        // also stops a stray drawing block from rendering its serialised coordinates as prose if
        // one ever reaches the flow path.
        ContentKind.Drawing => null,
        ContentKind.Image => BuildImage(block),
        ContentKind.Code => BuildCode(block),
        ContentKind.Markdown => BuildMarkdown(block),
        _ => BuildPlainText(block),
    };

    private Control BuildPlainText(ContentBlockDto block) => new TextBlock
    {
        Text = Project(block.Text),
        TextWrapping = TextWrapping.Wrap,
        FontSize = ContentFontSize,
        LineHeight = ContentFontSize * 1.55,
    };

    private Control BuildMarkdown(ContentBlockDto block)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = ContentFontSize,
            LineHeight = ContentFontSize * 1.55,
        };

        text.Inlines = InlineMarkdown.Render(Project(block.Text), ContentFontSize);

        return text;
    }

    private Control BuildCode(ContentBlockDto block)
    {
        var code = new TextBlock
        {
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = CodeTheme.MonoFont,
            FontSize = ContentFontSize - 1,
            LineHeight = (ContentFontSize - 1) * 1.5,
        };

        code.Inlines = CodeHighlighter.Highlight(Project(block.Text), block.Language, ContentFontSize - 1);

        var language = new TextBlock
        {
            Text = block.Language ?? "plaintext",
            FontSize = 10,
            Opacity = 0.45,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 0, 4),
        };

        var border = new Border
        {
            Child = new StackPanel
            {
                Children =
                {
                    language,
                    // Long lines scroll rather than reflow — wrapping code is worse than scrolling it.
                    new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        Content = code,
                    },
                },
            },
        };

        border.Classes.Add("codeSurface");

        return border;
    }

    private Control BuildImage(ContentBlockDto block)
    {
        var image = new Image
        {
            Stretch = block.Stretch switch
            {
                ImageStretch.None => Stretch.None,
                ImageStretch.Fill => Stretch.Fill,
                ImageStretch.UniformToFill => Stretch.UniformToFill,
                _ => Stretch.Uniform,
            },
            // Uniform without a cap would let a tall screenshot push the grade buttons off-screen.
            MaxHeight = block.MaxHeight ?? 420,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var placeholder = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(block.AltText) ? "(image)" : block.AltText,
            Opacity = 0.5,
            FontStyle = FontStyle.Italic,
            FontSize = ContentFontSize - 2,
        };

        var host = new Panel { Children = { placeholder, image } };

        if (block.MediaId is { } mediaId)
        {
            LoadImageAsync(mediaId, image, placeholder);
        }

        return host;
    }

    private static void LoadImageAsync(Guid mediaId, Image target, Control placeholder)
    {
        // Fire-and-forget on purpose: the layout is already correct with the placeholder showing,
        // and the bitmap swaps in when it arrives. Marshalled back to the UI thread explicitly
        // because ImageCache may have finished on a thread-pool thread.
        _ = Task.Run(async () =>
        {
            Bitmap? bitmap = null;

            try
            {
                bitmap = await App.Services.GetRequiredService<IImageCache>().GetAsync(mediaId);
            }
            catch (Exception)
            {
                // Leave the alt text in place.
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (bitmap is null)
                {
                    return;
                }

                target.Source = bitmap;
                placeholder.IsVisible = false;
            });
        });
    }

    /// <summary>Applies cloze masking when the question side is showing.</summary>
    private string Project(string? text)
        => HideClozeAnswers && ClozeParser.HasBlanks(text)
            ? ClozeParser.RenderPrompt(text)
            : ClozeParser.RenderSolution(text);
}
