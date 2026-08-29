-- ---------------------------------------------------------------------------
-- Multiple-choice answers can be pictures, not just words.
--
-- The MC designer offers four answer slots you can either type into or drop an
-- image onto, so a choice now carries an optional media reference alongside its
-- text. Both may be present (a captioned picture); at least one must be, which
-- is a rule the aggregate enforces rather than a CHECK constraint, because the
-- domain wants to phrase the failure in the user's language.
--
-- [T-SQL] SQLite's ALTER TABLE ADD COLUMN is a metadata-only operation and is
-- always cheap, but it is also nearly all SQLite lets you do: there is no
-- ALTER COLUMN, no DROP CONSTRAINT, and dropping a column only arrived in
-- 3.35. Reshaping anything else means the twelve-step dance of creating a new
-- table, copying, dropping, renaming. Adding a nullable column is the one
-- migration that stays a one-liner in both dialects.
--
-- ON DELETE SET NULL rather than CASCADE: losing the image should blank the
-- picture on the answer, not silently delete the answer itself.
-- ---------------------------------------------------------------------------

ALTER TABLE card_choices
    ADD COLUMN media_id TEXT NULL REFERENCES media (id) ON DELETE SET NULL;

-- The media garbage collector asks "does anything still point at this row?", so
-- it needs to reach choices by media id, not just by card.
CREATE INDEX IF NOT EXISTS ix_card_choices_media ON card_choices (media_id);
