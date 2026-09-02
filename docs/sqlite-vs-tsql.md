# SQLite for the T-SQL developer

Everything in this file shows up somewhere in the code. Where it does, the file and the
statement are named so you can go read the real thing rather than a toy example.

---

## 1. There are no data types, only storage classes

SQLite has five: `NULL`, `INTEGER`, `REAL`, `TEXT`, `BLOB`. Column declarations are advisory.

```sql
CREATE TABLE t (a VARCHAR(3));
INSERT INTO t VALUES ('this is very much longer than three characters');  -- succeeds
```

The declared type only sets a **type affinity** — a preference SQLite applies when converting a
value on the way in. `VARCHAR(3)`, `NVARCHAR(MAX)` and `TEXT` all give TEXT affinity and behave
identically. `DECIMAL(18,2)` gives NUMERIC affinity and silently stores a float.

Consequences you will hit:

| T-SQL | SQLite | Where in this repo |
|---|---|---|
| `uniqueidentifier` | `TEXT` holding the 36-char "D" format | `SqlMappings.GuidHandler` |
| `datetime2` / `datetimeoffset` | `TEXT` holding ISO-8601 round-trip format | `SqlMappings.DateTimeOffsetHandler` |
| `bit` | `INTEGER` 0/1, plus a `CHECK` constraint | `is_suspended` in migration 001 |
| `decimal` | `REAL` — do not store money in SQLite without thinking | `ease_factor`, `interval_days` |
| `IDENTITY(1,1)` | `INTEGER PRIMARY KEY AUTOINCREMENT` | `review_log.id` |

The Guid one bites hardest: `Microsoft.Data.Sqlite` will write a `Guid` as a 16-byte BLOB by
default. That value then does not compare equal to a TEXT id written by anything else, and it is
unreadable in a SQLite browser. `SqlMappings.Register()` pins both `Guid` and `DateTimeOffset` to
TEXT once, at startup.

The ISO-8601 "O" format matters for a second reason: it sorts lexicographically in the same order
it sorts chronologically. That is what makes `WHERE reviewed_utc >= @Since` work on a TEXT
column with no conversion and with an index still in play — the app counts "answers today" that
way (`StatsReadStore.GetOverallStatsAsync`).

That only holds while every stored value carries the *same* offset. Values here are all written
from `DateTimeOffset.UtcNow`, so they are all `+00:00`; a cutoff computed from local midnight must
be converted with `ToUniversalTime()` before it is bound, or `2026-09-02T00:00:00.0000000-05:00`
sorts nowhere near where you expect.

---

## 2. Foreign keys are off by default, per connection

This is the single most surprising difference.

```sql
PRAGMA foreign_keys = ON;   -- must be issued on EVERY connection you open
```

Without it, every `REFERENCES ... ON DELETE CASCADE` in the schema is inert: parents delete,
children stay, and nothing complains. There is no server-level setting to fix it globally, because
there is no server.

See `SqliteConnectionFactory.OpenAsync`, which issues it along with three other pragmas that
matter:

- `journal_mode = WAL` — write-ahead logging. Readers no longer block the writer, which is roughly
  what `READ_COMMITTED_SNAPSHOT ON` buys you in SQL Server. Unlike the others it is a persistent
  property of the *file*, not the connection.
- `busy_timeout = 5000` — SQLite allows exactly one writer. Without this a contended write returns
  `SQLITE_BUSY` immediately instead of waiting. Think `SET LOCK_TIMEOUT`.
- `synchronous = NORMAL` — safe under WAL and far faster than `FULL` for a local app.

---

## 3. There is no MERGE. There is upsert.

```sql
-- T-SQL
MERGE card_search AS target
USING (VALUES (@CardId, @Text)) AS source (card_id, search_text)
ON target.card_id = source.card_id
WHEN MATCHED THEN UPDATE SET search_text = source.search_text
WHEN NOT MATCHED THEN INSERT (card_id, search_text) VALUES (source.card_id, source.search_text);

-- SQLite
INSERT INTO card_search (card_id, search_text)
VALUES (@CardId, @Text)
ON CONFLICT (card_id) DO UPDATE SET search_text = excluded.search_text;
```

Notes:

- The conflict target must name a column set covered by a `UNIQUE` or `PRIMARY KEY` index.
- `excluded` is the pseudo-table holding the row that *would* have been inserted — the direct
  analogue of `source` in a `MERGE`.
- `DO NOTHING` replaces `INSERT IGNORE`.
- There is no `OUTPUT` clause, but there **is** `RETURNING` (SQLite 3.35+), which behaves like
  PostgreSQL's.

Real examples: the search-index triggers in `Migration002_SearchIndex.sql`, which keep `card_search`
in step on every block edit. `SubjectRepository.DeleteAsync` uses the shorter `INSERT OR IGNORE`
form when promoting cards to a parent subject, where a card that already wears the parent needs no
update at all.

---

## 4. Paging, top-N and window functions

```sql
-- T-SQL
SELECT ... ORDER BY updated_utc DESC OFFSET 50 ROWS FETCH NEXT 25 ROWS ONLY;
SELECT TOP (10) ...;

-- SQLite
SELECT ... ORDER BY updated_utc DESC LIMIT 25 OFFSET 50;
SELECT ... LIMIT 10;
```

Window functions arrived in SQLite 3.25 and the syntax is the same as T-SQL's. This repo uses
`COUNT(*) OVER ()` in `FlashcardReadStore.SearchAsync` to get the unpaged total in the same query
as the page — window functions are evaluated before `LIMIT`, so the count is the full result size.

SQLite also accepts `LIMIT 50, 25` (MySQL style). Avoid it: the arguments are reversed relative to
what you would guess.

---

## 5. String and null handling

| T-SQL | SQLite |
|---|---|
| `LEFT(x, n)` | `SUBSTR(x, 1, n)` — no `LEFT`/`RIGHT` at all |
| `LEN(x)` | `LENGTH(x)` |
| `x + y` | `x \|\| y` — `+` on two strings coerces them to numbers and gives `0` |
| `ISNULL(x, y)` | `IFNULL(x, y)` or `COALESCE` |
| `STRING_AGG(x, ',')` | `group_concat(x, ',')` — argument order reversed |
| `CHARINDEX(a, b)` | `INSTR(b, a)` — argument order reversed |
| `GETUTCDATE()` | `datetime('now')` |
| `NEWID()` | `RANDOM()` (a random signed 64-bit int, not a uuid) |
| `IIF(c, a, b)` | `IIF(c, a, b)` since 3.32; `CASE` before that |
| `TRY_CAST` | none — `CAST` never throws, it just produces junk |

The `+` one causes real bugs: `SELECT 'a' + 'b'` returns `0` in SQLite, silently.

Three-valued logic is identical to T-SQL, including the `NOT IN` trap: if the subquery yields any
`NULL`, `NOT IN` is never true for any row. `FileSystemMediaStore.CollectGarbageAsync` uses
`NOT EXISTS` for exactly that reason, and says so in a comment.

---

## 6. Collation: the default is the opposite of what you expect

SQL Server databases are usually created with a case-**insensitive** collation. SQLite's default
collation, `BINARY`, is case-**sensitive**.

```sql
SELECT * FROM subjects WHERE name = 'sql';          -- misses a row named 'SQL'
SELECT * FROM subjects WHERE name = 'sql' COLLATE NOCASE;   -- finds it
```

The portable fix is to declare the column itself:

```sql
name TEXT NOT NULL COLLATE NOCASE
```

which is what migration 001 does, so `= @Name` is case-insensitive *and* can still use the index
on `name`. Wrapping the column in `LOWER()` would work too and would destroy the index — same
trade-off as in T-SQL.

Caveat: `NOCASE` only folds ASCII A–Z. `Ä` and `ä` are still different. There is no built-in
Unicode-aware collation without the ICU extension.

Confusingly, `LIKE` goes the other way: it *is* case-insensitive for ASCII by default. So on the
same column, `=` and `LIKE` disagree about case unless you declare `COLLATE NOCASE`.

---

## 7. Triggers

Row-level only. No statement-level triggers, no `INSTEAD OF` on tables, and no `inserted`/`deleted`
pseudo-tables — you get `NEW` and `OLD` row aliases instead, which is closer to Oracle or Postgres.

Migration 002 keeps a flattened `card_search.search_text` per card so the management panel can
search question bodies with one indexed `LIKE` instead of a correlated `EXISTS` per row. The
delete trigger carries a `WHEN` guard:

```sql
CREATE TRIGGER trg_card_search_after_block_delete
AFTER DELETE ON card_blocks
WHEN EXISTS (SELECT 1 FROM flashcards f WHERE f.id = OLD.card_id)
BEGIN ... END;
```

Without the guard, deleting a flashcard cascades into `card_blocks`, fires this trigger, and the
trigger re-inserts a `card_search` row pointing at a card that no longer exists — a foreign key
violation on a plain `DELETE`. Cascade-plus-trigger interaction is worth being paranoid about in
either dialect.

---

## 8. Transactions and concurrency

SQLite's isolation level is `SERIALIZABLE`, always. There is no `READ UNCOMMITTED`, no `NOLOCK`,
no lock hints, no deadlock graph. Under WAL there is one writer and any number of concurrent
readers; a second writer waits out `busy_timeout` and then fails with `SQLITE_BUSY`.

DDL **is** transactional — a failed `CREATE TABLE` inside a transaction rolls back cleanly, same as
SQL Server, unlike MySQL or Oracle. `DatabaseInitializer` relies on this: each migration script
runs inside its own transaction with its `schema_migrations` row.

Two more differences worth knowing:

- `@@ROWCOUNT` becomes the function `changes()`, and `SCOPE_IDENTITY()` becomes
  `last_insert_rowid()`.
- The default bound-parameter limit is 999 (32766 since 3.32). Dapper's `IN @Ids` expansion emits
  one parameter per element, so a large `IN` list needs chunking. `ReviewLogRepository.ClearAsync`
  notes this — and `ClearAllAsync` beside it exists to avoid the problem entirely, because "forget
  every answer" is `DELETE FROM review_log` rather than every id in the library bound as a parameter.

---

## 9. `ALTER TABLE` is very limited

You get `RENAME TO`, `RENAME COLUMN`, `ADD COLUMN`, and `DROP COLUMN` (3.35+). That is all — no
`ALTER COLUMN`, no adding a constraint to an existing table.

The official recipe for anything else is the twelve-step dance: create a new table with the shape
you want, copy the rows, drop the old one, rename. If you ever need it, write it as a new numbered
migration file and let `DatabaseInitializer` apply it.

---

## 10. What SQLite does *not* have

Stored procedures. Functions (without registering them from the host language). Schemas beyond
`main`/`temp`/attached files. Users, roles or `GRANT`. `RIGHT`/`FULL OUTER JOIN` before 3.39.
Materialised views. Partitioning. Query hints. An execution-plan viewer beyond `EXPLAIN QUERY PLAN`,
which is genuinely useful and worth running on the search query if it ever feels slow:

```sql
EXPLAIN QUERY PLAN
SELECT ... FROM flashcards c INNER JOIN subjects s ON s.id = c.subject_id ...;
```

Its output names the index chosen per table — the equivalent of glancing at a graphical plan for
scan-versus-seek.

---

## 11. Things it has that SQL Server does not

- **FTS5** — a built-in full-text index, bundled in the `Microsoft.Data.Sqlite` native library.
  If `LIKE '%term%'` ever gets slow, replacing `card_search` with an FTS5 virtual table is about
  fifteen lines and gives you ranking and prefix queries.
- **`WITHOUT ROWID`** tables — closer to a clustered index on your own key.
- **Strict tables** (`CREATE TABLE ... STRICT`, 3.37+) — opt back in to real type enforcement.
  Worth considering for a greenfield schema; this one does not use it so the affinity behaviour
  stays visible.
- **`RETURNING`** on `INSERT`/`UPDATE`/`DELETE`.
