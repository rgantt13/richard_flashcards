using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Flashcards.Desktop.Services;
using Flashcards.Desktop.ViewModels.Design;
using Flashcards.Desktop.ViewModels.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Flashcards.Desktop.Views.Design;

public partial class CardEditorView : UserControl
{
    private IClipboardImageService? _images;

    /// <summary>Resolved lazily so the XAML previewer, which never runs App startup, can still load the view.</summary>
    private IClipboardImageService Images
        => _images ??= App.Services.GetRequiredService<IClipboardImageService>();

    public CardEditorView()
    {
        InitializeComponent();

        // Drag-and-drop is wired here, not in the view model: DragEventArgs is an Avalonia type
        // and keeping it out of the view model is the whole point of the split.
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Click-anywhere-to-select, the way a drawing tool behaves. Tunnelling means we see the
        // press before the TextBox swallows it to place a caret, so clicking straight into an
        // element's text still selects that element for the inspector.
        AddHandler(PointerPressedEvent, OnPointerPressedTunnel, RoutingStrategies.Tunnel);

        // Enter commits whatever is typed. Picking from the drop-down goes straight to a command on
        // the item, so there is no selection event to mirror here any more.
        TagBox.KeyDown += OnTagBoxKeyDown;

        // The prediction popup runs without light dismiss, so closing it is this handler's job.
        // Everything inside the popup is Focusable="False", which is what makes a blur here mean
        // "the user went somewhere else" rather than "the user clicked a suggestion".
        TagBox.LostFocus += OnTagBoxLostFocus;

        // Moving and resizing elements belongs to the freeform board, which owns the surface they
        // are dragged on; it wires its own pointer handlers. This view keeps only the press
        // handler, because clicking to select has to work on every board.
    }

    private void OnTagBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not CardEditorViewModel viewModel)
        {
            return;
        }

        // Escape puts the suggestions away without disturbing what has been typed.
        if (e.Key == Key.Escape && viewModel.IsSuggestionsOpen)
        {
            viewModel.IsSuggestionsOpen = false;
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter)
        {
            return;
        }

        // Applies the subject if it exists, creates it if it does not — the command decides which.
        viewModel.CommitTagCommand.Execute(null);
        e.Handled = true;
    }

    private void OnTagBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CardEditorViewModel viewModel)
        {
            viewModel.IsSuggestionsOpen = false;
        }
    }

    private void OnPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not CardEditorViewModel viewModel)
        {
            return;
        }

        var target = FindTargetUnder(e.Source as Control);

        // An armed Text or Image tool places an element wherever you click on bare canvas.
        // Clicking an existing element selects it instead, so a mis-aimed click does not bury one
        // element under another. Where exactly "bare canvas" is only the freeform board knows.
        if (target is null && viewModel.IsFreeform && FreeformBoard.TryPlaceElement(viewModel, e))
        {
            e.Handled = true;
            return;
        }

        switch (target)
        {
            case BlockEditorViewModel block:
                viewModel.SelectBlockCommand.Execute(block);
                FreeformBoard.BeginDrag(viewModel, block, e);
                break;

            case ChoiceEditorViewModel choice:
                viewModel.SelectChoiceCommand.Execute(choice);
                break;
        }

        // Deliberately not handled: the press must still reach the TextBox underneath.
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is not CardEditorViewModel viewModel)
        {
            return;
        }

        // The clipboard lives on the TopLevel, which only the view can reach. The view model
        // gets two delegates instead of a dependency on Avalonia.
        viewModel.ClipboardImageProvider = () => Images.TryGetImageAsync(this);
        viewModel.FileImageProvider = () => Images.PickImageAsync(this);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // DataTransfer, not the obsolete Data/IDataObject. Contains() is a cheap format probe —
        // it does not pull the payload across, which matters because DragOver fires continuously
        // while the pointer moves.
        e.DragEffects = Images.CanAccept(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        if (DataContext is not CardEditorViewModel viewModel)
        {
            return;
        }

        // Walk up from whatever was dropped on until we hit something that can hold a picture.
        // On the standard and cloze boards that is a content block; on the multiple-choice board
        // the four answer slots are drop targets in their own right.
        var target = FindTargetUnder(e.Source as Control);

        if (target is null)
        {
            return;
        }

        if (await Images.TryGetDroppedImageAsync(e) is not { } image)
        {
            return;
        }

        switch (target)
        {
            case BlockEditorViewModel block:
                await viewModel.AttachDroppedImageAsync(block, image);
                break;

            case ChoiceEditorViewModel choice:
                await viewModel.AttachDroppedChoiceImageAsync(choice, image);
                break;
        }
    }

    /// <summary>
    /// The nearest ancestor whose DataContext is a droppable element. Returns the view model
    /// itself rather than the control, because that is all the caller needs.
    /// </summary>
    private static object? FindTargetUnder(Control? control)
    {
        while (control is not null)
        {
            if (control.DataContext is BlockEditorViewModel or ChoiceEditorViewModel)
            {
                return control.DataContext;
            }

            control = control.Parent as Control;
        }

        return null;
    }
}
