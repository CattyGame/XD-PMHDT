#!/usr/bin/env bash
set -euo pipefail
: "${REPORTING_WRITER_PASSWORD:?Set the Reporting writer password outside Git}"
psql --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" --set ON_ERROR_STOP=1 <<'SQL'
\getenv writer_password REPORTING_WRITER_PASSWORD
SELECT format('ALTER ROLE aura_reporting_writer LOGIN PASSWORD %L', :'writer_password') \gexec
SQL
