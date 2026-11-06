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

The server has no static role or reader token: people sign in with OAuth, and agents use the per-session
tokens a signed-in session mints.

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

## Authentication failures in the log

To see why a client's requests are refused, add `WORKMAN_AUTH_LOG_LEVEL=Information` to `.env` and
`app deploy workman`. Each refused request then logs its scheme and reason in `docker logs workman-api-1`:
`was not authenticated. Failure message: Unrecognised bearer token.` for a token the server does not
accept, and only `was challenged` for a request that carried no `Authorization` header. Every request that ends 401 also logs one line, whatever this level, naming its method, path,
`X-Workman-Client`, `X-Workman-Agent`, `User-Agent`, whether it carried an `Authorization` header and the
reason. Token values are never logged. Remove the line (default `Warning`) when done.

## Restore

```bash
docker exec -i workman-db-1 sh -c 'pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" --clean --if-exists' < /var/backups/workman/<file>.dump
docker restart workman-api-1
```
