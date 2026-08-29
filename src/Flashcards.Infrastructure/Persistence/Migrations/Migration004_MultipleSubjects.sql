-- ---------------------------------------------------------------------------
-- A card can wear several subject tags.
--
-- "SQL Server internals" is reasonably tagged both SQL and Databases, so the
-- one-subject-per-card foreign key becomes a join table. Every existing card
-- keeps exactly the tag it already had.
--
-- [T-SQL] The interesting part is the teardown. SQLite only gained
-- ALTER TABLE DROP COLUMN in 3.35, and it still refuses when the column is
-- indexed -- so the two indexes that mention subject_id have to go first, in
-- this order. Anything more involved than dropping a column (changing a type,
-- dropping a constraint) has no ALTER at all and needs the twelve-step
-- create-copy-drop-rename dance the SQLite docs spell out.
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS card_subjects (
    card_id    TEXT NOT NULL REFERENCES flashcards (id) ON DELETE CASCADE,
    subject_id TEXT NOT NULL REFERENCES subjects   (id) ON DELETE CASCADE,
    PRIMARY KEY (card_id, subject_id)
);

-- The PK already indexes (card_id, subject_id). This is the other direction,
-- for "every card wearing this tag", which is what the quiz queue and the
-- subject counts both ask.
CREATE INDEX IF NOT EXISTS ix_card_subjects_subject ON card_subjects (subject_id, card_id);

INSERT OR IGNORE INTO card_subjects (card_id, subject_id)
SELECT id, subject_id FROM flashcards;

-- ux_flashcards_subject_name enforced "one card of this name per subject" in
-- the schema. That rule survives -- see IFlashcardRepository.ExistsWithNameAsync,
-- which now asks it across every tag the card wears -- but it can no longer be a
-- single unique index, because the pair it constrains lives in two tables.
DROP INDEX IF EXISTS ux_flashcards_subject_name;
DROP INDEX IF EXISTS ix_flashcards_subject;

ALTER TABLE flashcards DROP COLUMN subject_id;
