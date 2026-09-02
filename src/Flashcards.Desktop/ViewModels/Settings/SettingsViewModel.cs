using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Contracts;
using Flashcards.Application.Settings.Commands;
using Flashcards.Application.Settings.Queries;
using Flashcards.Application.Stats.Commands;
using Flashcards.Desktop.Services;
using Flashcards.Desktop.ViewModels.Shared;
using Flashcards.Infrastructure;

namespace Flashcards.Desktop.ViewModels.Settings;

/// <summary>
/// Preferences, where the library lives, and the two destructive things worth being able to do.
/// <para>
/// Every control here changes something immediately and saves as it goes â there is no Apply
/// button. With four settings, a staged edit you can abandon is more machinery than the thing it
/// is protecting, and a theme you have to confirm is a theme you cannot preview.
/// </para>
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly IDispatcher _dispatcher;
    private readonly IShellService _shell;
    private readonly IDialogService _dialogs;

    /// <summary>Set while loading, so writing the controls back does not save what was just read.</summary>
    private bool _loading;

    public SettingsViewModel(IDispatcher dispatcher, IShellService shell, IDialogService dialogs)
    {
        _dispatcher = dispatcher;
        _shell = shell;
        _dialogs = dialogs;
    }

    [ObservableProperty]
    private ThemePreference _theme = ThemePreference.Dark;

    [ObservableProperty]
    private int _defaultCardCount = 20;

    [ObservableProperty]
    private bool _shuffleChoices = true;

    [ObservableProperty]
    private string? _dataDirectory;

    /// <summary>Set when FLASHCARDS_DATA_DIR moved the library for this run.</summary>
    [ObservableProperty]
    private bool _isDataDirectoryOverridden;

    /// <summary>Explains the folder above: the usual place, or the variable that changed it.</summary>
    public string DataDirectorySource => IsDataDirectoryOverridden
        ? $"Moved here for this run by the {StoragePaths.OverrideVariable} environment variable. "
          + "Clear it and restart to go back to the usual folder."
        : $"The usual place. Set {StoragePaths.OverrideVariable} to a folder path to run against a "
          + "different library — useful for trying something destructive without touching your real cards.";

    /// <summary>What the app reports about itself. Read from the assembly, not written down twice.</summary>
    public string Version { get; } = ReadVersion();

    public string RepositoryUrl => "https://github.com/rgantt13/richard_flashcards";

    public override Task ActivateAsync() => RunAsync(async () =>
    {
        _loading = true;

        try
        {
            var settings = await _dispatcher.QueryAsync(new GetSettingsQuery());

            Theme = settings.Theme;
            DefaultCardCount = settings.DefaultCardCount;
            ShuffleChoices = settings.ShuffleChoices;
            var location = await _dispatcher.QueryAsync(new GetDataLocationQuery());

            DataDirectory = location.Path;
            IsDataDirectoryOverridden = location.IsOverridden;
            OnPropertyChanged(nameof(DataDirectorySource));
        }
        finally
        {
            _loading = false;
        }
    });

    // ---- saving ------------------------------------------------------------

    partial void OnThemeChanged(ThemePreference value)
    {
        // Applied before it is saved, so the window repaints the moment you pick â the setting is
        // its own preview.
        _shell.ApplyTheme(value);
        Persist();
    }

    partial void OnDefaultCardCountChanged(int value) => Persist();

    partial void OnShuffleChoicesChanged(bool value) => Persist();

    /// <summary>
    /// Writes the whole record. Fire-and-forget on purpose: a settings write is a few hundred
    /// bytes to a local file, and making a slider await it would stutter while you drag.
    /// </summary>
    private void Persist()
    {
        if (_loading)
        {
            return;
        }

        _ = SaveAsync();
    }

    private async Task SaveAsync()
    {
        try
        {
            await _dispatcher.SendAsync(new SaveSettingsCommand(new AppSettings
            {
                Theme = Theme,
                DefaultCardCount = DefaultCardCount,
                ShuffleChoices = ShuffleChoices,
            }));

            StatusMessage = "Saved.";
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    // ---- actions -----------------------------------------------------------

    [RelayCommand]
    private void OpenDataFolder()
    {
        if (DataDirectory is { Length: > 0 } path)
        {
            _shell.OpenFolder(path);
        }
    }

    [RelayCommand]
    private void OpenRepository() => _shell.OpenUrl(RepositoryUrl);

    /// <summary>
    /// Wipes every answer ever recorded. Confirmed first, and worded so it is clear the cards
    /// survive â "clear history" reads like "delete everything" to anyone who has not thought
    /// about which of the two this app keeps.
    /// </summary>
    [RelayCommand]
    private Task ClearHistoryAsync() => RunAsync(async () =>
    {
        if (!await _dialogs.ConfirmAsync(
                "Forget every answer?",
                "Every percentage in the app goes back to zero and the whole library reads as never "
                + "practised. Your cards, subjects and images are untouched. This cannot be undone.",
                confirmText: "Forget everything"))
        {
            return;
        }

        var removed = await _dispatcher.SendAsync(new ClearAllHistoryCommand());

        StatusMessage = removed == 1
            ? "Forgot 1 answer."
            : $"Forgot {removed} answers.";
    });

    /// <summary>
    /// The informational version carries a build's commit after a '+', which is worth keeping for
    /// a bug report but not worth showing in the heading. Both halves are surfaced separately.
    /// </summary>
    private static string ReadVersion()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return "unknown";
        }

        var plus = informational.IndexOf('+');

        return plus < 0
            ? informational
            : $"{informational[..plus]}  Â·  build {informational[(plus + 1)..][..Math.Min(7, informational.Length - plus - 1)]}";
    }
}
