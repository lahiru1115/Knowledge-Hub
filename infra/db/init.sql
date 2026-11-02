-- Runs once on first start (docker-entrypoint-initdb.d) or once by the project owner on Neon.
-- Doc 02 §7.1. Passwords here are local-development defaults only; override in any shared environment.
CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS citext;

CREATE ROLE core_migrator LOGIN PASSWORD 'core_migrator_dev';
CREATE ROLE core_app      LOGIN PASSWORD 'core_app_dev';
CREATE ROLE ai_migrator   LOGIN PASSWORD 'ai_migrator_dev';
CREATE ROLE ai_app        LOGIN PASSWORD 'ai_app_dev';

CREATE SCHEMA core AUTHORIZATION core_migrator;
CREATE SCHEMA ai   AUTHORIZATION ai_migrator;

REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO core_migrator, ai_migrator, core_app, ai_app; -- extension types (vector, citext)
GRANT USAGE ON SCHEMA core TO core_app;
GRANT USAGE ON SCHEMA ai   TO ai_app;

ALTER ROLE core_migrator SET search_path = core, public;
ALTER ROLE core_app      SET search_path = core, public;
ALTER ROLE ai_migrator   SET search_path = ai, public;
ALTER ROLE ai_app        SET search_path = ai, public;

ALTER DEFAULT PRIVILEGES FOR ROLE core_migrator IN SCHEMA core
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO core_app;
ALTER DEFAULT PRIVILEGES FOR ROLE core_migrator IN SCHEMA core
  GRANT USAGE, SELECT ON SEQUENCES TO core_app;
ALTER DEFAULT PRIVILEGES FOR ROLE ai_migrator IN SCHEMA ai
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ai_app;
ALTER DEFAULT PRIVILEGES FOR ROLE ai_migrator IN SCHEMA ai
  GRANT USAGE, SELECT ON SEQUENCES TO ai_app;
