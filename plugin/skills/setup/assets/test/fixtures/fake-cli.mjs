// A stand-in for `claude` and `docker` in the setup-script tests: logs each call as one JSON line to
// $FAKE_LOG. docker fails every call unless FAKE_DOCKER=ok, which makes every docker call succeed silently.
import { appendFileSync } from 'node:fs'

const [tool, ...args] = process.argv.slice(2)
appendFileSync(process.env.FAKE_LOG, JSON.stringify({ tool, args }) + '\n')
process.exit(tool === 'docker' && process.env.FAKE_DOCKER !== 'ok' ? 1 : 0)
