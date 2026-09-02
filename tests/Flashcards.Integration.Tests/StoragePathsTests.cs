using Flashcards.Infrastructure;
using Shouldly;

namespace Flashcards.Integration.Tests;

/// <summary>
/// Where the library lives, and what the FLASHCARDS_DATA_DIR override does to it.
/// <para>
/// These go through <see cref="StoragePaths.Resolve"/> rather than setting the environment
/// variable, deliberately. An environment variable is process-wide: setting one here would leak
/// into every other test in the run and into whatever the runner does next, and the ordering
/// would decide whether it mattered.
/// </para>
/// </summary>
public sealed class StoragePathsTests
{
    private static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RichardFlashcards");

    [Fact]
    public void With_nothing_set_the_library_lives_in_the_per_user_folder()
    {
        var paths = StoragePaths.Resolve(null);

        paths.RootDirectory.ShouldBe(DefaultRoot);
        paths.IsOverridden.ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_value_is_treated_as_unset(string value)
    {
        var paths = StoragePaths.Resolve(value);

        paths.RootDirectory.ShouldBe(DefaultRoot);
        paths.IsOverridden.ShouldBeFalse();
    }

    [Fact]
    public void An_absolute_path_moves_the_whole_library_together()
    {
        var target = Path.Combine(Path.GetTempPath(), "flashcards-override-test");

        var paths = StoragePaths.Resolve(target);

        paths.IsOverridden.ShouldBeTrue();
        paths.RootDirectory.ShouldBe(Path.GetFullPath(target));

        // The point of one root: the database, the images and the preferences move as a set, so a
        // throwaway library cannot half-share anything with the real one.
        paths.DatabasePath.ShouldStartWith(paths.RootDirectory);
        paths.MediaDirectory.ShouldStartWith(paths.RootDirectory);
        paths.SettingsPath.ShouldStartWith(paths.RootDirectory);
    }

    [Fact]
    public void A_relative_path_is_made_absolute()
    {
        var paths = StoragePaths.Resolve("decks");

        Path.IsPathRooted(paths.RootDirectory).ShouldBeTrue();
        paths.IsOverridden.ShouldBeTrue();
    }

    [Fact]
    public void Environment_variables_inside_the_value_are_expanded()
    {
        var paths = StoragePaths.Resolve(Path.Combine("%USERPROFILE%", "flashcards-test"));

        paths.RootDirectory.ShouldNotContain("%USERPROFILE%");
        paths.RootDirectory.ShouldEndWith("flashcards-test");
    }

    /// <summary>
    /// A value that cannot be a path falls back rather than refusing to start. That is only safe
    /// because the settings panel shows which folder is actually in use, so a typo is visible.
    /// </summary>
    [Fact]
    public void A_value_that_cannot_be_a_path_falls_back_to_the_default()
    {
        var paths = StoragePaths.Resolve("\0not a path\0");

        paths.RootDirectory.ShouldBe(DefaultRoot);
        paths.IsOverridden.ShouldBeFalse();
    }
}
