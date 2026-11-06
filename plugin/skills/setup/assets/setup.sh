#!/usr/bin/env bash
# setup.sh - install, start, register and prove a local Workman server, or register and prove a remote one.
# One script for every OS: bash on Linux and macOS, Git Bash on Windows (Claude Code's Bash tool).
#
# Every line this prints is safe to show a model: the passwords stay in the .env file and nothing here
# echoes them. It never runs `claude mcp get` or `claude mcp list`: both health-check the server without
# a token, which caches a needs-auth entry that blocks agent workers. The existing registration is read
# from Claude Code's config file instead, its url only (find-shadows.mjs --registered).
set -euo pipefail

usage() {
    cat <<'EOF'
usage: setup.sh [--clone <path>] [--port <port>] [--image-version <tag>] [--update] [--force] [--dry-run]
       setup.sh --url <https origin> [--force] [--dry-run]
       setup.sh --backup <file> | --restore <file>  [--clone <path>]

  --url <origin>          register a REMOTE server, e.g. https://work.supraman.ai: no Docker, no .env
  --clone <path>          use a clone of github.com/getsupraman/workman: <path>/server/compose.yml (builds
                          the image) and <path>/server/.env. Without it: ~/.workman (pulls the image)
  --port <port>           host port, written only into a NEW .env (default 5290)
  --image-version <tag>   image tag for the pull-only compose (default latest)
  --update                pull the image again (home) or rebuild it (clone) before starting
  --force                 replace an existing 'workman' registration (with --url: re-point it)
  --dry-run               write nothing, start nothing, register nothing; prove what is up and show
                          the registration it would write
  --backup <file>         pg_dump -Fc the database to <file>, and stop
  --restore <file>        restore <file> over the database (after a safety backup), and stop

Every registration is 'workman' at user scope with OAuth sign-in and the session helper; each one
also writes ~/.workman/server.json: {"url": "<origin>", "mcpName": "workman"}.
EOF
}

die() { printf '%s\n' "$1" >&2; exit 1; }
status() { printf '%-10s%s\n' "$1" "$2"; }

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
name="workman"; project="workman"; home_dir="$HOME/.workman"
clone=""; port="5290"; port_given=0; image_version=""; update=0; force=0; dry_run=0
backup=""; restore=""; url=""

while [ $# -gt 0 ]; do
    case "$1" in
        --url) url="${2:?--url needs an https origin}"; shift 2 ;;
        --clone) clone="${2:?--clone needs a path}"; shift 2 ;;
        --port) port="${2:?--port needs a value}"; port_given=1; shift 2 ;;
        --image-version) image_version="${2:?--image-version needs a tag}"; shift 2 ;;
        --update) update=1; shift ;;
        --force) force=1; shift ;;
        --dry-run) dry_run=1; shift ;;
        --backup) backup="${2:?--backup needs a file}"; shift 2 ;;
        --restore) restore="${2:?--restore needs a file}"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) usage >&2; die "unknown argument: $1" ;;
    esac
done

case "$port" in ''|*[!0-9]*) die "--port must be a number" ;; esac
[ -n "$backup" ] && [ -n "$restore" ] && die "--backup and --restore: pass one"
if [ -n "$url" ]; then
    while [ "${url%/}" != "$url" ]; do url="${url%/}"; done
    # https, or plain http on 127.0.0.1 only: a server already running on this machine
    case "$url" in
        https://*|http://127.0.0.1|http://127.0.0.1:*) ;;
        *) die "--url must be an https origin, such as https://work.supraman.ai (plain http only for 127.0.0.1)" ;;
    esac
    case "${url#*://}" in
        ''|*/*|*'?'*|*'#'*|*@*) die "--url is an origin, scheme://host[:port] with no path: $url" ;;
    esac
    local_only=""
    [ -n "$clone" ] && local_only="$local_only --clone"
    [ "$port_given" -eq 1 ] && local_only="$local_only --port"
    [ -n "$image_version" ] && local_only="$local_only --image-version"
    [ "$update" -eq 1 ] && local_only="$local_only --update"
    [ -n "$backup" ] && local_only="$local_only --backup"
    [ -n "$restore" ] && local_only="$local_only --restore"
    [ -z "$local_only" ] || die "--url with$local_only: those are for a local Docker install; a remote server is only registered"
fi
command -v node >/dev/null 2>&1 || die "node      not found: install Node.js; the session helper and the registration check run on it"

helper_file="$home_dir/session-header.mjs"
install_helper() { # copied to ~/.workman, because the plugin cache path changes per version
    if [ "$dry_run" -eq 1 ]; then status "helper" "dry run, would copy session-header.mjs to $helper_file"; return; fi
    mkdir -p "$home_dir"
    cmp -s "$script_dir/session-header.mjs" "$helper_file" 2>/dev/null || cp "$script_dir/session-header.mjs" "$helper_file"
    status "helper" "$helper_file (names each session)"
}

skip_prove=0
if [ -n "$url" ]; then
    install_helper
    base_url="$url"
    where_logs="is $url a Workman server, and up?"
else
command -v docker >/dev/null 2>&1 || die "docker    not found: install Docker Desktop (or Docker Engine) first"
docker info >/dev/null 2>&1 || die "docker    not reachable: start Docker Desktop (Linux: sudo systemctl start docker) and run this again"

# a. where the install lives
if [ -n "$clone" ]; then
    [ -f "$clone/server/compose.yml" ] || die "home      $clone/server/compose.yml not found: --clone must be the repo root"
    clone="$(cd "$clone" && pwd)"
    compose_file="$clone/server/compose.yml"
    env_path="$clone/server/.env"
    if ! git -C "$clone" check-ignore -q server/.env 2>/dev/null; then
        die "home      $clone/server/.env is NOT gitignored: stop and tell the user; a git add would publish the passwords"
    fi
else
    compose_file="$home_dir/compose.yml"
    env_path="$home_dir/.env"
fi
base_dir="$(dirname "$env_path")"
where_logs="docker compose -p $project logs api"

dc() { docker compose -p "$project" -f "$compose_file" --env-file "$env_path" "$@"; }
find_container() { # $1 = compose service
    docker ps -q --filter "label=com.docker.compose.project=$project" --filter "label=com.docker.compose.service=$1" | head -n 1
}

if [ -n "$backup" ] || [ -n "$restore" ]; then
    db="$(find_container db)"
    [ -n "$db" ] || die "backup    no running db container in compose project '$project'"
    dump_to() { # $1 = host file
        docker exec "$db" sh -c 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc -f /tmp/workman.dump' >/dev/null
        docker cp "$db:/tmp/workman.dump" "$1" >/dev/null
        docker exec "$db" rm -f /tmp/workman.dump
    }
    if [ -n "$backup" ]; then
        mkdir -p "$(dirname "$backup")"
        dump_to "$backup"
        status "backup" "wrote $backup ($(wc -c <"$backup" | tr -d ' ') bytes, pg_dump -Fc of project '$project')"
        exit 0
    fi
    [ -f "$restore" ] || die "restore   $restore not found"
    mkdir -p "$base_dir/backups"
    safety="$base_dir/backups/pre-restore-$(date +%Y%m%d-%H%M%S).dump"
    dump_to "$safety"
    status "backup" "safety copy of the current database: $safety"
    api="$(find_container api)"
    [ -n "$api" ] && docker stop "$api" >/dev/null
    docker cp "$restore" "$db:/tmp/workman-restore.dump" >/dev/null
    rc=0
    docker exec "$db" sh -c 'pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" --clean --if-exists --no-owner /tmp/workman-restore.dump' >/dev/null 2>"$base_dir/backups/restore.log" || rc=$?
    docker exec "$db" rm -f /tmp/workman-restore.dump
    [ -n "$api" ] && docker start "$api" >/dev/null
    [ "$rc" -eq 0 ] || die "restore   pg_restore exited $rc - see $base_dir/backups/restore.log; the safety copy is $safety"
    status "restore" "restored $restore into project '$project'; api restarted"
    exit 0
fi

if [ -n "$clone" ]; then
    status "home" "$clone (clone: builds server/Dockerfile)"
elif [ "$dry_run" -eq 1 ]; then
    status "home" "dry run, $home_dir (pull-only compose, not written)"
elif cmp -s "$script_dir/compose.yml" "$compose_file"; then
    status "home" "$home_dir (pull-only compose)"
else
    mkdir -p "$home_dir"
    cp "$script_dir/compose.yml" "$compose_file"
    status "home" "$home_dir (pull-only compose written)"
fi

# b. the .env - written once, never overwritten; it holds the dashboard and Postgres passwords
random_hex() { od -An -N32 -tx1 /dev/urandom | tr -d ' \n'; }
if [ -f "$env_path" ]; then
    status "env" "kept $env_path (exists; never overwritten)"
    env_port="$(sed -n 's/^WORKMAN_API_PORT=//p' "$env_path" | tail -n 1 | tr -d '\r')"
    port="${env_port:-5290}"
elif [ -n "$(docker volume ls -q --filter "name=^${project}_workman-db-data\$")" ]; then
    die "env       $env_path is missing but project '$project' already has a database volume, created with another .env: pass --clone <path> for that install, or copy its .env here, and run again"
elif [ "$dry_run" -eq 1 ]; then
    status "env" "dry run, would write $env_path: dashboard and Postgres passwords"
else
    mkdir -p "$base_dir"
    (
        umask 077
        cat >"$env_path" <<EOF
# Written by /workman:setup. Local-only throwaway credentials for a loopback service; never commit.
WORKMAN_DASHBOARD_USERNAME=operator
WORKMAN_DASHBOARD_PASSWORD=local-dashboard-$(random_hex)

WORKMAN_API_PORT=$port
WORKMAN_BIND=127.0.0.1

POSTGRES_DB=workman
POSTGRES_USER=workman
POSTGRES_PASSWORD=local-$(random_hex)
EOF
    )
    chmod 600 "$env_path" 2>/dev/null || true
    status "env" "wrote $env_path: dashboard and Postgres passwords (values not shown, mode 600)"
fi

install_helper
base_url="http://127.0.0.1:$port"

# c. start and wait for /health
health_of() { curl -s -m 2 -o /dev/null -w '%{http_code}' "$base_url/health" || true; }
if [ "$dry_run" -eq 1 ]; then
    health="$(health_of)"
    if [ "$health" = "200" ]; then
        status "start" "dry run, $base_url/health 200: already up, nothing started"
    else
        status "start" "dry run, $base_url/health answered ${health:-nothing}: would run docker compose -p $project up -d"
        skip_prove=1
    fi
else
    [ -n "$image_version" ] && export WORKMAN_VERSION="$image_version"
    up_log="$(mktemp)"
    trap 'rm -f "$up_log"' EXIT
    up_args=()
    if [ "$update" -eq 1 ]; then
        if [ -z "$clone" ]; then dc pull api >"$up_log" 2>&1 || { cat "$up_log" >&2; die "start     docker compose pull failed (output above)"; }
        else up_args=(--build); fi
    fi
    dc up -d "${up_args[@]+"${up_args[@]}"}" >"$up_log" 2>&1 || { cat "$up_log" >&2; die "start     docker compose up failed (output above)"; }
    health=""
    for _ in $(seq 1 90); do
        health="$(health_of)"
        [ "$health" = "200" ] && break
        sleep 2
    done
    [ "$health" = "200" ] || die "start     $base_url/health never answered 200 (last: ${health:-none}); $where_logs"
    status "start" "project '$project' up, $base_url/health 200"
fi
fi

# d. prove - the server asks for sign-in the way Claude Code discovers it, or this stops before registering
mcp_url="$base_url/mcp"
prm_check() { # $1 = url; prints what it answered, and succeeds only for an OAuth protected-resource document
    local reply code
    reply="$(curl -s -m 5 -w '\n%{http_code}' "$1" || true)"
    code="$(printf '%s\n' "$reply" | tail -n 1)"
    # 200 alone proves nothing: the dashboard answers unknown paths with its index.html
    if [ "$code" = "200" ] && printf '%s' "$reply" | grep -q '"authorization_servers"'; then printf '200'; return 0; fi
    if [ "$code" = "200" ]; then printf '200 without authorization_servers'; else printf '%s' "${code:-nothing}"; fi
    return 1
}
prove_oauth() {
    local reply code metadata metadata_code
    reply="$(curl -s -m 10 -o /dev/null -D - -w '%{http_code}' -X POST "$mcp_url" -H "Content-Type: application/json" \
        -H "Accept: application/json, text/event-stream" -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' || true)"
    code="$(printf '%s\n' "$reply" | tail -n 1)"
    metadata="$(printf '%s' "$reply" | tr -d '\r' | sed -n 's/^[Ww][Ww][Ww]-[Aa]uthenticate:.*resource_metadata="\([^"]*\)".*/\1/p' | head -n 1)"
    [ "$code" = "401" ] || die "prove     POST $mcp_url without a token answered ${code:-nothing}, not 401: this server does not ask for sign-in; $where_logs"
    if [ -z "$metadata" ]; then
        [ -n "$url" ] && die "prove     POST $mcp_url answered 401 without resource_metadata=: Claude Code cannot find the sign-in; $where_logs"
        die "prove     POST $mcp_url answered 401 without resource_metadata=: Claude Code cannot find the sign-in; an image older than OAuth (run with --update)"
    fi
    metadata_code="$(prm_check "$metadata")" || die "prove     $metadata answered $metadata_code, not the OAuth metadata: Claude Code cannot discover the sign-in; $where_logs"
    status "prove" "POST /mcp without a token: 401 with resource_metadata=$metadata, which answers 200 - Claude Code finds the sign-in from there"
}
if [ -n "$url" ]; then
    status "auth" "oauth: --url: a remote server is signed in to with OAuth, no token stored"
else
    status "auth" "oauth: sign in as the dashboard user, no token stored"
fi
[ "$skip_prove" -eq 1 ] || prove_oauth

# e. register - user scope, OAuth (no Authorization header), the session helper
here="$PWD"; helper_path="$helper_file"
if command -v cygpath >/dev/null 2>&1; then here="$(cygpath -m "$PWD")"; helper_path="$(cygpath -m "$helper_file")"; fi
existing_url="$(node "$script_dir/find-shadows.mjs" --registered --name "$name" 2>/dev/null || true)"
url_differs=0
[ -n "$existing_url" ] && [ "${existing_url%/}" != "$mcp_url" ] && url_differs=1
keep=0
[ -n "$existing_url" ] && [ "$force" -eq 0 ] && keep=1

json_str() { printf '"%s"' "$(printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g')"; }
registration="{\"type\":\"http\",\"url\":$(json_str "$mcp_url"),\"headersHelper\":$(json_str "node \"$helper_path\"")}"
shown="claude mcp add-json --scope user $name '$registration'"

write_server_json() { # $1 = origin; rule C181: only setup writes it, atomically; readers never default to localhost
    local tmp="$home_dir/.server.json.$$"
    mkdir -p "$home_dir"
    printf '{"url": %s, "mcpName": %s}\n' "$(json_str "$1")" "$(json_str "$name")" >"$tmp"
    mv -f "$tmp" "$home_dir/server.json"
    status "server" "$home_dir/server.json -> $1"
}

if [ "$keep" -eq 1 ] && [ "$url_differs" -eq 1 ]; then
    status "register" "kept the existing '$name' registration, which points at $existing_url, not $mcp_url (pass --force to re-point it)"
    # server.json follows the registration sessions actually use, so agent_launch mints on that same server
    kept_base="${existing_url%/}"; kept_base="${kept_base%/mcp}"; kept_origin=""
    if [ "$kept_base" != "${existing_url%/}" ]; then
        kept_origin="$(node -e 'try { const u = new URL(process.argv[1]); if (u.pathname === "/" && !u.search && !u.username && !u.password) process.stdout.write(u.origin) } catch {}' "$kept_base" 2>/dev/null || true)"
    fi
    if [ -z "$kept_origin" ]; then
        status "server" "not written: the kept registration's url $existing_url is not <origin>/mcp, so agent_launch has no Workman server; run again with --force"
    elif [ "$dry_run" -eq 1 ]; then
        status "server" "dry run, would write $home_dir/server.json -> $kept_origin (the kept registration)"
    else
        write_server_json "$kept_origin"
    fi
elif [ "$keep" -eq 1 ]; then
    status "register" "kept the existing '$name' registration of $mcp_url (pass --force to replace it)"
    if [ "$dry_run" -eq 1 ]; then status "server" "dry run, would write $home_dir/server.json -> $base_url"; else write_server_json "$base_url"; fi
elif [ "$dry_run" -eq 1 ]; then
    status "register" "dry run, would run: $shown"
    status "server" "dry run, would write $home_dir/server.json -> $base_url"
else
    command -v claude >/dev/null 2>&1 || die "register  the claude CLI is not on PATH: install Claude Code, or run this where it is"
    if [ -n "$existing_url" ]; then
        claude mcp remove "$name" --scope user >/dev/null 2>&1 || die "register  claude mcp remove $name --scope user failed"
    fi
    claude mcp add-json --scope user "$name" "$registration" >/dev/null 2>&1 || die "register  claude mcp add-json failed: $shown"
    status "register" "'$name' -> $mcp_url with OAuth sign-in, session helper (user scope; no token stored)"
    write_server_json "$base_url"
fi

# the shadow check - a local or project entry of the same name wins over the user one; removing it is the user's call.
# --all: here and in every project directory the claude config lists, not only $PWD
shadows="$(node "$script_dir/find-shadows.mjs" --name "$name" --dir "$here" --all 2>/dev/null || true)"
found=(); unchecked=0
while IFS= read -r line; do
    case "$line" in
        '') ;;
        'not checked'*) status "shadows" "$line"; unchecked=1 ;;
        *) found+=("$line") ;;
    esac
done <<<"$shadows"
if [ "${#found[@]}" -gt 0 ]; then
    status "shadows" "${#found[@]} '$name' entry(s) win over the user registration (local > project > user), so sessions there miss it and its session helper; this script removes nothing - run the command shown yourself:"
    for line in "${found[@]}"; do printf '          %s\n' "$line"; done
elif [ "$unchecked" -eq 0 ]; then
    status "shadows" "none: no local or project '$name' entry overrides it, here or in any project Claude Code lists"
fi

# f. what the human needs next
if [ -n "$url" ]; then
    status "dashboard" "$base_url - sign in with your account there"
else
    status "dashboard" "$base_url - sign in with WORKMAN_DASHBOARD_USERNAME / _PASSWORD from $env_path"
fi
if [ "$keep" -eq 1 ] && [ "$url_differs" -eq 1 ]; then
    status "next" "nothing changed: '$name' still points at $existing_url; run again with --force to re-point it to $mcp_url"
elif [ "$keep" -eq 1 ]; then
    status "next" "already registered; sign in only if /mcp shows '$name' needs authentication"
else
    status "next" "start a new Claude Code session, run /mcp, pick '$name', Authenticate, sign in in the browser and Approve: the work_* tools appear there, not in this one"
fi
