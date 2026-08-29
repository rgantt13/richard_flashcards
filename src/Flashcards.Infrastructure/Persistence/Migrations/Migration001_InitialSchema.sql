-- ---------------------------------------------------------------------------
-- Migration 001 — initial schema
--
-- Read this file top to bottom if you want the guided tour of how SQLite's DDL
-- differs from T-SQL. Every comment marked [T-SQL] contrasts the two.
-- ---------------------------------------------------------------------------

-- [T-SQL] There is no uniqueidentifier, no datetime2, no nvarchar, no bit.
-- SQLite has exactly five storage classes: NULL, INTEGER, REAL, TEXT, BLOB.
-- Everything else is a naming convention. A column declared VARCHAR(50) will
-- happily store a 4 MB string; the length is documentation, not a constraint.
-- We therefore store:
--   GUIDs      as TEXT  (36-char lowercase 'D' format — sorts and diffs cleanly)
--   timestamps as TEXT  (ISO-8601 with offset — the only format SQLite's date
--                        functions understand, and it sorts lexicographically)
--   booleans   as INTEGER 0/1
--   money/ease as REAL

CREATE TABLE IF NOT EXISTS subjects (
    id            TEXT    NOT NULL PRIMARY KEY,
    -- [T-SQL] COLLATE NOCASE is SQLite's answer to a CI collation. SQL Server
    -- databases are usually case-insensitive by default; SQLite is case-SENSITIVE
    -- by default, so every column you intend to search or compare loosely needs
    -- this spelled out. NOCASE only folds ASCII A-Z, not accented characters.
    name          TEXT    NOT NULL COLLATE NOCASE,
    color_hex     TEXT    NULL,
    description   TEXT    NULL,
    created_utc   TEXT    NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_subjects_name ON subjects (name);

CREATE TABLE IF NOT EXISTS flashcards (
    id            TEXT    NOT NULL PRIMARY KEY,
    -- [T-SQL] Inline REFERENCES works the same, but see the pragma note in
    -- SqliteConnectionFactory: foreign keys are OFF unless the connection turns
    -- them on. ON DELETE CASCADE is silently ignored without that pragma.
    subject_id    TEXT    NOT NULL REFERENCES subjects (id) ON DELETE CASCADE,
    name          TEXT    NOT NULL COLLATE NOCASE,
    card_type     INTEGER NOT NULL,
    notes         TEXT    NULL,
    -- [T-SQL] No BIT type. CHECK constraints are enforced the same way.
    is_suspended  INTEGER NOT NULL DEFAULT 0 CHECK (is_suspended IN (0, 1)),
    created_utc   TEXT    NOT NULL,
    updated_utc   TEXT    NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_flashcards_subject ON flashcards (subject_id);
CREATE INDEX IF NOT EXISTS ix_flashcards_name    ON flashcards (name);
-- Card names only have to be unique inside a subject.
CREATE UNIQUE INDEX IF NOT EXISTS ux_flashcards_subject_name ON flashcards (subject_id, name);

-- Media is declared before card_blocks because card_blocks references it.
-- (SQLite tolerates forward references in FK clauses, but only until the first
-- DML statement touches them, so ordering the DDL correctly avoids a surprise.)
CREATE TABLE IF NOT EXISTS media (
    id            TEXT    NOT NULL PRIMARY KEY,
    file_name     TEXT    NOT NULL,
    mime_type     TEXT    NOT NULL,
    byte_size     INTEGER NOT NULL,
    -- Content address. Two identical pastes collapse to one row and one file.
    sha256        TEXT    NOT NULL,
    created_utc   TEXT    NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_media_sha ON media (sha256);

CREATE TABLE IF NOT EXISTS card_blocks (
    id            TEXT    NOT NULL PRIMARY KEY,
    card_id       TEXT    NOT NULL REFERENCES flashcards (id) ON DELETE CASCADE,
    face          INTEGER NOT NULL CHECK (face IN (0, 1)),   -- 0 question, 1 answer
    ordinal       INTEGER NOT NULL,
    kind          INTEGER NOT NULL CHECK (kind IN (0, 1, 2, 3)), -- text/markdown/code/image
    text          TEXT    NULL,
    language      TEXT    NULL,
    media_id      TEXT    NULL REFERENCES media (id) ON DELETE SET NULL,
    stretch       INTEGER NOT NULL DEFAULT 2,
    max_height    REAL    NULL,
    alt_text      TEXT    NULL
);

CREATE INDEX IF NOT EXISTS ix_card_blocks_card ON card_blocks (card_id, face, ordinal);
CREATE INDEX IF NOT EXISTS ix_card_blocks_media ON card_blocks (media_id);

CREATE TABLE IF NOT EXISTS card_choices (
    id            TEXT    NOT NULL PRIMARY KEY,
    card_id       TEXT    NOT NULL REFERENCES flashcards (id) ON DELETE CASCADE,
    ordinal       INTEGER NOT NULL,
    text          TEXT    NOT NULL,
    is_correct    INTEGER NOT NULL CHECK (is_correct IN (0, 1))
);

CREATE INDEX IF NOT EXISTS ix_card_choices_card ON card_choices (card_id, ordinal);

CREATE TABLE IF NOT EXISTS review_states (
    -- One row per card, so the card id *is* the primary key.
    card_id           TEXT    NOT NULL PRIMARY KEY REFERENCES flashcards (id) ON DELETE CASCADE,
    repetitions       INTEGER NOT NULL DEFAULT 0,
    ease_factor       REAL    NOT NULL DEFAULT 2.5,
    interval_days     REAL    NOT NULL DEFAULT 0,
    due_utc           TEXT    NOT NULL,
    last_reviewed_utc TEXT    NULL,
    lapses            INTEGER NOT NULL DEFAULT 0
);

-- The hot index: "what is due right now" is the query the quiz screen runs constantly.
CREATE INDEX IF NOT EXISTS ix_review_states_due ON review_states (due_utc);

CREATE TABLE IF NOT EXISTS review_log (
    -- [T-SQL] IDENTITY(1,1) becomes INTEGER PRIMARY KEY AUTOINCREMENT. Note the
    -- type must be exactly INTEGER (not INT, not BIGINT) for this to alias rowid.
    -- Without AUTOINCREMENT, SQLite reuses ids of deleted rows; with it, it never
    -- does, at the cost of an extra sqlite_sequence table. For an append-only
    -- audit log, never reusing ids is worth it.
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    card_id             TEXT    NOT NULL REFERENCES flashcards (id) ON DELETE CASCADE,
    reviewed_utc        TEXT    NOT NULL,
    grade               INTEGER NOT NULL,
    prior_interval_days REAL    NOT NULL,
    new_interval_days   REAL    NOT NULL,
    ease_after          REAL    NOT NULL,
    elapsed_ms          INTEGER NOT NULL,
    was_correct         INTEGER NULL
);

CREATE INDEX IF NOT EXISTS ix_review_log_card ON review_log (card_id, reviewed_utc);
CREATE INDEX IF NOT EXISTS ix_review_log_date ON review_log (reviewed_utc);
