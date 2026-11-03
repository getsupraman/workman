// A stand-in for `claude` and `docker` in the setup-script tests: logs each call as one JSON line to
// $FAKE_LOG and answers `claude mcp get` from $FAKE_EXISTING_URL (unset: no registration).
import { appendFileSync } from 'node:fs'

const [tool, ...args] = process.argv.slice(2)
appendFileSync(process.env.FAKE_LOG, JSON.stringify({ tool, args }) + '\n')

if (tool === 'claude' && args[0] === 'mcp' && args[1] === 'get') {
    const url = process.env.FAKE_EXISTING_URL
    if (!url) {
        process.stdout.write(`No MCP server named ${args[2]} found.\n`)
        process.exit(1)
    }
    process.stdout.write(`${args[2]}:\n  Scope: User config (available in all your projects)\n  Status: ! Needs authentication\n  Type: http\n  URL: ${url}\n`)
}
process.exit(tool === 'docker' ? 1 : 0)
