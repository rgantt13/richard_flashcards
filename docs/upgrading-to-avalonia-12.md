# Upgrading to Avalonia 12

This app pins Avalonia **11.3.15** and Semi.Avalonia **11.3.14**.

**The clipboard and drag-drop work is already done.** Avalonia 11.3 ships the new
`DataFormat` / `IDataTransfer` API alongside the old one and marks the old one obsolete;
this app is written against the new API, so `ClipboardImageService` and
`CardEditorView.axaml.cs` carry forward to 12 unchanged. What follows is what is left.

## Package versions

In `Directory.Packages.props`, bump the `Avalonia` entries to `12.1.1` and `Semi.Avalonia` to
`12.1.0.1`. Avalonia 12 requires .NET 10 or later, so `net11.0` is fine.

## Breaking changes that affect this code

**Clipboard, data formats and drag-drop.** Nothing to do — already on the new API. For reference,
what changed: `IClipboard.GetDataAsync(string)`/`GetFormatsAsync()` gave way to `TryGetDataAsync()`
returning `IAsyncDataTransfer` plus the `ClipboardExtensions` helpers (`TryGetBitmapAsync`,
`TryGetFilesAsync`, `GetDataFormatsAsync`); `DataFormats.*` became `DataFormat.*`; and
`DragEventArgs.Data` became `DragEventArgs.DataTransfer`, typed `IDataTransfer` — the synchronous
sibling, because a drop handler has to decide immediately whether it accepted the payload.

**Bindings.** `IBinding` is removed in favour of the `BindingBase` class, and `InstancedBinding`
becomes `BindingExpressionBase`. This app writes no custom bindings, so nothing to do. Compiled
bindings are on by default in 12, which this app already opts into via
`AvaloniaUseCompiledBindingsByDefault`.

**Data validation.** Validation moved onto the base `Control` class and the DataAnnotations plugin
is off by default. This app validates in the Application layer, so nothing to do.

**Window.** `ExtendClientAreaChromeHints` is removed in favour of `WindowDecorations`, and
`SystemDecorations` was renamed to `WindowDecorations`. Not used here.

**Bitmap.** `Bitmap.CopyPixels()` no longer takes an `AlphaFormat`; read it from
`ILockedFramebuffer.AlphaFormat` instead. Not used here — `ImageCache` only decodes.

## Practical order

1. Bump the package versions, build, and read the errors. There should be few — the APIs that
   generated obsolete warnings on 11.3 have already been migrated.
2. Run `Flashcards.Domain.Tests` and `Flashcards.Integration.Tests`. Neither references Avalonia,
   so a green run tells you the upgrade did not disturb anything below the UI.
3. Exercise image paste from a browser *and* from the Snipping Tool. Those are two different
   clipboard formats and it is easy to fix one and break the other.
4. If paste works from both, delete the raw-format fallback block in
   `ClipboardImageService.TryGetImageAsync` (step 2, clearly marked). It exists only to cover a
   backend that advertises an image format without offering to decode it.
