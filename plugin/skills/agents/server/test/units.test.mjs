// @ts-check
import { test } from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { resolveClaude, parseBackgroundId } from '../claude.mjs'
import { saveRecord } from '../state.mjs'
import { serverUrl, agentsDir, clearAuthCacheEntry, NEVER_PROBE } from '../home.mjs'
import { tmp } from './helpers.mjs'

test('backgrounded line: id read through ANSI colour codes', () => {
    assert.equal(parseBackgroundId('\x1b[2mbackgrounded\x1b[0m · \x1b[1;36mc88dd0c8\x1b[0m · fix-B-07\n'), 'c88dd0c8')
    assert.equal(parseBackgroundId('backgrounded | c88dd0c8 | x'), 'c88dd0c8')
    assert.equal(parseBackgroundId('error: nope'), undefined)
})

test('CLAUDE_BIN must be a JSON array', () => {
    assert.deepEqual(resolveClaude({ CLAUDE_BIN: '["node","x.mjs"]' }), ['node', 'x.mjs'])
    assert.throws(() => resolveClaude({ CLAUDE_BIN: 'node x.mjs' }), /JSON array/)
})

test('Windows: claude.exe preferred; a lone claude.cmd is refused', { skip: process.platform !== 'win32' }, () => {
    const cmdOnly = tmp('cmd')
    fs.writeFileSync(path.join(cmdOnly, 'claude.cmd'), '@echo off\n')
    const withExe = tmp('exe')
    fs.writeFileSync(path.join(withExe, 'claude.exe'), '')
    const emptyHome = tmp('home')
    assert.deepEqual(resolveClaude({ PATH: [cmdOnly, withExe].join(path.delimiter), USERPROFILE: emptyHome }), [path.join(withExe, 'claude.exe')])
    assert.throws(() => resolveClaude({ PATH: cmdOnly, USERPROFILE: emptyHome }), /only a claude\.cmd shim/)
    fs.mkdirSync(path.join(emptyHome, '.local', 'bin'), { recursive: true })
    fs.writeFileSync(path.join(emptyHome, '.local', 'bin', 'claude.exe'), '')
    assert.deepEqual(resolveClaude({ PATH: cmdOnly, USERPROFILE: emptyHome }), [path.join(emptyHome, '.local', 'bin', 'claude.exe')])
})

/** Run fn with os.homedir() pointing at home. @param {string} home @param {() => void} fn */
function withHome(home, fn) {
    const saved = { HOME: process.env.HOME, USERPROFILE: process.env.USERPROFILE }
    process.env.HOME = home
    process.env.USERPROFILE = home
    try {
        fn()
    } finally {
        for (const [k, v] of Object.entries(saved)) {
            if (v === undefined) delete process.env[k]
            else process.env[k] = v
        }
    }
}

/** @param {string} home @param {unknown} v */
function writeServerJson(home, v) {
    fs.mkdirSync(path.join(home, '.workman'), { recursive: true })
    fs.writeFileSync(path.join(home, '.workman', 'server.json'), JSON.stringify(v))
}

test('server.json url is normalised to its origin; a path, query, credentials or another mcpName refuse', () => {
    const home = tmp('norm')
    withHome(home, () => {
        for (const [url, origin] of [
            ['https://work.example.test', 'https://work.example.test'],
            ['https://work.example.test/', 'https://work.example.test'],
            ['https://Work.EXAMPLE.test', 'https://work.example.test'],
            ['https://work.example.test:443/', 'https://work.example.test'],
            ['http://127.0.0.1:80', 'http://127.0.0.1'],
            ['http://127.0.0.1:5290/', 'http://127.0.0.1:5290'],
        ]) {
            writeServerJson(home, { url, mcpName: 'workman' })
            assert.equal(serverUrl(), origin, url)
        }
        writeServerJson(home, { url: 'https://work.example.test' })
        assert.equal(serverUrl(), 'https://work.example.test', 'mcpName may be absent')
        for (const bad of [
            { url: 'https://work.example.test/mcp', mcpName: 'workman' },
            { url: 'https://work.example.test/?a=1', mcpName: 'workman' },
            { url: 'https://work.example.test?', mcpName: 'workman' },
            { url: 'https://work.example.test/#x', mcpName: 'workman' },
            { url: 'https://u:p@work.example.test', mcpName: 'workman' },
            { url: 'ftp://work.example.test', mcpName: 'workman' },
            { url: 'https://work.example.test', mcpName: 'workman-agent' },
        ]) {
            writeServerJson(home, bad)
            assert.throws(
                () => serverUrl(),
                (/** @type {Error} */ e) => /Run \/workman:setup/.test(e.message) && e.message.includes(NEVER_PROBE) && !e.message.includes('u:p'),
                JSON.stringify(bad),
            )
        }
    })
})

test('records live in ~/.workman/agents: directory 0700, record 0600 (modes on POSIX only)', () => {
    const home = tmp('files')
    withHome(home, () => {
        const dir = agentsDir()
        assert.equal(dir, path.join(home, '.workman', 'agents'))
        saveRecord({ name: 'f-1', id: 'x', sessionId: undefined, repo_root: home, brief: 'b', mcpConfigPath: undefined, url: undefined, workman: null, launchedAt: 'now' })
        const rec = path.join(dir, 'f-1.json')
        assert.ok(fs.existsSync(rec))
        if (process.platform !== 'win32') {
            assert.equal(fs.statSync(dir).mode & 0o777, 0o700)
            assert.equal(fs.statSync(rec).mode & 0o777, 0o600)
        }
    })
})

test('the auth-cache clear edits the config dir of the env the worker is spawned with', () => {
    const configDir = tmp('cfg')
    const file = path.join(configDir, 'mcp-needs-auth-cache.json')
    fs.writeFileSync(file, JSON.stringify({ workman: { timestamp: Date.now() }, other: { timestamp: 1 } }))
    assert.equal(clearAuthCacheEntry({ CLAUDE_CONFIG_DIR: configDir }), undefined)
    assert.deepEqual(JSON.parse(fs.readFileSync(file, 'utf8')), { other: { timestamp: 1 } })
})
