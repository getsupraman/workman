<#
.SYNOPSIS
  Install, start, register and prove a local Workman server.
.DESCRIPTION
  Twin of setup.sh: same steps, same flags, same status lines. Change both together.

  Every line this prints is safe to show a model: tokens and passwords stay in the .env file and in
  the harness's own config, and nothing here echoes them (SKILL.md "What the model never sees").

  -Clone <path>          use a clone of github.com/getsupraman/workman: <path>/server/compose.yml (builds the
                         image) and <path>/server/.env. Without it: the Workman home.
  -HomeDir <dir>         the Workman home (default %USERPROFILE%\.workman): compose.yml (pulls the
                         published image) and .env
  -Auth <oauth|token>    how the registration authenticates. oauth: no token stored, the user signs in
                         on first use (/mcp -> Authenticate). token: a role's bearer token. Default:
                         oauth when the server offers it (its OAuth metadata answers 200), else token
  -Role <role>           orchestrator (default) | controller | worker - whose token to register
                         (token only: an OAuth sign-in always gets the controller toolset)
  -Name <name>           registration name (default workman). Without -Name, a 1.x registration named
                         workstate in the same scope is moved to workman: same url and token, its
                         X-WorkState-* headers renamed X-Workman-*; nothing is printed of its token
  -Agent <session>       register at LOCAL scope (this directory's project only) with the header
                         X-Workman-Agent: <session>, so sessions here are recorded by that name
  -Port <port>           host port, written only into a NEW .env (default 5290)
  -Project <name>        compose project (default workman)
  -ImageVersion <tag>    image tag for the pull-only compose (default latest)
  -Update                pull the image again (home) or rebuild it (clone) before starting
  -Force                 replace an existing registration of the same name
  -SessionNames          re-register an existing registration with the session helper (a new one gets
                         it anyway), keeping the role its token has unless -Role is given
  -DryRun               do everything except write the registration; show it (a token masked)
  -Backup <file>         pg_dump -Fc the database to <file>, and stop
  -Restore <file>        restore <file> over the database (after a safety backup), and stop
  -Migrate               move a 1.x install (project workstate, home %USERPROFILE%\.workstate) into this one:
                         dump its database, copy its .env with the keys renamed WORKSTATE_ -> WORKMAN_ (values
                         never read), restore into the new project, stop the old one (its volume stays),
                         then start, register and prove as usual. Safe to run again
  -FromHome <dir>        the 1.x home to migrate from (default %USERPROFILE%\.workstate; with -Clone the
                         clone's server/.env is renamed in place instead)
  -FromProject <name>    the 1.x compose project to migrate from (default workstate)
  -Fresh                 install beside a 1.x install that was found, instead of migrating it
#>
[CmdletBinding()]
param(
    [string]$Clone,
    [string]$HomeDir,
    [ValidateSet('oauth', 'token')][string]$Auth,
    [ValidateSet('orchestrator', 'controller', 'worker')][string]$Role = 'orchestrator',
    [string]$Name = 'workman',
    [string]$Agent,
    [ValidateRange(1, 65535)][int]$Port = 5290,
    [string]$Project = 'workman',
    [string]$ImageVersion,
    [switch]$Update,
    [switch]$Force,
    [switch]$SessionNames,
    [switch]$DryRun,
    [string]$Backup,
    [string]$Restore,
    [switch]$Migrate,
    [string]$FromHome,
    [string]$FromProject = 'workstate',
    [switch]$Fresh
)

$ErrorActionPreference = 'Continue'
Set-StrictMode -Version 3

function Die([string]$Line) { [Console]::Error.WriteLine($Line); exit 1 }
function Status([string]$Step, [string]$Text) { Write-Output ('{0,-10}{1}' -f $Step, $Text) }

$scriptDir = $PSScriptRoot
$onWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)

if ($Clone -and $HomeDir) { Die '-Clone and -HomeDir are two different installs; pass one' }
if ($Migrate -and $Fresh) { Die '-Migrate and -Fresh: pass one' }
if ($Migrate -and $FromProject -eq $Project) { Die "-FromProject and -Project are both '$Project': the migration needs a new project" }
$roleGiven = $PSBoundParameters.ContainsKey('Role')
if ($Auth -eq 'oauth' -and $roleGiven) { Die '-Role with -Auth oauth: an OAuth sign-in is the dashboard user with the controller toolset; -Role needs -Auth token' }

function Get-EnvValue([string]$Key) {
    $value = $null
    foreach ($line in [IO.File]::ReadAllLines($script:envFile)) {
        if ($line.StartsWith("$Key=")) { $value = $line.Substring($Key.Length + 1).TrimEnd("`r") }
    }
    $value
}

function Find-Container([string]$Service, [string]$InProject = $Project) {
    $id = & docker ps -q --filter "label=com.docker.compose.project=$InProject" --filter "label=com.docker.compose.service=$Service" 2>$null
    if ($id) { @($id)[0] } else { $null }
}

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { Die 'docker    not found: install Docker Desktop (or Docker Engine) first' }
& docker info *> $null
if ($LASTEXITCODE -ne 0) { Die 'docker    not reachable: start Docker Desktop (Linux: sudo systemctl start docker) and run this again' }

# a. where the install lives
if ($Clone) {
    if (-not (Test-Path -LiteralPath (Join-Path $Clone 'server/compose.yml'))) { Die "home      $Clone/server/compose.yml not found: -Clone must be the repo root" }
    $Clone = (Resolve-Path -LiteralPath $Clone).Path
    $composeFile = Join-Path $Clone 'server/compose.yml'
    $envFile = Join-Path $Clone 'server/.env'
    & git -C $Clone check-ignore -q server/.env 2>$null
    if ($LASTEXITCODE -ne 0) { Die "home      $Clone/server/.env is NOT gitignored: stop and tell the user; a git add would publish the tokens" }
    $mode = 'clone'
}
else {
    if (-not $HomeDir) { $HomeDir = Join-Path $HOME '.workman' }
    New-Item -ItemType Directory -Force -Path $HomeDir | Out-Null
    $HomeDir = (Resolve-Path -LiteralPath $HomeDir).Path
    $composeFile = Join-Path $HomeDir 'compose.yml'
    $envFile = Join-Path $HomeDir '.env'
    $mode = 'home'
}
$baseDir = Split-Path -Parent $envFile

function Invoke-Compose { & docker compose -p $Project -f $composeFile --env-file $envFile @args }

if ($Backup -or $Restore) {
    if ($Backup -and $Restore) { Die '-Backup and -Restore: pass one' }
    $db = Find-Container 'db'
    if (-not $db) { Die "backup    no running db container in compose project '$Project'" }
    function Save-Dump([string]$File) {
        & docker exec $db sh -c 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc -f /tmp/workman.dump' | Out-Null
        if ($LASTEXITCODE -ne 0) { Die "backup    pg_dump failed in container $db" }
        & docker cp "${db}:/tmp/workman.dump" $File | Out-Null
        if ($LASTEXITCODE -ne 0) { Die "backup    docker cp to $File failed" }
        & docker exec $db rm -f /tmp/workman.dump | Out-Null
    }
    if ($Backup) {
        $dir = Split-Path -Parent ([IO.Path]::GetFullPath($Backup))
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $Backup = [IO.Path]::GetFullPath($Backup)
        Save-Dump $Backup
        Status 'backup' "wrote $Backup ($((Get-Item -LiteralPath $Backup).Length) bytes, pg_dump -Fc of project '$Project')"
        exit 0
    }
    if (-not (Test-Path -LiteralPath $Restore)) { Die "restore   $Restore not found" }
    $Restore = (Resolve-Path -LiteralPath $Restore).Path
    $backups = Join-Path $baseDir 'backups'
    New-Item -ItemType Directory -Force -Path $backups | Out-Null
    $safety = Join-Path $backups ("pre-restore-{0}.dump" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
    Save-Dump $safety
    Status 'backup' "safety copy of the current database: $safety"
    $api = Find-Container 'api'
    if ($api) { & docker stop $api | Out-Null }
    & docker cp $Restore "${db}:/tmp/workman-restore.dump" | Out-Null
    $log = Join-Path $backups 'restore.log'
    & docker exec $db sh -c 'pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" --clean --if-exists --no-owner /tmp/workman-restore.dump' 2> $log | Out-Null
    $rc = $LASTEXITCODE
    & docker exec $db rm -f /tmp/workman-restore.dump | Out-Null
    if ($api) { & docker start $api | Out-Null }
    if ($rc -ne 0) { Die "restore   pg_restore exited $rc - see $log; the safety copy is $safety" }
    Status 'restore' "restored $Restore into project '$Project'; api restarted"
    exit 0
}

if ($mode -eq 'home') {
    $shipped = Join-Path $scriptDir 'compose.yml'
    if (-not (Test-Path -LiteralPath $composeFile)) {
        Copy-Item -LiteralPath $shipped -Destination $composeFile
        Status 'home' "$HomeDir (pull-only compose written)"
    }
    elseif ((Get-FileHash -LiteralPath $shipped).Hash -ne (Get-FileHash -LiteralPath $composeFile).Hash) {
        Copy-Item -LiteralPath $shipped -Destination $composeFile -Force
        Status 'home' "$HomeDir (pull-only compose updated to this skill's version)"
    }
    else { Status 'home' "$HomeDir (pull-only compose)" }
}
else { Status 'home' "$Clone (clone: builds server/Dockerfile)" }

# b. the .env - written once, never overwritten; with -Migrate, the 1.x install's with its keys renamed
function New-RandomHex {
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    -join ($bytes | ForEach-Object { $_.ToString('x2') })
}
function Protect-EnvFile([string]$File) {
    if ($onWindows) {
        & icacls $File /inheritance:r /grant:r "$($env:USERNAME):(F)" *> $null
        'owner-only ACL'
    }
    else {
        & chmod 600 $File
        'mode 600'
    }
}
# The key names change and nothing else: a regex rewrites the start of each line, and no value is printed.
function Convert-EnvKeys([string]$From, [string]$To) {
    $text = [IO.File]::ReadAllText($From)
    $text = [regex]::Replace($text, '(?m)^WORKSTATE_VERSION=.*(\r?\n|$)', '')
    $text = [regex]::Replace($text, '(?m)^WORKSTATE_', 'WORKMAN_')
    [IO.File]::WriteAllText($To, $text, (New-Object System.Text.UTF8Encoding $false))
    $text = $null
}
function Test-EnvHas1x([string]$File) {
    (Test-Path -LiteralPath $File) -and [bool](Select-String -LiteralPath $File -Pattern '^WORKSTATE_' -Quiet)
}
function Wait-Db([string]$Container) {
    for ($i = 0; $i -lt 60; $i++) {
        & docker exec $Container sh -c 'pg_isready -U "$POSTGRES_USER" -d "$POSTGRES_DB"' *> $null
        if ($LASTEXITCODE -eq 0) { return $true }
        Start-Sleep -Seconds 1
    }
    $false
}

if (-not $FromHome) { $FromHome = Join-Path $HOME '.workstate' }
$fromEnv = if ($mode -eq 'clone') { $envFile } else { Join-Path $FromHome '.env' }
$oldContainers = @(& docker ps -aq --filter "label=com.docker.compose.project=$FromProject" 2>$null | Where-Object { $_ })
if (-not $Migrate -and -not $Fresh) {
    # the offer - a 1.x install found where this one would go is migrated only when asked to
    if ($mode -eq 'clone' -and (Test-EnvHas1x $envFile)) {
        Die "migrate   $envFile has 1.x WORKSTATE_* keys: run this again with -Migrate to rename them and move project '$FromProject' into '$Project'"
    }
    elseif ($mode -eq 'home' -and -not (Test-Path -LiteralPath $envFile) -and ((Test-Path -LiteralPath $fromEnv) -or $oldContainers.Count -gt 0)) {
        Die "migrate   found a 1.x install (home $FromHome, project '$FromProject'): run this again with -Migrate to move it into $HomeDir, or with -Fresh to install beside it"
    }
}

if ($Migrate) {
    # m1. the .env
    if ($mode -eq 'clone' -and (Test-EnvHas1x $envFile)) {
        Copy-Item -LiteralPath $envFile -Destination "$envFile.1x" -Force
        Convert-EnvKeys $envFile $envFile
        Status 'env' "renamed the WORKSTATE_* keys in $envFile to WORKMAN_* (values not read; the 1.x copy is $envFile.1x)"
    }
    elseif (Test-Path -LiteralPath $envFile) {
        Status 'env' "kept $envFile (exists; never overwritten)"
    }
    else {
        if (-not (Test-Path -LiteralPath $fromEnv)) { Die "migrate   $fromEnv not found: pass -FromHome <the 1.x home>" }
        Convert-EnvKeys $fromEnv $envFile
        $null = Protect-EnvFile $envFile
        Status 'env' "wrote $envFile from $fromEnv, keys renamed WORKSTATE_ -> WORKMAN_ (values not read; POSTGRES_* kept as they were, so the restored database keeps its owner)"
    }
    # m2. the data: dump the 1.x database, restore it into the new project's empty volume
    $marker = Join-Path $baseDir ".migrated-from-$FromProject"
    if (Test-Path -LiteralPath $marker) {
        Status 'migrate' "project '$FromProject' was already restored into '$Project' ($marker)"
    }
    else {
        $oldDb = @(& docker ps -aq --filter "label=com.docker.compose.project=$FromProject" --filter 'label=com.docker.compose.service=db' 2>$null | Where-Object { $_ })
        if ($oldDb.Count -eq 0) { Die "migrate   no db container in compose project '$FromProject': pass -FromProject <the 1.x project>" }
        $oldDb = $oldDb[0]
        $oldApi = Find-Container 'api' $FromProject
        if ($oldApi) { & docker stop $oldApi | Out-Null }
        & docker start $oldDb | Out-Null
        if (-not (Wait-Db $oldDb)) { Die "migrate   the db of project '$FromProject' never became ready" }
        $backups = Join-Path $baseDir 'backups'
        New-Item -ItemType Directory -Force -Path $backups | Out-Null
        $dump = Join-Path $backups ("migrate-from-$FromProject-{0}.dump" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
        & docker exec $oldDb sh -c 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc -f /tmp/workman-migrate.dump' | Out-Null
        if ($LASTEXITCODE -ne 0) { Die "migrate   pg_dump in project '$FromProject' failed; nothing was changed$(if ($oldApi) { ", but its api is stopped: docker start $oldApi" })" }
        & docker cp "${oldDb}:/tmp/workman-migrate.dump" $dump | Out-Null
        & docker exec $oldDb rm -f /tmp/workman-migrate.dump | Out-Null
        Status 'migrate' "dumped project '$FromProject' to $dump ($((Get-Item -LiteralPath $dump).Length) bytes, pg_dump -Fc)"
        if ($ImageVersion) { $env:WORKMAN_VERSION = $ImageVersion }
        Invoke-Compose up -d db *> $null
        if ($LASTEXITCODE -ne 0) { Die "migrate   docker compose up db failed in project '$Project'" }
        $newDb = Find-Container 'db'
        if (-not $newDb -or -not (Wait-Db $newDb)) { Die "migrate   the db of project '$Project' never became ready" }
        $tables = ([string](& docker exec $newDb sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc "SELECT count(*) FROM pg_tables WHERE schemaname = ''public''"')).Trim()
        if ($tables -ne '0') { Die "migrate   project '$Project' already holds a database with $(if ($tables) { $tables } else { 'unknown' }) tables: refusing to restore over a non-empty volume ($($Project)_workman-db-data)" }
        # 1.x policies and grants name the role workstate_app, and pg_dump leaves roles out: they are the cluster's.
        # The 2.0 api's own migration takes it from there.
        & docker exec $newDb sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -v ON_ERROR_STOP=1 -qc "DO \$\$ BEGIN CREATE ROLE workstate_app NOLOGIN; EXCEPTION WHEN duplicate_object THEN NULL; END \$\$;"' | Out-Null
        if ($LASTEXITCODE -ne 0) { Die "migrate   could not create the 1.x role workstate_app in project '$Project'" }
        & docker cp $dump "${newDb}:/tmp/workman-migrate.dump" | Out-Null
        $log = Join-Path $backups 'migrate.log'
        & docker exec $newDb sh -c 'pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" --single-transaction --exit-on-error --no-owner /tmp/workman-migrate.dump' 2> $log | Out-Null
        $rc = $LASTEXITCODE
        & docker exec $newDb rm -f /tmp/workman-migrate.dump | Out-Null
        if ($rc -ne 0) { Die "migrate   pg_restore exited $rc - see $log; nothing was restored (one transaction) and project '$FromProject' is untouched: docker start $oldDb$(if ($oldApi) { " $oldApi" }) brings it back" }
        [IO.File]::WriteAllText($marker, ("migrated from compose project {0} on {1}`n" -f $FromProject, (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')))
        Status 'migrate' "restored into project '$Project'; its api migrates the schema to 2.0 when it starts"
    }
    # m3. the old project stops; its volume stays until the user removes it
    $running = @(& docker ps -q --filter "label=com.docker.compose.project=$FromProject" 2>$null | Where-Object { $_ })
    if ($running.Count -gt 0) { & docker stop @running | Out-Null }
    $oldVolume = @(& docker volume ls -q --filter "label=com.docker.compose.project=$FromProject" 2>$null | Where-Object { $_ }) | Select-Object -First 1
    Status 'migrate' "project '$FromProject' stopped, its volume $(if ($oldVolume) { $oldVolume } else { '(none found)' }) kept. Once '$Project' works, remove them: docker rm `$(docker ps -aq --filter label=com.docker.compose.project=$FromProject)$(if ($oldVolume) { " && docker volume rm $oldVolume" })"
}
elseif (Test-Path -LiteralPath $envFile) {
    Status 'env' "kept $envFile (exists; never overwritten)"
}
elseif (& docker volume ls -q --filter "name=^$($Project)_workman-db-data`$") {
    Die "env       $envFile is missing but project '$Project' already has a database volume, created with another .env: pass -Clone <path> for that install, or copy its .env here, and run again"
}
else {
    $content = @(
        '# Written by /workman:setup. Local-only throwaway credentials for a loopback service; never commit.'
        "WORKMAN_WORKER_TOKEN=local-worker-$(New-RandomHex)"
        "WORKMAN_ORCHESTRATOR_TOKEN=local-orchestrator-$(New-RandomHex)"
        "WORKMAN_CONTROLLER_TOKEN=local-controller-$(New-RandomHex)"
        'WORKMAN_READER_TOKEN='
        ''
        'WORKMAN_DASHBOARD_USERNAME=operator'
        "WORKMAN_DASHBOARD_PASSWORD=local-dashboard-$(New-RandomHex)"
        ''
        "WORKMAN_API_PORT=$Port"
        'WORKMAN_BIND=127.0.0.1'
        ''
        'POSTGRES_DB=workman'
        'POSTGRES_USER=workman'
        "POSTGRES_PASSWORD=local-$(New-RandomHex)"
    ) -join "`n"
    [IO.File]::WriteAllText($envFile, $content + "`n", (New-Object System.Text.UTF8Encoding $false))
    $modeNote = Protect-EnvFile $envFile
    Status 'env' "wrote ${envFile}: 3 tokens, dashboard and Postgres passwords (values not shown, $modeNote)"
}

# the session helper - copied next to the .env, because the plugin cache path changes per version
$helperShipped = Join-Path $scriptDir 'session-header.mjs'
$helperFile = Join-Path $baseDir 'session-header.mjs'
$node = Get-Command node -ErrorAction SilentlyContinue
if (-not (Test-Path -LiteralPath $helperFile) -or (Get-FileHash -LiteralPath $helperShipped).Hash -ne (Get-FileHash -LiteralPath $helperFile).Hash) {
    Copy-Item -LiteralPath $helperShipped -Destination $helperFile -Force
}
if ($node) { Status 'helper' "$helperFile (names each session; needs node, found)" }
else { Status 'helper' "$helperFile copied, but node is not on PATH: registering without it, so sessions stay unnamed" }

$portValue = Get-EnvValue 'WORKMAN_API_PORT'
if (-not $portValue) { $portValue = '5290' }
$baseUrl = "http://127.0.0.1:$portValue"

# c. start and wait for /health
if ($ImageVersion) { $env:WORKMAN_VERSION = $ImageVersion }
$upArgs = @()
if ($Update) {
    if ($mode -eq 'home') {
        $out = Invoke-Compose pull api 2>&1
        if ($LASTEXITCODE -ne 0) { [Console]::Error.WriteLine(($out | Out-String)); Die 'start     docker compose pull failed (output above)' }
    }
    else { $upArgs = @('--build') }
}
$out = Invoke-Compose up -d @upArgs 2>&1
if ($LASTEXITCODE -ne 0) { [Console]::Error.WriteLine(($out | Out-String)); Die 'start     docker compose up failed (output above)' }
$health = ''
for ($i = 0; $i -lt 90; $i++) {
    try {
        $health = [string](Invoke-WebRequest -Uri "$baseUrl/health" -TimeoutSec 2 -UseBasicParsing).StatusCode
    }
    catch {
        $response = $_.Exception.PSObject.Properties['Response']
        $health = if ($response -and $response.Value) { [string][int]$response.Value.StatusCode } else { '' }
    }
    if ($health -eq '200') { break }
    Start-Sleep -Seconds 2
}
if ($health -ne '200') { Die "start     $baseUrl/health never answered 200 (last: $(if ($health) { $health } else { 'none' })); docker compose -p $Project logs api" }
Status 'start' "project '$Project' up, $baseUrl/health 200"

# d. register - the token goes from the .env straight into the harness's config; with oauth none is stored
$mcpUrl = "$baseUrl/mcp"

# One request, whatever it answers: Invoke-WebRequest throws on 4xx, with the response on the exception.
function Invoke-Probe([string]$Uri, [string]$Method = 'Get') {
    $result = @{ Status = ''; Body = ''; Authenticate = '' }
    $request = @{ Uri = $Uri; Method = $Method; TimeoutSec = 10; UseBasicParsing = $true }
    if ($Method -eq 'Post') {
        $request.ContentType = 'application/json'
        $request.Headers = @{ Accept = 'application/json, text/event-stream' }
        $request.Body = '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
    }
    try {
        $r = Invoke-WebRequest @request
        $result.Status = [string][int]$r.StatusCode
        $result.Body = if ($r.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($r.Content) } else { [string]$r.Content }
    }
    catch {
        $response = $_.Exception.PSObject.Properties['Response']
        if ($response -and $response.Value) {
            $result.Status = [string][int]$response.Value.StatusCode
            $headers = $response.Value.Headers
            if ($headers -is [System.Net.WebHeaderCollection]) { $result.Authenticate = [string]$headers['WWW-Authenticate'] }
            else { $result.Authenticate = (@($headers.WwwAuthenticate) | ForEach-Object { $_.ToString() }) -join ', ' }
        }
    }
    $result
}
# What the url answered, and Offers only for an OAuth protected-resource document.
function Test-Prm([string]$Uri) {
    $probe = Invoke-Probe $Uri
    # 200 alone proves nothing: the dashboard answers unknown paths with its index.html
    if ($probe.Status -eq '200' -and $probe.Body.Contains('"authorization_servers"')) { return @{ Offers = $true; Answer = '200' } }
    if ($probe.Status -eq '200') { return @{ Offers = $false; Answer = '200 without authorization_servers' } }
    @{ Offers = $false; Answer = $(if ($probe.Status) { $probe.Status } else { 'nothing' }) }
}
# WS_SETUP_PRM_URL is a test seam: point it at a 404 to see the token fallback without stopping the server.
$prmUrl = if ($env:WS_SETUP_PRM_URL) { $env:WS_SETUP_PRM_URL } else { "$baseUrl/.well-known/oauth-protected-resource/mcp" }
$prm = Test-Prm $prmUrl
$existing = $false
$scope = 'user'
if ($Agent) { $scope = 'local' }
$getOut = ''
$here = (Get-Location).Path

# the move - a 1.x registration becomes '$Name' with the same url, scope and token, its X-WorkState-* headers
# renamed and its headersHelper pointed at this home's copy. The plan's third line is the entry, token
# included: captured here, never printed.
$helperCommand = "node `"$($helperFile.Replace('\', '/'))`"\"
$legacyName = 'workstate'
$nameGiven = $PSBoundParameters.ContainsKey('Name')
if (-not $nameGiven -and (Get-Command claude -ErrorAction SilentlyContinue)) {
    if ($node) {
        $plan = @(& node (Join-Path $scriptDir 'move-registration.mjs') --from $legacyName --to $Name --scope $scope --dir $here --helper $helperCommand 2>$null)
        $verdict = if ($plan.Count -gt 0) { [string]$plan[0] } else { 'not checked: the move check printed nothing' }
        if ($verdict -eq 'move' -and $plan.Count -ge 3) {
            $moveJson = [string]$plan[2]
            if ($DryRun) { Status 'move' "dry run, would move '$legacyName' to '$Name' ($scope scope; $($plan[1]))" }
            else {
                # Windows PowerShell 5.1 strips the JSON's quotes from a native argument unless they are escaped.
                if ($PSVersionTable.PSVersion.Major -lt 7 -or (Get-Variable PSNativeCommandArgumentPassing -ValueOnly -ErrorAction SilentlyContinue) -eq 'Legacy') {
                    $moveJson = $moveJson.Replace('"', '\"')
                }
                & claude mcp add-json --scope $scope $Name $moveJson *> $null
                if ($LASTEXITCODE -ne 0) { Die "move      claude mcp add-json --scope $scope $Name failed; '$legacyName' is left as it is" }
                & claude mcp remove $legacyName --scope $scope *> $null
                if ($LASTEXITCODE -ne 0) { Die "move      '$Name' added, but claude mcp remove $legacyName --scope $scope failed: run it yourself" }
                Status 'move' "'$legacyName' -> '$Name' ($scope scope; $($plan[1])); with OAuth sign in again: /mcp, pick '$Name', Authenticate"
            }
            $moveJson = $null
            $plan = $null
        }
        elseif ($verdict -eq 'both') {
            Status 'move' "'$legacyName' and '$Name' are both registered ($scope scope): '$legacyName' left as it is; once '$Name' works, run: claude mcp remove $legacyName -s $scope"
        }
        elseif ($verdict -ne 'none') { Status 'move' $verdict }
    }
    else {
        Status 'move' "not checked (needs node): a 1.x '$legacyName' registration is not moved; once '$Name' works, run: claude mcp remove $legacyName -s $scope"
    }
}
if (-not (Get-Command claude -ErrorAction SilentlyContinue)) {
    if (-not $DryRun) { Die 'register  the claude CLI is not on PATH: install Claude Code, or run this where it is' }
}
else {
    # Read here and never printed: SKILL.md forbids the model this command because it shows the header.
    $getOut = (& claude mcp get $Name 2>&1 | Out-String)
    if ($LASTEXITCODE -eq 0) { $existing = $true }
    elseif ($getOut -notmatch 'No MCP server named') { Die "register  claude mcp get $Name failed; run claude mcp list to see why" }
    if ($existing -and $scope -eq 'local' -and $getOut -notmatch 'Scope: Local') { $existing = $false }
}
$existingAuth = if ($getOut.Contains('Bearer ')) { 'token' } else { 'oauth' }

# which auth: a kept registration keeps its own; else -Auth, -Role, the re-registered one's, the server's offer
$keep = $existing -and -not ($Force -or $SessionNames)
if ($keep) {
    $why = "the existing '$Name' registration uses $existingAuth, kept"
    if ($Auth -and $Auth -ne $existingAuth) { $why += " (-Force replaces it with $Auth)" }
    $Auth = $existingAuth
}
elseif ($Auth) { $why = "-Auth $Auth" }
elseif ($roleGiven) { $Auth = 'token'; $why = "-Role $Role needs a token" }
elseif ($existing) { $Auth = $existingAuth; $why = "the existing '$Name' registration uses $existingAuth" }
elseif ($prm.Offers) { $Auth = 'oauth'; $why = "the server offers OAuth sign-in ($prmUrl 200)" }
else { $Auth = 'token'; $why = "the server has no OAuth sign-in ($prmUrl answered $($prm.Answer)): an image older than OAuth; -Update gets a newer one" }
if ($Auth -eq 'oauth' -and -not $keep -and -not $prm.Offers) {
    Die "auth      -Auth oauth, but $prmUrl answered $($prm.Answer): this server predates OAuth sign-in; run with -Update (home: pulls the image, clone: rebuilds it), or pass -Auth token"
}
Status 'auth' "${Auth}: $why"

# -SessionNames on an existing registration keeps the role its token already has.
if ($Auth -eq 'token' -and $existing -and $SessionNames -and -not $roleGiven) {
    foreach ($candidate in 'controller', 'orchestrator', 'worker') {
        $candidateToken = Get-EnvValue "WORKMAN_$($candidate.ToUpperInvariant())_TOKEN"
        if ($candidateToken -and $getOut.Contains("Bearer $candidateToken")) { $Role = $candidate; break }
    }
}
$token = ''
if ($Auth -eq 'token') {
    $tokenKey = "WORKMAN_$($Role.ToUpperInvariant())_TOKEN"
    $token = Get-EnvValue $tokenKey
    if (-not $token) { Die "env       $envFile has no $tokenKey" }
}

function Get-Registration([string]$Bearer) {
    $headers = [ordered]@{}
    if ($Auth -eq 'token') { $headers['Authorization'] = "Bearer $Bearer" }
    if ($Agent) { $headers['X-Workman-Agent'] = $Agent }
    $entry = [ordered]@{ type = 'http'; url = $mcpUrl }
    if ($headers.Count -gt 0) { $entry['headers'] = $headers }
    if ($node) { $entry['headersHelper'] = $helperCommand }
    $entry | ConvertTo-Json -Compress
}
$shown = "claude mcp add-json --scope $scope $Name '$(Get-Registration '***')'"

if ($keep) {
    Status 'register' "kept the existing '$Name' registration (pass -SessionNames to add the session helper, -Force to replace it)"
}
elseif ($DryRun) {
    Status 'register' "dry run, would run: $shown"
}
else {
    if ($existing) {
        & claude mcp remove $Name --scope $scope *> $null
        if ($LASTEXITCODE -ne 0) { Die "register  claude mcp remove $Name --scope $scope failed (is it registered in another scope?)" }
    }
    $json = Get-Registration $token
    # Windows PowerShell 5.1 strips the JSON's quotes from a native argument unless they are escaped.
    if ($PSVersionTable.PSVersion.Major -lt 7 -or (Get-Variable PSNativeCommandArgumentPassing -ValueOnly -ErrorAction SilentlyContinue) -eq 'Legacy') {
        $json = $json.Replace('"', '\"')
    }
    & claude mcp add-json --scope $scope $Name $json *> $null
    if ($LASTEXITCODE -ne 0) { Die "register  claude mcp add-json failed: $shown" }
    if ($Auth -eq 'oauth') {
        Status 'register' "'$Name' -> $mcpUrl with OAuth sign-in$(if ($Agent) { ", agent $Agent" })$(if ($node) { ', session helper' }) ($scope scope; no token stored)"
    }
    else {
        Status 'register' "'$Name' -> $mcpUrl as $Role$(if ($Agent) { ", agent $Agent" })$(if ($node) { ', session helper' }) ($scope scope; token stored by Claude Code, not shown)"
    }
}

# the shadow check - a local or project entry of the same name wins over the user one; removing it is the user's call
if ($node) {
    $finderArgs = @((Join-Path $scriptDir 'find-shadows.mjs'), '--name', $Name, '--dir', $here)
    $names = "'$Name'"
    if ($scope -eq 'local') { $finderArgs += @('--own', $here) }
    $shadows = @(& node @finderArgs 2>$null | Where-Object { $_ })
    $unchecked = @($shadows | Where-Object { $_.StartsWith('not checked') })
    $found = @($shadows | Where-Object { -not $_.StartsWith('not checked') })
    foreach ($line in $unchecked) { Status 'shadows' $line }
    if ($found.Count -gt 0) {
        Status 'shadows' "$($found.Count) $names entry(s) win over the user registration (local > project > user), so sessions there miss it and its session helper; this script removes nothing - run the command shown yourself:"
        foreach ($line in $found) { Write-Output "          $line" }
    }
    elseif ($unchecked.Count -eq 0) { Status 'shadows' "none: no local or project $names entry overrides it" }
}
else { Status 'shadows' 'not checked (needs node)' }

# e. prove - oauth: the server asks for sign-in the way Claude Code discovers it; token: count what it lists
if ($Auth -eq 'oauth') {
    $reply = Invoke-Probe $mcpUrl 'Post'
    $code = if ($reply.Status) { $reply.Status } else { 'nothing' }
    if ($code -ne '401') { Die "prove     POST $mcpUrl without a token answered $code, not 401: this server does not ask for sign-in; docker compose -p $Project logs api" }
    $match = [regex]::Match($reply.Authenticate, 'resource_metadata="([^"]*)"')
    if (-not $match.Success) { Die "prove     POST $mcpUrl answered 401 without resource_metadata=: Claude Code cannot find the sign-in; an image older than OAuth (run with -Update)" }
    $metadata = $match.Groups[1].Value
    $metadataCheck = Test-Prm $metadata
    if (-not $metadataCheck.Offers) { Die "prove     $metadata answered $($metadataCheck.Answer), not the OAuth metadata: Claude Code cannot discover the sign-in; docker compose -p $Project logs api" }
    Status 'prove' "POST /mcp without a token: 401 with resource_metadata=$metadata, which answers 200 - Claude Code finds the sign-in from there"
}
else {
    $body = ''
    try {
        $body = (Invoke-WebRequest -Uri $mcpUrl -Method Post -TimeoutSec 10 -UseBasicParsing -ContentType 'application/json' `
                -Headers @{ Accept = 'application/json, text/event-stream'; Authorization = "Bearer $token" } `
                -Body '{"jsonrpc":"2.0","id":1,"method":"tools/list"}').Content
        if ($body -is [byte[]]) { $body = [Text.Encoding]::UTF8.GetString($body) }
    }
    catch { $body = '' }
    $count = @([regex]::Matches([string]$body, '"name":"work_[a-z_]*"') | ForEach-Object { $_.Value } | Sort-Object -Unique).Count
    switch ($count) {
        19 { $implied = 'orchestrator/controller' }
        6 { $implied = 'worker' }
        0 { Die "prove     tools/list returned no work_* tool for the $Role token: the token or the server is wrong; docker compose -p $Project logs api" }
        default { $implied = 'unexpected - an image older or newer than these skills (run with -Update)' }
    }
    Status 'prove' "$count work_* tools for the $Role token: $implied"
}

# f. what the human needs next
Status 'dashboard' "$baseUrl - sign in with WORKMAN_DASHBOARD_USERNAME / _PASSWORD from $envFile"
if ($Auth -eq 'oauth') {
    Status 'next' "start a new Claude Code session, run /mcp, pick '$Name', Authenticate, sign in with that dashboard user and Approve: the work_* tools appear there, not in this one"
}
else { Status 'next' 'start a new Claude Code session: the work_* tools appear there, not in this one' }
