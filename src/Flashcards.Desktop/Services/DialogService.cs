using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Flashcards.Desktop.ViewModels.Manage;
using Flashcards.Desktop.ViewModels.Subjects;
using Flashcards.Desktop.Views.Manage;
using Flashcards.Desktop.Views.Subjects;

namespace Flashcards.Desktop.Services;

public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "Delete", bool destructive = true);

    Task ShowErrorAsync(string title, string message);

    /// <summary>
    /// Asks for a single line of text. Returns null if the user cancelled, which is deliberately
    /// distinct from returning an empty string — "I changed my mind" and "I cleared the box" are
    /// different answers, and only the caller knows what to do about the second.
    /// </summary>
    Task<string?> PromptAsync(string title, string message, string? initialValue = null, string confirmText = "Save");

    /// <summary>
    /// Opens the create-a-subject dialog. Returns the name of the subject that was created, or null
    /// if the user backed out.
    /// <para>
    /// The view model is built by the caller rather than here: it needs a dispatcher to load the
    /// tree and to issue the create, and this service deliberately knows nothing but Avalonia.
    /// </para>
    /// </summary>
    Task<string?> CreateSubjectAsync(SubjectCreateViewModel model);

    /// <summary>
    /// Opens the import/export picker. Returns whether the user committed; what they ticked is
    /// read back off the model, which the caller owns.
    /// <para>
    /// The model arrives already loaded, for the same reason as above: filling it needs a
    /// dispatcher, and this service deliberately knows nothing but Avalonia.
    /// </para>
    /// </summary>
    Task<bool> TransferDeckAsync(DeckTransferViewModel model);
}

/// <summary>
/// Avalonia ships no MessageBox. Rather than take a dependency for two dialogs, this builds them
/// from primitives — which also means they pick up the Semi theme automatically.
/// </summary>
public sealed class DialogService : IDialogService
{
    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "Delete", bool destructive = true)
        => ShowAsync(title, message, confirmText, destructive, showCancel: true);

    public async Task ShowErrorAsync(string title, string message)
        => await ShowAsync(title, message, "Close", destructive: false, showCancel: false);

    public async Task<string?> CreateSubjectAsync(SubjectCreateViewModel model)
    {
        var owner = (App.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

        if (owner is null)
        {
            return null;
        }

        // Loaded before the window is shown so the tree is already populated when it appears —
        // a dialog that pops up empty and fills in a frame later reads as broken.
        await model.LoadAsync();

        var window = new SubjectCreateWindow { DataContext = model };
        var created = await window.ShowDialog<bool>(owner);

        return created ? model.CreatedName : null;
    }

    public async Task<bool> TransferDeckAsync(DeckTransferViewModel model)
    {
        var owner = (App.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

        if (owner is null)
        {
            return false;
        }

        return await new DeckTransferWindow { DataContext = model }.ShowDialog<bool>(owner);
    }

    public async Task<string?> PromptAsync(string title, string message, string? initialValue = null, string confirmText = "Save")
    {
        var owner = (App.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

        if (owner is null)
        {
            return null;
        }

        string? result = null;

        var input = new TextBox { Text = initialValue ?? string.Empty };

        var confirm = new Button
        {
            Content = confirmText,
            MinWidth = 92,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsDefault = true,
        };

        confirm.Classes.Add("Primary");

        var cancel = new Button
        {
            Content = "Cancel",
            MinWidth = 92,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsCancel = true,
        };

        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.Height,
            Width = 460,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(24),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 },
                    input,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancel, confirm },
                    },
                },
            },
        };

        confirm.Click += (_, _) => { result = input.Text; dialog.Close(); };
        cancel.Click += (_, _) => { result = null; dialog.Close(); };

        // Opened with the existing text selected, so renaming is type-over rather than
        // select-all-then-type.
        dialog.Opened += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        await dialog.ShowDialog(owner);

        return result;
    }

    private static async Task<bool> ShowAsync(string title, string message, string confirmText, bool destructive, bool showCancel)
    {
        var owner = (App.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

        if (owner is null)
        {
            return false;
        }

        var result = false;

        var confirm = new Button
        {
            Content = confirmText,
            MinWidth = 92,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };

        if (destructive)
        {
            confirm.Classes.Add("Danger");
        }
        else
        {
            confirm.Classes.Add("Primary");
        }

        var cancel = new Button
        {
            Content = "Cancel",
            MinWidth = 92,
            IsVisible = showCancel,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, confirm },
        };

        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.Height,
            Width = 460,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(24),
                Spacing = 18,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 },
                    buttons,
                },
            },
        };

        confirm.Click += (_, _) => { result = true; dialog.Close(); };
        cancel.Click += (_, _) => { result = false; dialog.Close(); };

        await dialog.ShowDialog(owner);

        return result;
    }
}
