-- ---------------------------------------------------------------------------
-- Migration 002 — denormalised search text
--
-- The management panel searches card names AND question text. Question text
-- lives across N rows in card_blocks, so a naive search needs a correlated
-- EXISTS on every row. Instead we keep a flattened copy per card, maintained
-- by triggers.
--
-- [T-SQL] SQLite triggers are FOR EACH ROW only — there is no statement-level
-- trigger and no `inserted`/`deleted` pseudo-tables. You get `NEW` and `OLD`
-- row aliases instead, which is closer to Oracle or Postgres than to SQL Server.
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS card_search (
    card_id      TEXT NOT NULL PRIMARY KEY REFERENCES flashcards (id) ON DELETE CASCADE,
    search_text  TEXT NOT NULL COLLATE NOCASE
);

CREATE INDEX IF NOT EXISTS ix_card_search_text ON card_search (search_text);

-- Rebuild one card's flattened text.
-- [T-SQL] group_concat() is SQLite's STRING_AGG(). Argument order is reversed:
-- group_concat(expr, separator) vs STRING_AGG(expr, separator). SQLite's version
-- has no WITHIN GROUP (ORDER BY ...) clause before 3.44; use a subquery if order
-- matters. Here it does not.
CREATE TRIGGER IF NOT EXISTS trg_card_search_after_block_insert
AFTER INSERT ON card_blocks
BEGIN
    INSERT INTO card_search (card_id, search_text)
    VALUES (
        NEW.card_id,
        (SELECT COALESCE(group_concat(b.text, ' '), '')
         FROM card_blocks b
         WHERE b.card_id = NEW.card_id AND b.text IS NOT NULL)
    )
    -- [T-SQL] This is SQLite's upsert. There is no MERGE, and no
    -- IF EXISTS ... UPDATE ELSE INSERT inside a trigger body without extra
    -- statements. ON CONFLICT (col) DO UPDATE SET x = excluded.x is the idiom;
    -- `excluded` is the row that would have been inserted.
    ON CONFLICT (card_id) DO UPDATE SET search_text = excluded.search_text;
END;

CREATE TRIGGER IF NOT EXISTS trg_card_search_after_block_update
AFTER UPDATE ON card_blocks
BEGIN
    INSERT INTO card_search (card_id, search_text)
    VALUES (
        NEW.card_id,
        (SELECT COALESCE(group_concat(b.text, ' '), '')
         FROM card_blocks b
         WHERE b.card_id = NEW.card_id AND b.text IS NOT NULL)
    )
    ON CONFLICT (card_id) DO UPDATE SET search_text = excluded.search_text;
END;

-- The WHEN guard matters. Deleting a flashcard cascades to card_blocks, which
-- fires this trigger; without the guard it would re-insert a card_search row
-- pointing at a card that no longer exists and blow up on the foreign key.
CREATE TRIGGER IF NOT EXISTS trg_card_search_after_block_delete
AFTER DELETE ON card_blocks
WHEN EXISTS (SELECT 1 FROM flashcards f WHERE f.id = OLD.card_id)
BEGIN
    INSERT INTO card_search (card_id, search_text)
    VALUES (
        OLD.card_id,
        (SELECT COALESCE(group_concat(b.text, ' '), '')
         FROM card_blocks b
         WHERE b.card_id = OLD.card_id AND b.text IS NOT NULL)
    )
    ON CONFLICT (card_id) DO UPDATE SET search_text = excluded.search_text;
END;

-- Backfill anything that already exists.
INSERT INTO card_search (card_id, search_text)
SELECT c.id, COALESCE((SELECT group_concat(b.text, ' ')
                       FROM card_blocks b
                       WHERE b.card_id = c.id AND b.text IS NOT NULL), '')
FROM flashcards c
WHERE NOT EXISTS (SELECT 1 FROM card_search s WHERE s.card_id = c.id);
