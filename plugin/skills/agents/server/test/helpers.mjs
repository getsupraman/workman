// helpers.mjs — a fake Workman, a throwaway git repo, and a stdio client for agents.mjs.
//
// Every server runs with a temp home directory (HOME and USERPROFILE: os.homedir() reads one or the
// other), whose ~/.workman/server.json points at the fake Workman created last.
// @ts-check

import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import http from 'node:http'
import { spawn, execFileSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
export const SERVER = path.join(here, '..', 'agents.mjs')
export const FAKE_CLAUDE = path.join(here, 'fake-claude.mjs')
export const FAKE_BROWSER = path.join(here, 'fake-browser.mjs')

export const MINTED_TOKEN = 'wss_FAKEminted0123456789abcdefMINTED'
export const CONTROLLER_TOKEN = 'ctl_FAKEcontroller0123456789SECRET'
export const CONTROLLER = 'skills-5d [e50df6]'
const SEEDED_REFRESH = 'wsrFAKEseededrefresh0123456789'

/** @type {string[]} */
const created = []
process.on('exit', () => {
    for (const d of created) fs.rmSync(d, { recursive: true, force: true })
})

/** @param {string} prefix */
export function tmp(prefix) {
    const d = fs.mkdtempSync(path.join(os.tmpdir(), `agents-${prefix}-`))
    created.push(d)
    return d
}

/**
 * A git working tree with .claude/briefs/ ignored and a brief.
 * @param {{ignoreBriefs?: boolean}} [opts]
 */
export function makeRepo(opts = {}) {
    const root = tmp('repo')
    execFileSync('git', ['init', '-q'], { cwd: root })
    if (opts.ignoreBriefs !== false) fs.writeFileSync(path.join(root, '.gitignore'), '.claude/briefs/\n')
    fs.writeFileSync(path.join(root, 'task.md'), 'Do the unit.\n')
    return { root, briefs: path.join(root, '.claude', 'briefs') }
}

/** @type {{url: string, grant: boolean}|undefined} */
let current

/**
 * Point the servers started from now on at this Workman. With grant, each gets a stored agent_signin
 * grant for it, which fakeWorkman redeems for CONTROLLER_TOKEN.
 * @param {string} url
 * @param {boolean} grant
 */
export function useWorkman(url, grant) {
    current = { url, grant }
}

/** @param {string} home */
export const homeEnv = (home) => ({ HOME: home, USERPROFILE: home })

/**
 * @typedef {{method: string, url: string, auth: string|undefined, body: any}} Recorded
 * @param {{mintStatus?: number}} [opts]
 */
export async function fakeWorkman(opts = {}) {
    /** @type {Recorded[]} */
    const requests = []
    const server = http.createServer((req, res) => {
        let raw = ''
        req.on('data', (d) => (raw += d))
        req.on('end', () => {
            let body = null
            try { body = raw ? JSON.parse(raw) : null } catch { body = raw }
            const json = (/** @type {number} */ status, /** @type {unknown} */ v) => {
                res.writeHead(status, { 'Content-Type': 'application/json' })
                res.end(v === undefined ? '' : JSON.stringify(v))
            }
            // The sign-in behind the seeded grant; not recorded in requests.
            if (req.url === '/.well-known/oauth-protected-resource/api') return json(200, { resource: `${url}/api`, authorization_servers: [url] })
            if (req.url === '/.well-known/oauth-authorization-server') return json(200, { authorization_endpoint: `${url}/connect/authorize`, token_endpoint: `${url}/connect/token` })
            if (req.method === 'POST' && req.url === '/connect/token') {
                return json(String(body).includes(`refresh_token=${SEEDED_REFRESH}`) ? 200 : 400, { access_token: CONTROLLER_TOKEN, token_type: 'Bearer', expires_in: 3600 })
            }
            requests.push({ method: req.method ?? '', url: req.url ?? '', auth: req.headers.authorization, body })
            if (req.method === 'POST' && req.url === '/api/agent-sessions') {
                if (opts.mintStatus && opts.mintStatus !== 201) return json(opts.mintStatus, { error: 'fake refusal' })
                return json(201, { sessionId: 'ws-42', name: body?.name, role: body?.role, token: MINTED_TOKEN, expiresAt: '2026-10-27T00:00:00Z' })
            }
            if (req.method === 'PATCH' && req.url === '/api/agent-sessions/ws-42') return json(200, { ok: true })
            if (req.method === 'DELETE' && req.url === '/api/agent-sessions/ws-42') {
                res.writeHead(204)
                return res.end()
            }
            json(404, { error: 'not found' })
        })
    })
    await new Promise((r) => server.listen(0, '127.0.0.1', () => r(undefined)))
    const addr = /** @type {import('node:net').AddressInfo} */ (server.address())
    const url = `http://127.0.0.1:${addr.port}`
    useWorkman(url, true)
    return {
        url,
        requests,
        close: () => new Promise((r) => server.close(() => r(undefined))),
    }
}

/**
 * Spawn agents.mjs with the fake claude and talk JSON-RPC to it.
 * @param {Record<string, string>} [env]  HOME here (see homeEnv) shares a home between servers
 * @param {{url?: string|null, grant?: boolean}} [opts]  the server.json url (null: write none) and
 *   whether to seed a grant for it; default the last fake Workman
 */
export function startServer(env = {}, opts = {}) {
    const data = tmp('data')
    const claudeLog = path.join(data, 'claude-argv.jsonl')
    const home = env.HOME ?? tmp('home')
    const url = 'url' in opts ? opts.url : current?.url
    const workman = path.join(home, '.workman')
    fs.mkdirSync(workman, { recursive: true })
    if (url) {
        fs.writeFileSync(path.join(workman, 'server.json'), JSON.stringify({ url, mcpName: 'workman' }))
        if (opts.grant ?? current?.grant) {
            fs.writeFileSync(path.join(workman, 'grants.json'), JSON.stringify({ [url]: { client_id: 'client-seeded', refresh_token: SEEDED_REFRESH } }))
        }
    }
    /** @type {NodeJS.ProcessEnv} */
    const childEnv = {
        ...process.env,
        CLAUDE_BIN: JSON.stringify([process.execPath, FAKE_CLAUDE]),
        FAKE_CLAUDE_LOG: claudeLog,
        CLAUDE_PLUGIN_DATA: data,
        AGENTS_SESSION_WAIT_MS: '1500',
        AGENTS_BROWSER: JSON.stringify([process.execPath, FAKE_BROWSER]),
        ...homeEnv(home),
        ...env,
    }
    if (!('CLAUDE_CONFIG_DIR' in env)) delete childEnv.CLAUDE_CONFIG_DIR
    const child = spawn(process.execPath, [SERVER], { env: childEnv, stdio: ['pipe', 'pipe', 'pipe'] })
    let stdout = ''
    let stderr = ''
    let buf = ''
    /** @type {Map<number, (v: any) => void>} */
    const waiting = new Map()
    child.stdout.setEncoding('utf8').on('data', (d) => {
        stdout += d
        buf += d
        let i
        while ((i = buf.indexOf('\n')) >= 0) {
            const line = buf.slice(0, i)
            buf = buf.slice(i + 1)
            const msg = JSON.parse(line)
            waiting.get(msg.id)?.(msg)
            waiting.delete(msg.id)
        }
    })
    child.stderr.setEncoding('utf8').on('data', (d) => (stderr += d))
    let next = 1

    /** @param {string} method @param {unknown} [params] @returns {Promise<any>} */
    function request(method, params) {
        const id = next++
        return new Promise((resolve) => {
            waiting.set(id, resolve)
            child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n')
        })
    }
    /** @param {string} method @param {unknown} [params] */
    function notify(method, params) {
        child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method, params }) + '\n')
    }
    /** @param {string} name @param {unknown} args */
    async function call(name, args) {
        const msg = await request('tools/call', { name, arguments: args })
        if (msg.error) throw new Error(`rpc error ${msg.error.code}: ${msg.error.message}`)
        return msg.result
    }
    /** @returns {{argv: string[], cwd: string}[]} */
    function claudeCalls() {
        if (!fs.existsSync(claudeLog)) return []
        return fs.readFileSync(claudeLog, 'utf8').split('\n').filter(Boolean).map((l) => JSON.parse(l))
    }
    async function close() {
        child.stdin.end()
        await new Promise((r) => child.on('exit', r))
    }
    return { request, notify, call, claudeCalls, close, data, home, output: () => ({ stdout, stderr }) }
}

/**
 * @param {string} dir
 * @returns {string[]} every file under dir
 */
export function walk(dir) {
    if (!fs.existsSync(dir)) return []
    /** @type {string[]} */
    const out = []
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
        const p = path.join(dir, e.name)
        if (e.isDirectory()) out.push(...walk(p))
        else out.push(p)
    }
    return out
}
