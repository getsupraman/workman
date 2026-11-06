// The minted mcp config and the launch record under ~/.workman/agents, a relaunched name, the
// repo_root shadow refusal, and refusals and descriptions naming their next step.
// @ts-check
import { test } from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { startServer, makeRepo, fakeWorkman, CONTROLLER, MINTED_TOKEN, agentsDir } from './helpers.mjs'
import { NEVER_PROBE } from '../home.mjs'

/** @param {ReturnType<typeof makeRepo>} repo @param {string} name @param {Record<string, unknown>} [extra] */
const launchArgs = (repo, name, extra = {}) => ({ name, brief: 'task.md', repo_root: repo.root, controller: CONTROLLER, workman: {}, ...extra })

test('the minted config is written to ~/.workman/agents (0600 on POSIX), never into the repo, and agent_stop deletes it', async () => {
    const repo = makeRepo({ ignoreBriefs: false })
    const ws = await fakeWorkman()
    const s = startServer()
    try {
        const r = await s.call('agent_launch', launchArgs(repo, 'f-1'))
        assert.equal(r.isError, false, r.content[0].text)
        const mcp = path.join(agentsDir(s), 'mcp-f-1.json')
        assert.ok(fs.readFileSync(mcp, 'utf8').includes(MINTED_TOKEN))
        assert.ok(fs.existsSync(path.join(agentsDir(s), 'f-1.json')), 'the record sits beside it')
        assert.ok(!fs.existsSync(path.join(repo.briefs, 'mcp-f-1.json')), 'nothing secret in the repo, so no gitignore is needed')
        if (process.platform !== 'win32') {
            assert.equal(fs.statSync(agentsDir(s)).mode & 0o777, 0o700)
            assert.equal(fs.statSync(mcp).mode & 0o777, 0o600)
            assert.equal(fs.statSync(path.join(agentsDir(s), 'f-1.json')).mode & 0o777, 0o600)
        }
        const list = await s.call('agent_list', {})
        assert.deepEqual(list.structuredContent.agents.filter((/** @type {any} */ a) => a.tracked).map((/** @type {any} */ a) => a.name), ['f-1'], 'the config is not read as a record')

        const again = await s.call('agent_launch', launchArgs(repo, 'f-1'))
        assert.equal(again.isError, true)
        assert.match(again.content[0].text, /already recorded: agent_stop it first/)
        assert.equal(ws.requests.filter((q) => q.method === 'POST').length, 1, 'a relaunch mints nothing')

        const stop = await s.call('agent_stop', { name: 'f-1' })
        assert.equal(stop.isError, false, stop.content[0].text)
        assert.deepEqual(fs.readdirSync(agentsDir(s)), [], 'config and record deleted')
    } finally {
        await s.close()
        await ws.close()
    }
})

test('a project (.mcp.json) or local (~/.claude.json) "workman" entry for repo_root refuses a minted launch; role none is not refused', async () => {
    const ws = await fakeWorkman()
    const project = makeRepo()
    fs.writeFileSync(path.join(project.root, '.mcp.json'), JSON.stringify({ mcpServers: { workman: { type: 'http', url: 'https://team.example.test/mcp' } } }))
    const local = makeRepo()
    const other = makeRepo()
    const s = startServer()
    fs.writeFileSync(path.join(s.home, '.claude.json'), JSON.stringify({
        projects: {
            [local.root]: { mcpServers: { workman: { type: 'http', url: 'https://local.example.test/mcp', headers: { Authorization: 'Bearer SENTINEL-x' } } } },
            [other.root]: { mcpServers: { unrelated: { type: 'http', url: 'https://x.example.test/mcp' } } },
        },
    }))
    try {
        for (const [repo, scope, host] of /** @type {const} */ ([[project, 'project', 'team\\.example\\.test'], [local, 'local', 'local\\.example\\.test']])) {
            for (const dryRun of [false, true]) {
                const r = await s.call('agent_launch', launchArgs(repo, `sh-${scope}-${dryRun}`, { dry_run: dryRun }))
                assert.equal(r.isError, true, `${scope} dry_run=${dryRun}`)
                assert.match(r.content[0].text, new RegExp(`would replace the worker's minted one[\\s\\S]*\\n${scope}\\s+\\S.*${host}`))
                assert.ok(!r.content[0].text.includes('SENTINEL'), 'no header value is shown')
            }
            const none = await s.call('agent_launch', launchArgs(repo, `sh-${scope}-none`, { workman: { role: 'none' } }))
            assert.equal(none.isError, false, none.content[0].text)
        }
        const ok = await s.call('agent_launch', launchArgs(other, 'sh-other'))
        assert.equal(ok.isError, false, ok.content[0].text)
        assert.equal(ws.requests.filter((q) => q.method === 'POST').length, 1, 'only the unshadowed repo minted')
    } finally {
        await s.close()
        await ws.close()
    }
})

test('refusals and descriptions name the next step and the never-probe rule', async () => {
    const repo = makeRepo()
    const noServer = startServer({}, { url: null })
    const noGrant = startServer({}, { url: 'https://work.example.test', grant: false })
    try {
        const init = await noServer.request('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 't', version: '0' } })
        assert.ok(init.result.instructions.includes(NEVER_PROBE))
        assert.match(init.result.instructions, /the human runs \/mcp → workman/)
        const tools = (await noServer.request('tools/list', {})).result.tools
        const launch = tools.find((/** @type {any} */ t) => t.name === 'agent_launch')
        assert.ok(launch.description.includes(NEVER_PROBE))
        assert.match(launch.inputSchema.properties.workman.properties.role.description, /"none" .* your own Claude Code MCP servers and identity/)

        const missing = await noServer.call('agent_launch', launchArgs(repo, 'r-1'))
        assert.match(missing.content[0].text, /Run \/workman:setup/)
        assert.ok(missing.content[0].text.includes(NEVER_PROBE))

        for (const dryRun of [false, true]) {
            const unsigned = await noGrant.call('agent_launch', launchArgs(repo, `r-2-${dryRun}`, { dry_run: dryRun }))
            assert.match(unsigned.content[0].text, /call agent_signin/)
            assert.ok(unsigned.content[0].text.includes(NEVER_PROBE))
        }
    } finally {
        await noServer.close()
        await noGrant.close()
    }
})
