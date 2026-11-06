// ~/.workman/server.json (the only source of the Workman url), the mcp-needs-auth-cache.json clear
// before a minted launch, and the header's no-Workman-tools paragraph.
// @ts-check
import { test } from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { startServer, makeRepo, fakeWorkman, tmp, CONTROLLER } from './helpers.mjs'

/** @param {ReturnType<typeof makeRepo>} repo @param {string} name @param {Record<string, unknown>} [extra] */
const launchArgs = (repo, name, extra = {}) => ({ name, brief: 'task.md', repo_root: repo.root, controller: CONTROLLER, ...extra })

/** @param {ReturnType<typeof startServer>} s */
const cacheFile = (s) => path.join(s.home, '.claude', 'mcp-needs-auth-cache.json')

/** @param {string} file @param {unknown} v */
function writeJson(file, v) {
    fs.mkdirSync(path.dirname(file), { recursive: true })
    fs.writeFileSync(file, typeof v === 'string' ? v : JSON.stringify(v))
}

test('server.json missing, unreadable, without url, or with a url that is not a bare origin: every Workman call refuses naming /workman:setup', async () => {
    const repo = makeRepo()
    const ws = await fakeWorkman()
    const s = startServer()
    const file = path.join(s.home, '.workman', 'server.json')
    const good = fs.readFileSync(file, 'utf8')
    try {
        /** @type {[string, unknown][]} */
        const bad = [
            ['missing', undefined],
            ['not JSON', '{nope'],
            ['no url', { mcpName: 'workman' }],
            ['url with a path', { url: `${ws.url}/mcp`, mcpName: 'workman' }],
            ['url with a trailing slash', { url: `${ws.url}/`, mcpName: 'workman' }],
            ['not http', { url: 'ftp://127.0.0.1', mcpName: 'workman' }],
        ]
        for (const [label, content] of bad) {
            fs.rmSync(file, { force: true })
            if (content !== undefined) writeJson(file, content)
            for (const [tool, args] of /** @type {[string, unknown][]} */ ([
                ['agent_launch', launchArgs(repo, 'sj-1')],
                ['agent_launch', launchArgs(repo, 'sj-dry', { dry_run: true })],
                ['agent_signin', {}],
                ['agent_signout', {}],
            ])) {
                const r = await s.call(tool, args)
                assert.equal(r.isError, true, `${label}: ${tool}`)
                assert.match(r.content[0].text, /server\.json[\s\S]*Run \/workman:setup/, `${label}: ${tool}`)
            }
        }
        assert.equal(ws.requests.length, 0, 'nothing is minted anywhere')
        assert.equal(s.claudeCalls().length, 0)

        const plain = await s.call('agent_launch', launchArgs(repo, 'sj-none', { workman: { role: 'none' } }))
        assert.equal(plain.isError, false, `role none needs no server.json: ${plain.content[0].text}`)

        fs.writeFileSync(file, good)
        const ok = await s.call('agent_launch', launchArgs(repo, 'sj-ok'))
        assert.equal(ok.isError, false, ok.content[0].text)
        assert.equal(JSON.parse(fs.readFileSync(path.join(repo.briefs, 'mcp-sj-ok.json'), 'utf8')).mcpServers.workman.url, `${ws.url}/mcp`)
    } finally {
        await s.close()
        await ws.close()
    }
})

test('a minted launch removes only the stale "workman" entry from mcp-needs-auth-cache.json', async () => {
    const repo = makeRepo()
    const ws = await fakeWorkman()
    const s = startServer()
    const configDir = tmp('claude-config')
    const viaEnv = startServer({ CLAUDE_CONFIG_DIR: configDir })
    try {
        const others = { 'claude.ai Figma': { timestamp: 1793965706000, id: 'x-1' }, other: { timestamp: 5 } }
        for (const [server, file] of /** @type {[ReturnType<typeof startServer>, string][]} */ ([
            [s, cacheFile(s)],
            [viaEnv, path.join(configDir, 'mcp-needs-auth-cache.json')],
        ])) {
            writeJson(file, { workman: { timestamp: Date.now() - 6000 }, ...others })
            const r = await server.call('agent_launch', launchArgs(repo, `cache-${server === s ? 'home' : 'env'}`))
            assert.equal(r.isError, false, r.content[0].text)
            assert.deepEqual(JSON.parse(fs.readFileSync(file, 'utf8')), others)
            assert.deepEqual(r.structuredContent.warnings, [])
            assert.ok(!fs.readdirSync(path.dirname(file)).some((f) => f.endsWith('.tmp')), 'no temp file left behind')
        }
    } finally {
        await s.close()
        await viaEnv.close()
        await ws.close()
    }
})

test('role none and dry_run leave the cache alone', async () => {
    const repo = makeRepo()
    const ws = await fakeWorkman()
    const s = startServer()
    try {
        const stale = JSON.stringify({ workman: { timestamp: Date.now() } })
        writeJson(cacheFile(s), stale)
        for (const args of [launchArgs(repo, 'c-none', { workman: { role: 'none' } }), launchArgs(repo, 'c-dry', { dry_run: true })]) {
            const r = await s.call('agent_launch', args)
            assert.equal(r.isError, false, r.content[0].text)
            assert.equal(fs.readFileSync(cacheFile(s), 'utf8'), stale)
        }
    } finally {
        await s.close()
        await ws.close()
    }
})

test('a missing, corrupt or unwritable cache never fails the launch; the last two are warnings', async () => {
    const repo = makeRepo()
    const ws = await fakeWorkman()
    const s = startServer()
    const file = cacheFile(s)
    try {
        const missing = await s.call('agent_launch', launchArgs(repo, 'cm-1'))
        assert.equal(missing.isError, false, missing.content[0].text)
        assert.deepEqual(missing.structuredContent.warnings, [])
        assert.ok(!fs.existsSync(file), 'a missing cache is not created')

        for (const [i, corrupt] of ['{"workman": {"timestamp": 1', '["workman"]'].entries()) {
            writeJson(file, corrupt)
            const r = await s.call('agent_launch', launchArgs(repo, `cc-${i}`))
            assert.equal(r.isError, false, r.content[0].text)
            assert.match(r.content[0].text, /warning: .*mcp-needs-auth-cache\.json is not a JSON object; left it untouched/)
            assert.equal(fs.readFileSync(file, 'utf8'), corrupt, 'left byte for byte')
        }

        writeJson(file, { workman: { timestamp: Date.now() - 30000 }, keep: { timestamp: 1 } })
        // Windows refuses to rename over a read-only file; elsewhere a read-only directory refuses it.
        const lockTarget = process.platform === 'win32' ? file : path.dirname(file)
        fs.chmodSync(lockTarget, process.platform === 'win32' ? 0o444 : 0o555)
        try {
            const r = await s.call('agent_launch', launchArgs(repo, 'cl-1'))
            assert.equal(r.isError, false, r.content[0].text)
            assert.match(r.structuredContent.warnings[0], /could not remove "workman" from .*mcp-needs-auth-cache\.json \(entry 3\d s old\)/)
            assert.match(r.content[0].text, /^warning: could not remove "workman"/m)
        } finally {
            fs.chmodSync(lockTarget, process.platform === 'win32' ? 0o666 : 0o755)
        }
        assert.ok('workman' in JSON.parse(fs.readFileSync(file, 'utf8')))
    } finally {
        await s.close()
        await ws.close()
    }
})

test('the header tells a minted worker to stop when it has no Workman tools; role none is not told', async () => {
    const repo = makeRepo()
    const ws = await fakeWorkman()
    const s = startServer()
    try {
        assert.equal((await s.call('agent_launch', launchArgs(repo, 'h-minted', { dry_run: true }))).isError, false)
        assert.equal((await s.call('agent_launch', launchArgs(repo, 'h-none', { dry_run: true, workman: { role: 'none' } }))).isError, false)
        const minted = fs.readFileSync(path.join(repo.briefs, 'brief-h-minted.txt'), 'utf8')
        const none = fs.readFileSync(path.join(repo.briefs, 'brief-h-none.txt'), 'utf8')
        assert.match(minted, /no Workman work_report \(a deferred mcp__workman__work_report counts\), send\nBLOCKED no Workman tools to the controller and stop\./)
        assert.doesNotMatch(none, /Workman/)
        assert.doesNotMatch(minted + none, /\r/)
    } finally {
        await s.close()
        await ws.close()
    }
})
