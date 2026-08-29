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
| Interface | `IFlashcardRepository` | `IFlashcardReadStore` |
| Returns | `Flashcard` aggregates | flat DTOs from `Contracts` |
| Used by | command handlers | query handlers |
| SQL style | load/save whole aggregates | joins, `GROUP BY`, window functions |

The management panel's grid needs card name, subject name, subject colour, block count, whether it
has images, and its next due date. Loading four aggregates and stitching them in C# to produce that
row would be silly; `FlashcardReadStore.SearchAsync` produces it in one statement, paged, with the
total count from `COUNT(*) OVER ()`.

There is no separate read database and no eventual consistency — both sides hit the same SQLite
file in the same transaction. CQRS here means *two models*, not *two stores*.

## Aggregates

`Flashcard` is the aggregate root. It owns its `ContentBlock`s and `ChoiceOption`s; nothing outside
holds a reference to them or mutates them directly, which is why `AddTextBlock`, `MoveBlock` and
`ReplaceBlocks` are methods on the card rather than list manipulation in a handler. Block ordinals
stay dense and gap-free because the card compacts them itself.

`ReviewState` is a **separate** aggregate keyed by the same id. Content changes rarely; scheduling
changes on every single review. Splitting them means a grade writes one narrow row instead of
rewriting a card and its children.

`Subject` is trivially its own aggregate.

`Flashcard.Validate()` returns a list rather than throwing on the first failure, so the editor can
show every problem at once. Guard clauses that protect a single invariant still throw
`DomainException` immediately — the two styles serve different callers.

## Scheduling

`Sm2Scheduler` is a pure static function of `(repetitions, ease, interval, lapses, grade, now)`.
That is deliberate: scheduling is the only genuinely tricky logic in the app, and purity means the
whole of `Sm2SchedulerTests` runs with no database, no clock and no UI.

`ReviewState.Grade()` applies the result and returns the `ReviewLogEntry` describing what changed;
the handler persists both in one transaction.

Two documented deviations from published SM-2, both borrowed from Anki and both switchable in
`Sm2Options`:

- **Hard** multiplies the previous interval by 1.2 rather than by the ease factor. Published SM-2
  treats `q = 3` as a full success, which makes "Hard" grow almost as fast as "Good".
- **Easy** gets a 1.3× bonus on top of the ease multiplication.

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

## Where to start reading

1. `Migration001_InitialSchema.sql` — the data model, heavily annotated.
2. `Flashcard.cs` — the aggregate and its invariants.
3. `Sm2Scheduler.cs` — the algorithm, then its tests.
4. `Dispatcher.cs` — how CQRS actually resolves a handler.
5. `FlashcardReadStore.cs` — the read side, and most of the SQL worth studying.
