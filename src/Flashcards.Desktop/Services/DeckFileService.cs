using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Flashcards.Application.Transfer;

namespace Flashcards.Desktop.Services;

/// <summary>A deck file the user chose, already read.</summary>
public sealed record DeckFile(string Name, byte[] Bytes);

/// <summary>
/// Getting a deck file off disk and back onto it.
/// <para>
/// Separate from <see cref="IDialogService"/> because it is a different kind of thing: that one
/// builds windows out of Avalonia primitives, this one talks to the platform's file pickers. Both
/// exist so the view models can stay free of Avalonia types.
/// </para>
/// </summary>
public interface IDeckFileService
{
    /// <summary>Asks for a deck and reads it. Null means the user backed out.</summary>
    Task<DeckFile?> OpenAsync();

    /// <summary>
    /// Asks where to write a deck and writes it. Returns the name it was saved under, or null if
    /// the user backed out.
    /// </summary>
    Task<string?> SaveAsync(string suggestedName, byte[] bytes);
}

public sealed class DeckFileService : IDeckFileService
{
    private static readonly FilePickerFileType DeckFiles = new("Flashcards deck")
    {
        // The extension is a convention, not a gate — Read parses whatever it is handed and says
        // so if it is not a deck. JSON is offered alongside because that is what the file is.
        Patterns = ["*" + DeckSerializer.FileExtension, "*.json"],
        MimeTypes = ["application/json"],
    };

    public async Task<DeckFile?> OpenAsync()
    {
        if (Storage is not { } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a deck to import",
            AllowMultiple = false,
            FileTypeFilter = [DeckFiles, FilePickerFileTypes.All],
        });

        if (files.Count == 0)
        {
            return null;
        }

        await using var stream = await files[0].OpenReadAsync();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);

        return new DeckFile(files[0].Name, buffer.ToArray());
    }

    public async Task<string?> SaveAsync(string suggestedName, byte[] bytes)
    {
        if (Storage is not { } storage)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the deck",
            SuggestedFileName = suggestedName,
            DefaultExtension = DeckSerializer.FileExtension.TrimStart('.'),
            FileTypeChoices = [DeckFiles],
            ShowOverwritePrompt = true,
        });

        if (file is null)
        {
            return null;
        }

        // Truncated explicitly: writing over a longer file without it would leave the tail of the
        // old one behind, and the result would still parse as far as the closing brace.
        await using var stream = await file.OpenWriteAsync();
        stream.SetLength(0);
        await stream.WriteAsync(bytes);

        return file.Name;
    }

    /// <summary>
    /// The storage provider hangs off the top level, so this reaches for the main window the same
    /// way <see cref="DialogService"/> reaches for a dialog owner.
    /// </summary>
    private static IStorageProvider? Storage
        => (App.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is { } window
            ? TopLevel.GetTopLevel(window)?.StorageProvider
            : null;
}
