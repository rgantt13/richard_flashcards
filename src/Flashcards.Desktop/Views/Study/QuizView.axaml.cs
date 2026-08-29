using Avalonia.Controls;
using Avalonia.Input;
using Flashcards.Desktop.ViewModels.Study;

namespace Flashcards.Desktop.Views.Study;

public partial class QuizView : UserControl
{
    /// <summary>
    /// Whether a Ctrl press so far has been a press of Ctrl and nothing else.
    /// <para>
    /// Ctrl on its own marks a card wrong, but Ctrl is also the front half of every shortcut on the
    /// machine. Acting on the key going <em>down</em> would mark the card the instant somebody
    /// started Ctrl+C. So the mark happens on release, and only if no other key was struck in
    /// between — the difference between tapping Ctrl and holding it.
    /// </para>
    /// </summary>
    private bool _ctrlAlone;

    public QuizView()
    {
        InitializeComponent();

        // Keyboard marking: space reveals, then 1 or 2 marks it. This is the difference between
        // a study app you use and one you abandon.
        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnKeyUp, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Focusable = true;
    }

    private static bool IsCtrl(Key key) => key is Key.LeftCtrl or Key.RightCtrl;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not QuizViewModel viewModel || !viewModel.IsStudying)
        {
            return;
        }

        // Never steal keys from a focused text input.
        if (e.Source is TextBox)
        {
            return;
        }

        if (IsCtrl(e.Key))
        {
            // Arm on the first Ctrl down. Held keys repeat, so only the transition counts.
            _ctrlAlone = viewModel.IsAnswerVisible;
            return;
        }

        // Anything else pressed means this was a chord, not a tap.
        _ctrlAlone = false;

        if (!viewModel.IsAnswerVisible)
        {
            if (e.Key is Key.Space or Key.Enter)
            {
                viewModel.RevealCommand.Execute(null);
                e.Handled = true;
            }

            return;
        }

        // 1 = missed, 2 = got it. Space and Enter carry on meaning "the expected thing", which
        // once the answer is showing is marking it correct.
        var answer = e.Key switch
        {
            Key.D1 or Key.NumPad1 => "false",
            Key.D2 or Key.NumPad2 or Key.Space or Key.Enter => "true",
            _ => null,
        };

        if (answer is not null && viewModel.AnswerCommand.CanExecute(answer))
        {
            viewModel.AnswerCommand.Execute(answer);
            e.Handled = true;
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (!IsCtrl(e.Key))
        {
            return;
        }

        var tapped = _ctrlAlone;
        _ctrlAlone = false;

        if (!tapped
            || e.Source is TextBox
            || DataContext is not QuizViewModel viewModel
            || !viewModel.IsStudying
            || !viewModel.IsAnswerVisible)
        {
            return;
        }

        if (viewModel.AnswerCommand.CanExecute("false"))
        {
            viewModel.AnswerCommand.Execute("false");
            e.Handled = true;
        }
    }
}
