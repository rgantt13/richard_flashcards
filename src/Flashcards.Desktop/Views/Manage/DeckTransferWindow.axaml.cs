using Avalonia.Controls;
using Flashcards.Desktop.ViewModels.Manage;

namespace Flashcards.Desktop.Views.Manage;

public partial class DeckTransferWindow : Window
{
    public DeckTransferWindow()
    {
        InitializeComponent();

        // The view model says when it is finished and whether the user committed; the window's
        // only job is to carry that out. Same arrangement as SubjectCreateWindow.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is DeckTransferViewModel model)
            {
                model.Closed += (_, confirmed) => Close(confirmed);
            }
        };
    }
}
