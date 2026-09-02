using System.Diagnostics;
using Avalonia.Styling;
using Flashcards.Application.Contracts;

namespace Flashcards.Desktop.Services;

/// <summary>
/// The two things the settings panel needs that are neither a dialog nor a file picker: painting
/// the window in a different theme, and handing something to the operating system to open.
/// <para>
/// A seam rather than direct calls from the view model for the usual reason — <c>Application.Current</c>
/// and <c>Process.Start</c> are both untestable statics, and the theme is applied from two places
/// (startup and the panel) which would otherwise be two copies of the same mapping.
/// </para>
/// </summary>
public interface IShellService
{
    /// <summary>Repaints the window. Takes effect immediately; nothing needs restarting.</summary>
    void ApplyTheme(ThemePreference theme);

    /// <summary>Opens a folder in the system file browser.</summary>
    void OpenFolder(string path);

    void OpenUrl(string url);
}

public sealed class ShellService : IShellService
{
    public void ApplyTheme(ThemePreference theme)
    {
        if (App.Current is { } app)
        {
            // Default is Avalonia's "follow the OS", and it keeps following it — switching Windows
            // to light while the app is open repaints it, which is what System should mean.
            app.RequestedThemeVariant = theme switch
            {
                ThemePreference.Light => ThemeVariant.Light,
                ThemePreference.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
    }

    public void OpenFolder(string path)
    {
        if (Directory.Exists(path))
        {
            Launch(path);
        }
    }

    public void OpenUrl(string url) => Launch(url);

    /// <summary>
    /// UseShellExecute is the point: without it this would try to execute the path as a program
    /// rather than asking the OS to open it with whatever is registered.
    /// </summary>
    private static void Launch(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            // Nothing registered to handle it, or the shell refused. Not worth an error dialog
            // over a convenience button.
        }
    }
}
