using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Flashcards.Desktop.ViewModels.Design;
using Flashcards.Desktop.ViewModels.Manage;
using Flashcards.Desktop.ViewModels.Shared;
using Flashcards.Desktop.ViewModels.Study;

namespace Flashcards.Desktop.ViewModels.Shell;

public sealed record NavigationItem(string Key, string Title, string Glyph, string Description);

/// <summary>
/// The shell. Owns one instance of each panel view model and swaps which one the ContentControl
/// is bound to; the ViewLocator turns that into the right view.
/// <para>
/// Panels are kept alive rather than recreated so that, for example, your management filters
/// survive a trip to quiz mode and back. <see cref="ViewModelBase.ActivateAsync"/> is what
/// refreshes their data on the way in.
/// </para>
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel(
        CardEditorViewModel editor,
        ManagementViewModel management,
        QuizViewModel quiz)
    {
        Editor = editor;
        Management = management;
        Quiz = quiz;

        // Clicking Edit on a search result opens the designer loaded with that card.
        Management.EditRequested += async (_, id) =>
        {
            SelectedNavigation = Navigation.First(n => n.Key == "editor");
            await Editor.LoadCardAsync(id);
        };

        // Saving a card invalidates the management results and the tag counts.
        Editor.Saved += (_, _) => _ = Management.SearchCommand.ExecuteAsync(null);

        _currentPanel = Quiz;
        _selectedNavigation = Navigation[0];
    }

    public CardEditorViewModel Editor { get; }

    public ManagementViewModel Management { get; }

    public QuizViewModel Quiz { get; }

    // Subjects lost its panel when it became a tag: you type one into the designer rather than
    // maintaining a list of them, and an unused tag retires itself.

    public IReadOnlyList<NavigationItem> Navigation { get; } =
    [
        new("quiz", "Study", "", "Work through what is due"),
        new("editor", "Design", "", "Build a card"),
        new("manage", "Manage", "", "Search, edit and prune"),
    ];

    [ObservableProperty]
    private NavigationItem _selectedNavigation;

    [ObservableProperty]
    private ViewModelBase _currentPanel;

    public async Task InitializeAsync() => await ShowAsync(SelectedNavigation);

    private async Task ShowAsync(NavigationItem item)
    {
        var panel = item.Key switch
        {
            "editor" => (ViewModelBase)Editor,
            "manage" => Management,
            _ => Quiz,
        };

        CurrentPanel = panel;
        await panel.ActivateAsync();
    }

    [RelayCommand]
    private async Task NewCardAsync()
    {
        SelectedNavigation = Navigation.First(n => n.Key == "editor");
        await Editor.LoadSubjectsAsync();

        // Show the designer first, then ask — so the draft about to be discarded is on screen
        // while the question is being answered.
        await Editor.TryStartNewCardAsync();
    }

    partial void OnSelectedNavigationChanged(NavigationItem value) => _ = ShowAsync(value);
}
