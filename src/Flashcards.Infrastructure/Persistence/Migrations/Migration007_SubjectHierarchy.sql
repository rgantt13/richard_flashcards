-- ---------------------------------------------------------------------------
-- Subjects become a tree.
--
-- "What does ACID stand for" belongs to SQL generally, not to MSSQL or SQLite
-- separately, so subjects gain a parent and a card tagged with a child is
-- understood to wear every ancestor too.
--
-- That ancestry is *derived*, never stored: card_subjects still holds exactly
-- the tag the user applied, and the walk up the tree happens at query time.
-- The alternative -- writing a row per ancestor -- would mean re-parenting a
-- subject had to rewrite every card beneath it, and any bug in that rewrite
-- would silently mis-tag cards with no way to tell the copy from the original.
--
-- [T-SQL] A self-referencing FK is the one kind of ALTER TABLE ADD COLUMN
-- SQLite will still take, because every existing row gets NULL and NULL
-- satisfies the reference. Adding a NOT NULL column with a foreign key would
-- need the full create-copy-drop-rename dance instead.
--
-- ON DELETE SET NULL is a backstop, not the intended path: deleting a subject
-- is meant to promote its children into its own place, which the delete
-- command does explicitly. This only catches a row removed some other way, and
-- it fails safe -- an orphan surfaces at the top of the tree rather than
-- disappearing from it.
-- ---------------------------------------------------------------------------

ALTER TABLE subjects ADD COLUMN parent_id TEXT NULL REFERENCES subjects (id) ON DELETE SET NULL;

-- "Children of X" is asked once per node while the tree is built, on every
-- panel that shows subjects.
CREATE INDEX IF NOT EXISTS ix_subjects_parent ON subjects (parent_id);
