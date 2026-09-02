# Flashcards

A local desktop study app: write cards mixing text, Markdown, code, pasted images and freehand ink,
file them under a tree of subjects, and study them however you feel like. Everything lives in one
folder on your machine — no server, no account, no sync.

Built on .NET 10, Avalonia 11 + Semi Design, Dapper over SQLite, Clean Architecture with CQRS.

**Nothing is scheduled.** There is no due date, no ease factor and no algorithm deciding what you
owe it today. You pick a way of studying, it draws cards, and it keeps an honest record of how you
did. That was a deliberate removal — see [Study modes](#study-modes).

---

## Getting it running

You need the **.NET 10 SDK**. `global.json` pins `10.0.0` with `rollForward: latestFeature`, so any
10.0.x build works.

```powershell
cd C:\Users\rgant\source\repos\richard_flashcards

dotnet restore
dotnet build
dotnet test
dotnet run --project src\Flashcards.Desktop
```

First launch creates `%APPDATA%\RichardFlashcards\flashcards.db`, applies all seven migrations, and
seeds four sample cards across three subjects so nothing is empty.

### Running against a different library

`FLASHCARDS_DATA_DIR` moves the whole library — database, images and `settings.json` — somewhere
else for that run. Point it at an empty folder and the app builds a fresh seeded library there,
leaving your real cards alone:

```powershell
$env:FLASHCARDS_DATA_DIR = "$env:TEMP\flashcards-scratch"; dotnet run --project src\Flashcards.Desktop
```

Worth reaching for before anything destructive — importing a deck you have not seen, clearing all
history, or a timed drill that records real answers. The Settings panel always shows which folder is
in use and says when the variable is what put it there, so a stray value is visible rather than a
mystery. A blank or unusable value falls back to the default folder.

### Notes on the build

Both test projects declare `<Using Include="Xunit" />`, so test files need no `using Xunit;` of their
own — xunit v2 ships no global usings and `ImplicitUsings` does not cover it.

Every package version lives in `Directory.Packages.props` (central package management — individual
`.csproj` files carry no `Version` attributes). If a version has moved on, that is the only file to
edit.

`AvaloniaUseCompiledBindingsByDefault` is on, so every `{Binding}` is type-checked against the view's
`x:DataType` at build time. A typo'd binding path is a build error, not a control that silently
renders nothing.

---

## The panels

**Study** — pick a mode, set that mode's options, work the queue. See below.

**Design** — the card editor, one artboard per card type. A Create | Edit pill shows which mode you
are in and tints the panel to match. Each face of a card is an ordered list of blocks, so a question
can be a Markdown paragraph, then a C# snippet, then a screenshot.

**Manage** — search by name or by anything written on either side, scope to a subject and everything
under it, filter by type or by never-answered, sort, page. Per row: edit, forget (clear that card's
answer history), suspend, delete. Generate, Import and Export live up here too.

**Statistics** — your record and nothing else: the whole library, the subjects that stand out, one
subject at a time, one card at a time. Deliberately has no way to start a sitting.

**Settings** — theme, study defaults, where your library lives, and a way to forget every answer.
Everything saves as you change it.

---

## Study modes

Choosing a mode leads to a prep screen carrying only the options that mode actually has. Custom is
the only one that shows you the library first, because it is the only one where choosing cards is
the point.

| Mode | Draws |
|---|---|
| **Custom** | Exactly the subjects and cards you tick |
| **Random** | An even shuffle of everything |
| **Suggested** | Ranked by lifetime wrong-ratio, never-answered cards leading |
| **Fresh cards** | Only cards you have never answered |
| **Recently missed** | Cards whose *most recent* answer was wrong, newest first |
| **Speed drill** | A per-question clock, auto-graded card types only |
| **Marathon** | Everything, with no cap on how many |

Recently missed is not Suggested with a different sort. Suggested ranks by a lifetime average, so a
card you have finally learned scores badly for a long time; Recently missed keys off the latest
answer only, which is the thing worth another look today.

Every prep screen offers: how many cards, a time limit for the sitting, a time limit per question,
auto-graded card types only, and shuffle multiple-choice options. A mode's preferences are defaults,
not locks. Running out of time on a question marks it wrong and moves on — and like any wrong
answer, the card returns to the back of the queue so it comes round again before you finish.

In a sitting: **space** or **Enter** reveals, then **1** or **Ctrl** for Wrong and **2** or **Space**
for Correct. Multiple-choice and cloze cards mark themselves.

---

## Cards

### Types

- **Question & answer** — two sides, you grade yourself after revealing.
- **Multiple choice** — two to eight options; tick every correct one. More than one correct turns it
  into multi-select. Auto-scored, then the answer side is shown as the explanation. Removing options
  down to two is how you write a true/false card without leaving blanks behind.
- **Fill in the blank** (cloze) — wrap words in double braces to hide them:
  `The capital of France is {{Paris}}.` Add a hint after a double colon: `{{Paris::a city}}`. Select
  text and press **Blank** to wrap it. Blanks are numbered across every question-side block.
- **Custom design** (freeform) — a fixed 960×600 canvas per face where you place text and images
  anywhere and draw over them with a pen. Coordinates are stored in card space, not screen pixels, so
  a card designed in a small window studies correctly in a large one.

### Block formats

Apply per block; a face can mix as many as you like.

| Block | Notes |
|---|---|
| Text | Plain, wrapped |
| Markdown | `**bold**`, `*italic*`, `` `code` ``, `# headings`, `- bullets`, `1.` lists, `>` quotes |
| Code | Syntax highlighted for C#, SQL, JavaScript/TypeScript/JSON and Python; scrolls rather than wraps |
| Image | Paste, browse or drag-drop; per-image stretch and max height |
| Drawing | Freehand ink, stored as vector strokes; the eraser removes whole strokes |

### Pasting images

Clipboard behaviour on Windows depends entirely on the source app: a browser writes a `PNG` blob, the
Snipping Tool and most native apps write `CF_DIB` (a BMP with its 14-byte file header stripped).
`ClipboardImageService` asks Avalonia for a decoded bitmap first, which handles both. If a backend
advertises a raw image format without offering to decode it, a clearly-marked fallback fetches the
bytes and reattaches the BMP header itself. Failing that, a copied file path is read. Failing
everything, use **Browse…** or drag a file onto the block.

Images are content-addressed by SHA-256: the same screenshot on ten cards is stored once.

---

## Subjects

Subjects are tags you type into the designer, arranged as a tree up to five levels deep. Ancestry is
**derived, never stored** — a card tagged `MSSQL` answers to `SQL` and `Databases` because of where
that tag sits, so re-filing a subject changes what every card under it answers to without rewriting a
single row. Selecting a subject anywhere selects everything beneath it.

Deleting a subject promotes what it held — child subjects *and* cards — up one level rather than
destroying it. Where there is nowhere to promote a card to, the delete is refused and the blocking
cards are named so you can fix them.

---

## Sharing cards

**Generate** builds a prompt for making a deck with a language model and copies it to the
clipboard. The app itself never calls a model — no key, no account, no network request — so you
paste the prompt into whatever assistant you already use, save the answer as a `.fcdeck` file, and
import it. The prompt and what to do when a deck comes back wrong are in
[docs/generating-decks.md](docs/generating-decks.md).

**Export** writes chosen subjects and cards to a `.fcdeck` file: readable JSON, with images inline
and the subject tree carried as names rather than ids, so it rebuilds anywhere. **Import** shows the
same picker over the file's contents and lets you skip or replace anything you already have.

Answer history deliberately does not travel. A deck is content; how *you* did on it belongs to your
library.

---

## Where your data is

| | |
|---|---|
| Database | `%APPDATA%\RichardFlashcards\flashcards.db` |
| Images | `%APPDATA%\RichardFlashcards\media\` |
| Preferences | `%APPDATA%\RichardFlashcards\settings.json` |

Backing up is copying that folder. WAL mode means there may also be `-wal` and `-shm` files alongside
the database; copy them too, or close the app first.

Preferences sit *beside* the database rather than inside it on purpose — a copy of the database is a
copy of your cards, and your choice of theme has no business travelling with it.

---

## Layout

```
src/
  Flashcards.Domain           entities and invariants — references nothing
    Cards/Enums                 one enum per file
    Cards/Validation            the collect-every-error rules, out of the aggregate
  Flashcards.Application      CQRS dispatcher, commands, queries, contracts, persistence seams
  Flashcards.Infrastructure   Dapper repositories, read stores, migrations, media and settings stores
    Persistence/ReadStores      one per concern: cards, subjects, stats, quiz
    Persistence/Rows            Dapper materialisation shapes, shared
    Persistence/Sql             SQL more than one store needs
  Flashcards.Desktop          Avalonia views, view models, custom renderers
    Views|ViewModels|Controls   each split by panel: Study, StudySetup, Design, Manage,
                                Statistics, Settings, Subjects, Shell, Shared
tests/
  Flashcards.Domain.Tests       pure unit tests — no database, no clock, no UI
  Flashcards.Integration.Tests  the real container against a throwaway SQLite file
docs/
  architecture.md               how the layers fit together and why
  sqlite-vs-tsql.md             the dialect guide
  upgrading-to-avalonia-12.md   what breaks and where, when you want to move
```

Namespaces match folders throughout, including in XAML.

---

## Reading it

The SQL is commented for someone coming from T-SQL — every notable difference is marked `[T-SQL]` in
the source, with the same ground covered systematically in `docs/sqlite-vs-tsql.md`. The three that
cause the most grief:

1. `PRAGMA foreign_keys = ON` is per connection and off by default, so cascades are inert without it
   (`SqliteConnectionFactory`).
2. There is no `MERGE`; `INSERT ... ON CONFLICT DO UPDATE` with the `excluded` pseudo-table is the
   idiom (the search-index triggers in `Migration002_SearchIndex.sql`).
3. Guids and timestamps have no native types and must be pinned to TEXT explicitly, or
   `Microsoft.Data.Sqlite` writes Guids as BLOBs that compare unequal to everything else
   (`SqlMappings`).

For the architecture, start with `docs/architecture.md`, then `Dispatcher.cs` — the reflection that
makes CQRS feel magical is about sixty readable lines.

---

## Giving it to someone

```powershell
dotnet publish src\Flashcards.Desktop -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist
```

That produces one ~48 MB `Flashcards.Desktop.exe` needing no .NET runtime installed. Bump `<Version>`
in `Directory.Build.props` first; the build stamps the commit SHA alongside it, and the Settings
panel shows both.

It is **unsigned**, so Windows will show a SmartScreen warning that needs *More info → Run anyway*,
and a machine with Smart App Control enabled may refuse it outright with no override. Do not tell
people to disable Smart App Control — it cannot be re-enabled without reinstalling Windows. Signing
is the fix; Azure Artifact Signing is the cheapest legitimate route.

---

## Known rough edges

- **The light theme has had less attention than the dark one.** The app was designed against dark;
  light is Semi's palette doing the work. It is usable, not tuned.
- **Windows only, in practice.** A `linux-x64` publish succeeds and pulls in the X11 and Skia
  natives, but nothing has been *run* there. The likeliest first surprise is case-sensitivity: Linux
  will reject an `avares://` path whose casing does not match the file, where Windows never would.
  Android would need a separate `Avalonia.Android` head and a lot more than that — the layout assumes
  a wide window and a mouse.
- **Import matches cards by id, then by name plus a shared tag.** Two genuinely different cards that
  share a name and a subject look like one card to it. Skip or replace is the whole conflict story;
  there is no merge.
- **Statistics has no history over time.** Every figure is a lifetime total or today's. The
  `review_log` table records a timestamp per answer, so a heatmap or a streak is a query away and
  would be a good next feature.
- **Answer history is per-card, not per-session.** Sessions live in memory and vanish when they end,
  so there is no "what did I do on Tuesday" view to build from.
