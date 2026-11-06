// home.mjs — the files this server reads or writes outside a repo: ~/.workman/server.json (which
// Workman, written only by /workman:setup), ~/.workman/grants.json's directory, ~/.workman/agents/
// (launch records and minted mcp configs), and Claude Code's mcp-needs-auth-cache.json.
// @ts-check

import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import crypto from 'node:crypto'
import { Refusal, isObject } from './launch-args.mjs'

/** The name of the Workman server in the user's config, in a worker's --mcp-config and in the cache. */
export const MCP_NAME = 'workman'

/** Appended where a stuck session looks for what to do next. */
export const NEVER_PROBE = 'Never run claude mcp get/list or a claude process to check Workman.'

export const workmanDir = () => path.join(os.homedir(), '.workman')

/** Launch records and minted mcp configs: owner-only (0700; on Windows the profile already is). */
export function agentsDir() {
    const dir = path.join(workmanDir(), 'agents')
    fs.mkdirSync(dir, { recursive: true, mode: 0o700 })
    try {
        fs.chmodSync(dir, 0o700)
    } catch {}
    return dir
}

/** @param {string} name */
export const mcpConfigFile = (name) => path.join(agentsDir(), `mcp-${name}.json`)

/** @param {number} ms */
export const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

/**
 * The Workman origin from ~/.workman/server.json, {"url": "<origin>", "mcpName": "workman"}, as
 * new URL(url).origin: a trailing slash, an upper-case host or a default port are fine; a path,
 * query or credentials are not. Never a default: a missing or unusable file refuses with the setup call.
 * @returns {string}
 */
export function serverUrl() {
    const file = path.join(workmanDir(), 'server.json')
    const refuse = (/** @type {string} */ why) => new Refusal(`${file} ${why}. Run /workman:setup: it writes the Workman server this machine uses. ${NEVER_PROBE}`)
    let j
    try {
        j = JSON.parse(fs.readFileSync(file, 'utf8').replace(/^﻿/, ''))
    } catch (e) {
        const code = /** @type {NodeJS.ErrnoException} */ (e).code
        throw refuse(code === 'ENOENT' ? 'does not exist' : code ? `cannot be read (${code})` : 'is not valid JSON')
    }
    const url = isObject(j) ? j.url : undefined
    if (typeof url !== 'string') throw refuse('has no "url"')
    if (isObject(j) && j.mcpName !== undefined && j.mcpName !== MCP_NAME) throw refuse(`has "mcpName" ${JSON.stringify(j.mcpName)}; only "${MCP_NAME}" is supported`)
    let u
    try {
        u = new URL(url)
    } catch {
        throw refuse(`has a "url" that is not a URL (${url})`)
    }
    if (!/^https?:$/.test(u.protocol)) throw refuse(`has "url" ${url}, which is not http(s)`)
    if (u.username || u.password || u.search || u.hash || /[?#]/.test(url) || u.pathname !== '/') {
        throw refuse(`has "url" ${u.origin}…, which carries a path, query or credentials; it must be an origin`)
    }
    return u.origin
}

/**
 * Write via a temp file in the same directory and rename it over `file`. Windows refuses the rename
 * while another process has the target open, so EPERM is retried for about a second.
 * @param {string} file
 * @param {string} text
 * @param {number} [mode]
 */
export function writeFileAtomic(file, text, mode) {
    const tmp = `${file}.${process.pid}.${crypto.randomBytes(4).toString('hex')}.tmp`
    fs.writeFileSync(tmp, text, mode === undefined ? undefined : { mode })
    if (mode !== undefined) {
        try {
            fs.chmodSync(tmp, mode)
        } catch {}
    }
    for (let i = 0; ; i++) {
        try {
            fs.renameSync(tmp, file)
            return
        } catch (e) {
            if (i >= 20 || /** @type {NodeJS.ErrnoException} */ (e).code !== 'EPERM') {
                fs.rmSync(tmp, { force: true })
                throw e
            }
            Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 50)
        }
    }
}

/**
 * Remove the "workman" entry from Claude Code's mcp-needs-auth-cache.json in the config dir the
 * worker is spawned with (env.CLAUDE_CONFIG_DIR, else ~/.claude). While the entry is younger than
 * 15 minutes Claude Code skips connecting a server of that name, the worker's own --mcp-config one
 * included, and the worker gets no work_* tools. Every other key is kept, and the file is re-read
 * just before the rename, so only a write landing in that instant is lost. Never throws.
 * @param {NodeJS.ProcessEnv} [env] the environment the worker is spawned with
 * @returns {string|undefined} a warning when the entry could not be removed
 */
export function clearAuthCacheEntry(env = process.env) {
    const file = path.join(env.CLAUDE_CONFIG_DIR || path.join(os.homedir(), '.claude'), 'mcp-needs-auth-cache.json')
    let age = ''
    /** @returns {Record<string, any>|undefined|null} null: no file */
    const read = () => {
        let text
        try {
            text = fs.readFileSync(file, 'utf8')
        } catch (e) {
            if (/** @type {NodeJS.ErrnoException} */ (e).code === 'ENOENT') return null
            throw e
        }
        try {
            const v = JSON.parse(text)
            return isObject(v) ? v : undefined
        } catch {
            return undefined
        }
    }
    try {
        let cache = read()
        if (cache === null) return undefined
        if (cache === undefined) return `${file} is not a JSON object; left it untouched, so a stale "${MCP_NAME}" entry there may hide this worker's Workman tools`
        if (!(MCP_NAME in cache)) return undefined
        const ts = cache[MCP_NAME]?.timestamp
        if (typeof ts === 'number') age = ` (entry ${Math.round((Date.now() - ts) / 1000)} s old)`
        const fresh = read()
        if (isObject(fresh)) cache = fresh
        if (!(MCP_NAME in cache)) return undefined
        delete cache[MCP_NAME]
        writeFileAtomic(file, JSON.stringify(cache))
        return undefined
    } catch (e) {
        return `could not remove "${MCP_NAME}" from ${file}${age}: ${/** @type {NodeJS.ErrnoException} */ (e).code ?? /** @type {Error} */ (e).message}; if it is younger than 15 minutes the worker starts without Workman tools`
    }
}
