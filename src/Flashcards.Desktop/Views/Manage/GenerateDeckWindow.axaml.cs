using Avalonia.Controls;
using Flashcards.Desktop.ViewModels.Manage;

namespace Flashcards.Desktop.Views.Manage;

public partial class GenerateDeckWindow : Window
{
    public GenerateDeckWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is not GenerateDeckViewModel model)
            {
                return;
            }

            model.Closed += (_, _) => Close();

            // The clipboard hangs off the TopLevel, which only a Visual can reach — so the window
            // supplies it as a delegate rather than the view model taking a dependency on Avalonia.
            // Same arrangement the card editor uses for its image providers.
            model.CopyToClipboard = async text =>
            {
                if (GetTopLevel(this)?.Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(text);
                }
            };
        };
    }
}
