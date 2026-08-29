using Avalonia;

namespace Flashcards.Desktop;

internal static class Program
{
    // Avalonia needs this to be the very first thing that runs, before any Avalonia type is
    // touched — hence no async Main and no DI work up here. Composition happens in App.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
