using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.Primitives;

namespace Flashcards.Desktop.Controls.Subjects;

/// <summary>
/// A subject tag rendered as a chip — the desktop equivalent of the Chip/Tag component you would
/// reach for in a web component library.
/// <para>
/// This is a <see cref="TemplatedControl"/>, which is Avalonia's "lookless control" pattern and the
/// right shape for a small reusable presentational element: the class below declares only data
/// (name, colour, optional count), and the appearance lives entirely in a
/// <c>ControlTheme</c> in <c>SubjectChip.axaml</c>. Anywhere a subject appears — the manage list,
/// the study header, the designer's tag box, the filter lists — uses this one control, so the
/// chips cannot drift apart from each other.
/// </para>
/// <para>
/// The alternative, a <c>UserControl</c>, would bake the layout into the type and make restyling a
/// fork rather than a theme override. That is the wrong trade for something this small and this
/// repeated.
/// </para>
/// </summary>
public class SubjectChip : TemplatedControl
{
    public static readonly StyledProperty<string?> SubjectNameProperty =
        AvaloniaProperty.Register<SubjectChip, string?>(nameof(SubjectName));

    /// <summary>"#RRGGBB" identity colour. The chip tints itself from this and shows a solid dot.</summary>
    public static readonly StyledProperty<string?> ColorHexProperty =
        AvaloniaProperty.Register<SubjectChip, string?>(nameof(ColorHex));

    public static readonly StyledProperty<int> CountProperty =
        AvaloniaProperty.Register<SubjectChip, int>(nameof(Count));

    /// <summary>Shows the trailing count badge. Off by default — most placements just want the name.</summary>
    public static readonly StyledProperty<bool> ShowCountProperty =
        AvaloniaProperty.Register<SubjectChip, bool>(nameof(ShowCount));

    /// <summary>
    /// Shows a trailing "remove" affordance, the way a component library's chip exposes onDelete.
    /// Only meaningful where the chip is editable — the designer's tag box — so it is off by default.
    /// </summary>
    public static readonly StyledProperty<bool> ShowRemoveProperty =
        AvaloniaProperty.Register<SubjectChip, bool>(nameof(ShowRemove));

    public static readonly StyledProperty<ICommand?> RemoveCommandProperty =
        AvaloniaProperty.Register<SubjectChip, ICommand?>(nameof(RemoveCommand));

    public static readonly StyledProperty<object?> RemoveCommandParameterProperty =
        AvaloniaProperty.Register<SubjectChip, object?>(nameof(RemoveCommandParameter));

    public string? SubjectName
    {
        get => GetValue(SubjectNameProperty);
        set => SetValue(SubjectNameProperty, value);
    }

    public string? ColorHex
    {
        get => GetValue(ColorHexProperty);
        set => SetValue(ColorHexProperty, value);
    }

    public int Count
    {
        get => GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    public bool ShowCount
    {
        get => GetValue(ShowCountProperty);
        set => SetValue(ShowCountProperty, value);
    }

    public bool ShowRemove
    {
        get => GetValue(ShowRemoveProperty);
        set => SetValue(ShowRemoveProperty, value);
    }

    public ICommand? RemoveCommand
    {
        get => GetValue(RemoveCommandProperty);
        set => SetValue(RemoveCommandProperty, value);
    }

    public object? RemoveCommandParameter
    {
        get => GetValue(RemoveCommandParameterProperty);
        set => SetValue(RemoveCommandParameterProperty, value);
    }
}
