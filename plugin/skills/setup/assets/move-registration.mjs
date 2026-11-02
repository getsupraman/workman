#!/usr/bin/env node
// move-registration.mjs — the plan for moving a 1.x `workstate` registration to `workman`, read from the
// claude config the way find-shadows.mjs reads it:
//   node move-registration.mjs [<claude config>] [--from workstate] [--to workman] [--scope user|local] [--dir <dir>]
//                              [--helper <command>]
// Prints, for the setup script to CAPTURE (line 3 can hold a token; the script never prints it):
//   none                          no <from> entry in that scope: nothing to move
//   both                          <from> and <to> are both there: nothing is moved, the user decides
//   not checked: <why>            the config is missing or unreadable
//   move                          then two more lines:
//   <summary>                       safe to show: the url (no credentials or query) and what the entry carries
//   <entry json>                    the entry for `claude mcp add-json`: same url and headers, each X-WorkState-*
//                                   header renamed X-Workman-* (a 2.0 server reads no other); its headersHelper
//                                   replaced by --helper when it had one, else kept
// Exit 0 always. A parse error is not quoted, because it quotes the file.
// @ts-check

import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { safeUrl, sameDir } from './find-shadows.mjs'

export const LEGACY_NAME = 'workstate'
export const NAME = 'workman'

/** @param {unknown} servers @param {string} name */
function entryOf(servers, name) {
    return servers && typeof servers === 'object' && Object.hasOwn(servers, name) ? /** @type {any} */ (servers)[name] : undefined
}

/**
 * The servers object of one scope: the top-level one for user, the directory's for local.
 * @param {any} config @param {'user'|'local'} scope @param {string} [dir]
 */
export function serversOf(config, scope, dir) {
    if (scope === 'user') return config?.mcpServers
    const projects = config?.projects
    if (!projects || typeof projects !== 'object' || !dir) return undefined
    const key = Object.keys(projects).find((d) => sameDir(d) === sameDir(dir))
    return key === undefined ? undefined : projects[key]?.mcpServers
}

/**
 * The entry under its new name: everything kept, each X-WorkState-* header renamed X-Workman-*, and a
 * headersHelper pointed at `helper` (the new home's copy) when one is given.
 * @param {any} entry @param {string} [helper]
 */
export function movedEntry(entry, helper) {
    const out = { ...entry }
    if (entry?.headers && typeof entry.headers === 'object') {
        /** @type {Record<string, unknown>} */
        const headers = {}
        for (const [k, v] of Object.entries(entry.headers)) {
            const m = /^x-workstate-(.+)$/i.exec(k)
            headers[m ? `X-Workman-${m[1]}` : k] = v
        }
        out.headers = headers
    }
    if (helper && typeof entry?.headersHelper === 'string' && entry.headersHelper.trim()) out.headersHelper = helper
    return out
}

/** @param {any} entry what the summary may say: never a header value */
export function summary(entry) {
    const headers = entry?.headers && typeof entry.headers === 'object' ? Object.keys(entry.headers) : []
    const auth = headers.some((h) => h.toLowerCase() === 'authorization') ? 'token kept by Claude Code, not shown' : 'no token stored'
    const helper = typeof entry?.headersHelper === 'string' && entry.headersHelper.trim() ? 'session helper' : 'no session helper'
    return `${safeUrl(entry?.url)}, ${helper}, ${auth}`
}

/**
 * @param {unknown} config the parsed claude config
 * @param {{from: string, to: string, scope: 'user'|'local', dir?: string, helper?: string}} opt
 * @returns {string[]}
 */
export function plan(config, opt) {
    const servers = serversOf(config, opt.scope, opt.dir)
    const from = entryOf(servers, opt.from)
    if (from === undefined || from === null || typeof from !== 'object') return ['none']
    if (entryOf(servers, opt.to) !== undefined) return ['both']
    return ['move', summary(from), JSON.stringify(movedEntry(from, opt.helper))]
}

/** @param {string[]} argv @returns {string[]} */
export function moveRegistration(argv) {
    /** @type {{config?: string, from: string, to: string, scope: 'user'|'local', dir?: string, helper?: string}} */
    const opt = { from: LEGACY_NAME, to: NAME, scope: 'user' }
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i]
        if (a === '--from') opt.from = argv[++i] ?? opt.from
        else if (a === '--to') opt.to = argv[++i] ?? opt.to
        else if (a === '--scope') opt.scope = argv[++i] === 'local' ? 'local' : 'user'
        else if (a === '--dir') opt.dir = argv[++i]
        else if (a === '--helper') opt.helper = argv[++i]
        else if (!opt.config) opt.config = a
    }
    const configFile = opt.config ?? path.join(process.env.CLAUDE_CONFIG_DIR || os.homedir(), '.claude.json')
    let text
    try {
        text = fs.readFileSync(configFile, 'utf8')
    } catch (e) {
        return [`not checked: ${configFile} is ${/** @type {any} */ (e)?.code === 'ENOENT' ? 'missing' : 'not readable'}`]
    }
    let config
    try {
        config = JSON.parse(text.replace(/^﻿/, ''))
    } catch {
        return [`not checked: ${configFile} is not valid JSON`]
    }
    return plan(config, opt)
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    try {
        for (const line of moveRegistration(process.argv.slice(2))) process.stdout.write(`${line}\n`)
    } catch {
        process.stdout.write('not checked: the move check failed\n')
    }
    process.exit(0)
}
