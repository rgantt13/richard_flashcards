using Avalonia.Controls;
using Flashcards.Desktop.ViewModels.Subjects;

namespace Flashcards.Desktop.Views.Subjects;

public partial class SubjectCreateWindow : Window
{
    public SubjectCreateWindow()
    {
        InitializeComponent();

        // Opened with the seeded name selected, so a name carried over from the designer's box can
        // be typed straight over without reaching for the mouse.
        Opened += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };

        DataContextChanged += (_, _) =>
        {
            if (DataContext is SubjectCreateViewModel model)
            {
                model.Closed += (_, created) => Close(created);
            }
        };
    }
}
