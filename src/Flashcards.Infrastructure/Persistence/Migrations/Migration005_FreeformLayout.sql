-- ---------------------------------------------------------------------------
-- Designed ("freeform") cards: elements carry their own position and size, and
-- a new content kind holds freehand ink.
--
-- This one has to rebuild card_blocks rather than just ALTER it, because the
-- kind column carries CHECK (kind IN (0,1,2,3)) and ink is kind 4.
--
-- [T-SQL] Adding a column is a one-liner in both dialects, but *changing a
-- constraint* is where they part company. SQL Server has
-- ALTER TABLE ... DROP CONSTRAINT / ADD CONSTRAINT. SQLite has neither: the
-- only supported route is the create-copy-drop-rename procedure from the
-- "Making Other Kinds Of Table Schema Changes" section of the ALTER TABLE
-- docs, which is what the rest of this file is.
--
-- Two things make that safe here:
--   * Nothing has a foreign key *pointing at* card_blocks (it points outward,
--     at flashcards and media), so dropping it cannot orphan another table.
--     That is why this does not need to toggle PRAGMA foreign_keys -- which it
--     could not do anyway, since the migrator runs each script in a
--     transaction and that pragma is a no-op inside one.
--   * The triggers are dropped before the rename. SQLite rewrites references
--     inside surviving triggers when a table is renamed, so dropping them
--     first avoids depending on that behaviour and lets us reinstate exactly
--     the definitions we want.
--
-- The geometry columns are nullable REAL. A block is either placed (all four
-- present) or in flow (all four NULL), and every pre-existing block is in flow
-- -- so NULL is both the right default and a free backfill.
--
-- [T-SQL] REAL in SQLite is a 64-bit IEEE double -- the equivalent of FLOAT(53),
-- not T-SQL's 4-byte REAL. There is no true DECIMAL either: SQLite accepts the
-- keyword and stores it as REAL regardless.
-- ---------------------------------------------------------------------------

CREATE TABLE card_blocks_new (
    id            TEXT    NOT NULL PRIMARY KEY,
    card_id       TEXT    NOT NULL REFERENCES flashcards (id) ON DELETE CASCADE,
    face          INTEGER NOT NULL CHECK (face IN (0, 1)),   -- 0 question, 1 answer
    ordinal       INTEGER NOT NULL,
    -- 4 is Drawing: freehand ink, serialised into the text column.
    kind          INTEGER NOT NULL CHECK (kind IN (0, 1, 2, 3, 4)), -- text/markdown/code/image/drawing
    text          TEXT    NULL,
    language      TEXT    NULL,
    media_id      TEXT    NULL REFERENCES media (id) ON DELETE SET NULL,
    stretch       INTEGER NOT NULL DEFAULT 2,
    max_height    REAL    NULL,
    alt_text      TEXT    NULL,
    -- Placement on the logical card canvas. See BlockBounds / CardCanvas.
    x             REAL    NULL,
    y             REAL    NULL,
    width         REAL    NULL,
    height        REAL    NULL
);

INSERT INTO card_blocks_new
    (id, card_id, face, ordinal, kind, text, language, media_id, stretch, max_height, alt_text)
SELECT id, card_id, face, ordinal, kind, text, language, media_id, stretch, max_height, alt_text
FROM   card_blocks;

DROP TRIGGER IF EXISTS trg_card_search_after_block_insert;
DROP TRIGGER IF EXISTS trg_card_search_after_block_update;
DROP TRIGGER IF EXISTS trg_card_search_after_block_delete;

DROP TABLE card_blocks;

ALTER TABLE card_blocks_new RENAME TO card_blocks;

CREATE INDEX IF NOT EXISTS ix_card_blocks_card  ON card_blocks (card_id, face, ordinal);
CREATE INDEX IF NOT EXISTS ix_card_blocks_media ON card_blocks (media_id);

-- ---------------------------------------------------------------------------
-- The search triggers, reinstated from migration 002 with one change: drawing
-- blocks are excluded.
--
-- A drawing's text column holds serialised stroke coordinates. Without the
-- `kind <> 4` guard the search index would fill up with numbers, a search for
-- "40" would match any card containing a stroke through x=40, and the question
-- preview on the manage screen would show raw coordinate soup.
-- ---------------------------------------------------------------------------

CREATE TRIGGER trg_card_search_after_block_insert
AFTER INSERT ON card_blocks
BEGIN
    INSERT INTO card_search (card_id, search_text)
    VALUES (
        NEW.card_id,
        (SELECT COALESCE(group_concat(b.text, ' '), '')
         FROM card_blocks b
         WHERE b.card_id = NEW.card_id AND b.text IS NOT NULL AND b.kind <> 4)
    )
    ON CONFLICT (card_id) DO UPDATE SET search_text = excluded.search_text;
END;

CREATE TRIGGER trg_card_search_after_block_update
AFTER UPDATE ON card_blocks
BEGIN
    INSERT INTO card_search (card_id, search_text)
    VALUES (
        NEW.card_id,
        (SELECT COALESCE(group_concat(b.text, ' '), '')
         FROM card_blocks b
         WHERE b.card_id = NEW.card_id AND b.text IS NOT NULL AND b.kind <> 4)
    )
    ON CONFLICT (card_id) DO UPDATE SET search_text = excluded.search_text;
END;

-- The WHEN guard matters. Deleting a flashcard cascades to card_blocks, which
-- fires this trigger; without the guard it would re-insert a card_search row
-- pointing at a card that no longer exists and blow up on the foreign key.
CREATE TRIGGER trg_card_search_after_block_delete
AFTER DELETE ON card_blocks
WHEN EXISTS (SELECT 1 FROM flashcards f WHERE f.id = OLD.card_id)
BEGIN
    INSERT INTO card_search (card_id, search_text)
    VALUES (
        OLD.card_id,
        (SELECT COALESCE(group_concat(b.text, ' '), '')
         FROM card_blocks b
         WHERE b.card_id = OLD.card_id AND b.text IS NOT NULL AND b.kind <> 4)
    )
    ON CONFLICT (card_id) DO UPDATE SET search_text = excluded.search_text;
END;

-- The copy above moved rows without firing any trigger, so refresh every card's
-- flattened text from the rebuilt table.
--
-- The trailing `WHERE true` is not padding. In an INSERT ... SELECT ... upsert,
-- SQLite's parser cannot tell whether ON CONFLICT starts an upsert clause or is
-- part of the SELECT, and it resolves the ambiguity by requiring the SELECT to
-- end in a WHERE clause first. Without it this is a syntax error at "DO".
-- [T-SQL] No equivalent wart: MERGE has an explicit WHEN MATCHED.
INSERT INTO card_search (card_id, search_text)
SELECT c.id, COALESCE((SELECT group_concat(b.text, ' ')
                       FROM card_blocks b
                       WHERE b.card_id = c.id AND b.text IS NOT NULL AND b.kind <> 4), '')
FROM flashcards c
WHERE true
ON CONFLICT (card_id) DO UPDATE SET search_text = excluded.search_text;
