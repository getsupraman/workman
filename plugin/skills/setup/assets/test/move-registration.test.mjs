// @ts-check
import { test } from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const HERE = path.dirname(fileURLToPath(import.meta.url))
const ASSETS = path.join(HERE, '..')
const MOVER = path.join(ASSETS, 'move-registration.mjs')
const FIXTURES = path.join(HERE, 'fixtures', 'shadows')
const MOVE = path.join(FIXTURES, 'move.json')

/** Run the mover as the setup scripts do. @param {string[]} args */
function mover(args) {
    const r = spawnSync(process.execPath, [MOVER, ...args], { encoding: 'utf8' })
    assert.equal(r.status, 0, r.stderr)
    return { stdout: r.stdout, stderr: r.stderr, lines: r.stdout.split('\n').filter(Boolean) }
}

test('a user-scope workstate entry moves to workman with the same url, headers and headersHelper', () => {
    const { lines } = mover([MOVE])
    assert.equal(lines.length, 3)
    assert.equal(lines[0], 'move')
    assert.equal(lines[1], 'http://127.0.0.1:5290/mcp, session helper, token kept by Claude Code, not shown')
    const original = JSON.parse(fs.readFileSync(MOVE, 'utf8')).mcpServers.workstate
    assert.deepEqual(JSON.parse(lines[2]), original)
})

test('a local entry moves from its own directory only, and an X-WorkState-* header gains its X-Workman-* twin', () => {
    const { lines } = mover([MOVE, '--scope', 'local', '--dir', 'P:\\Projects\\Acme\\Skills\\'])
    assert.equal(lines[0], 'move')
    assert.equal(lines[1], 'http://127.0.0.1:5290/mcp, session helper, no token stored')
    const entry = JSON.parse(lines[2])
    assert.deepEqual(entry.headers, { 'X-Workman-Agent': 'aw-skills', 'X-WorkState-Agent': 'aw-skills' })
    assert.equal(entry.url, 'http://127.0.0.1:5290/mcp')
    assert.equal(entry.type, 'http')
    assert.equal(entry.headersHelper, 'node "C:/Users/u/.workstate/session-header.mjs"')

    assert.deepEqual(mover([MOVE, '--scope', 'local', '--dir', 'P:/Projects/Elsewhere']).lines, ['none'])
})

test('nothing moves without a workstate entry, or when workman is already there beside it', () => {
    assert.deepEqual(mover([path.join(FIXTURES, 'none.json'), '--from', 'absent']).lines, ['none'])
    assert.deepEqual(mover([MOVE, '--scope', 'local', '--dir', 'P:/Projects/Both']).lines, ['both'])
})

test('a missing or malformed config is reported without quoting it', () => {
    const malformed = path.join(FIXTURES, 'malformed.json')
    const missing = path.join(FIXTURES, 'nope.json')
    assert.deepEqual(mover([malformed]).lines, [`not checked: ${malformed} is not valid JSON`])
    assert.deepEqual(mover([missing]).lines, [`not checked: ${missing} is missing`])
})

test('only the entry line carries a header value; the status and summary lines never do', () => {
    for (const args of [[MOVE], [MOVE, '--scope', 'local', '--dir', 'P:/Projects/Acme/Skills']]) {
        const { lines, stderr } = mover(args)
        for (const line of [lines[0], lines[1], stderr]) {
            assert.ok(!line.includes('SENTINEL-TOKEN'), line)
            assert.ok(!line.includes('Bearer'), line)
            assert.ok(!line.includes('aw-skills'), line)
        }
    }
})

test('both setup scripts default to workman, capture the plan, add workman before removing workstate, never print the entry', () => {
    const scripts = {
        'setup.ps1': { name: "[string]$Name = 'workman'", add: 'mcp add-json --scope $scope $Name $moveJson', remove: 'mcp remove $legacyName --scope $scope', entry: /\$moveJson/ },
        'setup.sh': { name: 'name="workman"', add: 'mcp add-json --scope "$scope" "$name" "$move_json"', remove: 'mcp remove "$legacy_name" --scope "$scope"', entry: /\$move_json/ },
    }
    for (const [file, s] of Object.entries(scripts)) {
        const text = fs.readFileSync(path.join(ASSETS, file), 'utf8')
        assert.ok(text.includes(s.name), `${file} registers workman by default`)
        assert.ok(text.includes('move-registration.mjs'), `${file} runs the mover`)
        const add = text.indexOf(s.add)
        const remove = text.indexOf(s.remove)
        assert.ok(add > 0, `${file} adds the moved entry`)
        assert.ok(remove > add, `${file} removes the old entry only after the new one is added`)
        for (const line of text.split('\n').filter((l) => s.entry.test(l))) {
            assert.ok(!/^\s*(Write-Output|Status|status|printf|echo|\[Console\])/.test(line) && !/\b(Status|status) /.test(line), `${file} prints the entry: ${line.trim()}`)
        }
    }
})
