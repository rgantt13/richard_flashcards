using System.Buffers.Binary;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace Flashcards.Desktop.Services;

public sealed record PastedImage(byte[] Bytes, string? SuggestedFileName);

public interface IClipboardImageService
{
    /// <summary>Pulls an image off the system clipboard, or null if there is not one.</summary>
    Task<PastedImage?> TryGetImageAsync(Visual anchor);

    /// <summary>Opens the platform file picker for an image.</summary>
    Task<PastedImage?> PickImageAsync(Visual anchor);

    /// <summary>True when a drag payload carries something we could turn into an image block.</summary>
    bool CanAccept(DragEventArgs args);

    /// <summary>Reads an image out of a drag-and-drop payload.</summary>
    Task<PastedImage?> TryGetDroppedImageAsync(DragEventArgs args);
}

/// <summary>
/// Everything platform-specific about getting image bytes into the app lives here.
/// <para>
/// This is written against Avalonia's <c>DataFormat</c> / <c>IDataTransfer</c> API rather than the
/// older <c>GetDataAsync(string)</c> / <c>IDataObject</c> pair, which is obsolete in 11.3 and
/// removed in 12. Two things follow from that. It compiles warning-free today, and the whole file
/// carries forward to Avalonia 12 unchanged.
/// </para>
/// <para>
/// It also got a lot shorter. What lands on the Windows clipboard depends entirely on the source
/// app — a browser writes a PNG blob, the Snipping Tool and most native apps write CF_DIB, which
/// is a BMP with its 14-byte file header sliced off. That negotiation now happens inside the
/// platform backend behind <c>TryGetBitmapAsync</c>, so the header surgery this file used to do
/// by hand is gone - it survives only as a clearly-marked fallback for backends that advertise a
/// raw image format without offering to decode it.
/// </para>
/// </summary>
public sealed class ClipboardImageService : IClipboardImageService
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"];

    /// <summary>Raw platform format names a PNG blob may be advertised under.</summary>
    private static readonly string[] PngFormatNames = ["PNG", "image/png", "public.png", "image/x-png"];

    /// <summary>
    /// Raw platform format names CF_DIB may be advertised under. Windows only names *registered*
    /// clipboard formats, so standard ones like CF_DIB (id 8) surface under whatever fallback
    /// spelling the backend picks.
    /// </summary>
    private static readonly string[] DibFormatNames =
        ["DeviceIndependentBitmap", "DIB", "CF_DIB", "Format8", "Unknown_Format_8", "image/bmp"];

    public async Task<PastedImage?> TryGetImageAsync(Visual anchor)
    {
        // The clipboard hangs off the TopLevel, which is why this takes a Visual rather than
        // being callable from a view model.
        var clipboard = TopLevel.GetTopLevel(anchor)?.Clipboard;

        if (clipboard is null)
        {
            return null;
        }

        // 1. A real image on the clipboard, whatever wire format the source app chose.
        try
        {
            if (await clipboard.TryGetBitmapAsync() is { } bitmap)
            {
                using (bitmap)
                {
                    return new PastedImage(Encode(bitmap), "pasted.png");
                }
            }
        }
        catch (Exception)
        {
            // A clipboard owned by a hung process can throw or stall. Fall through to files.
        }

        // 2. Insurance. If a backend advertises a raw image format but declines to convert it to a
        //    Bitmap, go and fetch the bytes directly. Delete this block if step 1 proves reliable
        //    everywhere you use the app - it exists because Windows clipboard behaviour varies by
        //    source application, not because the framework is expected to fall short.
        try
        {
            var formats = await clipboard.GetDataFormatsAsync();

            if (await TryReadRawAsync(clipboard, formats, PngFormatNames) is { Length: > 0 } png)
            {
                return new PastedImage(png, "pasted.png");
            }

            if (await TryReadRawAsync(clipboard, formats, DibFormatNames) is { Length: > 40 } dib
                && DibToBmp(dib) is { } bmp)
            {
                return new PastedImage(bmp, "pasted.bmp");
            }
        }
        catch (Exception)
        {
            // Fall through to files.
        }

        // 3. Copying a file in Explorer puts its path on the clipboard, not its contents.
        try
        {
            if (await clipboard.TryGetFilesAsync() is { Length: > 0 } files)
            {
                return await ReadFirstImageAsync(files);
            }
        }
        catch (Exception)
        {
            // Nothing usable.
        }

        return null;
    }

    /// <summary>
    /// Asks the clipboard for the first of <paramref name="candidates"/> it actually advertises,
    /// as raw bytes. Matching on <see cref="DataFormat.Identifier"/> rather than constructing a
    /// format blind avoids asking a backend for something it never offered.
    /// </summary>
    private static async Task<byte[]?> TryReadRawAsync(
        IClipboard clipboard,
        IReadOnlyList<DataFormat> formats,
        string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!formats.Any(f => string.Equals(f.Identifier, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var bytes = await clipboard.TryGetValueAsync(DataFormat.CreateBytesPlatformFormat(candidate));

            if (bytes is { Length: > 0 })
            {
                return bytes;
            }
        }

        return null;
    }

    /// <summary>
    /// CF_DIB is a BMP file minus its 14-byte BITMAPFILEHEADER. Reattaching one turns it back into
    /// something any decoder will open.
    /// <para>
    /// Layout: 'BM' | total size (4) | reserved (4) | offset to pixel data (4). The offset is
    /// 14 + header size + palette size, and the palette only exists at 8 bits per pixel and below -
    /// except under BI_BITFIELDS, where three 4-byte channel masks sit where the palette would.
    /// </para>
    /// </summary>
    private static byte[]? DibToBmp(byte[] dib)
    {
        if (dib.Length < 40)
        {
            return null;
        }

        var span = dib.AsSpan();
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(span[..4]);

        // 40 = BITMAPINFOHEADER, 108 = V4, 124 = V5. Anything else is not a DIB we understand.
        if (headerSize is not (40 or 52 or 56 or 108 or 124) || dib.Length < headerSize)
        {
            return null;
        }

        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(14, 2));
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(16, 4));
        var colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(32, 4));

        uint paletteBytes = 0;

        if (bitCount <= 8)
        {
            var entries = colorsUsed != 0 ? colorsUsed : 1u << bitCount;
            paletteBytes = entries * 4;
        }
        else if (compression == 3 && headerSize == 40)
        {
            // BI_BITFIELDS with the plain header: three DWORD channel masks follow it.
            paletteBytes = 12;
        }

        var offset = 14 + headerSize + paletteBytes;

        if (offset > dib.Length)
        {
            return null;
        }

        var bmp = new byte[14 + dib.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2, 4), (uint)bmp.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(6, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10, 4), offset);
        dib.CopyTo(bmp, 14);

        return bmp;
    }

    public async Task<PastedImage?> PickImageAsync(Visual anchor)
    {
        var storage = TopLevel.GetTopLevel(anchor)?.StorageProvider;

        if (storage is null)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose an image",
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });

        return files.Count == 0 ? null : await ReadFileAsync(files[0]);
    }

    public bool CanAccept(DragEventArgs args)
        => args.DataTransfer.Contains(DataFormat.File) || args.DataTransfer.Contains(DataFormat.Bitmap);

    public async Task<PastedImage?> TryGetDroppedImageAsync(DragEventArgs args)
    {
        // Drag-and-drop uses the synchronous IDataTransfer: a drop handler has to decide
        // immediately whether it accepted the payload, so there is nothing to await.
        var transfer = args.DataTransfer;

        try
        {
            if (transfer.TryGetBitmap() is { } bitmap)
            {
                using (bitmap)
                {
                    return new PastedImage(Encode(bitmap), "dropped.png");
                }
            }
        }
        catch (Exception)
        {
            // Fall through to files.
        }

        var files = transfer.TryGetFiles();

        return files is null ? null : await ReadFirstImageAsync(files);
    }

    /// <summary>
    /// Re-encodes a decoded bitmap back to bytes for storage. Avalonia's Skia backend writes PNG.
    /// Even if that ever changed, <c>FileSystemMediaStore</c> sniffs the magic bytes rather than
    /// trusting the extension, so the file would still be stored and served correctly.
    /// </summary>
    private static byte[] Encode(Bitmap bitmap)
    {
        using var buffer = new MemoryStream();
        bitmap.Save(buffer);

        return buffer.ToArray();
    }

    private static async Task<PastedImage?> ReadFirstImageAsync(IEnumerable<IStorageItem> items)
    {
        foreach (var item in items)
        {
            if (item is not IStorageFile file)
            {
                continue;
            }

            var extension = Path.GetExtension(file.Name);

            if (ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
                && await ReadFileAsync(file) is { } image)
            {
                return image;
            }
        }

        return null;
    }

    private static async Task<PastedImage?> ReadFileAsync(IStorageFile file)
    {
        try
        {
            await using var stream = await file.OpenReadAsync();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);

            return new PastedImage(buffer.ToArray(), file.Name);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
