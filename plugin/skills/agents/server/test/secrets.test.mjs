// No tool result, brief, state file or log line may contain a token. The fake tokens are distinctive
// strings, so a plain substring search over everything the server produced is the whole check.
// @ts-check
import { test } from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { startServer, makeRepo, fakeWorkman, walk, tmp, homeEnv, CONTROLLER, CONTROLLER_TOKEN, MINTED_TOKEN, agentsDir } from './helpers.mjs'
import { fakeOAuthWorkman } from './fake-oauth.mjs'

/** @param {string} where @param {string} text */
function assertClean(where, text) {
    assert.ok(!text.includes(MINTED_TOKEN), `minted token found in ${where}`)
    assert.ok(!text.includes(CONTROLLER_TOKEN), `controller token found in ${where}`)
}

test('launch, list, failed launch and stop leak no token anywhere but mcp-<name>.json', async () => {
    const repo = makeRepo()
    const ws = await fakeWorkman()
    const s = startServer()
    const failing = startServer({ FAKE_CLAUDE_BG_FAIL: '1' })
    try {
        const results = []
        const args = { brief: 'task.md', repo_root: repo.root, controller: CONTROLLER, workman: {} }
        results.push(await s.call('agent_launch', { ...args, name: 'leak-1' }))
        results.push(await s.call('agent_launch', { ...args, name: 'leak-dry', dry_run: true }))
        results.push(await failing.call('agent_launch', { ...args, name: 'leak-fail' }))
        results.push(await s.call('agent_list', {}))

        const mcp = path.join(agentsDir(s), 'mcp-leak-1.json')
        assert.ok(fs.readFileSync(mcp, 'utf8').includes(MINTED_TOKEN), 'the config is the one place the token belongs')

        for (const f of walk(s.data)) if (!f.endsWith('claude-argv.jsonl')) assertClean(f, fs.readFileSync(f, 'utf8'))
        assertClean('claude argv', fs.readFileSync(path.join(s.data, 'claude-argv.jsonl'), 'utf8'))
        for (const f of walk(repo.briefs)) if (f !== mcp) assertClean(f, fs.readFileSync(f, 'utf8'))
        for (const f of [...walk(agentsDir(s)), ...walk(agentsDir(failing))]) if (f !== mcp) assertClean(f, fs.readFileSync(f, 'utf8'))

        results.push(await s.call('agent_stop', { name: 'leak-1', keep_brief: true }))
        for (const r of results) assertClean('a tool result', JSON.stringify(r))
        for (const f of [...walk(repo.briefs), ...walk(agentsDir(s))]) assertClean(f, fs.readFileSync(f, 'utf8'))
    } finally {
        await s.close()
        await failing.close()
        await ws.close()
    }
    for (const srv of [s, failing]) {
        const { stdout, stderr } = srv.output()
        assertClean('server stdout', stdout)
        assertClean('server stderr', stderr)
    }
})

test('sign-in, launch, list, failed launch, stop and sign-out leak no OAuth token, code, verifier or minted token', async () => {
    const repo = makeRepo()
    const ws = await fakeOAuthWorkman()
    const grants = path.join(tmp('home'), '.workman', 'grants.json')
    const home = homeEnv(path.dirname(path.dirname(grants)))
    const s = startServer(home)
    const failing = startServer({ ...home, FAKE_CLAUDE_BG_FAIL: '1' })
    /** @param {string} where @param {string} text @param {string[]} [allowed] */
    const clean = (where, text, allowed = []) => {
        for (const x of ws.secrets) if (!allowed.includes(x)) assert.ok(!text.includes(x), `${x.slice(0, 14)}... found in ${where}`)
    }
    try {
        const results = []
        const args = { brief: 'task.md', repo_root: repo.root, controller: CONTROLLER, workman: {} }
        results.push(await s.call('agent_signin', {}))
        results.push(await s.call('agent_launch', { ...args, name: 'oleak-1' }))
        results.push(await s.call('agent_launch', { ...args, name: 'oleak-dry', dry_run: true }))
        results.push(await failing.call('agent_launch', { ...args, name: 'oleak-fail' }))
        results.push(await s.call('agent_list', {}))
        for (const r of results) assert.equal(r.isError, r === results[3], r.content[0].text)

        const mcp = path.join(agentsDir(s), 'mcp-oleak-1.json')
        const [minted] = ws.minted()
        assert.ok(fs.readFileSync(mcp, 'utf8').includes(minted), 'the config is the one place the minted token belongs')
        const refresh = JSON.parse(fs.readFileSync(grants, 'utf8'))[ws.url].refresh_token
        clean(grants, fs.readFileSync(grants, 'utf8'), [refresh])
        for (const f of walk(s.data)) clean(f, fs.readFileSync(f, 'utf8'))
        for (const f of walk(failing.data)) clean(f, fs.readFileSync(f, 'utf8'))
        for (const f of [...walk(repo.briefs), ...walk(agentsDir(s)), ...walk(agentsDir(failing))]) clean(f, fs.readFileSync(f, 'utf8'), f === mcp ? [minted] : [])

        results.push(await s.call('agent_stop', { name: 'oleak-1', keep_brief: true }))
        results.push(await s.call('agent_signout', {}))
        results.push(await s.call('agent_launch', { ...args, name: 'oleak-out' }))
        for (const r of results) clean('a tool result', JSON.stringify(r))
        for (const f of [...walk(repo.briefs), ...walk(agentsDir(s))]) clean(f, fs.readFileSync(f, 'utf8'))
        clean(grants, fs.readFileSync(grants, 'utf8'))
    } finally {
        await s.close()
        await failing.close()
        await ws.close()
    }
    for (const srv of [s, failing]) {
        const { stdout, stderr } = srv.output()
        clean('server stdout', stdout)
        clean('server stderr', stderr)
    }
})
