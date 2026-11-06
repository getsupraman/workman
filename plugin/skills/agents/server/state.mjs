// state.mjs — one JSON file per launched agent in ~/.workman/agents/<name>.json, so agent_stop and
// agent_list survive a restart. Holds paths, ids and the Workman url only: no token or grant is ever
// written here (the token is in mcp-<name>.json beside it, which loadRecords skips).
// @ts-check

import fs from 'node:fs'
import path from 'node:path'
import { agentsDir, writeFileAtomic } from './home.mjs'

/**
 * @typedef {object} AgentRecord
 * @property {string} name
 * @property {string|undefined} id          claude's short id
 * @property {string|undefined} sessionId   claude's session uuid
 * @property {string} repo_root
 * @property {string} brief
 * @property {string|undefined} mcpConfigPath
 * @property {string|undefined} url         the Workman server that minted the token; agent_stop revokes there
 * @property {{sessionId: string, role: string, expiresAt: string|undefined}|null} workman
 * @property {string} launchedAt
 */

/** @param {string} name */
function fileFor(name) {
    return path.join(agentsDir(), `${name}.json`)
}

/** @param {AgentRecord} rec */
export function saveRecord(rec) {
    writeFileAtomic(fileFor(rec.name), JSON.stringify(rec, null, 2) + '\n', 0o600)
}

/** @returns {AgentRecord[]} */
export function loadRecords() {
    const dir = agentsDir()
    /** @type {AgentRecord[]} */
    const out = []
    for (const f of fs.readdirSync(dir)) {
        if (!f.endsWith('.json') || f.startsWith('mcp-')) continue
        try {
            out.push(JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8')))
        } catch {}
    }
    return out
}

/** @param {{id?: string, name?: string}} key */
export function findRecord(key) {
    return loadRecords().find((r) => (key.name !== undefined && r.name === key.name) || (key.id !== undefined && r.id === key.id))
}

/** @param {string} name */
export function deleteRecord(name) {
    fs.rmSync(fileFor(name), { force: true })
}
