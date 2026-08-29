using Flashcards.Application.Contracts;

namespace Flashcards.Application.Abstractions.Persistence;

/// <summary>
/// Binary storage for pasted images. Content-addressed: the same screenshot pasted onto ten cards
/// is stored once, because the key is the SHA-256 of the bytes.
/// </summary>
public interface IMediaStore
{
    Task<MediaDescriptor> SaveAsync(byte[] bytes, string? suggestedFileName, CancellationToken cancellationToken);

    Task<byte[]?> LoadAsync(Guid mediaId, CancellationToken cancellationToken);

    Task<MediaDescriptor?> DescribeAsync(Guid mediaId, CancellationToken cancellationToken);

    /// <summary>Deletes media rows and files no card references any more. Returns how many were removed.</summary>
    Task<int> CollectGarbageAsync(CancellationToken cancellationToken);
}
