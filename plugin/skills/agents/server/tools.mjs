// tools.mjs — agent_launch, agent_stop, agent_list, agent_signin, agent_signout: schemas and handlers.
//
// Nothing returned from here, logged, or written outside mcp-<name>.json may hold a token. The
// access token lives in a local variable for the length of one call; the minted one is written to
// its file and dropped.
// @ts-check

import fs from 'node:fs'
import path from 'node:path'
import { spawnSync } from 'node:child_process'
import { protocolHeader } from './header.mjs'
import {
    Refusal, validateLaunch, buildArgv, isObject, rejectUnknown, PERMISSION_MODES, TOOLSETS, EFFORTS, ROLES, GRANTS,
} from './launch-args.mjs'
import { runClaude, parseBackgroundId, listLiveAgents, stripAnsi, resolveClaude } from './claude.mjs'
import { mintSession, bindClaudeSession, revokeSession, mcpConfigJson } from './workman.mjs'
import { signIn, signOut, accessToken, requireGrant, forgetAccessToken } from './oauth.mjs'
import { serverUrl, clearAuthCacheEntry, writeFileAtomic, sleep } from './home.mjs'
import { saveRecord, loadRecords, findRecord, deleteRecord } from './state.mjs'
import { repoKeys } from './repo.mjs'

/** @param {string} line */
export function log(line) {
    process.stderr.write(`agents: ${line}\n`)
}

const pathArray = { type: 'array', items: { type: 'string' } }
const noParams = { type: 'object', properties: {}, additionalProperties: false }

export const TOOLS = [
    {
        name: 'agent_launch',
        description: 'Launch a nested Claude Code session in the background (claude --bg) with the protocol header prepended to its brief and, unless workman.role is "none", its own Workman token, minted with the agent_signin grant for the server in ~/.workman/server.json (written by /workman:setup). Returns name, id, sessionId and brief; never a token.',
        inputSchema: {
            type: 'object',
            properties: {
                name: { type: 'string', description: 'Session name and SendMessage address. Letters, digits, . _ -' },
                brief: { type: 'string', description: 'Brief file inside repo_root (absolute, or relative to repo_root)' },
                repo_root: { type: 'string', description: 'Absolute path of the git working tree the agent runs in' },
                controller: { type: 'string', description: "Your address from ListAgents, ref included: 'name [6-hex]'" },
                permission_mode: { type: 'string', enum: PERMISSION_MODES, default: 'auto', description: '--permission-mode' },
                toolset: { type: 'string', enum: TOOLSETS, default: 'fix', description: 'Allow-list profile (agents §3)' },
                allowed_tools: { ...pathArray, description: 'Merged into --allowedTools' },
                disallowed_tools: { ...pathArray, description: '--disallowedTools' },
                add_dirs: { ...pathArray, description: '--add-dir' },
                model: { type: 'string', description: '--model' },
                effort: { type: 'string', enum: EFFORTS, description: '--effort' },
                settings: { type: 'string', description: '--settings: a file path or a JSON string' },
                append_system_prompt: { type: 'string', description: '--append-system-prompt' },
                workman: {
                    type: 'object',
                    description: 'The per-session Workman token. Default {role: "worker"}',
                    properties: {
                        role: { type: 'string', enum: ROLES, default: 'worker', description: '"none" mints nothing and passes no --mcp-config' },
                        ttl_hours: { type: 'integer', minimum: 1, maximum: 720, description: 'Idle window in hours (server default 72): the token lapses after this long unused; every use renews it, no hard cap' },
                        grants: { type: 'array', items: { type: 'string', enum: GRANTS }, description: 'Capabilities on the minted token. "import" shows work_import; the server allows it only on role orchestrator or controller' },
                    },
                    additionalProperties: false,
                },
                dry_run: { type: 'boolean', default: false, description: 'Validate and return the argv; mint and spawn nothing' },
            },
            required: ['name', 'brief', 'repo_root', 'controller'],
            additionalProperties: false,
        },
    },
    {
        name: 'agent_stop',
        description: 'Stop a launched agent (claude stop, and claude rm with remove), revoke its Workman token and delete its mcp config; the brief too unless keep_brief.',
        inputSchema: {
            type: 'object',
            properties: {
                id: { type: 'string', description: "claude's short id" },
                name: { type: 'string', description: 'The name it was launched with' },
                remove: { type: 'boolean', default: false, description: 'Also claude rm the session' },
                keep_brief: { type: 'boolean', default: false, description: 'Keep the brief, e.g. for a failed agent' },
            },
            additionalProperties: false,
        },
    },
    {
        name: 'agent_list',
        description: 'claude agents --json joined with what agent_launch recorded: name, id, status, waitingFor, repo_root, Workman session and role.',
        inputSchema: noParams,
    },
    {
        name: 'agent_signin',
        description: 'Sign in once, in the browser (OAuth with PKCE), to the Workman server in ~/.workman/server.json, so agent_launch and agent_stop can mint and revoke. Opens the system browser and waits up to 5 minutes for the sign-in; the result also carries the URL to open by hand. The grant is stored in ~/.workman/grants.json; no token is ever returned.',
        inputSchema: noParams,
    },
    {
        name: 'agent_signout',
        description: 'Revoke the agent_signin grant for the Workman server in ~/.workman/server.json and delete it. Every agent session minted with it is refused on its next request.',
        inputSchema: noParams,
    },
]

/**
 * @typedef {{text: string, structured: Record<string, unknown>, isError?: boolean}} ToolResult
 */

/**
 * @param {string} name
 * @param {unknown} args
 * @returns {Promise<ToolResult>}
 */
export async function callTool(name, args) {
    try {
        if (name === 'agent_launch') return await agentLaunch(args ?? {})
        if (name === 'agent_stop') return await agentStop(args ?? {})
        if (name === 'agent_list') return await agentList()
        if (name === 'agent_signin') return await signIn(noArgs(name, args))
        if (name === 'agent_signout') return await signOut(noArgs(name, args))
    } catch (e) {
        if (e instanceof Refusal) return { text: `refused: ${e.message}`, structured: { refused: e.message }, isError: true }
        throw e
    }
    throw new Error(`unknown tool ${name}`)
}

/**
 * @param {string} tool
 * @param {unknown} args
 * @returns {string} the server url
 */
function noArgs(tool, args) {
    if (args !== undefined && args !== null) {
        if (!isObject(args)) throw new Refusal(`${tool} takes no parameters`)
        rejectUnknown(args, [], tool)
    }
    return serverUrl()
}

/**
 * A file holding a secret must not be committable: refuse unless git says it is ignored.
 * @param {string} repoRoot
 * @param {string} file
 */
function ensureIgnored(repoRoot, file) {
    const rel = path.relative(repoRoot, file).split(path.sep).join('/')
    const r = spawnSync('git', ['check-ignore', '-q', rel], { cwd: repoRoot, windowsHide: true })
    if (r.status === 0) return
    if (r.status === 1) {
        throw new Refusal(`${rel} is not gitignored in ${repoRoot}, and it would hold a Workman token. Add .claude/briefs/ to that repo's .gitignore.`)
    }
    throw new Refusal(`could not confirm with git check-ignore that ${rel} is gitignored (${r.error?.message ?? `exit ${r.status}`}); it would hold a Workman token`)
}

/**
 * @param {string} id
 * @param {number} waitMs
 */
async function waitForSessionId(id, waitMs) {
    const deadline = Date.now() + waitMs
    let lastError
    for (;;) {
        const { agents, error } = await listLiveAgents()
        lastError = error
        const a = agents.find((x) => x.id === id)
        if (a?.sessionId) return { sessionId: a.sessionId }
        if (Date.now() >= deadline) return { error: lastError ?? `${id} was not listed with a sessionId within ${waitMs} ms` }
        await sleep(500)
    }
}

/**
 * @param {unknown} input
 * @returns {Promise<ToolResult>}
 */
async function agentLaunch(input) {
    const p = validateLaunch(input)
    const briefDir = path.join(p.repoRoot, '.claude', 'briefs')
    const briefPath = path.join(briefDir, `brief-${p.name}.txt`)
    const mints = p.workman.role !== 'none'
    const mcpConfigPath = mints ? path.join(briefDir, `mcp-${p.name}.json`) : undefined

    let url = ''
    let token = ''
    if (mints) {
        ensureIgnored(p.repoRoot, /** @type {string} */ (mcpConfigPath))
        url = serverUrl()
        if (p.dryRun) requireGrant(url)
        else token = await accessToken(url)
    }
    if (!p.dryRun) resolveClaude()

    const argv = buildArgv(p, { briefPath, mcpConfigPath })
    const briefText = protocolHeader(p.controller, p.name, mints) + fs.readFileSync(p.brief, 'utf8').replace(/^﻿/, '')
    fs.mkdirSync(briefDir, { recursive: true })

    if (p.dryRun) {
        fs.writeFileSync(briefPath, briefText)
        const lines = ['dry-run: nothing minted, claude NOT started', `brief: ${briefPath}`, `cwd:   ${p.repoRoot}`, 'argv:', '  [0] claude']
        argv.forEach((a, i) => lines.push(`  [${i + 1}] ${a}`))
        const mcpConfig = mints ? { path: mcpConfigPath, content: JSON.parse(mcpConfigJson(url, '<minted>')) } : null
        if (mcpConfig) lines.push(`mcp config (would be written, token shown as <minted>): ${mcpConfigPath}`, JSON.stringify(mcpConfig.content))
        return {
            text: lines.join('\n'),
            structured: { dry_run: true, argv: ['claude', ...argv], brief: briefPath, cwd: p.repoRoot, mcpConfig },
        }
    }

    /** @type {import('./workman.mjs').Minted|undefined} */
    let minted
    if (mints) {
        minted = await mintSession({
            url,
            token,
            name: p.name,
            role: p.workman.role,
            launchedBy: p.controller.replace(/\s\[[0-9a-fA-F]{6}\]$/, ''),
            cwd: p.repoRoot,
            ttlHours: p.workman.ttlHours,
            grants: p.workman.grants,
            repo: repoKeys(p.repoRoot),
        }).catch((e) => {
            forgetAccessToken(url)
            throw e
        })
    }

    /** @type {string[]} */
    const warnings = []
    if (minted) {
        const w = clearAuthCacheEntry()
        if (w) warnings.push(w)
    }

    let id
    try {
        if (minted) writeFileAtomic(/** @type {string} */ (mcpConfigPath), mcpConfigJson(url, minted.token), 0o600)
        fs.writeFileSync(briefPath, briefText)
        const run = await runClaude(argv, { cwd: p.repoRoot })
        id = parseBackgroundId(run.stdout + '\n' + run.stderr)
        if (!id) {
            const output = stripAnsi(`${run.stdout}\n${run.stderr}`).trim().slice(0, 2000)
            throw new Refusal(`could not read the background id from claude's output (${run.error ?? `exit ${run.code}`}):\n${output}`)
        }
    } catch (e) {
        let cleanup = ''
        if (minted) {
            const err = await revokeSession({ url, token, sessionId: minted.sessionId })
            fs.rmSync(/** @type {string} */ (mcpConfigPath), { force: true })
            cleanup = err ? ` The minted token could NOT be revoked (${err}); it lapses once unused past its idle window (current expiry ${minted.expiresAt ?? 'unknown'}, renewed on each use).` : ' The minted token was revoked and its config deleted.'
        }
        throw new Refusal(`${/** @type {Error} */ (e).message}${cleanup}`)
    }

    /** @type {import('./state.mjs').AgentRecord} */
    const rec = {
        name: p.name,
        id,
        sessionId: undefined,
        repo_root: p.repoRoot,
        brief: briefPath,
        mcpConfigPath,
        url: mints ? url : undefined,
        workman: minted ? { sessionId: minted.sessionId, role: minted.role, expiresAt: minted.expiresAt } : null,
        launchedAt: new Date().toISOString(),
    }
    saveRecord(rec)
    log(`launched ${p.name} as ${id}`)

    const waitMs = Number(process.env.AGENTS_SESSION_WAIT_MS ?? 10000)
    const found = await waitForSessionId(id, waitMs)
    if (found.sessionId) {
        rec.sessionId = found.sessionId
        saveRecord(rec)
        if (minted) {
            const err = await bindClaudeSession({ url, token, sessionId: minted.sessionId }, found.sessionId)
            if (err) warnings.push(`could not record the Claude session id on Workman session ${minted.sessionId}: ${err}`)
        }
    } else {
        warnings.push(`no sessionId yet: ${found.error}. The agent is running; agent_list shows it once claude lists it.`)
    }

    const ws = workmanRow(rec)
    const lines = [
        `launched ${p.name} (id ${id}${rec.sessionId ? `, session ${rec.sessionId}` : ''})`,
        `brief: ${briefPath}`,
        `permission mode: ${p.permissionMode}; toolset: ${p.toolset}`,
        ws ? `workman: session ${ws.session}, role ${ws.role}, idle expiry ${ws.expiresAt ?? '?'} (renewed on use)` : 'workman: none (no token minted, no --mcp-config)',
        ...warnings.map((w) => `warning: ${w}`),
    ]
    return {
        text: lines.join('\n'),
        structured: { name: p.name, id, sessionId: rec.sessionId ?? null, brief: briefPath, repo_root: p.repoRoot, permission_mode: p.permissionMode, toolset: p.toolset, workman: ws, warnings },
    }
}

/** @param {import('./state.mjs').AgentRecord} r */
const workmanRow = (r) => (r.workman ? { session: r.workman.sessionId, role: r.workman.role, expiresAt: r.workman.expiresAt } : null)

/**
 * @param {unknown} input
 * @returns {Promise<ToolResult>}
 */
async function agentStop(input) {
    if (!isObject(input)) throw new Refusal('agent_stop takes {id | name, remove?, keep_brief?}')
    const a = input
    rejectUnknown(a, ['id', 'name', 'remove', 'keep_brief'], 'agent_stop')
    const idIn = typeof a.id === 'string' && a.id ? a.id : undefined
    const nameIn = typeof a.name === 'string' && a.name ? a.name : undefined
    if (!idIn && !nameIn) throw new Refusal('agent_stop needs id or name')
    if (idIn?.startsWith('-')) throw new Refusal('id must not start with "-"')

    const rec = findRecord({ id: idIn, name: nameIn })
    const id = idIn ?? rec?.id
    if (!id && !rec) throw new Refusal(`no agent named "${nameIn}" was launched by this server; pass its id`)

    /** @type {string[]} */
    const done = []
    /** @type {string[]} */
    const problems = []
    /** @type {string[]} */
    const notes = []

    /** @param {string[]} args @param {string[]} onFail @param {string} hint */
    const claude = async (args, onFail, hint) => {
        const r = await runClaude(args, { timeoutMs: 30000 })
        if (r.code === 0 && !r.error) done.push(`claude ${args.join(' ')}`)
        else onFail.push(`claude ${args.join(' ')}: ${r.error ?? `exit ${r.code}`} ${stripAnsi(r.stderr || r.stdout).trim().slice(0, 300)}${hint}`)
    }
    if (id) {
        await claude(['stop', id], notes, ' (already stopped?)')
        if (a.remove === true) await claude(['rm', id], problems, '')
    }

    let revoked = false
    if (rec?.workman) {
        try {
            // The server that minted the token, as recorded at launch.
            if (!rec.url) throw new Refusal('the launch record has no url')
            const err = await revokeSession({ url: rec.url, token: await accessToken(rec.url), sessionId: rec.workman.sessionId })
            if (err) throw new Error(err)
            revoked = true
            done.push(`revoked Workman session ${rec.workman.sessionId}`)
        } catch (e) {
            problems.push(`could not revoke Workman session ${rec.workman.sessionId}: ${/** @type {Error} */ (e).message}; the token stays valid until unused past its idle window (expiry ${rec.workman.expiresAt ?? 'unknown'} when minted, renewed on each use). Retry agent_stop.`)
        }
    } else if (!rec) {
        notes.push('no launch record for this agent, so no token was revoked and no files were deleted')
    }

    if (rec?.mcpConfigPath) {
        fs.rmSync(rec.mcpConfigPath, { force: true })
        done.push(`deleted ${rec.mcpConfigPath}`)
    }
    if (rec && a.keep_brief !== true) {
        fs.rmSync(rec.brief, { force: true })
        done.push(`deleted ${rec.brief}`)
    }
    if (rec && problems.length === 0) deleteRecord(rec.name)
    log(`stopped ${rec?.name ?? id}${problems.length ? ' with problems' : ''}`)

    const lines = [...done.map((d) => `done: ${d}`), ...notes.map((n) => `note: ${n}`), ...problems.map((p) => `problem: ${p}`)]
    return {
        text: lines.join('\n') || 'nothing to do',
        structured: { name: rec?.name ?? nameIn ?? null, id: id ?? null, done, notes, problems, revoked },
        isError: problems.length > 0,
    }
}

/** @returns {Promise<ToolResult>} */
async function agentList() {
    const { agents: live, error } = await listLiveAgents()
    /** @type {{name: string|null, id: string|null, status: string|null, state: string|null, waitingFor: string|null, sessionId: string|null, repo_root: string|null, workman: ReturnType<typeof workmanRow>, tracked: boolean}[]} */
    const rows = loadRecords().map((r) => {
        const l = live.find((x) => (r.id && x.id === r.id) || (r.sessionId && x.sessionId === r.sessionId))
        return {
            name: r.name,
            id: r.id ?? null,
            status: l?.status ?? 'not listed',
            state: l?.state ?? null,
            waitingFor: l?.waitingFor ?? null,
            sessionId: l?.sessionId ?? r.sessionId ?? null,
            repo_root: r.repo_root,
            workman: workmanRow(r),
            tracked: true,
        }
    })
    for (const l of live) {
        if (rows.some((x) => x.id && x.id === l.id)) continue
        rows.push({
            name: l.name ?? null,
            id: l.id ?? null,
            status: l.status ?? null,
            state: l.state ?? null,
            waitingFor: l.waitingFor ?? null,
            sessionId: l.sessionId ?? null,
            repo_root: l.cwd ?? null,
            workman: null,
            tracked: false,
        })
    }
    const lines = rows.map((x) =>
        [x.name ?? '?', x.id ?? '?', x.status, x.waitingFor ? `waiting for ${x.waitingFor}` : '', x.repo_root ?? '',
            x.workman ? `ws ${x.workman.session} ${x.workman.role}` : '', x.tracked ? '' : '(not launched by agent_launch)']
            .filter(Boolean).join('  '))
    if (error) lines.push(`warning: ${error}`)
    return { text: lines.join('\n') || 'no agents', structured: { agents: rows, warning: error ?? null } }
}
