using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Flashcards.Desktop.ViewModels.Design;
using Flashcards.Desktop.ViewModels.Manage;
using Flashcards.Desktop.ViewModels.Shared;
using Flashcards.Desktop.ViewModels.Statistics;
using Flashcards.Desktop.ViewModels.Study;

namespace Flashcards.Desktop.ViewModels.Shell;

/// <summary>
/// One entry in the sidebar.
/// <para>
/// <paramref name="IconKey"/> names a geometry in App.axaml rather than holding one, so the shell's
/// view model stays free of Avalonia types — the same arrangement the study mode catalogue uses for
/// its accent colours.
/// </para>
/// <para>
/// The line of description each entry used to carry is gone. "Search, edit and prune" under a
/// heading that already says Manage was explaining a word that needed no explaining, and the icon
/// now does the job in less room.
/// </para>
/// </summary>
public sealed record NavigationItem(string Key, string Title, string IconKey);

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
        QuizViewModel quiz,
        StatisticsViewModel statistics)
    {
        Editor = editor;
        Management = management;
        Quiz = quiz;
        Statistics = statistics;

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

    public StatisticsViewModel Statistics { get; }

    // Subjects lost its panel when it became a tag: you type one into the designer rather than
    // maintaining a list of them, and an unused tag retires itself.

    public IReadOnlyList<NavigationItem> Navigation { get; } =
    [
        new("quiz",   "Study",      "IconStudy"),
        new("editor", "Design",     "IconEdit"),
        new("manage", "Manage",     "IconManage"),
        new("stats",  "Statistics", "IconStats"),
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
            "stats" => Statistics,
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
