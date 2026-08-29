using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Flashcards.Application.Abstractions.Messaging;
using Flashcards.Application.Media.Queries;

namespace Flashcards.Desktop.Services;

public interface IImageCache
{
    Task<Bitmap?> GetAsync(Guid mediaId, CancellationToken cancellationToken = default);

    void Put(Guid mediaId, byte[] bytes);

    void Clear();
}

/// <summary>
/// Decoded-bitmap cache keyed by media id.
/// <para>
/// This exists because <see cref="Bitmap"/> is a GPU/native resource, not a POCO. Re-decoding a
/// 2 MB screenshot every time a list virtualises a row back into view is exactly the kind of thing
/// that makes a desktop app feel sludgy. One decode, held for the session.
/// </para>
/// </summary>
public sealed class ImageCache(IDispatcher dispatcher) : IImageCache
{
    private readonly ConcurrentDictionary<Guid, Bitmap?> _cache = new();

    public async Task<Bitmap?> GetAsync(Guid mediaId, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(mediaId, out var cached))
        {
            return cached;
        }

        var bytes = await dispatcher.QueryAsync(new LoadMediaQuery(mediaId), cancellationToken);

        var bitmap = Decode(bytes);
        _cache[mediaId] = bitmap;

        return bitmap;
    }

    public void Put(Guid mediaId, byte[] bytes) => _cache[mediaId] = Decode(bytes);

    public void Clear()
    {
        foreach (var bitmap in _cache.Values)
        {
            bitmap?.Dispose();
        }

        _cache.Clear();
    }

    private static Bitmap? Decode(byte[]? bytes)
    {
        if (bytes is null or { Length: 0 })
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            // A corrupt or unsupported file should show the alt text, not take the app down.
            return null;
        }
    }
}
