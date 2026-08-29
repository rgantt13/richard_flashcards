namespace Flashcards.Application.Contracts;

/// <summary>One stored image, as the media store describes it back.</summary>
public sealed record MediaDescriptor(Guid Id, string FileName, string MimeType, long ByteSize, string Sha256);
