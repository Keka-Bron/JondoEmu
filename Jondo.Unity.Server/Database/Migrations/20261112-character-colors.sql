-- Registered with DatabaseManager.Initialize(), after verified backup and before character seeding.
-- Target: world.db. No attached databases or connection to auth.db are required.
--
-- CharacterColorsMigration.Apply supplies a single transaction, checks the named
-- migration before running this script, and registers jondo_look_color.
-- That function decodes the historical protobuf palette; it is NOT a built-in
-- SQLite function. This script cannot be run directly in a stock SQLite shell.
-- $migrationName and $appliedAt are bound parameters supplied by the caller.
-- For a fresh database (six colour columns, no Look), the helper validates that
-- schema and executes only the Migration history section. There is one marker.
--
-- There are exactly six colour slots, represented by Color1 through Color6.
-- Each stores plain RGB (0..16777215), without a packed slot index.
-- NULL means use the breed/sex default for that slot.
-- Bare-RGB palettes fill columns in order; indexed palettes use their slot index.
-- The decoder prefers packed field 1, then packed field 4 in the original seed
-- layout (scalar field 1 = 1 and scalar field 3 = 3).
-- Duplicate or out-of-range indexed slots and palettes longer than six entries
-- abort the migration instead of silently discarding values.
-- Adding the columns, backfilling, dropping Look and recording completion commit
-- together. Any failure rolls the entire change back to the original schema/data.

ALTER TABLE Characters ADD COLUMN Color1 INTEGER
    CHECK (Color1 IS NULL OR (typeof(Color1) = 'integer' AND Color1 BETWEEN 0 AND 16777215));

ALTER TABLE Characters ADD COLUMN Color2 INTEGER
    CHECK (Color2 IS NULL OR (typeof(Color2) = 'integer' AND Color2 BETWEEN 0 AND 16777215));

ALTER TABLE Characters ADD COLUMN Color3 INTEGER
    CHECK (Color3 IS NULL OR (typeof(Color3) = 'integer' AND Color3 BETWEEN 0 AND 16777215));

ALTER TABLE Characters ADD COLUMN Color4 INTEGER
    CHECK (Color4 IS NULL OR (typeof(Color4) = 'integer' AND Color4 BETWEEN 0 AND 16777215));

ALTER TABLE Characters ADD COLUMN Color5 INTEGER
    CHECK (Color5 IS NULL OR (typeof(Color5) = 'integer' AND Color5 BETWEEN 0 AND 16777215));

ALTER TABLE Characters ADD COLUMN Color6 INTEGER
    CHECK (Color6 IS NULL OR (typeof(Color6) = 'integer' AND Color6 BETWEEN 0 AND 16777215));

UPDATE Characters
SET Color1 = jondo_look_color(Look, 1),
    Color2 = jondo_look_color(Look, 2),
    Color3 = jondo_look_color(Look, 3),
    Color4 = jondo_look_color(Look, 4),
    Color5 = jondo_look_color(Look, 5),
    Color6 = jondo_look_color(Look, 6);

ALTER TABLE Characters DROP COLUMN Look;

-- Migration history
-- Shared by the legacy upgrade above and fresh databases already in final form.
CREATE TABLE IF NOT EXISTS JondoMigrations (
    Name TEXT PRIMARY KEY,
    AppliedAt TEXT NOT NULL
);

INSERT INTO JondoMigrations (Name, AppliedAt)
VALUES ($migrationName, $appliedAt);
