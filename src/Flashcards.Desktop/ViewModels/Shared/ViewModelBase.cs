using CommunityToolkit.Mvvm.ComponentModel;
using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Domain.Common;

namespace Flashcards.Desktop.ViewModels.Shared;

/// <summary>
/// Base for every view model. <see cref="ObservableObject"/> supplies INotifyPropertyChanged;
/// the <c>[ObservableProperty]</c> and <c>[RelayCommand]</c> source generators build the
/// boilerplate at compile time, so there is no runtime reflection and the generated members
/// are navigable in the IDE.
/// <para>
/// [WPF] This replaces the hand-rolled <c>BindableBase</c>/<c>DelegateCommand</c> pair you would
/// have written in .NET Framework, and the generated <c>*Command</c> properties are what XAML binds to.
/// </para>
/// </summary>
public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Runs work with the busy flag set and any failure surfaced rather than swallowed.</summary>
    protected async Task RunAsync(Func<Task> work, string? busyMessage = null)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = busyMessage;

        try
        {
            await work();
        }
        catch (ValidationException validation)
        {
            ErrorMessage = string.Join(Environment.NewLine, validation.Errors);
        }
        catch (DomainException domain)
        {
            ErrorMessage = domain.Message;
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;

            if (StatusMessage == busyMessage)
            {
                StatusMessage = null;
            }
        }
    }

    /// <summary>Called when the panel becomes visible. Panels reload here rather than in their constructor.</summary>
    public virtual Task ActivateAsync() => Task.CompletedTask;
}
