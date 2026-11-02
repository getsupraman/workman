#!/usr/bin/env bash
# Nightly pg_dump of the Workman database (custom format, restorable with pg_restore). Keeps the
# newest $KEEP dumps in $DIR. Run by workman-backup.timer; run it by hand before any upgrade.
set -euo pipefail

DIR=${WORKMAN_BACKUP_DIR:-/var/backups/workman}
KEEP=${WORKMAN_BACKUP_KEEP:-14}

mkdir -p "$DIR"
chmod 700 "$DIR"
file="$DIR/workman-$(date -u +%Y%m%dT%H%M%SZ).dump"

# The container's own POSTGRES_USER / POSTGRES_DB, whatever the .env named them.
docker exec workman-db-1 sh -c 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc' > "$file.part"
mv "$file.part" "$file"
echo "backup $file $(stat -c %s "$file") bytes"

ls -1t "$DIR"/workman-*.dump | tail -n +$((KEEP + 1)) | xargs -r rm --
