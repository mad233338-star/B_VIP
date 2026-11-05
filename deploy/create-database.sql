-- B_VIP Software - database bootstrap (PostgreSQL 14+)
--
-- Run as a superuser, passing the application password as a psql variable so it never
-- appears in this file:
--
--   psql -h 127.0.0.1 -p 5432 -U postgres -v app_password='<strong password>' -f deploy/create-database.sql
--
-- Optional overrides: -v db_name=b_vip -v role_name=b_vip_app
--
-- The backend applies the schema itself on first start (EF Core migrations), so this script
-- only creates the login role and the empty database it owns.

\set ON_ERROR_STOP on

\if :{?app_password}
\else
  \echo 'Missing -v app_password=... ; refusing to create a role without a password.'
  \quit 1
\endif

\if :{?db_name}
\else
  \set db_name b_vip
\endif

\if :{?role_name}
\else
  \set role_name b_vip_app
\endif

-- Application login role. Never use the postgres superuser for the backend.
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', :'role_name', :'app_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'role_name')
\gexec

SELECT format('ALTER ROLE %I WITH LOGIN PASSWORD %L', :'role_name', :'app_password')
\gexec

-- Database owned by the application role.
SELECT format('CREATE DATABASE %I OWNER %I ENCODING ''UTF8''', :'db_name', :'role_name')
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = :'db_name')
\gexec

\connect :db_name

-- Keep the schema tidy: the application role owns it, nobody else may create objects.
SELECT format('ALTER SCHEMA public OWNER TO %I', :'role_name')
\gexec

REVOKE ALL ON SCHEMA public FROM PUBLIC;

SELECT format('GRANT USAGE, CREATE ON SCHEMA public TO %I', :'role_name')
\gexec

\echo 'Database and application role are ready. Start the backend once to apply migrations and seed data.'
