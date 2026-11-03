// @ts-check
// setup.sh / setup.ps1 --url: a remote server is registered with OAuth and the session helper, proved,
// and nothing of Docker is touched. `claude` and `docker` are fakes (fixtures/fake-cli.mjs); the
// "remote" server is a loopback HTTP server, which --url accepts as plain http on 127.0.0.1 only.
import { test, before, after } from 'node:test'
import assert from 'node:assert/strict'
import { execFile, spawnSync } from 'node:child_process'
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync, chmodSync } from 'node:fs'
import http from 'node:http'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const HERE = path.dirname(fileURLToPath(import.meta.url))
const ASSETS = path.join(HERE, '..')
const FAKE = path.join(HERE, 'fixtures', 'fake-cli.mjs')

/** @returns {string | null} */
function findBash() {
    if (process.platform !== 'win32') return spawnSync('bash', ['--version']).status === 0 ? 'bash' : null
    // not System32\bash.exe: that is WSL, which cannot run the Windows node the fakes need
    for (const p of ['C:\\Program Files\\Git\\bin\\bash.exe', 'C:\\Program Files (x86)\\Git\\bin\\bash.exe']) {
        if (existsSync(p)) return p
    }
    return null
}
const BASH = findBash()
const PWSH = spawnSync('pwsh', ['-NoProfile', '-Command', 'exit 0']).status === 0 ? 'pwsh' : null

/** @type {'ok' | 'no401' | 'nometa' | 'badprm'} */
let mode = 'ok'
/** @type {http.Server} */
let server
let origin = ''

before(async () => {
    server = http.createServer((req, res) => {
        const prm = `${origin}/.well-known/oauth-protected-resource/mcp`
        if (req.method === 'POST' && req.url === '/mcp') {
            if (mode === 'no401') { res.writeHead(200, { 'Content-Type': 'application/json' }); res.end('{}'); return }
            const headers = mode === 'nometa' ? {} : { 'WWW-Authenticate': `Bearer resource_metadata="${prm}"` }
            res.writeHead(401, headers); res.end(); return
        }
        if (req.method === 'GET' && req.url === '/.well-known/oauth-protected-resource/mcp' && mode !== 'badprm') {
            res.writeHead(200, { 'Content-Type': 'application/json' })
            res.end(JSON.stringify({ resource: `${origin}/mcp`, authorization_servers: [origin] }))
            return
        }
        res.writeHead(404); res.end()
    })
    await new Promise((resolve) => server.listen(0, '127.0.0.1', () => resolve(undefined)))
    const address = server.address()
    origin = `http://127.0.0.1:${typeof address === 'object' && address ? address.port : 0}`
})
after(() => new Promise((resolve) => server.close(() => resolve(undefined))))

/** A scratch home, cwd and fake bin per run. */
function sandbox() {
    const root = mkdtempSync(path.join(os.tmpdir(), 'workman-setup-url-'))
    const home = path.join(root, 'home'); const cwd = path.join(root, 'cwd')
    const binSh = path.join(root, 'bin-sh'); const binPs = path.join(root, 'bin-ps')
    for (const d of [home, cwd, binSh, binPs]) mkdirSync(d)
    const fake = FAKE.replaceAll('\\', '/'); const node = process.execPath.replaceAll('\\', '/')
    for (const tool of ['claude', 'docker']) {
        const sh = path.join(binSh, tool)
        writeFileSync(sh, `#!/bin/sh\nexec "${node}" "${fake}" ${tool} "$@"\n`)
        chmodSync(sh, 0o755)
        writeFileSync(path.join(binPs, `${tool}.ps1`), `& '${node}' '${fake}' ${tool} @args\nexit $LASTEXITCODE\n`)
    }
    return { root, home, cwd, binSh, binPs, log: path.join(root, 'calls.jsonl') }
}

/** --kebab-case to -PascalCase, for the PowerShell twin. @param {string[]} args */
const psArgs = (args) => args.map((a) => a.startsWith('--')
    ? '-' + a.slice(2).split('-').map((w) => w[0].toUpperCase() + w.slice(1)).join('')
    : a)

/**
 * @param {'sh' | 'ps1'} kind @param {string[]} args @param {{ existingUrl?: string }} [opts]
 * @returns {Promise<{ code: number, stdout: string, stderr: string, calls: { tool: string, args: string[] }[], box: ReturnType<typeof sandbox> }>}
 */
function run(kind, args, opts = {}) {
    const box = sandbox()
    const env = {
        ...process.env,
        HOME: box.home, USERPROFILE: box.home, CLAUDE_CONFIG_DIR: box.home,
        PATH: (kind === 'sh' ? box.binSh : box.binPs) + path.delimiter + process.env.PATH,
        FAKE_LOG: box.log, FAKE_EXISTING_URL: opts.existingUrl ?? '',
    }
    const [file, argv] = kind === 'sh'
        ? [/** @type {string} */ (BASH), [path.join(ASSETS, 'setup.sh'), ...args]]
        : [/** @type {string} */ (PWSH), ['-NoProfile', '-NonInteractive', '-File', path.join(ASSETS, 'setup.ps1'), ...psArgs(args)]]
    return new Promise((resolve) => {
        execFile(file, argv, { cwd: box.cwd, env, timeout: 60_000 }, (err, stdout, stderr) => {
            const calls = existsSync(box.log)
                ? readFileSync(box.log, 'utf8').split('\n').filter(Boolean).map((l) => JSON.parse(l))
                : []
            const code = err ? (typeof err.code === 'number' ? err.code : 1) : 0
            resolve({ code, stdout, stderr, calls, box })
        })
    })
}

/** @param {{ tool: string, args: string[] }[]} calls @param {string} sub */
const claudeCalls = (calls, sub) => calls.filter((c) => c.tool === 'claude' && c.args[1] === sub)

for (const kind of /** @type {const} */ (['sh', 'ps1'])) {
    const skip = kind === 'sh' ? (BASH ? false : 'no bash') : (PWSH ? false : 'no pwsh')
    const flag = (/** @type {string} */ f) => (kind === 'sh' ? f : psArgs([f])[0])

    test(`${kind}: --url registers <url>/mcp with OAuth and the session helper, and never calls docker`, { skip }, async () => {
        mode = 'ok'
        const r = await run(kind, ['--url', `${origin}/`])
        try {
            assert.equal(r.code, 0, r.stderr)
            assert.deepEqual(r.calls.filter((c) => c.tool === 'docker'), [], 'no docker call')
            const adds = claudeCalls(r.calls, 'add-json')
            assert.equal(adds.length, 1, 'one registration written')
            const [, , , scope, name, json] = adds[0].args
            assert.equal(scope, 'user'); assert.equal(name, 'workman')
            const entry = JSON.parse(json)
            assert.equal(entry.type, 'http')
            assert.equal(entry.url, `${origin}/mcp`)
            assert.equal(entry.headers, undefined, 'OAuth: no Authorization header stored')
            assert.match(entry.headersHelper, /^node "[^"]*session-header\.mjs"$/)
            const helper = path.join(r.box.home, '.workman', 'session-header.mjs')
            assert.ok(existsSync(helper), 'the helper is copied to ~/.workman')
            assert.ok(!existsSync(path.join(r.box.home, '.workman', '.env')), 'no .env')
            assert.match(r.stdout, /^auth {6}oauth: .*[Uu]rl: a remote server/m)
            assert.match(r.stdout, /^prove {5}POST \/mcp without a token: 401 with resource_metadata=/m)
            assert.match(r.stdout, new RegExp(`^register  'workman' -> ${origin}/mcp with OAuth sign-in, session helper \\(user scope; no token stored\\)`, 'm'))
            assert.match(r.stdout, /^next {6}start a new Claude Code session, run \/mcp, pick 'workman', Authenticate/m)
        } finally { rmSync(r.box.root, { recursive: true, force: true }) }
    })

    test(`${kind}: --url --dry-run writes nothing`, { skip }, async () => {
        mode = 'ok'
        const r = await run(kind, ['--url', origin, '--dry-run'])
        try {
            assert.equal(r.code, 0, r.stderr)
            assert.ok(!existsSync(path.join(r.box.home, '.workman')), 'no home created')
            assert.deepEqual(claudeCalls(r.calls, 'add-json'), [])
            assert.deepEqual(claudeCalls(r.calls, 'remove'), [])
            assert.match(r.stdout, /^helper {4}dry run, would copy session-header\.mjs/m)
            assert.match(r.stdout, /^register  dry run, would run: claude mcp add-json --scope user workman/m)
        } finally { rmSync(r.box.root, { recursive: true, force: true }) }
    })

    for (const [m, why] of /** @type {const} */ ([
        ['no401', /answered 200, not 401/],
        ['nometa', /answered 401 without resource_metadata=/],
        ['badprm', /answered 404, not the OAuth metadata/],
    ])) {
        test(`${kind}: --url fails the proof (${m}) with a clear line and registers nothing`, { skip }, async () => {
            mode = m
            const r = await run(kind, ['--url', origin])
            try {
                assert.notEqual(r.code, 0)
                assert.match(r.stderr, /^prove {5}/m)
                assert.match(r.stderr, why)
                assert.match(r.stderr, /a Workman server, and up\?/)
                assert.deepEqual(claudeCalls(r.calls, 'add-json'), [], 'nothing registered')
            } finally { rmSync(r.box.root, { recursive: true, force: true }) }
        })
    }

    test(`${kind}: an existing registration elsewhere is kept without --force`, { skip }, async () => {
        mode = 'ok'
        const r = await run(kind, ['--url', origin], { existingUrl: 'https://old.example/mcp' })
        try {
            assert.equal(r.code, 0, r.stderr)
            assert.deepEqual(claudeCalls(r.calls, 'add-json'), [])
            assert.deepEqual(claudeCalls(r.calls, 'remove'), [])
            assert.match(r.stdout, /^register  kept the existing 'workman' registration, which points at https:\/\/old\.example\/mcp/m)
            assert.match(r.stdout, new RegExp(`^next {6}nothing changed: .*${flag('--force')} to re-point it`, 'm'))
        } finally { rmSync(r.box.root, { recursive: true, force: true }) }
    })

    test(`${kind}: --force re-points an existing registration to the new url`, { skip }, async () => {
        mode = 'ok'
        const r = await run(kind, ['--url', origin, '--force'], { existingUrl: 'https://old.example/mcp' })
        try {
            assert.equal(r.code, 0, r.stderr)
            const removes = claudeCalls(r.calls, 'remove')
            assert.equal(removes.length, 1, 'the old one removed')
            assert.deepEqual(removes[0].args.slice(2), ['workman', '--scope', 'user'])
            const adds = claudeCalls(r.calls, 'add-json')
            assert.equal(adds.length, 1)
            assert.equal(JSON.parse(adds[0].args[5]).url, `${origin}/mcp`)
            assert.ok(r.calls.indexOf(removes[0]) < r.calls.indexOf(adds[0]), 'removed before added')
        } finally { rmSync(r.box.root, { recursive: true, force: true }) }
    })

    for (const extra of [
        ['--clone', '.'], ['--port', '5291'], ['--project', 'other'], ['--image-version', '2.1.0'], ['--update'],
        ['--backup', 'x.dump'], ['--restore', 'x.dump'], ['--auth', 'token'], ['--role', 'worker'],
    ]) {
        test(`${kind}: --url refuses ${extra.join(' ')}`, { skip }, async () => {
            const r = await run(kind, ['--url', 'https://work.example', ...extra])
            try {
                assert.notEqual(r.code, 0)
                const shown = extra[0] === '--auth' ? `${flag('--auth')} token` : flag(extra[0])
                assert.ok(r.stderr.includes(`with ${shown}`), r.stderr)
                assert.match(r.stderr, /for a local Docker install/)
                assert.deepEqual(r.calls, [], 'neither claude nor docker called')
            } finally { rmSync(r.box.root, { recursive: true, force: true }) }
        })
    }

    for (const bad of ['http://work.example', 'https://work.example/mcp', 'work.example']) {
        test(`${kind}: --url refuses ${bad}`, { skip }, async () => {
            const r = await run(kind, ['--url', bad])
            try {
                assert.notEqual(r.code, 0)
                assert.match(r.stderr, /[Uu]rl (must be an https origin|is an origin)/)
                assert.deepEqual(r.calls, [])
            } finally { rmSync(r.box.root, { recursive: true, force: true }) }
        })
    }
}
