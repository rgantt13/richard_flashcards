# Architecture

## The dependency rule

```
Flashcards.Desktop  ──────►  Flashcards.Application  ──────►  Flashcards.Domain
        │                             ▲
        └──► Flashcards.Infrastructure ┘
```

Arrows are compile-time references. Two properties fall out of them:

- **Domain references nothing.** No NuGet packages at all — check the `.csproj`. If a change ever
  seems to need Dapper or Avalonia in there, the design has gone wrong.
- **Application references only DI abstractions.** It declares the interfaces it needs
  (`IFlashcardRepository`, `IMediaStore`, `IClock`) and Infrastructure implements them. That is
  the dependency inversion that makes "Clean" clean: the inner layer owns the contract, the outer
  layer obeys it.

Desktop references Infrastructure only to call `AddInfrastructure()` in `App.axaml.cs`. No view
model touches a repository — they take `IDispatcher` and nothing else.

## CQRS without a library

`Flashcards.Application/Abstractions/Messaging` is about 200 lines and does everything this app
needs from a mediator:

- `ICommand<TResult>` / `IQuery<TResult>` mark requests.
- `ICommandHandler<,>` / `IQueryHandler<,>` handle exactly one each.
- `Dispatcher` resolves the handler for the runtime request type and invokes it.
- `IValidator<TRequest>` runs before the handler; failures aggregate into one `ValidationException`.

Two implementation details are worth reading rather than skimming.

**The invoker cache.** Calling a closed generic method when you only know the type at runtime is
usually done with `MethodInfo.Invoke`, which is slow and wraps your exceptions in
`TargetInvocationException`. Instead `Dispatcher` caches a non-generic abstract `CommandInvoker<TResult>`
per request type whose generic subclass performs the cast. One `Activator.CreateInstance` per
request *type*, then a plain virtual call forever after.

**The per-request DI scope.** `SendAsync` and `QueryAsync` each open an `IServiceScope`.
`DbSession` is registered scoped, so a handler and every repository it touches share exactly one
`SqliteConnection` and one transaction, disposed when the request finishes. Without this, a
desktop app firing two async operations off the UI thread would have them fighting over one
connection.

Where a behaviour pipeline would go — logging, retries, an ambient transaction decorator — is the
validator loop in `Dispatcher.RunValidators`. Adding `IPipelineBehavior<TRequest, TResult>` there
is a contained change.

Handlers are registered explicitly in `DependencyInjection.AddApplication`. Assembly scanning
would be shorter; explicit registration means a handler you forgot to write is a compile error
instead of a resolution failure at runtime.

## Read side vs write side

This is the part of CQRS that pays for itself in a small app.

| | Write side | Read side |
|---|---|---|
| Interface | `IFlashcardRepository`, `ISubjectRepository`, `IReviewLogRepository` | `IFlashcardReadStore`, `ISubjectReadStore`, `IStatsReadStore`, `IQuizReadStore` |
| Returns | aggregates | flat DTOs from `Contracts` |
| Used by | command handlers | query handlers |
| SQL style | load/save whole aggregates | joins, `GROUP BY`, window functions, recursive CTEs |

The management panel's grid needs card name, every subject it wears with colours and whether each
was inherited, block count, whether it has images, and its answer tally. Loading aggregates and
stitching them in C# to produce that row would be silly; `FlashcardReadStore.SearchAsync` produces
it in one statement, paged, with the total count from `COUNT(*) OVER ()`.

The read side is **four interfaces, not one**. It was one until it reached six hundred lines
covering cards, subjects, statistics and the quiz queue. A handler after a card's detail should not
also be handed every way of counting a subject, and four narrower seams are what let the SQL behind
them live in four files. They mirror the query folders in the application layer.

There is no separate read database and no eventual consistency — both sides hit the same SQLite
file in the same transaction. CQRS here means *two models*, not *two stores*.

## Aggregates

`Flashcard` is the aggregate root. It owns its `ContentBlock`s and `ChoiceOption`s; nothing outside
holds a reference to them or mutates them directly, which is why `AddTextBlock`, `MoveBlock` and
`ReplaceBlocks` are methods on the card rather than list manipulation in a handler. Block ordinals
stay dense and gap-free because the card compacts them itself.

`Subject` is its own aggregate, but a thin one: it holds a name, a colour and a single nullable
`ParentId`. That one column is the entire hierarchy — see [Subjects](#subjects) below.

`ReviewRecord` is append-only and not an aggregate at all. It is a fact about something that
happened, and facts do not get edited.

`Flashcard.Validate()` returns a list rather than throwing on the first failure, so the editor can
show every problem at once. The rules themselves live in `Cards/Validation/FlashcardRules` — there
is one per card type and they are the part most likely to change, so adding a card type is a rule
here rather than a longer method on the aggregate.

Guard clauses are a different thing and stayed put. `Rename("")` and `RemoveSubject` on the last tag
still throw `DomainException` immediately, because they protect an invariant: they stop the object
entering a state it must never be in. Validation collects, because a half-built card is a perfectly
normal thing to be holding.

## No scheduling

There was an `Sm2Scheduler` and a `ReviewState` aggregate. Both are gone, along with the
`review_states` table (`Migration006_DropScheduling.sql`).

The reasoning is worth keeping because it explains the shape of everything downstream. Spaced
repetition decides *what you owe it today*; this app is for someone who wants to study when they
feel like studying. Once nothing is due, an ease factor has no consumer, a four-point grade has
nothing to feed, and "next review in 4 days" is a number nobody acts on.

What survives is `ReviewRecord` — card, timestamp, right or wrong, how long it took — appended on
every answer and never updated. Every figure in the app is an aggregate over that one table, which
is why the statistics are cheap and why clearing history is a single `DELETE`.

The replacement for scheduling is **study modes** (`ViewModels/StudySetup/StudyMode.cs`): seven ways
of choosing what to put in front of you, expressed as a `QuizDraw` the read store turns into a
filter and an `ORDER BY`. `Suggested` is the closest thing to the old behaviour — weakest cards
first — but it is one option among seven rather than a schedule you are behind on.

## Subjects

One nullable `parent_id` column, and ancestry **derived at query time** rather than stored.

The alternative — writing a `card_subjects` row per ancestor — would mean re-tagging every card
beneath a subject each time it moved. Instead a recursive CTE (`Persistence/Sql/SubjectClosure`)
produces the transitive closure on demand, so a card tagged `MSSQL` answers to `SQL` and `Databases`
because of where that tag currently sits. Re-filing a branch rewrites one row.

`SubjectHierarchy` in the domain owns every rule about the shape — no cycles, nothing past five
levels — because those are properties of the whole tree and a single `Subject` cannot see the tree
it is part of. Write handlers load the whole thing (subject tables are small) and validate against
it before saving.

The closure CTE uses `UNION`, not `UNION ALL`. SQLite has no `MAXRECURSION`, and `UNION` discards
rows it has already produced — so a cycle that somehow reached storage terminates with a wrong
answer instead of hanging the app.

## Presentation

MVVM with `CommunityToolkit.Mvvm`. `[ObservableProperty]` and `[RelayCommand]` are source
generators — the generated members are real, navigable code, so `Ctrl+click` on `SearchCommand`
goes somewhere.

Note the generator's naming rule: `SearchAsync()` generates `SearchCommand`, not
`SearchAsyncCommand`. The `Async` suffix is stripped.

`ViewLocator` maps `ViewModels.QuizViewModel` to `Views.QuizView` by name and is registered as an
application-level `DataTemplate`. Any `ContentControl` bound to a view model resolves its view —
the equivalent of one implicit `DataTemplate` per view model in a WPF `App.xaml`, minus the
maintenance.

The editor and quiz share `RichContentPresenter`, a `Decorator` that turns a list of
`ContentBlockDto` into controls. It derives from `Decorator` rather than `ContentControl` on
purpose: a control derived from `ContentControl` renders **nothing** unless a `ControlTheme` is
registered for its concrete type — the most common "my custom control is invisible" bug in
Avalonia. `Decorator` just hosts its `Child`.

Two things live in code-behind rather than a view model, and should:

- **Clipboard and file-picker access** (`CardEditorView`) — the clipboard hangs off `TopLevel`,
  which only a `Visual` can reach. The view assigns two delegates onto the view model.
- **Drag-and-drop** (`CardEditorView`) — `DragEventArgs` is an Avalonia type and keeping it out of
  the view model is the entire point of the split.

## Images

Content-addressed. `FileSystemMediaStore` hashes the bytes with SHA-256, writes
`%APPDATA%\RichardFlashcards\media\<hash>.<ext>`, and records metadata in the `media` table with a
unique index on the hash. Pasting the same screenshot onto ten cards stores it once.

Bytes stay out of the database because screenshots run 500 KB to 2 MB. SQLite is genuinely fast at
blobs under about 100 KB, but past that keeping them out keeps the `.db` file small enough to copy
and keeps a stray `SELECT *` from pulling megabytes into memory.

Format detection reads magic bytes rather than trusting a clipboard-supplied filename.
`ClipboardImageService` handles the awkward Windows reality that a browser puts a `PNG` blob on the
clipboard while the Snipping Tool puts `CF_DIB` — a BMP file with its 14-byte header removed, which
the service reattaches.

`ImageCache` holds decoded `Bitmap`s for the session. A `Bitmap` is a native/GPU resource, and
re-decoding one every time a virtualised list row scrolls back into view is exactly what makes a
desktop app feel sludgy.

## Settings

Preferences go through the dispatcher like everything else, but their store is deliberately not a
repository. `ISettingsStore` hangs off no `DbSession` and no unit of work, because preferences are
neither transactional nor part of any aggregate — and because the theme has to be read before a
window exists, which is earlier than the database is opened.

`JsonSettingsStore` writes a small file beside the database. Not a table: a settings row would need
a migration to add a field, and it would ride along in any copy of the library, so restoring a
backup of your cards would inherit whoever made it's theme. Reads never throw — a file that is
missing, truncated or hand-edited into nonsense yields defaults, because losing your theme is not a
reason to refuse to start. Writes go to a temporary file and are moved into place, so an interrupted
write cannot leave a half-file that silently reads back as defaults.

`StoragePaths` resolves where everything lives, honouring the `FLASHCARDS_DATA_DIR` override. The
resolution rules are a static function of a candidate string rather than a read of the environment,
so they are testable without setting a process-wide variable that would leak into every other test.

## Where to start reading

1. `Migration001_InitialSchema.sql` — the data model, heavily annotated.
2. `Flashcard.cs` — the aggregate and its invariants, then `Cards/Validation/FlashcardRules.cs`.
3. `SubjectHierarchy.cs` — the tree rules, then `Persistence/Sql/SubjectClosure.cs` for how ancestry
   is derived in SQL.
4. `Dispatcher.cs` — how CQRS actually resolves a handler.
5. `FlashcardReadStore.cs` and `QuizReadStore.cs` — the read side, and most of the SQL worth
   studying.
