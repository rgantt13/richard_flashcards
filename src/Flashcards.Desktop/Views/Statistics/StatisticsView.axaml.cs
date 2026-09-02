using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Flashcards.Desktop.Views.Statistics;

public partial class StatisticsView : UserControl
{
    public StatisticsView()
    {
        InitializeComponent();

        // The panel opens at the top, and stays there.
        //
        // Each tier below holds a list that selects a row as it loads, and a selected row asks to
        // be brought into view. That request bubbles: the tier's own scroller acts on it, and then
        // the page's scroller acts on it too, dragging the whole page down past the library card
        // to show a list that was never out of sight. Marking it handled here — on the column
        // inside the page scroller, after every inner scroller has had its turn — stops the second
        // half of that without touching the first.
        Tiers.AddHandler(RequestBringIntoViewEvent, (_, e) => e.Handled = true);
    }
}
