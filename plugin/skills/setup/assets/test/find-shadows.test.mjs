// @ts-check
import { test } from 'node:test'
import assert from 'node:assert/strict'
import path from 'node:path'
import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const HERE = path.dirname(fileURLToPath(import.meta.url))
const FINDER = path.join(HERE, '..', 'find-shadows.mjs')
const FIXTURES = path.join(HERE, 'fixtures', 'shadows')
const CONFIG = path.join(FIXTURES, 'claude.json')
const REPO = path.join(FIXTURES, 'repo')

/** Run the finder as the setup scripts do. @param {string[]} args */
function finder(args) {
    const r = spawnSync(process.execPath, [FINDER, ...args], { encoding: 'utf8' })
    assert.equal(r.status, 0, r.stderr)
    return { stdout: r.stdout, stderr: r.stderr, lines: r.stdout.split('\n').filter(Boolean) }
}

/** @param {string[]} lines @param {string} dir */
const lineFor = (lines, dir) => lines.find((l) => l.includes(`${dir}  `))

test('a local entry without headersHelper is reported with its remove command', () => {
    const line = lineFor(finder([CONFIG]).lines, 'P:/Projects/Acme/Skills')
    assert.ok(line, 'the Skills local entry is reported')
    assert.match(line, /^local\s+P:\/Projects\/Acme\/Skills  http:\/\/localhost:5290\/mcp  no headersHelper/)
    assert.match(line, /in that directory run: claude mcp remove workman -s local$/)
})

test('a local entry WITH headersHelper is still reported: it shadows the user entry and its token', () => {
    const line = lineFor(finder([CONFIG]).lines, 'P:/Projects/Other')
    assert.ok(line, 'the Other local entry is reported')
    assert.match(line, /has headersHelper/)
    assert.match(line, /claude mcp remove workman -s local$/)
})

test('a project .mcp.json entry is reported as the committed team file, not as a file to delete', () => {
    const line = finder([path.join(FIXTURES, 'none.json'), '--dir', REPO]).lines.find((l) => l.startsWith('project'))
    assert.ok(line, 'the .mcp.json entry is reported')
    assert.ok(line.includes(path.join(REPO, '.mcp.json')))
    assert.match(line, /mcpServers\.workman in the committed team file/)
    assert.match(line, /do not delete the file/)
    assert.match(line, /claude mcp remove workman -s project/)
})

test('another server name is ignored, and --name selects it instead', () => {
    const { lines } = finder([CONFIG])
    assert.equal(lines.length, 2)
    assert.ok(!lines.some((l) => l.includes('Unrelated')))
    const staging = finder([CONFIG, '--name', 'workman-staging']).lines
    assert.equal(staging.length, 1)
    assert.match(staging[0], /^local\s+P:\/Projects\/Unrelated .*claude mcp remove workman-staging -s local$/)
})

test('only workman is checked by default; a 1.x workstate entry is a name of its own, checked with --name', () => {
    const { lines } = finder([CONFIG])
    assert.equal(lineFor(lines, 'P:/Projects/Renamed'), undefined)
    assert.match(lineFor(lines, 'P:/Projects/Acme/Skills') ?? '', /claude mcp remove workman -s local$/)
    assert.equal(finder([CONFIG, '--name', 'workman']).lines.length, 2)
    assert.match(finder([CONFIG, '--name', 'workstate']).lines[0] ?? '', /Renamed .*claude mcp remove workstate -s local$/)
    assert.equal(finder([CONFIG, '--name', 'workman', '--name', 'workstate']).lines.length, 3)
})

test('--own skips the local entry the script itself just wrote', () => {
    const { lines } = finder([CONFIG, '--own', 'P:\\Projects\\Acme\\Skills\\'])
    assert.equal(lines.length, 1)
    assert.ok(lines.some((l) => l.includes('P:/Projects/Other')))
    assert.ok(!lines.some((l) => l.includes('P:/Projects/Acme/Skills')))
})

test('no shadowing entry prints nothing', () => {
    const r = finder([path.join(FIXTURES, 'none.json'), '--dir', FIXTURES])
    assert.equal(r.stdout, '')
    assert.equal(r.stderr, '')
})

test('a malformed or missing config prints one line and exits 0', () => {
    const bad = finder([path.join(FIXTURES, 'malformed.json')])
    assert.deepEqual(bad.lines, [`not checked: ${path.join(FIXTURES, 'malformed.json')} is not valid JSON`])
    const missing = finder([path.join(FIXTURES, 'does-not-exist.json')])
    assert.deepEqual(missing.lines, [`not checked: ${path.join(FIXTURES, 'does-not-exist.json')} is missing`])
})

test('no header value or token ever reaches stdout or stderr', () => {
    for (const args of [[CONFIG, '--dir', REPO], [CONFIG, '--name', 'workman-staging'], [path.join(FIXTURES, 'malformed.json'), '--dir', REPO]]) {
        const r = finder(args)
        assert.ok(r.lines.length > 0, `${args.join(' ')} printed something to check`)
        for (const stream of [r.stdout, r.stderr]) {
            assert.ok(!stream.includes('SENTINEL-TOKEN'), `a sentinel leaked for ${args.join(' ')}:\n${stream}`)
            assert.ok(!stream.includes('Bearer'), `a header leaked for ${args.join(' ')}:\n${stream}`)
        }
    }
})

test('--all also checks the .mcp.json of every project directory the config lists; shadowsForDir sees one directory only', async () => {
    const { mkdtempSync, writeFileSync } = await import('node:fs')
    const os = await import('node:os')
    const { shadowsForDir } = await import('../find-shadows.mjs')
    const root = mkdtempSync(path.join(os.tmpdir(), 'shadows-all-'))
    const config = path.join(root, 'claude.json')
    writeFileSync(config, JSON.stringify({ projects: { [REPO]: { mcpServers: {} }, [path.join(root, 'gone')]: {} } }))
    assert.deepEqual(finder([config]).lines, [], 'without --all only --dir reads a .mcp.json')
    const all = finder([config, '--all']).lines
    assert.equal(all.length, 1)
    assert.match(all[0], /^project\s/)
    assert.ok(all[0].includes(path.join(REPO, '.mcp.json')))
    assert.equal(finder([config, '--all', '--dir', REPO]).lines.length, 1, 'a directory is checked once')

    assert.deepEqual(shadowsForDir(REPO, { configFile: config }).shadows.map((s) => s.scope), ['project'])
    assert.deepEqual(shadowsForDir(path.join(root, 'gone'), { configFile: config }).shadows, [])
    const local = shadowsForDir('P:/Projects/Acme/Skills', { configFile: CONFIG }).shadows
    assert.deepEqual(local.map((s) => [s.scope, s.url]), [['local', 'http://localhost:5290/mcp']])
})
