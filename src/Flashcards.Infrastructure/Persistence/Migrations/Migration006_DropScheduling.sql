-- ---------------------------------------------------------------------------
-- Scheduling is gone; the answer history stays.
--
-- The app no longer decides when a card is due, so review_states -- which held
-- nothing but SM-2 bookkeeping (interval, ease, repetitions, lapses, due date)
-- -- has no reader left and is dropped outright.
--
-- review_log is a different matter. It is the only record of what was answered
-- and whether it was right, which is exactly what the statistics on the manage
-- screen are built from. So it is rebuilt rather than dropped: the scheduling
-- columns come off, and was_correct is promoted from nullable to required.
--
-- Existing rows are preserved. Where was_correct was never set, it is recovered
-- from the old grade: the four-point scale ran Again=0, Hard=3, Good=4, Easy=5,
-- and anything at or above Hard was a successful recall. That keeps whatever
-- history a user already had counting towards their percentages instead of
-- silently resetting them to zero.
--
-- [T-SQL] Dropping a column would be ALTER TABLE ... DROP COLUMN in both
-- dialects, but making a nullable column NOT NULL is ALTER COLUMN in T-SQL and
-- has no equivalent here at all -- SQLite can only change a column's
-- nullability by rebuilding the table, which is what this does.
-- ---------------------------------------------------------------------------

DROP INDEX IF EXISTS ix_review_states_due;
DROP TABLE IF EXISTS review_states;

CREATE TABLE review_log_new (
    -- [T-SQL] IDENTITY(1,1) becomes INTEGER PRIMARY KEY AUTOINCREMENT. The type
    -- must be exactly INTEGER (not INT, not BIGINT) for this to alias rowid.
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    card_id      TEXT    NOT NULL REFERENCES flashcards (id) ON DELETE CASCADE,
    reviewed_utc TEXT    NOT NULL,
    was_correct  INTEGER NOT NULL CHECK (was_correct IN (0, 1)),
    elapsed_ms   INTEGER NOT NULL
);

INSERT INTO review_log_new (id, card_id, reviewed_utc, was_correct, elapsed_ms)
SELECT id,
       card_id,
       reviewed_utc,
       -- Trust an explicitly recorded answer; otherwise read it out of the grade.
       COALESCE(was_correct, CASE WHEN grade >= 3 THEN 1 ELSE 0 END),
       elapsed_ms
FROM   review_log;

DROP INDEX IF EXISTS ix_review_log_card;
DROP INDEX IF EXISTS ix_review_log_date;

DROP TABLE review_log;

ALTER TABLE review_log_new RENAME TO review_log;

-- (card_id, reviewed_utc) serves both "this card's history" and "this card's
-- most recent answer"; the date-only index serves the overall totals.
CREATE INDEX IF NOT EXISTS ix_review_log_card ON review_log (card_id, reviewed_utc);
CREATE INDEX IF NOT EXISTS ix_review_log_date ON review_log (reviewed_utc);
