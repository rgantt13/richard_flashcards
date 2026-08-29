using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Flashcards.Desktop.ViewModels.Shared;

namespace Flashcards.Desktop;

/// <summary>
/// Convention-based view resolution: <c>Flashcards.Desktop.ViewModels.Study.QuizViewModel</c>
/// renders as <c>Flashcards.Desktop.Views.Study.QuizView</c>. Registered in App.axaml as an
/// application-level DataTemplate, so any ContentControl bound to a view model just works.
/// <para>
/// [WPF] Same idea as the implicit <c>DataTemplate DataType="{x:Type vm:FooViewModel}"</c> you
/// would put in App.xaml resources, minus one entry per view model.
/// </para>
/// <para>
/// Since the panels moved into folders of their own, the swap below has a sub-namespace to carry
/// across as well — <c>ViewModels.Study</c> to <c>Views.Study</c> — which holds only while a view
/// model and its view sit in matching folders. That is the arrangement, but it is a convention
/// rather than something the compiler checks, so a failed swap falls back to searching the
/// assembly by type name instead of rendering an error where a screen should be.
/// </para>
/// </summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control Build(object? param)
    {
        if (param is null)
        {
            return new TextBlock { Text = "No view model." };
        }

        var viewModelType = param.GetType();

        var expected = viewModelType.FullName!
            .Replace(".ViewModels.", ".Views.", StringComparison.Ordinal)
            .Replace("ViewModel", "View", StringComparison.Ordinal);

        var type = Type.GetType(expected) ?? FindByName(expected);

        return type is not null
            ? (Control)Activator.CreateInstance(type)!
            : new TextBlock { Text = $"View not found: {expected}" };
    }

    /// <summary>
    /// Last resort when the view is not where the convention says it should be — a view model and
    /// its view filed under different panels. Matches on the short name alone, which is unique
    /// across this assembly.
    /// </summary>
    private static Type? FindByName(string expectedFullName)
    {
        var shortName = expectedFullName[(expectedFullName.LastIndexOf('.') + 1)..];

        return Assembly.GetExecutingAssembly()
            .GetTypes()
            .FirstOrDefault(t => t.Name == shortName && typeof(Control).IsAssignableFrom(t));
    }

    public bool Match(object? data) => data is ViewModelBase;
}
