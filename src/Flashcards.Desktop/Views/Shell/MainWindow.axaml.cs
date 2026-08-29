using Avalonia.Controls;
using Avalonia.Interactivity;
using Flashcards.Desktop.ViewModels.Shell;

namespace Flashcards.Desktop.Views.Shell;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (DataContext is MainWindowViewModel viewModel)
        {
            // The first panel loads its data here rather than in the constructor: constructors
            // cannot await, and doing database work before the window is shown makes startup
            // look like a hang.
            await viewModel.InitializeAsync();
        }
    }
}
