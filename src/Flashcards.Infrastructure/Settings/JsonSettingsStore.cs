using System.Text.Json;
using System.Text.Json.Serialization;
using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Application.Contracts;
using Flashcards.Infrastructure;

namespace Flashcards.Infrastructure.Settings;

/// <summary>
/// Preferences as a small JSON file beside the database.
/// <para>
/// Not a table. A settings row is not a domain concept, it would need a migration to add a field,
/// and it would ride along in any copy of the library — so somebody restoring a backup of their
/// cards would inherit the theme of whoever made it. A file you can open and read is also the
/// easier thing to fix when it goes wrong.
/// </para>
/// <para>
/// Reads never throw. A file that is missing, truncated by a bad shutdown, or hand-edited into
/// nonsense yields defaults, because losing your theme is not a reason to refuse to start.
/// </para>
/// </summary>
internal sealed class JsonSettingsStore(StoragePaths paths) : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    public DataLocation Location =>
        new(paths.RootDirectory, paths.IsOverridden, StoragePaths.OverrideVariable);

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(paths.SettingsPath))
            {
                return AppSettings.Default;
            }

            await using var stream = File.OpenRead(paths.SettingsPath);

            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, Options, cancellationToken)
                   ?? AppSettings.Default;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return AppSettings.Default;
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.RootDirectory);

        // Written to a temporary file and moved into place. A direct write that is interrupted
        // leaves a half-file, and the next read would silently fall back to defaults — losing
        // settings quietly is worse than the write failing loudly.
        var temporary = paths.SettingsPath + ".tmp";

        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, settings, Options, cancellationToken);
        }

        File.Move(temporary, paths.SettingsPath, overwrite: true);
    }
}
