# Workman on a multi-deploy server

Runs the published image `ghcr.io/getsupraman/workman` behind
[multi-deploy](https://github.com/r-Larch/multi-deploy)'s Traefik, with HTTPS from Let's Encrypt.
The image is the same one a laptop runs. Only `.env` differs: the public origin, the trusted proxy
and this server's own secrets.

## First install (as root on the server)

```bash
mkdir -p /opt/multi-deploy/apps/workman
cd /opt/multi-deploy/apps/workman
for f in compose.yml app.env gen-env.sh backup.sh workman-backup.service workman-backup.timer; do
  curl -fsSLO "https://raw.githubusercontent.com/getsupraman/workman/main/server/deploy/multi-deploy/$f"
done
chmod +x gen-env.sh backup.sh

./gen-env.sh workman.example.com 2.0.0   # random secrets, generated here; prints none of them
app deploy workman                       # pulls the image, starts db + api, Traefik picks it up

cp workman-backup.service workman-backup.timer /etc/systemd/system/
systemctl daemon-reload && systemctl enable --now workman-backup.timer
./backup.sh                              # the first dump, and a check that backups work
```

DNS for the host must point at the server before `app deploy`, or Let's Encrypt cannot issue the
certificate.

## Check it

```bash
curl -s -o /dev/null -w "%{http_code}\n" https://workman.example.com/health        # 200
curl -s -D - -o /dev/null -X POST https://workman.example.com/mcp \
  -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize"}' | grep -i www-auth
# WWW-Authenticate: Bearer resource_metadata="https://workman.example.com/.well-known/oauth-protected-resource/mcp"
```

If the challenge names `http://` instead of `https://`, Traefik's address is not in
`WORKMAN_TRUSTED_PROXIES`. Compare it with `docker network inspect web`.

## Upgrade

```bash
cd /opt/multi-deploy/apps/workman
./backup.sh
sed -i 's/^WORKMAN_VERSION=.*/WORKMAN_VERSION=<new>/' .env
app deploy workman
```

Every connected Claude Code session then needs `/mcp` → Reconnect: MCP sessions live in the
process.

The server accepts no static role token (doc 08 §4): `compose.yml` passes none, and `gen-env.sh`
writes none.

## Upgrading a 1.x workstate app to 2.0

Workman 2.0 reads none of the 1.x names: not the `WORKSTATE_*` keys, not the `WorkState__*` settings,
and the compose project, volume, Traefik router and backup units are all called `workman` now. So a
1.x app in `apps/workstate` is not upgraded in place. It moves to a **new app folder** `apps/workman`,
with its data dumped and restored, and the old app stays on disk, stopped, until the new one is
verified. Downtime is the dump, the restore and one start.

1. **Take a last 1.x backup and stop the 1.x api**, so nothing writes after the dump:

   ```bash
   cd /opt/multi-deploy/apps/workstate
   ./backup.sh                                    # writes /var/backups/workstate/workstate-<time>.dump
   docker stop workstate-api-1
   docker exec workstate-db-1 sh -c 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc' > /root/workstate-final.dump
   ```

2. **Create `apps/workman`** with the 2.0 files, as in *First install*, but **do not run `gen-env.sh`**:
   the new `.env` is the old one with its key names changed. `sed` rewrites the start of each line and
   prints nothing; the values, including `POSTGRES_DB`, `POSTGRES_USER` and `POSTGRES_PASSWORD`, stay as
   they were, so the restored database keeps its owner:

   ```bash
   mkdir -p /opt/multi-deploy/apps/workman && cd /opt/multi-deploy/apps/workman
   for f in compose.yml app.env gen-env.sh backup.sh workman-backup.service workman-backup.timer; do
     curl -fsSLO "https://raw.githubusercontent.com/getsupraman/workman/main/server/deploy/multi-deploy/$f"
   done
   chmod +x gen-env.sh backup.sh
   (umask 077; sed -e 's/^WORKSTATE_/WORKMAN_/' ../workstate/.env > .env)
   sed -i 's/^WORKMAN_VERSION=.*/WORKMAN_VERSION=2.0.0/' .env
   sed -i '/^WORKMAN_\(WORKER\|ORCHESTRATOR\|CONTROLLER\)_TOKEN=/d' .env   # unused since 1.0.0
   ```

3. **Restore into the new, empty volume** before the 2.0 api ever starts. The 1.x row-level-security
   policies name the role `workstate_app`; roles belong to the cluster, not the dump, so create it
   first. `--single-transaction` restores everything or nothing:

   ```bash
   docker compose -p workman --env-file .env -f compose.yml up -d db
   docker exec workman-db-1 sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc "SELECT count(*) FROM pg_tables WHERE schemaname = '"'"'public'"'"'"'
   # must print 0; anything else means this volume is not empty: stop here
   docker exec workman-db-1 sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "CREATE ROLE workstate_app NOLOGIN"'
   docker exec -i workman-db-1 sh -c 'pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" --single-transaction --exit-on-error --no-owner' < /root/workstate-final.dump
   ```

4. **Stop the 1.x app and start the 2.0 one.** Both route the same host, so the old one goes first. Its
   containers and its volume `workstate_workstate-db-data` stay:

   ```bash
   docker stop workstate-db-1
   app deploy workman              # the 2.0 api migrates the restored database on start
   curl -s -o /dev/null -w "%{http_code}\n" https://workman.example.com/health   # 200
   ```

5. **Move the backups** to the 2.0 units:

   ```bash
   systemctl disable --now workstate-backup.timer
   rm /etc/systemd/system/workstate-backup.service /etc/systemd/system/workstate-backup.timer
   cp workman-backup.service workman-backup.timer /etc/systemd/system/
   systemctl daemon-reload && systemctl enable --now workman-backup.timer
   ./backup.sh
   ```

6. **Verify, then remove the 1.x app.** Sign in to the dashboard and check that the projects and their
   items are all there. Every client signs in again: the 2.0 server's key ring is new, so 1.x cookies,
   OAuth grants and refresh tokens no longer open it (`/mcp` → Authenticate; `agent_signin` again for
   agents). Only then:

   ```bash
   docker rm workstate-api-1 workstate-db-1
   docker volume rm workstate_workstate-db-data
   rm -r /opt/multi-deploy/apps/workstate
   ```

   Until then, going back is `docker stop workman-api-1 workman-db-1` and
   `docker start workstate-db-1 workstate-api-1`.

## More than one host

The same server can answer on several names, each its own OAuth issuer: a client signed in at one name
keeps its token there, and a sign-in at another name works at that name. Point DNS for each host at the
server, then add to `.env` (placeholders shown):

```bash
WORKMAN_PUBLIC_URLS=https://workman.example.org
WORKMAN_HOSTS=Host(`workman.example.com`) || Host(`workman.example.org`)
```

`WORKMAN_PUBLIC_URL` stays the first origin, and the one a request naming no configured host is
answered as. `WORKMAN_HOSTS` is the Traefik rule for both routers (unset: `Host(WORKMAN_HOST)`), so
Let's Encrypt issues a certificate for every host it names. Then `app deploy workman`.

## Restore

```bash
docker exec -i workman-db-1 sh -c 'pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" --clean --if-exists' < /var/backups/workman/<file>.dump
docker restart workman-api-1
```
