#!/usr/bin/env node
// session-header.mjs — the workman registration's headersHelper. Prints ONE JSON object of headers:
//   X-Workman-Client  "<host>/<pid>/<start epoch s>" of the nearest Claude Code ancestor process
//   X-Workman-Agent   that process's name, only when `claude agents --json` lists its pid with one
// Anything that goes wrong prints {} and exits 0: a session without the headers is merely unnamed.
// Identity comes from the process table only. CLAUDE_PID and CLAUDE_JOB_DIR are inherited from
// whatever started the process and would name a session after another one (doc 11), so they are
// never read. No secret is read or printed; nothing but the JSON goes to stdout.
// WS_HELPER_PS_JSON, WS_HELPER_AGENTS_JSON and WS_HELPER_SELF_PID replace the two lookups and the
// starting pid in tests; WS_HELPER_DEBUG_FILE appends each result to a file.
// @ts-check

import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { spawn, spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const BUDGET_MS = 8000

/** @typedef {{pid: number, ppid: number, name: string, start: number, exe?: string, args?: string}} Proc */

/**
 * Whether a process table row is the Claude Code CLI. Windows: claude.exe. macOS/Linux: an
 * executable whose basename is `claude`, or a node running Claude Code's cli — assumed from the
 * install layouts (native binary, npm package), not measured on those platforms.
 * @param {Proc} p
 */
export function isClaude(p) {
    const name = path.basename(p.name ?? '').toLowerCase()
    if (name === 'claude.exe' || name === 'claude') return true
    const first = (p.args ?? '').trim().split(/\s+/)[0] ?? ''
    if (path.basename(first) === 'claude') return true
    return (name === 'node' || name === 'node.exe') && /claude-code[\\/].*cli\.(m?js)\b/.test(p.args ?? '')
}

/**
 * The nearest ancestor of `self` that is Claude Code. A parent that started after its child is a
 * reused pid, not the parent, and ends the walk.
 * @param {Proc[]} table
 * @param {number} self
 * @returns {Proc | undefined}
 */
export function nearestClaude(table, self) {
    const byPid = new Map(table.map((p) => [p.pid, p]))
    let child = byPid.get(self)
    for (let depth = 0; child && depth < 64; depth++) {
        const parent = byPid.get(child.ppid)
        if (!parent || parent.pid === child.pid || parent.start > child.start) return undefined
        if (isClaude(parent)) return parent
        child = parent
    }
    return undefined
}

/**
 * @param {Proc} claude
 * @param {string} host
 * @param {unknown} agents what `claude agents --json` printed, parsed
 * @returns {Record<string, string>}
 */
export function headersFor(claude, host, agents) {
    /** @type {Record<string, string>} */
    const headers = { Client: `${host}/${claude.pid}/${claude.start}` }
    const list = Array.isArray(agents)
        ? agents
        : ['agents', 'sessions', 'items'].map((k) => /** @type {any} */ (agents)?.[k]).find(Array.isArray) ?? []
    const mine = list.find((a) => Number(a?.pid) === claude.pid)
    if (typeof mine?.name === 'string' && mine.name.trim()) headers.Agent = mine.name.trim()
    return prefixed(headers)
}

/**
 * Each header under its X-Workman-* name, the only one a 2.0 server reads.
 * @param {Record<string, string>} bare  names without the prefix, e.g. {Agent: 'm27'}
 * @returns {Record<string, string>}
 */
export function prefixed(bare) {
    /** @type {Record<string, string>} */
    const out = {}
    for (const [k, v] of Object.entries(bare)) out[`X-Workman-${k}`] = v
    return out
}

/** @param {string} text */
export function parseWindowsTable(text) {
    const rows = JSON.parse(text)
    return (Array.isArray(rows) ? rows : [rows]).map((r) => ({
        pid: Number(r.p), ppid: Number(r.q), name: String(r.n ?? ''), start: Number(r.s ?? 0), exe: r.x ?? undefined,
    }))
}

/**
 * `ps -A -o pid=,ppid=,lstart=,comm=,args=` under LC_ALL=C: lstart is five fields
 * ("Mon Oct 26 15:40:34 2026"), comm one, args the rest.
 * @param {string} text
 */
export function parsePsTable(text) {
    /** @type {Proc[]} */
    const rows = []
    for (const line of text.split('\n')) {
        const f = line.trim().split(/\s+/)
        if (f.length < 8) continue
        const start = Math.floor(Date.parse(f.slice(3, 7).join(' ')) / 1000)
        rows.push({ pid: Number(f[0]), ppid: Number(f[1]), start: Number.isFinite(start) ? start : 0, name: f[7], args: f.slice(8).join(' ') })
    }
    return rows
}

const WINDOWS_TABLE = [
    'Get-CimInstance Win32_Process -Property ProcessId,ParentProcessId,Name,CreationDate,ExecutablePath |',
    'ForEach-Object { [pscustomobject]@{ p = $_.ProcessId; q = $_.ParentProcessId; n = $_.Name;',
    's = $(if ($_.CreationDate) { ([DateTimeOffset]$_.CreationDate).ToUnixTimeSeconds() } else { 0 });',
    'x = $_.ExecutablePath } } | ConvertTo-Json -Compress',
].join(' ')

/** @type {Set<import('node:child_process').ChildProcess>} */
const running = new Set()

/**
 * Kill a child and everything it started: on Windows powershell.exe and claude.exe spawn their own
 * children, which child.kill() would leave running.
 * @param {import('node:child_process').ChildProcess} child
 */
export function killTree(child) {
    running.delete(child)
    if (child.exitCode !== null || child.signalCode !== null || !child.pid) return
    if (process.platform === 'win32') {
        spawnSync('taskkill', ['/T', '/F', '/PID', String(child.pid)], { stdio: 'ignore', windowsHide: true })
    } else {
        try {
            process.kill(-child.pid, 'SIGKILL')
        } catch {
            child.kill('SIGKILL')
        }
    }
}

/** Every child still running, e.g. when the overall budget runs out. */
export function killAll() {
    for (const child of [...running]) killTree(child)
}

/**
 * @param {string} cmd @param {string[]} args @param {number} timeoutMs @param {NodeJS.ProcessEnv} [env]
 * @returns {Promise<string>} stdout, or a rejection on error, non-zero exit or timeout
 */
export function run(cmd, args, timeoutMs, env) {
    return new Promise((resolve, reject) => {
        let out = ''
        // POSIX: its own process group, so killTree reaches its children too.
        const child = spawn(cmd, args, { stdio: ['ignore', 'pipe', 'ignore'], windowsHide: true, shell: false, env: env ?? process.env, detached: process.platform !== 'win32' })
        running.add(child)
        const timer = setTimeout(() => {
            killTree(child)
            reject(new Error(`${cmd} timed out`))
        }, Math.max(timeoutMs, 1))
        child.stdout.setEncoding('utf8').on('data', (d) => (out += d))
        child.on('error', (e) => {
            running.delete(child)
            clearTimeout(timer)
            reject(e)
        })
        child.on('close', (code) => {
            running.delete(child)
            clearTimeout(timer)
            code === 0 ? resolve(out) : reject(new Error(`${cmd} exited ${code}`))
        })
    })
}

/** @param {number} deadline */
async function processTable(deadline) {
    if (process.env.WS_HELPER_PS_JSON) return /** @type {Proc[]} */ (JSON.parse(process.env.WS_HELPER_PS_JSON))
    if (process.platform === 'win32') {
        const encoded = Buffer.from(WINDOWS_TABLE, 'utf16le').toString('base64')
        return parseWindowsTable(await run('powershell.exe', ['-NoProfile', '-NonInteractive', '-EncodedCommand', encoded], deadline - Date.now() - 1500))
    }
    const text = await run('ps', ['-A', '-o', 'pid=,ppid=,lstart=,comm=,args='], deadline - Date.now() - 1500, { ...process.env, LC_ALL: 'C' })
    return parsePsTable(text)
}

/** @param {Proc} claude @param {number} deadline */
async function liveAgents(claude, deadline) {
    if (process.env.WS_HELPER_AGENTS_JSON) return JSON.parse(process.env.WS_HELPER_AGENTS_JSON)
    const exe = claude.exe && fs.existsSync(claude.exe) ? claude.exe : process.platform === 'win32' ? 'claude.exe' : 'claude'
    return JSON.parse(await run(exe, ['agents', '--json'], deadline - Date.now() - 200))
}

/** @returns {Promise<Record<string, string>>} */
export async function sessionHeaders() {
    const deadline = Date.now() + BUDGET_MS
    const self = process.env.WS_HELPER_SELF_PID ? Number(process.env.WS_HELPER_SELF_PID) : process.pid
    const claude = nearestClaude(await processTable(deadline), self)
    if (!claude) return {}
    let agents = []
    try {
        agents = await liveAgents(claude, deadline)
    } catch {
        // The key alone still lets work_session bind a name.
    }
    return headersFor(claude, os.hostname(), agents)
}

/** @param {Record<string, string>} headers */
function emit(headers) {
    const line = JSON.stringify(headers)
    if (process.env.WS_HELPER_DEBUG_FILE) {
        try {
            fs.appendFileSync(process.env.WS_HELPER_DEBUG_FILE, `${new Date().toISOString()} ${line}\n`)
        } catch {}
    }
    process.stdout.write(line)
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    const timer = setTimeout(() => {
        killAll()
        emit({})
        process.exit(0)
    }, BUDGET_MS)
    sessionHeaders()
        .catch(() => ({}))
        .then((headers) => {
            clearTimeout(timer)
            emit(headers)
            process.exit(0)
        })
}
