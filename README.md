# Flashcards

A local desktop study app: build flashcards with mixed text, Markdown, code and pasted images,
then review them with SM-2 spaced repetition. Everything lives in one SQLite file on your machine —
no server, no account, no sync.

Built on .NET 11, Avalonia + Semi Design, Dapper over SQLite, Clean Architecture with CQRS.

---

## Getting it running

You need the **.NET 10 SDK**. `global.json` pins `10.0.0` with `rollForward: latestFeature`, so
any 10.0.x build works. .NET 10 is the LTS release; nothing in the code needs .NET 11, so moving
up later is a one-line change to `<TargetFramework>` in `Directory.Build.props`.

```powershell
cd C:\Users\rgant\source\repos\richard_flashcards

dotnet restore
dotnet build
dotnet test
dotnet run --project src\Flashcards.Desktop
```

First launch creates `%APPDATA%\RichardFlashcards\flashcards.db`, applies both migrations, and
seeds four sample cards across two subjects so nothing is empty.

Both test projects declare `<Using Include="Xunit" />`, so test files need no `using Xunit;` of
their own — xunit v2 ships no global usings and `ImplicitUsings` does not cover it.

### If restore fails

Every package version lives in one file, `Directory.Packages.props` (central package management —
individual `.csproj` files carry no `Version` attributes). If a version has moved on since this was
written, that is the only file to edit.

---

## The panels

**Study** — tick the subjects you want, set a session size, start. Question first; space or
Enter reveals; then 1/2/3/4 for Again / Hard / Good / Easy. Each button shows the interval it
would schedule. Multiple-choice cards score your selection before you grade yourself. "Again"
puts the card back at the end of the session queue.

**Create** — the card editor. Each side of a card is an ordered list of blocks, so a question can
be a Markdown paragraph, then a C# snippet, then a screenshot. Live preview on the right.

**Manage** — search by name or by anything written on either side of a card, filter by subject,
type, or due status, and sort. Per row: edit, forget (reset the schedule), suspend, delete.

**Subjects** — create, colour and delete subjects. Deleting one deletes its cards, and the
confirmation tells you how many.

---

## Card types and formats

**Formats** apply per block, and a side can mix as many as you like:

| Block | Notes |
|---|---|
| Text | Plain, wrapped |
| Markdown | `**bold**`, `*italic*`, `` `code` ``, `# headings`, `- bullets`, `1.` lists, `>` quotes |
| Code | Syntax highlighted for C#, SQL, JavaScript/TypeScript/JSON and Python; scrolls rather than wraps |
| Image | Paste, browse or drag-drop; per-image stretch (Uniform / UniformToFill / Fill / None) and max height |

**Types** control how a card is answered:

- **Standard** — question, answer, self-grade after flipping.
- **Multiple choice** — a list of options; tick every correct one. Two or more correct turns it
  into multi-select. Auto-scored, then the answer side is shown as the explanation.
- **Cloze** — wrap words in double braces to hide them: `The capital of France is {{Paris}}.`
  Add a hint after a double colon: `{{Paris::a city}}`. Select text and press **Blank** to wrap it.
  Blanks are numbered across every question-side block.

### Pasting images

Clipboard behaviour on Windows depends entirely on the source app: a browser writes a `PNG` blob,
the Snipping Tool and most native apps write `CF_DIB` (a BMP with its 14-byte file header stripped).
`ClipboardImageService` asks Avalonia for a decoded bitmap first, which handles both. If a backend
advertises a raw image format without offering to decode it, a clearly-marked fallback fetches the
bytes directly and reattaches the BMP header itself. Failing that, a copied file path is read.
Failing everything, use **Browse…** or drag a file onto the block.

Images are content-addressed by SHA-256: the same screenshot on ten cards is stored once. Orphaned
files are swept when cards or subjects are deleted.

---

## Layout

```
src/
  Flashcards.Domain           entities, invariants, the SM-2 algorithm — references nothing
  Flashcards.Application      CQRS dispatcher, commands, queries, repository interfaces, DTOs
  Flashcards.Infrastructure   Dapper repositories, SQLite migrations, media store
  Flashcards.Desktop          Avalonia views, view models, custom renderers
tests/
  Flashcards.Domain.Tests       pure unit tests — no database, no clock, no UI
  Flashcards.Integration.Tests  the real container against a throwaway SQLite file
docs/
  architecture.md               how the layers fit together and why
  sqlite-vs-tsql.md             the dialect guide
  upgrading-to-avalonia-12.md   what breaks and where, when you want to move
```

---

## Reading it

The SQL is commented for someone coming from T-SQL — every notable difference is marked `[T-SQL]`
in the source, with the same ground covered systematically in `docs/sqlite-vs-tsql.md`. The three
that cause the most grief:

1. `PRAGMA foreign_keys = ON` is per connection and off by default, so cascades are inert without
   it (`SqliteConnectionFactory`).
2. There is no `MERGE`; `INSERT ... ON CONFLICT DO UPDATE` with the `excluded` pseudo-table is the
   idiom (`ReviewStateRepository.UpsertAsync`).
3. Guids and timestamps have no native types and must be pinned to TEXT explicitly, or
   `Microsoft.Data.Sqlite` writes Guids as BLOBs that compare unequal to everything else
   (`SqlMappings`).

For the architecture, start with `docs/architecture.md`, then `Dispatcher.cs` — the reflection that
makes CQRS feel magical is about sixty readable lines.

---

## Where your data is

| | |
|---|---|
| Database | `%APPDATA%\RichardFlashcards\flashcards.db` |
| Images | `%APPDATA%\RichardFlashcards\media\` |

Backing up is copying that folder. WAL mode means there may also be `-wal` and `-shm` files
alongside the database; copy them too, or close the app first.

---

## Known rough edges

- **Not fully compile-verified.** This was written without a .NET SDK available. The SQL *was*
  executed against a real SQLite 3.45 — schema, triggers, cascades, search, quiz-queue and stats
  queries all run correctly — and the CF_DIB reconstruction in `ClipboardImageService` was verified
  byte-for-byte against 24bpp, 8bpp-with-palette and BI_BITFIELDS layouts. Expect the occasional
  remaining typo rather than a structural problem.
- **Semi.Avalonia is referenced as `<semi:SemiTheme />`**, not as an `avares://` StyleInclude.
  Avalonia compiles `StyleInclude` ahead of time, so an avares path that does not resolve is a
  build error, not a runtime one. The seven `SemiColor*` brushes the views use are verified
  against the 11.3.14 palette; because they are `DynamicResource`, a key that ever went missing
  would degrade to an unstyled surface rather than throwing.
- No import/export yet. The obvious next feature is Anki `.apkg` or plain CSV.
- Statistics are a single summary row. A review-history heatmap would be a good use of the
  `review_log` table, which already records everything needed.
