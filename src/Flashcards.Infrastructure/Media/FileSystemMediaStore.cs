using Flashcards.Infrastructure.Persistence.Rows;
using System.Security.Cryptography;
using Dapper;
using Flashcards.Application.Abstractions.Persistence;
using Flashcards.Application.Contracts;
using Flashcards.Infrastructure.Persistence;

namespace Flashcards.Infrastructure.Media;

/// <summary>
/// Content-addressed image storage: bytes go on disk under their SHA-256, metadata goes in the
/// <c>media</c> table.
/// <para>
/// Why not a BLOB column? SQLite is genuinely fast at small blobs — faster than the filesystem
/// under about 100 KB, per SQLite's own benchmarks — but screenshots are routinely 500 KB to 2 MB.
/// Keeping them out of the database keeps the file small enough to copy around, and keeps every
/// <c>SELECT *</c> you write while debugging from dragging megabytes into memory.
/// </para>
/// </summary>
internal sealed class FileSystemMediaStore(DbSession session, string mediaDirectory) : IMediaStore
{
    public async Task<MediaDescriptor> SaveAsync(byte[] bytes, string? suggestedFileName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length == 0)
        {
            throw new InvalidOperationException("Refusing to store an empty image.");
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var connection = await session.GetConnectionAsync(cancellationToken);

        // Already stored? Then this is the same screenshot pasted twice — reuse the row.
        var existing = await connection.QuerySingleOrDefaultAsync<MediaRow>(new CommandDefinition(
            """
            SELECT id AS Id, file_name AS FileName, mime_type AS MimeType,
                   byte_size AS ByteSize, sha256 AS Sha256
            FROM   media
            WHERE  sha256 = @Hash;
            """,
            new { Hash = hash }, session.DbTransaction, cancellationToken: cancellationToken));

        if (existing is not null)
        {
            return new MediaDescriptor(existing.Id, existing.FileName, existing.MimeType, existing.ByteSize, existing.Sha256);
        }

        var (mime, extension) = DetectFormat(bytes);
        var id = Guid.CreateVersion7();
        var fileName = SanitizeFileName(suggestedFileName) ?? $"image{extension}";

        Directory.CreateDirectory(mediaDirectory);
        await File.WriteAllBytesAsync(Path.Combine(mediaDirectory, hash + extension), bytes, cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO media (id, file_name, mime_type, byte_size, sha256, created_utc)
            VALUES (@Id, @FileName, @MimeType, @ByteSize, @Sha256, @CreatedUtc);
            """,
            new
            {
                Id = id,
                FileName = fileName,
                MimeType = mime,
                ByteSize = (long)bytes.Length,
                Sha256 = hash,
                CreatedUtc = DateTimeOffset.UtcNow,
            },
            session.DbTransaction, cancellationToken: cancellationToken));

        return new MediaDescriptor(id, fileName, mime, bytes.Length, hash);
    }

    public async Task<byte[]?> LoadAsync(Guid mediaId, CancellationToken cancellationToken)
    {
        var descriptor = await DescribeAsync(mediaId, cancellationToken);

        if (descriptor is null)
        {
            return null;
        }

        var path = ResolvePath(descriptor.Sha256, descriptor.MimeType);

        return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
    }

    public async Task<MediaDescriptor?> DescribeAsync(Guid mediaId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<MediaRow>(new CommandDefinition(
            """
            SELECT id AS Id, file_name AS FileName, mime_type AS MimeType,
                   byte_size AS ByteSize, sha256 AS Sha256
            FROM   media
            WHERE  id = @Id;
            """,
            new { Id = mediaId }, session.DbTransaction, cancellationToken: cancellationToken));

        return row is null ? null : new MediaDescriptor(row.Id, row.FileName, row.MimeType, row.ByteSize, row.Sha256);
    }

    public async Task<int> CollectGarbageAsync(CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);

        // NOT EXISTS rather than NOT IN: card_blocks.media_id and card_choices.media_id are both
        // nullable, and `NOT IN` over a set containing a single NULL evaluates to NULL for every
        // row — deleting nothing, silently, forever. That three-valued-logic trap is identical in
        // T-SQL and bites people just as often there.
        //
        // Both referencing tables have to be checked. An image used only as a multiple-choice
        // answer has no card_blocks row at all, so testing blocks alone would collect it while it
        // is still on screen.
        var orphans = (await connection.QueryAsync<MediaRow>(new CommandDefinition(
            """
            SELECT id AS Id, file_name AS FileName, mime_type AS MimeType,
                   byte_size AS ByteSize, sha256 AS Sha256
            FROM   media m
            WHERE  NOT EXISTS (SELECT 1 FROM card_blocks  b WHERE b.media_id = m.id)
              AND  NOT EXISTS (SELECT 1 FROM card_choices c WHERE c.media_id = m.id);
            """,
            transaction: session.DbTransaction, cancellationToken: cancellationToken))).ToList();

        if (orphans.Count == 0)
        {
            return 0;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM media WHERE id IN @Ids;",
            new { Ids = orphans.Select(o => o.Id).ToArray() },
            session.DbTransaction, cancellationToken: cancellationToken));

        foreach (var orphan in orphans)
        {
            var path = ResolvePath(orphan.Sha256, orphan.MimeType);

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // The row is gone; a locked file will be swept on the next pass.
            }
        }

        return orphans.Count;
    }

    private string ResolvePath(string sha256, string mimeType)
        => Path.Combine(mediaDirectory, sha256 + ExtensionFor(mimeType));

    private static string ExtensionFor(string mimeType) => mimeType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/bmp" => ".bmp",
        "image/webp" => ".webp",
        _ => ".bin",
    };

    /// <summary>Sniffs the format from magic bytes rather than trusting a clipboard-supplied name.</summary>
    private static (string Mime, string Extension) DetectFormat(byte[] bytes)
    {
        static bool StartsWith(byte[] data, params byte[] prefix)
            => data.Length >= prefix.Length && data.AsSpan(0, prefix.Length).SequenceEqual(prefix);

        if (StartsWith(bytes, 0x89, 0x50, 0x4E, 0x47)) { return ("image/png", ".png"); }
        if (StartsWith(bytes, 0xFF, 0xD8, 0xFF)) { return ("image/jpeg", ".jpg"); }
        if (StartsWith(bytes, 0x47, 0x49, 0x46, 0x38)) { return ("image/gif", ".gif"); }
        if (StartsWith(bytes, 0x42, 0x4D)) { return ("image/bmp", ".bmp"); }

        if (bytes.Length > 12
            && StartsWith(bytes, 0x52, 0x49, 0x46, 0x46)
            && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            return ("image/webp", ".webp");
        }

        return ("application/octet-stream", ".bin");
    }

    private static string? SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var cleaned = new string([.. name.Where(c => !Path.GetInvalidFileNameChars().Contains(c))]).Trim();

        return string.IsNullOrEmpty(cleaned) ? null : cleaned[..Math.Min(cleaned.Length, 120)];
    }

}
