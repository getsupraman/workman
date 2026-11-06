// workman.mjs — mint, bind and revoke per-session Workman tokens (docs/14 §2).
//
// The OAuth access token (oauth.mjs) goes nowhere but an Authorization header. A minted token goes
// nowhere but the mcp-<name>.json it is written to. Error messages are built from status codes and
// server error text, never from a header or body this code sent.
// @ts-check

import { Refusal } from './launch-args.mjs'
import { MCP_NAME } from './home.mjs'

/**
 * @param {Response} res
 */
export async function serverReason(res) {
    let detail = ''
    try {
        const body = await res.text()
        try {
            const j = JSON.parse(body)
            detail = j.error ?? j.title ?? j.message ?? j.detail ?? ''
        } catch {
            detail = body.slice(0, 200)
        }
    } catch {}
    return `HTTP ${res.status}${detail ? `: ${String(detail).slice(0, 300)}` : ''}`
}

/**
 * @param {string} url
 * @param {RequestInit} init
 */
export async function call(url, init) {
    try {
        return await fetch(url, { ...init, signal: AbortSignal.timeout(15000) })
    } catch (e) {
        const err = /** @type {Error & {cause?: {code?: string}}} */ (e)
        throw new Error(`Workman at ${new URL(url).origin} is unreachable (${err.cause?.code ?? err.name})`)
    }
}

/**
 * /api/agent-sessions[/{id}] with the access token.
 * @param {{url: string, token: string}} a
 * @param {string} method
 * @param {string|undefined} sessionId
 * @param {unknown} [body]
 */
function sessions(a, method, sessionId, body) {
    /** @type {Record<string, string>} */
    const headers = { Authorization: `Bearer ${a.token}` }
    if (body !== undefined) headers['Content-Type'] = 'application/json'
    const tail = sessionId === undefined ? '' : `/${encodeURIComponent(sessionId)}`
    return call(`${a.url}/api/agent-sessions${tail}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) })
}

/**
 * @typedef {{sessionId: string, role: string, token: string, expiresAt: string|undefined}} Minted
 */

/**
 * POST /api/agent-sessions. Throws Refusal with the reason on any failure.
 * @param {{url: string, token: string, name: string, role: string, launchedBy: string, cwd: string, ttlHours?: number, grants?: string[], repo?: {remote?: string, root?: string}}} a
 * @returns {Promise<Minted>}
 */
export async function mintSession(a) {
    /** @type {Record<string, unknown>} */
    const body = { name: a.name, role: a.role, launchedBy: a.launchedBy, cwd: a.cwd }
    if (a.ttlHours !== undefined) body.ttlHours = a.ttlHours
    if (a.grants && a.grants.length > 0) body.grants = a.grants
    if (a.repo && (a.repo.remote || a.repo.root)) body.repo = a.repo
    const failed = (/** @type {string} */ why) => new Refusal(`minting a Workman token failed: ${why} Nothing was launched.`)
    let res
    try {
        res = await sessions(a, 'POST', undefined, body)
    } catch (e) {
        throw failed(`${/** @type {Error} */ (e).message}.`)
    }
    if (!res.ok) {
        const hint = res.status === 409
            ? ` A live token already exists for "${a.name}": agent_stop that agent first, or pick another name.`
            : res.status === 401 || res.status === 403
                ? ' The sign-in was refused, or it may not mint this role (a role is never raised); if it was signed out, run agent_signin.'
                : ''
        throw failed(`${await serverReason(res)}.${hint}`)
    }
    let j
    try {
        j = await res.json()
    } catch {
        throw failed('the response was not JSON.')
    }
    if (!j || typeof j.token !== 'string' || !j.token || j.sessionId === undefined || j.sessionId === null) {
        throw failed('the response had no token or sessionId.')
    }
    return { sessionId: String(j.sessionId), role: String(j.role ?? a.role), token: j.token, expiresAt: j.expiresAt }
}

/**
 * @param {Promise<Response>} request
 * @param {number[]} [alsoOk]
 * @returns {Promise<string|undefined>} an error, or undefined on success
 */
async function bestEffort(request, alsoOk = []) {
    try {
        const res = await request
        return res.ok || alsoOk.includes(res.status) ? undefined : await serverReason(res)
    } catch (e) {
        return /** @type {Error} */ (e).message
    }
}

/**
 * PATCH the Claude session uuid onto the Workman session.
 * @param {{url: string, token: string, sessionId: string}} a
 * @param {string} claudeSessionId
 */
export const bindClaudeSession = (a, claudeSessionId) => bestEffort(sessions(a, 'PATCH', a.sessionId, { claudeSessionId }))

/**
 * DELETE /api/agent-sessions/{id}. A 404 counts as revoked: there is nothing left to revoke.
 * @param {{url: string, token: string, sessionId: string}} a
 */
export const revokeSession = (a) => bestEffort(sessions(a, 'DELETE', a.sessionId), [404])

/**
 * The --mcp-config file. A server named "workman" here replaces the user-scope one, and a --bg
 * session gets no ${VAR} expansion, so the token value itself goes in (docs/12 §5).
 * @param {string} url
 * @param {string} token
 */
export function mcpConfigJson(url, token) {
    return JSON.stringify({ mcpServers: { [MCP_NAME]: { type: 'http', url: `${url}/mcp`, headers: { Authorization: `Bearer ${token}` } } } }, null, 2) + '\n'
}
