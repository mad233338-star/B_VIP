-- ============================================================================
--  B_VIP Tool - PostgreSQL UUID / INTEGER type-mismatch repair
--  Symptom in the Render logs:
--      DB query error: invalid input syntax for type integer: 'c7b3...'
--
--  What it means: a UUID string is being sent to a column that Postgres still
--  declares as integer. Node's pg driver sends the parameter as text, so the
--  server tries to coerce 'c7b3...' to integer and fails. It is never a
--  parameter-binding bug: the *column type* is wrong for the data model.
--
--  Why it survives restarts: a `CREATE TABLE IF NOT EXISTS` (or a migration
--  that was already recorded as applied) does nothing to an existing table.
--  The integer column created by the first deploy stays integer forever.
--
--  Run with psql against the Render external connection string:
--      psql "<External Database URL>" -v ON_ERROR_STOP=1 -f postgres-uuid-fix.sql
--  Every section below is independent; run the one you need.
--  Target: PostgreSQL 13+ (gen_random_uuid is built in).
-- ============================================================================


-- ----------------------------------------------------------------------------
-- 1. DIAGNOSE (read-only - run this first, it changes nothing)
-- ----------------------------------------------------------------------------

-- 1a. Every integer column in the schema. The offender is in this list.
SELECT table_name,
       column_name,
       data_type,
       is_nullable
FROM information_schema.columns
WHERE table_schema = 'public'
  AND data_type IN ('integer', 'bigint', 'smallint')
ORDER BY table_name, ordinal_position;

-- 1b. Columns that are *supposed* to hold UUIDs, and what they actually are.
--     Any row here whose data_type is integer/bigint is the bug.
SELECT table_name,
       column_name,
       data_type,
       udt_name
FROM information_schema.columns
WHERE table_schema = 'public'
  AND (column_name = 'id' OR column_name LIKE '%\_id' ESCAPE '\')
ORDER BY table_name, column_name;

-- 1c. Existing foreign keys, so you know what a type change has to respect.
SELECT conrelid::regclass AS "table",
       conname            AS constraint_name,
       pg_get_constraintdef(oid) AS definition
FROM pg_constraint
WHERE contype = 'f'
  AND connamespace = 'public'::regnamespace
ORDER BY conrelid::regclass::text, conname;


-- ----------------------------------------------------------------------------
-- 2. RECOMMENDED for a fresh deployment: rebuild the schema (DESTRUCTIVE)
--
--    Use this when the database only holds the seeded admin, plans and a few
--    test technicians - i.e. the situation where the error appeared right
--    after the admin logged in. The API recreates the schema through its
--    migrations and re-seeds the accounts/plans on the next boot, so the UUID
--    definitions are correct and no manual column work is needed.
--
--    !! Deletes all rows in the public schema. Take a Render backup first. !!
-- ----------------------------------------------------------------------------

-- BEGIN;
-- DROP SCHEMA public CASCADE;
-- CREATE SCHEMA public;
-- GRANT ALL ON SCHEMA public TO PUBLIC;
-- COMMIT;
-- Then restart the Render service (or redeploy) so the migrations run on the
-- empty schema and the seeder writes the admin account and license plans.


-- ----------------------------------------------------------------------------
-- 3. IN-PLACE conversion (keeps data)
--
--    Converts an integer id column to uuid and rewrites its foreign keys with
--    the *same* deterministic mapping, so relationships survive:
--        integer 7  in "users"          -> md5('users:7')::uuid
--        integer 7  in "licenses.user_id" -> md5('users:7')::uuid
--    (Postgres accepts a 32-character unhyphenated uuid.)
--
--    Order matters: drop the foreign keys, convert parent then child columns,
--    recreate the foreign keys. Adapt the table/column names to your schema
--    first - section 1a tells you exactly which ones are integer.
-- ----------------------------------------------------------------------------

-- 3a. Inspect the keys you are about to drop (run 1c and copy the definitions).
-- 3b. Drop only the foreign keys that touch the columns you convert:
--
--   ALTER TABLE licenses DROP CONSTRAINT IF EXISTS licenses_user_id_fkey;
--   ALTER TABLE licenses DROP CONSTRAINT IF EXISTS licenses_plan_id_fkey;
--   ALTER TABLE device_service_operations
--     DROP CONSTRAINT IF EXISTS device_service_operations_user_id_fkey;
--
-- 3c. Convert. One statement per column; keep the first argument in md5()
--     equal to the *parent table name* for every column that references it.
--
--   BEGIN;
--
--   ALTER TABLE users
--     ALTER COLUMN id TYPE uuid USING (md5('users:' || id::text)::uuid);
--
--   ALTER TABLE licenses
--     ALTER COLUMN id         TYPE uuid USING (md5('licenses:' || id::text)::uuid),
--     ALTER COLUMN user_id    TYPE uuid USING (md5('users:' || user_id::text)::uuid),
--     ALTER COLUMN license_plan_id
--                             TYPE uuid USING (md5('license_plans:' || license_plan_id::text)::uuid);
--
--   ALTER TABLE license_plans
--     ALTER COLUMN id TYPE uuid USING (md5('license_plans:' || id::text)::uuid);
--
--   COMMIT;
--
-- 3d. Recreate the keys, now uuid to uuid:
--
--   ALTER TABLE licenses
--     ADD CONSTRAINT licenses_user_id_fkey
--       FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE,
--     ADD CONSTRAINT licenses_plan_id_fkey
--       FOREIGN KEY (license_plan_id) REFERENCES license_plans(id) ON DELETE RESTRICT;
--
-- 3e. If the column had a sequence/serial default, it is gone with the type
--     change. Give uuid ids a proper default:
--   ALTER TABLE users ALTER COLUMN id SET DEFAULT gen_random_uuid();
--
-- 3f. Indexes and primary keys do not need to be dropped: they follow the
--     column. Re-run 1a afterwards - it must return no id/user_id integer rows.


-- ----------------------------------------------------------------------------
-- 4. Keep the Node layer from re-creating the mismatch
--
-- 4a. DDL must declare uuids, not serials. `CREATE TABLE IF NOT EXISTS` will
--     never migrate an existing column, so a first deploy with INTEGER/SERIAL
--     is permanent until section 2 or 3 runs:
--
--   CREATE TABLE IF NOT EXISTS users (
--     id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
--     user_name     VARCHAR(64)  NOT NULL,
--     normalized_user_name VARCHAR(64) NOT NULL UNIQUE,
--     email         VARCHAR(256) NOT NULL,
--     normalized_email      VARCHAR(256) NOT NULL UNIQUE,
--     password_hash VARCHAR(256) NOT NULL,
--     status        VARCHAR(32)  NOT NULL DEFAULT 'Active',
--     created_utc   TIMESTAMPTZ  NOT NULL DEFAULT now()
--   );
--
-- 4b. Every parameter that carries an id is cast to the column's real type.
--     The cast is what turns a wrong value into a clear error instead of a
--     driver-level surprise:
--
--   -- admin dashboard: one technician
--   SELECT * FROM users WHERE id = $1::uuid;
--
--   -- admin dashboard: technicians + their current licence
--   SELECT u.id, u.user_name, u.email, u.hardware_id, u.status,
--          p.name AS plan_name, l.expires_at, l.status AS license_status
--     FROM users u
--     LEFT JOIN LATERAL (
--       SELECT * FROM licenses
--        WHERE user_id = u.id
--        ORDER BY created_utc DESC
--        LIMIT 1
--     ) l ON TRUE
--     LEFT JOIN license_plans p ON p.id = l.license_plan_id
--    WHERE ($1::text IS NULL OR u.user_name ILIKE '%' || $1 || '%'
--                            OR u.email     ILIKE '%' || $1 || '%')
--    ORDER BY u.created_utc DESC
--    LIMIT $2 OFFSET $3;
--
-- 4c. Validate before you query, so a malformed or numeric id never reaches
--     the database (and never produces the integer-syntax error):
--
--   const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
--   if (!UUID_RE.test(req.params.id)) {
--     return res.status(400).json({ error: { code: 'invalid-id', message: 'Invalid technician id.' } });
--   }
--
-- 4d. Never coerce an id with parseInt/Number: a UUID is not a number, and the
--     only thing that coercion can produce is NaN or a truncated wrong row.
--     Pick one type per id and use it on both sides of every join - uuid here.
--
-- 4e. Dashboard counters (no id parameters at all - these must never throw):
--
--   SELECT
--     (SELECT count(*) FROM users
--       WHERE status = 'Active')                                        AS total_technicians,
--     (SELECT count(*) FROM licenses
--       WHERE status = 'Active'
--         AND (expires_at IS NULL OR expires_at > now()))               AS active_subscriptions,
--     (SELECT count(*) FROM licenses
--       WHERE expires_at IS NOT NULL AND expires_at <= now())           AS expired_accounts,
--     (SELECT count(*) FROM device_service_operations
--       WHERE "QueuedUtc" >= date_trunc('day', now() AT TIME ZONE 'utc')) AS operations_today;
--     -- the .NET schema quotes PascalCase columns; a snake_case schema uses
--     -- queued_utc instead. Match the column name to what section 1a showed.


-- ----------------------------------------------------------------------------
-- 5. VERIFY
-- ----------------------------------------------------------------------------

-- 5a. No id/user_id column may be integer any more.
SELECT table_name, column_name, data_type
FROM information_schema.columns
WHERE table_schema = 'public'
  AND (column_name = 'id' OR column_name LIKE '%\_id' ESCAPE '\')
  AND data_type IN ('integer', 'bigint', 'smallint');

-- 5b. The exact query shape that failed, with a real uuid. Must return 0+ rows
--     and never "invalid input syntax for type integer".
-- SELECT id, user_name FROM users WHERE id = 'c7b3f0e2-0000-4000-8000-000000000000'::uuid;

-- 5c. Foreign keys are back and point at uuid columns.
SELECT conrelid::regclass AS "table", conname, pg_get_constraintdef(oid) AS definition
FROM pg_constraint
WHERE contype = 'f' AND connamespace = 'public'::regnamespace
ORDER BY 1, 2;
