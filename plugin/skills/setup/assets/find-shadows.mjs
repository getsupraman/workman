#!/usr/bin/env node
// find-shadows.mjs — lists every registration of the Workman server that wins over the user-scope
// one: a local-scope entry in the claude config (projects[<dir>].mcpServers[<name>]) and a
// project-scope entry in <dir>/.mcp.json. Claude Code resolves local > project > user, so a session in
// such a directory never sees the user registration or its headersHelper.
//   node find-shadows.mjs [<claude config>] [--name <server>]... [--dir <dir>] [--own <dir>] [--all]
//   node find-shadows.mjs [<claude config>] --registered [--name <server>]
// Without --name it checks workman. Each --name replaces it; repeat it to check several.
// <claude config> defaults to $CLAUDE_CONFIG_DIR/.claude.json, else ~/.claude.json. --dir also checks
// that directory's .mcp.json; --all also checks the .mcp.json of every project directory the config
// lists. --own skips the local entry of that directory (the one -Agent wrote).
// One line per shadowing entry with the command that removes it; nothing when none shadows; exit 0.
// --registered instead prints the url of the user-scope entry (top-level mcpServers.<name>), or
// nothing: setup reads the existing registration here, because `claude mcp get` health-checks it.
// Header values are never read into the output: only the url (without credentials or query) and
// whether headersHelper is set. A parse error is not printed either, because it quotes the file.
// @ts-check

import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

/** @typedef {{scope: 'local' | 'project', dir: string, file: string, url: string, helper: boolean}} Shadow */

/** The registration's name. */
export const DEFAULT_NAMES = Object.freeze(['workman'])

/** @param {string} dir */
export function sameDir(dir) {
    const d = dir.replace(/\\/g, '/').replace(/\/+$/, '')
    return process.platform === 'win32' ? d.toLowerCase() : d
}

/** The url with any userinfo, query and fragment dropped. @param {unknown} raw */
export function safeUrl(raw) {
    if (typeof raw !== 'string') return '(no url)'
    try {
        const u = new URL(raw)
        return `${u.protocol}//${u.host}${u.pathname}`
    } catch {
        return '(unparseable url)'
    }
}

/** @param {unknown} entry @returns {{url: string, helper: boolean}} */
function describe(entry) {
    const e = /** @type {any} */ (entry)
    return { url: safeUrl(e?.url), helper: typeof e?.headersHelper === 'string' && e.headersHelper.trim() !== '' }
}

/** @param {unknown} servers @param {string} name */
function entryOf(servers, name) {
    return servers && typeof servers === 'object' && Object.hasOwn(servers, name) ? /** @type {any} */ (servers)[name] : undefined
}

/**
 * @param {unknown} config the parsed claude config
 * @param {string} name
 * @param {string} configFile
 * @param {string} [own] a directory whose local entry is the one just written, not a shadow
 * @returns {Shadow[]}
 */
export function localShadows(config, name, configFile, own) {
    const projects = /** @type {any} */ (config)?.projects
    if (!projects || typeof projects !== 'object') return []
    /** @type {Shadow[]} */
    const found = []
    for (const dir of Object.keys(projects)) {
        if (own && sameDir(dir) === sameDir(own)) continue
        const entry = entryOf(projects[dir]?.mcpServers, name)
        if (entry === undefined) continue
        found.push({ scope: 'local', dir, file: configFile, ...describe(entry) })
    }
    return found
}

/** @param {unknown} mcpJson the parsed .mcp.json @param {string} name @param {string} dir @param {string} file @returns {Shadow[]} */
export function projectShadows(mcpJson, name, dir, file) {
    const entry = entryOf(/** @type {any} */ (mcpJson)?.mcpServers, name)
    return entry === undefined ? [] : [{ scope: 'project', dir, file, ...describe(entry) }]
}

/**
 * The user-scope registration's url, read from the config and nothing else of the entry.
 * @param {unknown} config the parsed claude config @param {string} name @returns {string | undefined}
 */
export function registeredUrl(config, name) {
    const entry = entryOf(/** @type {any} */ (config)?.mcpServers, name)
    return entry === undefined ? undefined : safeUrl(entry?.url)
}

/** @param {Shadow} s @param {string} name */
export function formatShadow(s, name) {
    const helper = s.helper ? 'has headersHelper' : 'no headersHelper'
    const fix = s.scope === 'local'
        ? `in that directory run: claude mcp remove ${name} -s local`
        : `it is mcpServers.${name} in the committed team file ${s.file} - do not delete the file; agree with the repo to drop that entry (claude mcp remove ${name} -s project, run in that directory, edits it)`
    return `${s.scope.padEnd(8)}${s.dir}  ${s.url}  ${helper}  -> ${fix}`
}

/** @param {string} file @returns {{ok: true, value: unknown} | {ok: false, why: string}} */
function readJson(file) {
    let text
    try {
        text = fs.readFileSync(file, 'utf8')
    } catch (e) {
        return { ok: false, why: /** @type {any} */ (e)?.code === 'ENOENT' ? 'missing' : 'not readable' }
    }
    try {
        return { ok: true, value: JSON.parse(text.replace(/^﻿/, '')) }
    } catch {
        return { ok: false, why: 'not valid JSON' }
    }
}

/** $CLAUDE_CONFIG_DIR/.claude.json, else ~/.claude.json. @param {NodeJS.ProcessEnv} [env] */
export function defaultConfigFile(env = process.env) {
    return path.join(env.CLAUDE_CONFIG_DIR || os.homedir(), '.claude.json')
}

/**
 * The entries that shadow `name` for sessions started in `dir`: its local entry in the claude config
 * and its .mcp.json entry. Reads only key names, urls and whether headersHelper is set.
 * @param {string} dir @param {{configFile?: string, name?: string}} [opt]
 * @returns {{shadows: Shadow[], unchecked: string[]}}
 */
export function shadowsForDir(dir, opt = {}) {
    const name = opt.name ?? DEFAULT_NAMES[0]
    const configFile = opt.configFile ?? defaultConfigFile()
    /** @type {Shadow[]} */
    const shadows = []
    /** @type {string[]} */
    const unchecked = []
    const config = readJson(configFile)
    if (config.ok) shadows.push(...localShadows(config.value, name, configFile).filter((s) => sameDir(s.dir) === sameDir(dir)))
    else if (config.why !== 'missing') unchecked.push(`${configFile} is ${config.why}`)
    const mcpFile = path.join(dir, '.mcp.json')
    if (fs.existsSync(mcpFile)) {
        const mcp = readJson(mcpFile)
        if (mcp.ok) shadows.push(...projectShadows(mcp.value, name, dir, mcpFile))
        else unchecked.push(`${mcpFile} is ${mcp.why}`)
    }
    return { shadows, unchecked }
}

/** @param {string[]} argv @returns {string[]} the lines to print */
export function findShadows(argv) {
    /** @type {{config?: string, names: string[], dir?: string, own?: string, registered?: boolean, all?: boolean}} */
    const opt = { names: [] }
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i]
        if (a === '--name') {
            const n = argv[++i]
            if (n && !opt.names.includes(n)) opt.names.push(n)
        }
        else if (a === '--dir') opt.dir = argv[++i]
        else if (a === '--own') opt.own = argv[++i]
        else if (a === '--registered') opt.registered = true
        else if (a === '--all') opt.all = true
        else if (!opt.config) opt.config = a
    }
    if (opt.names.length === 0) opt.names.push(...DEFAULT_NAMES)
    const configFile = opt.config ?? defaultConfigFile()
    /** @type {string[]} */
    const lines = []
    const config = readJson(configFile)
    if (opt.registered) {
        const url = config.ok ? registeredUrl(config.value, opt.names[0]) : undefined
        return url === undefined ? [] : [url]
    }
    if (config.ok) {
        for (const name of opt.names) {
            for (const s of localShadows(config.value, name, configFile, opt.own)) lines.push(formatShadow(s, name))
        }
    } else {
        lines.push(`not checked: ${configFile} is ${config.why}`)
    }
    /** @type {string[]} */
    const dirs = opt.dir ? [opt.dir] : []
    if (opt.all && config.ok) {
        const projects = /** @type {any} */ (config.value)?.projects
        for (const d of projects && typeof projects === 'object' ? Object.keys(projects) : []) {
            if (!dirs.some((x) => sameDir(x) === sameDir(d))) dirs.push(d)
        }
    }
    for (const dir of dirs) {
        const mcpFile = path.join(dir, '.mcp.json')
        if (!fs.existsSync(mcpFile)) continue
        const mcp = readJson(mcpFile)
        if (mcp.ok) {
            for (const name of opt.names) {
                for (const s of projectShadows(mcp.value, name, dir, mcpFile)) lines.push(formatShadow(s, name))
            }
        }
        else lines.push(`not checked: ${mcpFile} is ${mcp.why}`)
    }
    return lines
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    try {
        for (const line of findShadows(process.argv.slice(2))) process.stdout.write(`${line}\n`)
    } catch {
        process.stdout.write('not checked: the shadow check failed\n')
    }
    process.exit(0)
}
