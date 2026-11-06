/**
 * The state vocabulary, case by case, through the one function every view colours with
 * (`src/state.ts`), plus the plan filter and the link builder that sit on top of it. No server:
 * every input is written here, so a change to the precedence fails here before it reaches a page.
 *
 *   node verify/state-cases.mjs
 */
import { CLOSED, LEAF, answer, countTitle, counted, findingsOpen, independent, progress, state, tally } from '../src/state.ts'
import { DEFAULT_FILTER, FILTERS, asFilter, cancelledLines, plan, planOrder, planQuery, withoutCancelled } from '../src/plan.ts'
import { readFileSync } from 'node:fs'
import { KEYS, KEYS_COMMAND, isHelpKey, keysTable } from '../src/keys.ts'
import { activityDays, activityOrder, backlogOrder, projectOrder, projectRuns } from '../src/lists.ts'
import { listMove, openRuns, runMove, runOnPath, sideProjects, sideRuns, step, typingIn } from '../src/listnav.ts'
import { boardHref, copyValue, itemHref, pathCrumbs, routeReads } from '../src/nav.ts'
import { appIconHref, consentForm, consentStep, consentView, hiddenFields, iconStep, initials, isConnectPath, returnTarget, scopeLines, signInHref, signInStart, signInStep } from '../src/connect.ts'
import { formerName, isReleased, releasable, tokenLabel } from '../src/sessions.ts'
import { arrange, chordLabel, commands, entryHref, isPaletteChord, isSlash, matchCommands } from '../src/palette.ts'
import { HANDLE, LIST_MIN, PANEL_MIN, PANEL_SHARE, WIDTH_KEY, clampWidth, docks, panelWidth, rememberWidth, rememberedWidth } from '../src/split.ts'
import { MEMBERS_ANCHOR, memberLine, membersHref, projectOnPath, shareMarker } from '../src/sharing.ts'
import { PHONE_QUERY, closesOnMove, pageName } from '../src/menu.ts'
import { appLine, revokeWarning, sessionsLabel } from '../src/apps.ts'
import { BOARDS_MAX, boardsRead } from '../src/boards.ts'
import { cardFacts, choreRow, decisionRow, forController, groupHousekeeping, needsYou, needsYouCount, rowProject } from '../src/inbox.ts'
import { expiresIn, isLastAdmin, isLinkPath, joinStep, joinToken, nameProblem, passwordMessage, passwordProblem, resetStep, resetToken, roleLabel, showSwitcher, slugFrom, slugProblem } from '../src/admin.ts'

let failures = 0

const check = (label, actual, expected) => {
  const ok = JSON.stringify(actual) === JSON.stringify(expected)
  if (!ok) failures += 1
  console.log(`   ${ok ? 'PASS' : 'FAIL'} ${label.padEnd(58)} ${JSON.stringify(actual)}${ok ? '' : `  (expected ${JSON.stringify(expected)})`}`)
}

const input = (over) => ({
  kind: 'task',
  status: 'open',
  resolution: null,
  claimExpired: false,
  proof: false,
  waitingOn: [],
  ...over,
})

console.log('── state(): every case, in precedence order ─────────────────────────────────')
const cases = [
  ['superseded', input({ status: 'superseded' }), { k: 'gone', label: 'Superseded' }],
  ['cancelled', input({ status: 'cancelled' }), { k: 'gone', label: 'Cancelled' }],
  ['blocked', input({ status: 'blocked' }), { k: 'stop', label: 'Blocked' }],
  ['blocked beats an expired claim', input({ status: 'blocked', claimExpired: true }), { k: 'stop', label: 'Blocked' }],
  ['in_progress, claim expired', input({ status: 'in_progress', claimExpired: true }), { k: 'stop', label: 'Claim expired' }],
  ['open, claim expired', input({ claimExpired: true }), { k: 'stop', label: 'Claim expired' }],
  ['done, stale claim row ignored', input({ status: 'done', claimExpired: true, proof: true }), { k: 'proven', label: 'Verified' }],
  ['in_progress', input({ status: 'in_progress' }), { k: 'doing', label: 'In progress' }],
  ['review', input({ status: 'review' }), { k: 'doing', label: 'In review' }],
  ['done + obsolete (a deferral)', input({ status: 'done', resolution: 'obsolete', proof: true }), { k: 'gone', label: 'Moved' }],
  ['done container (phase)', input({ kind: 'phase', status: 'done' }), { k: 'proven', label: 'Done' }],
  ['done container (run)', input({ kind: 'run', status: 'done' }), { k: 'proven', label: 'Done' }],
  ['done leaf, independent proof', input({ status: 'done', proof: true }), { k: 'proven', label: 'Verified' }],
  ['done leaf, no independent proof', input({ status: 'done' }), { k: 'claimed', label: 'Done, not verified' }],
  ['done question, answered (a human verification)', input({ kind: 'question', status: 'done', proof: true }), { k: 'proven', label: 'Answered' }],
  ['done question, no answer recorded', input({ kind: 'question', status: 'done' }), { k: 'claimed', label: 'Done, not verified' }],
  ['done leaf, proof unknown (ItemRef only)', input({ status: 'done', proof: null }), { k: 'claimed', label: 'Done' }],
  ['open, waiting on an open blocker', input({ waitingOn: ['m7/2.1'] }), { k: 'wait', label: 'Waiting on m7/2.1' }],
  ['ready, waiting on two', input({ status: 'ready', waitingOn: ['a', 'b'] }), { k: 'wait', label: 'Waiting on a, b' }],
  ['in_progress beats waiting', input({ status: 'in_progress', waitingOn: ['a'] }), { k: 'doing', label: 'In progress' }],
  ['open', input({}), { k: 'idle', label: 'Open' }],
  ['ready', input({ status: 'ready' }), { k: 'idle', label: 'Open' }],
]
for (const [label, i, expected] of cases) check(label, state(i), expected)

console.log('\n── answer(): the decision a done question closed with ───────────────────────')
const question = (status, notes) => ({ item: { id: 'q', kind: 'question', status }, notes })
const decision = (body, at, from = 'q', kind = 'decision') => ({ kind, body, from: { id: from }, createdAt: at })
check('done question, its decision note', answer(question('done', [decision('Use Postgres 17', '2026-10-29T10:00:00Z')])), 'Use Postgres 17')
check('the latest of two decisions', answer(question('done', [decision('old', '2026-10-28T10:00:00Z'), decision('new', '2026-10-29T10:00:00Z')])), 'new')
check('a decision from another item is not its answer', answer(question('done', [decision('elsewhere', '2026-10-29T10:00:00Z', 'x')])), null)
check('an observation is not an answer', answer(question('done', [decision('seen', '2026-10-29T10:00:00Z', 'q', 'observation')])), null)
check('an open question has no answer yet', answer(question('open', [decision('early', '2026-10-29T10:00:00Z')])), null)
check('a task has no answer', answer({ ...question('done', [decision('d', '2026-10-29T10:00:00Z')]), item: { id: 'q', kind: 'task', status: 'done' } }), null)

console.log('\n── the phone menu: the page name in its top bar, and what closes it ──────────')
const sidebar = [{ item: { id: 'p1', title: 'Skills' }, runs: [{ item: { id: 'r1', title: 'm64 backlog polish' } }] }]
check('the overview', pageName('/', sidebar), 'Overview')
check('needs you', pageName('/inbox', sidebar), 'Needs you')
check('a run, by its title', pageName('/runs/r1', sidebar), 'm64 backlog polish')
check('a run with an item open, by the run', pageName('/runs/r1/i9', sidebar), 'm64 backlog polish')
check('a run the overview has not loaded', pageName('/runs/r1', undefined), 'Run')
check('a project, by its title', pageName('/projects/p1', sidebar), 'Skills')
check('a project backlog', pageName('/projects/p1/backlog', sidebar), 'Skills · Backlog')
check('an admin page', pageName('/admin/users', sidebar), 'Admin')
check('an item page', pageName('/items/i9', sidebar), 'Item')
check('an unknown path', pageName('/nowhere', sidebar), 'Workman')
check('another page closes it', closesOnMove('/inbox', '/runs/r1'), true)
check('another query closes it', closesOnMove('/runs/r1', '/runs/r1?filter=all'), true)
check('the same URL keeps it open', closesOnMove('/runs/r1', '/runs/r1'), false)
check('the breakpoint matches index.css', PHONE_QUERY, '(max-width: 760px)')

console.log('\n── independent(): who may prove a leaf ──────────────────────────────────────')
check('passed, run by orchestrator', independent({ passed: true, runBy: 'orchestrator' }), true)
check('passed, run by ci', independent({ passed: true, runBy: 'ci' }), true)
check('passed, run by human', independent({ passed: true, runBy: 'human' }), true)
check('passed, run by the worker itself', independent({ passed: true, runBy: 'worker' }), false)
check('failed, run by orchestrator', independent({ passed: false, runBy: 'orchestrator' }), false)
check('no verification', independent(null), false)
check('leaf kinds', [...LEAF].sort(), ['followup', 'incident', 'question', 'spike', 'subtask', 'task'])
check('closed statuses', [...CLOSED].sort(), ['cancelled', 'done', 'superseded'])

// A board in the v1 shape: the run, a loose task, two phases. Pre-order, depth from the run.
let n = 0
const ref = (kind, status, title = kind) => ({ id: `id-${++n}`, ref: String(n), qualifiedRef: `r/${n}`, kind, title, status, fullRef: `p/r/${n}` })
const row = (item, depth, over = {}) => ({
  item,
  parentId: null,
  depth,
  seq: '1',
  hasChildren: false,
  resolution: null,
  priority: 0,
  severity: null,
  criteria: { checked: 0, total: 0 },
  rollup: null,
  claim: null,
  commit: null,
  verification: null,
  blockedBy: [],
  flags: [],
  openedAt: '2026-10-01T00:00:00Z',
  startedAt: null,
  closedAt: null,
  ...over,
})
const proven = { passed: true, runBy: 'orchestrator', at: '2026-10-02T00:00:00Z' }
const rows = [
  row(ref('run', 'in_progress'), 0),
  row(ref('task', 'open', 'loose'), 1),
  row(ref('phase', 'done', 'P1'), 1),
  row(ref('task', 'done'), 2, { verification: proven }),
  row(ref('task', 'done'), 2, { verification: { ...proven, runBy: 'worker' } }),
  row(ref('task', 'done'), 2, { resolution: 'obsolete' }),
  row(ref('phase', 'in_progress', 'P2'), 1),
  row(ref('task', 'blocked'), 2),
  row(ref('subtask', 'in_progress'), 3, { claim: { expired: true } }),
  row(ref('task', 'open'), 2, { blockedBy: [ref('task', 'blocked')] }),
  row(ref('task', 'cancelled'), 2),
]
const board = { rows }

console.log('\n── tally(): the segmented bar ───────────────────────────────────────────────')
const t = tally(rows)
check('counts', { proven: t.proven, claimed: t.claimed, doing: t.doing, stop: t.stop, wait: t.wait, idle: t.idle, gone: t.gone }, {
  proven: 1, claimed: 1, doing: 0, stop: 2, wait: 1, idle: 1, gone: 2,
})
check('total excludes gone, done = proven + claimed', [t.total, t.done], [6, 2])
check('segments drawn', t.leaves.length, 6)

console.log('\n── plan(): the filters ──────────────────────────────────────────────────────')
const shape = (groups) => groups.map((g) => [g.head?.item.title ?? 'Tasks', g.rows.length, g.tally ? `${g.tally.done}/${g.tally.total}` : '-'])
check('All, cancelled shown', shape(plan(board, 'all', true)), [['Tasks', 1, '-'], ['P1', 3, '2/2'], ['P2', 4, '0/3']])
check('All, cancelled hidden (the default)', shape(plan(board, 'all')), [['Tasks', 1, '-'], ['P1', 3, '2/2'], ['P2', 3, '0/3']])
check('Open', shape(plan(board, 'open')), [['Tasks', 1, '-'], ['P2', 3, '0/3']])
check('Open ignores the cancelled flag', shape(plan(board, 'open', true)), [['Tasks', 1, '-'], ['P2', 3, '0/3']])
check('Needs attention (stop or claimed)', shape(plan(board, 'attention')), [['P1', 1, '2/2'], ['P2', 2, '0/3']])
check('indent under a phase', plan(board, 'all', true)[2].rows.map((r) => r.indent), [0, 1, 0, 0])
check('cancelled lines the toggle counts', cancelledLines(board), 1)

console.log('\n── an empty phase: listed as itself, counted as one item ────────────────────')
// The real case: one done task, two open follow-ups, one open phase with nothing in it yet.
const emptyRows = [
  row(ref('run', 'in_progress'), 0),
  row(ref('task', 'done', 'shipped'), 1, { verification: proven }),
  row(ref('followup', 'open', 'fu-a'), 1),
  row(ref('followup', 'open', 'fu-b'), 1),
  row(ref('phase', 'open', 'Empty'), 1),
]
const empty = { rows: emptyRows }
check('Open lists the open empty phase, no rows below it', shape(plan(empty, 'open')), [['Tasks', 2, '-'], ['Empty', 0, '0/0']])
check('All lists it too, unchanged', shape(plan(empty, 'all')), [['Tasks', 3, '-'], ['Empty', 0, '0/0']])
check('Needs attention leaves out an idle empty phase', shape(plan(empty, 'attention')), [])
check('tally() counts it: 1 of 4, as the rollup does', progress(tally(emptyRows)), '1 of 4 done.')
const emptyRollup = { total: 4, done: 1, inProgress: 0, review: 0, blocked: 0, open: 3, cancelled: 0, superseded: 0 }
check('the API rollup of that run, counted', counted(emptyRollup), { done: 1, total: 4, cancelled: 0 })
const lone = (phase) => ({ rows: [row(ref('run', 'in_progress'), 0), phase] })
const donePhase = lone(row(ref('phase', 'done', 'Closed'), 1))
check('a done empty phase stays hidden under Open', shape(plan(donePhase, 'open')), [])
check('a done empty phase is listed under All', shape(plan(donePhase, 'all')), [['Closed', 0, '0/0']])
check('a done empty phase is one done item', [tally(donePhase.rows).done, tally(donePhase.rows).total], [1, 1])
check('a blocked empty phase needs attention', shape(plan(lone(row(ref('phase', 'blocked', 'Stuck'), 1)), 'attention')), [['Stuck', 0, '0/0']])
check('an expired claim on an empty phase needs attention', shape(plan(lone(row(ref('phase', 'open', 'Held'), 1, { claim: { expired: true } })), 'attention')), [['Held', 0, '0/0']])
const gonePhase = lone(row(ref('phase', 'cancelled', 'Gone'), 1))
check('a cancelled empty phase: hidden, the toggle counts it', [shape(plan(gonePhase, 'all')), cancelledLines(gonePhase)], [[], 1])
const onlyCancelled = { rows: [row(ref('run', 'in_progress'), 0), row(ref('phase', 'open', 'P'), 1, { hasChildren: true }), row(ref('task', 'cancelled'), 2)] }
check('a phase whose only child is cancelled is not empty', shape(plan(onlyCancelled, 'open')), [])
check('a phase with children is still not counted itself', tally(rows).total, 6)
check('hasChildren alone keeps a phase out of the count', tally([row(ref('phase', 'open'), 1, { hasChildren: true })]).total, 0)
check('an empty run or a finding is not an item of work', tally([row(ref('run', 'open'), 0), row(ref('finding', 'open'), 1)]).total, 0)

console.log('\n── findings on the run: their own group, never counted ──────────────────────')
// 8 of 10 done over open findings filed on the run: they list after the phases, not among the tasks.
const findingRows = [
  row(ref('run', 'in_progress'), 0),
  row(ref('task', 'done', 'shipped'), 1, { verification: proven }),
  row(ref('finding', 'open', 'F-run'), 1),
  row(ref('phase', 'in_progress', 'P'), 1, { hasChildren: true }),
  row(ref('task', 'open', 'in-p'), 2, { hasChildren: true }),
  row(ref('finding', 'open', 'F-task'), 3),
  row(ref('finding', 'done', 'F-closed'), 1),
  row(ref('task', 'open', 'loose-late'), 1),
]
const findingBoard = { rows: findingRows }
const labelled = (groups) => groups.map((g) => [g.head?.item.title ?? g.label, g.rows.map((r) => r.row.item.title)])
check('Open: Tasks, the phase, then Findings', labelled(plan(findingBoard, 'open')), [
  ['Tasks', ['loose-late']], ['P', ['in-p', 'F-task']], ['Findings', ['F-run']],
])
check('All: a closed finding on the run is still under Findings', labelled(plan(findingBoard, 'all')), [
  ['Tasks', ['shipped', 'loose-late']], ['P', ['in-p', 'F-task']], ['Findings', ['F-run', 'F-closed']],
])
check('no findings on the run, no Findings group', labelled(plan(empty, 'all')).map((g) => g[0]), ['Tasks', 'Empty'])
check('the X of Y leaves findings out: 1 of 3', [tally(findingRows).done, tally(findingRows).total], [1, 3])
check('header: several open', findingsOpen(3), '3 findings open.')
check('header: one open', findingsOpen(1), '1 finding open.')
check('header: none, nothing printed', [findingsOpen(0), findingsOpen(undefined)], ['', ''])

console.log('\n── the run page opens on Open, and its links keep an explicit filter ───────')
check('no filter in the URL is Open', asFilter(undefined), 'open')
check('an unknown filter is Open', asFilter('bogus'), 'open')
check('filter=all is All', asFilter('all'), 'all')
check('the default filter', DEFAULT_FILTER, 'open')
check('Open is the first filter in the nav', FILTERS[0], ['open', 'Open'])
check('query for the default leaves the filter out', planQuery('open', 'overview'), '')
check('query keeps an explicit All', planQuery('all', 'overview'), '?filter=all')
check('query keeps All, a tab and shown cancelled', planQuery('all', 'evidence', true), '?filter=all&tab=evidence&cancelled=show')
check('All survives a round trip through its own link', asFilter(new URLSearchParams(planQuery('all', 'overview')).get('filter') ?? undefined), 'all')

console.log('\n── the run sub line names what it leaves out ───────────────────────────────')
check('progress(), the sample board', progress(t), '2 of 6 done, 1 cancelled, 1 moved or superseded, 1 without independent proof, 2 blocked, 1 waiting on other tasks.')
const backlogRows = [
  row(ref('run', 'in_progress'), 0),
  ...[1, 2, 3].map(() => row(ref('followup', 'done'), 1, { verification: proven })),
  ...[1, 2, 3].map(() => row(ref('followup', 'open'), 1)),
  ...Array.from({ length: 13 }, () => row(ref('followup', 'cancelled'), 1)),
]
check('progress(), backlog: 3 done, 3 open, 13 cancelled', progress(tally(backlogRows)), '3 of 6 done, 13 cancelled.')
check('tally().cancelled', tally(backlogRows).cancelled, 13)
const rollup = { total: 19, done: 3, inProgress: 0, review: 0, blocked: 0, open: 3, cancelled: 13, superseded: 0 }
check('a rollup counted as tally() counts', counted(rollup), { done: 3, total: 6, cancelled: 13 })
check('the compact count tooltip', countTitle(counted(rollup)), '3 of 6 done, 13 cancelled')
check('the backlog toggle count, from its plan', cancelledLines({ rows: backlogRows }), 13)
const kids = [{ item: ref('task', 'open') }, { item: ref('task', 'cancelled') }, { item: ref('task', 'done') }]
check('child list, cancelled hidden', withoutCancelled(kids, false).shown.length, 2)
check('child list, cancelled shown', withoutCancelled(kids, true).shown.length, 3)
check('child list, count either way', [withoutCancelled(kids, false).count, withoutCancelled(kids, true).count], [1, 1])

console.log('\n── copy puts the full path on the clipboard ────────────────────────────────')
const shared = { id: 'S', ref: 'shared-server', qualifiedRef: 'shared-server', kind: 'followup', title: 't', status: 'open', fullRef: 'workman/backlog/shared-server' }
check('copyValue() is fullRef, not qualifiedRef', copyValue(shared), 'workman/backlog/shared-server')
const pj = { id: 'PJ', ref: 'workman', qualifiedRef: 'workman', kind: 'project', title: 'Workman', status: 'open', fullRef: 'workman' }
const bl = { id: 'BL', ref: 'backlog', qualifiedRef: 'backlog', kind: 'run', title: 'Backlog', status: 'open', fullRef: 'workman/backlog' }
const ph = { id: 'PH', ref: '1', qualifiedRef: 'backlog/1', kind: 'phase', title: 'P', status: 'open', fullRef: 'workman/backlog/1' }
const crumbs = (item, path) => pathCrumbs(item, path).map((c) => [c.text, c.item?.id ?? null])
check('item crumbs: the full path, each segment linked', crumbs(shared, [pj, bl, ph]), [['workman', 'PJ'], ['backlog', 'BL'], ['shared-server', null]])
check('run crumbs', crumbs(bl, [pj]), [['workman', 'PJ'], ['backlog', null]])
check('project crumbs', crumbs(pj, []), [['workman', null]])
check('a project and run of one name link in order', crumbs({ ...shared, fullRef: 'x/x/shared-server' }, [{ ...pj, ref: 'x' }, { ...bl, ref: 'x' }]), [['x', 'PJ'], ['x', 'BL'], ['shared-server', null]])

console.log('\n── itemHref() / routeReads(): every link has a page ─────────────────────────')
const run = { id: 'R', kind: 'run' }
check('a run', itemHref(run, null), '/runs/R')
check('a task inside a run', itemHref({ id: 'T', kind: 'task' }, run), '/runs/R/T')
check('a task with no run known', itemHref({ id: 'T', kind: 'task' }, null), '/items/T')
check('a project, even with a run given', itemHref({ id: 'P', kind: 'project' }, run), '/items/P')
check('the run passed as its own run', itemHref(run, run), '/runs/R')
const onBoard = (id) => id === 'T'
check('run page: its own item', boardHref('R', onBoard, { id: 'T', kind: 'task' }), '/runs/R/T')
check('run page: the run itself', boardHref('R', onBoard, run), '/runs/R')
check('run page: a blocker in another run', boardHref('R', onBoard, { id: 'X', kind: 'task' }), '/items/X')
check('run page: another run', boardHref('R', onBoard, { id: 'S', kind: 'run' }), '/runs/S')
check('reads for /', routeReads('/'), ['/api/v1/overview'])
check('reads for /runs/R/T', routeReads('/runs/R/T?tab=evidence'), ['/api/v1/runs/R/board', '/api/v1/items/T'])
check('reads for /items/P', routeReads('/items/P'), ['/api/v1/items/P'])
check('reads for a backlog', routeReads('/projects/P/backlog'), ['/api/v1/backlog?project=P'])
check('reads for /sessions', routeReads('/sessions'), ['/api/v1/sessions'])
check('an unknown path has no page', routeReads('/runs'), [])

console.log('\n── the run split: docked or a sheet, by the measured width ─────────────────')
const fits = LIST_MIN + HANDLE + PANEL_MIN
check('docking width is list + handle + panel minimums', fits, 769)
check('docks at exactly the docking width', docks(fits), true)
check('a sheet one pixel below it', docks(fits - 1), false)
check('a phone-width run area is a sheet', docks(390), false)
check('a wide run area docks', docks(1600), true)

console.log('\n── the run split: a dragged width is clamped ───────────────────────────────')
check('below the panel minimum', clampWidth(100, 1600), PANEL_MIN)
check('past the list minimum', clampWidth(5000, 1600), 1600 - HANDLE - LIST_MIN)
check('inside both floors, rounded', clampWidth(500.4, 1600), 500)
check('at the docking width the panel has its minimum only', clampWidth(600, fits), PANEL_MIN)
check('default share before any drag', panelWidth(undefined, 1600), Math.round((1600 - HANDLE) * PANEL_SHARE))
check('a remembered width wider than the room yields', panelWidth(1400, 1000), 1000 - HANDLE - LIST_MIN)

console.log('\n── the run split: the remembered width ─────────────────────────────────────')
const memory = (value) => {
  const map = new Map(value === undefined ? [] : [[WIDTH_KEY, value]])
  return { getItem: (k) => map.get(k) ?? null, setItem: (k, v) => map.set(k, v), removeItem: (k) => map.delete(k), map }
}
const throwing = { getItem: () => { throw new Error('blocked') }, setItem: () => { throw new Error('blocked') }, removeItem: () => { throw new Error('blocked') } }
check('a stored width is read', rememberedWidth(() => memory('512')), 512)
check('nothing stored is no preference', rememberedWidth(() => memory(undefined)), undefined)
check('garbage stored is no preference', rememberedWidth(() => memory('wide')), undefined)
check('getItem throwing falls back', rememberedWidth(() => throwing), undefined)
check('localStorage itself throwing falls back', rememberedWidth(() => { throw new Error('SecurityError') }), undefined)
check('the fallback is the default share', panelWidth(rememberedWidth(() => throwing), 1600), panelWidth(undefined, 1600))
const kept = memory(undefined)
rememberWidth(487.6, () => kept)
check('a width is stored rounded', kept.map.get(WIDTH_KEY), '488')
rememberWidth(undefined, () => kept)
check('a reset forgets it', kept.map.has(WIDTH_KEY), false)
let threw = false
try { rememberWidth(400, () => throwing) } catch { threw = true }
check('storing into throwing storage does not throw', threw, false)

console.log('\n── the consent page: loading, client shown, approve, deny, error ────────────')
const search = '?response_type=code&client_id=c1&redirect_uri=http%3A%2F%2F127.0.0.1%3A7777%2Fcallback&scope=openid+mcp+offline_access&state=s&resource=r1&resource=r2'
const info = { clientName: 'Claude Code', clientHost: null, redirectHost: '127.0.0.1', loopbackOnly: true }
const loading = { k: 'loading' }
const shown = consentStep(loading, { t: 'loaded', info })
const pick = (v) => ({ client: v.client, canAnswer: v.canAnswer, approve: v.approveLabel, deny: v.denyLabel, error: v.error })
check('loading: no client, no answer yet', pick(consentView(loading)), { client: null, canAnswer: false, approve: 'Approve', deny: 'Deny', error: null })
check('client shown: name, may answer', pick(consentView(shown)), { client: 'Claude Code', canAnswer: true, approve: 'Approve', deny: 'Deny', error: null })
check('client shown: where the answer goes', [consentView(shown).destination, consentView(shown).declaredBy, consentView(shown).loopbackOnly], ['127.0.0.1', null, true])
check('an unregistered callback names no host', consentView(consentStep(loading, { t: 'loaded', info: { ...info, redirectHost: null } })).destination, null)
const approving = consentStep(shown, { t: 'answer', answer: 'granted' })
check('approve: one answer, labelled in flight', pick(consentView(approving)), { client: 'Claude Code', canAnswer: false, approve: 'Approving…', deny: 'Deny', error: null })
check('approve: a second press changes nothing', consentStep(approving, { t: 'answer', answer: 'denied' }), approving)
check('approve: posts every parameter back, then consent=granted', consentForm(search, 'granted'), [
  ['response_type', 'code'], ['client_id', 'c1'], ['redirect_uri', 'http://127.0.0.1:7777/callback'], ['scope', 'openid mcp offline_access'],
  ['state', 's'], ['resource', 'r1'], ['resource', 'r2'], ['consent', 'granted'],
])
const denying = consentStep(shown, { t: 'answer', answer: 'denied' })
check('deny: labelled in flight', pick(consentView(denying)), { client: 'Claude Code', canAnswer: false, approve: 'Approve', deny: 'Denying…', error: null })
check('deny: posts consent=denied', consentForm(search, 'denied').at(-1), ['consent', 'denied'])
check('an answer before the client is shown is ignored', consentStep(loading, { t: 'answer', answer: 'granted' }), loading)
check('error: 404 names the cause', consentView(consentStep(loading, { t: 'failed', status: 404 })).error, 'No client by that id is registered here. Start the connection again from your MCP client.')
check('error: no answer at all', consentView(consentStep(loading, { t: 'failed', status: 0 })).error, 'Workman did not answer. Check that it is running, then reload this page.')
check('error: cannot answer', consentView(consentStep(loading, { t: 'failed', status: 500 })).canAnswer, false)
check('a late failure does not replace a shown client', consentStep(shown, { t: 'failed', status: 500 }), shown)
check('a stale consent field is never forwarded twice', hiddenFields('?client_id=c1&consent=granted'), [['client_id', 'c1']])
check('what it gets: openid is not a line', scopeLines(search).map((l) => l.scope), ['mcp', 'offline_access'])
check('what it gets: mcp is controller access', scopeLines(search)[0].label, 'Full access to your Workman as controller')

console.log('\n── the consent page: the workspace it acts in, and the switch ───────────────')
const tenants = [
  { id: 't-a', slug: 'alpha', role: 'member', current: true },
  { id: 't-b', slug: 'beta', role: 'admin', current: false },
]
const inA = consentStep(loading, { t: 'loaded', info, tenants })
const where = (v) => ({ tenant: v.tenant?.slug ?? null, others: v.others.map((t) => t.slug), switching: v.switchingTo, canAnswer: v.canAnswer })
check('the session\'s tenant is named, the other offered', where(consentView(inA)), { tenant: 'alpha', others: ['beta'], switching: null, canAnswer: true })
check('one tenant: nothing to switch to', where(consentView(consentStep(loading, { t: 'loaded', info, tenants: [tenants[0]] }))), { tenant: 'alpha', others: [], switching: null, canAnswer: true })
check('no tenants answered: none named', consentView(shown).tenant, null)
const moving = consentStep(inA, { t: 'switch', to: 't-b' })
check('switching: named, and no answer meanwhile', where(consentView(moving)), { tenant: 'alpha', others: ['beta'], switching: 'beta', canAnswer: false })
check('switching: an answer meanwhile changes nothing', consentStep(moving, { t: 'answer', answer: 'granted' }), moving)
check('a switch to the current tenant changes nothing', consentStep(inA, { t: 'switch', to: 't-a' }), inA)
check('a switch to a tenant not offered changes nothing', consentStep(inA, { t: 'switch', to: 't-elsewhere' }), inA)
check('a switch during an answer changes nothing', consentStep(consentStep(inA, { t: 'answer', answer: 'granted' }), { t: 'switch', to: 't-b' }).k, 'answering')
check('a refused switch shows the server\'s words', consentView(consentStep(moving, { t: 'switch-failed', message: 'nope' })).error, 'nope')
check('approve: the tenant shown goes with the answer', consentForm(search, 'granted', 't-a').slice(-2), [['workman_tenant', 't-a'], ['consent', 'granted']])
check('a stale tenant field is never forwarded twice', hiddenFields('?client_id=c1&workman_tenant=t-a'), [['client_id', 'c1']])

console.log('\n── the consent page: the client\'s icon, or its initials ────────────────────')
check('the icon is asked of this origin, for this client', appIconHref('?client_id=https%3A%2F%2Fclaude.ai%2Foauth%2Fm&x=1'), '/connect/app-icon?client_id=https%3A%2F%2Fclaude.ai%2Foauth%2Fm')
check('no client id, no icon request', appIconHref('?scope=mcp'), null)
check('an icon that loads is shown', iconStep('loading', 'load'), 'image')
check('no icon (404 or broken): the initials', iconStep('loading', 'error'), 'initials')
check('a late load does not replace the initials', iconStep('initials', 'load'), 'initials')
check('initials: two words', initials('Claude Code'), 'CC')
check('initials: one word', initials('cursor'), 'CU')
check('initials: three words take the first two', initials('  my mcp   client '), 'MM')
check('initials: never empty', initials('   '), '?')
check('initials: a surrogate pair stays whole', initials('🦀 rust'), '🦀R')
check('an unknown scope is shown by name', scopeLines('?scope=mystery').map((l) => [l.scope, l.label]), [['mystery', 'mystery']])
check('the /api scopes in plain words', scopeLines('?scope=workman.read%20workman.sessions%20offline_access').map((l) => [l.scope, l.label]), [
  ['workman.read', 'Read your board and its live updates'],
  ['workman.sessions', 'Start agent sessions on your behalf'],
  ['offline_access', 'Stay connected'],
])
check('a lapsed session goes back through sign-in', signInHref('?client_id=c1&state=s'), '/connect/signin?returnUrl=%2Fconnect%2Fauthorize%3Fclient_id%3Dc1%26state%3Ds')

console.log('\n── the sign-in page: where it may continue to ───────────────────────────────')
check('authorize with a query', returnTarget('/connect/authorize?client_id=x'), '/connect/authorize?client_id=x')
check('bare authorize', returnTarget('/connect/authorize'), '/connect/authorize')
check('another origin', returnTarget('https://evil.example/connect/authorize?x'), null)
check('protocol-relative', returnTarget('//evil.example/connect/authorize'), null)
check('backslash trick', returnTarget('/\evil.example/connect/authorize'), null)
check('a lookalike path', returnTarget('/connect/authorizeX'), null)
check('a dashboard path', returnTarget('/api/v1/overview'), null)
check('none at all', returnTarget(null), null)
check('an invalid target never shows the form', signInStart('?returnUrl=https%3A%2F%2Fevil.example'), { k: 'invalid' })
const checking = signInStart('?returnUrl=%2Fconnect%2Fauthorize%3Fclient_id%3Dx')
check('a valid target checks for a session first', checking, { k: 'checking', target: '/connect/authorize?client_id=x' })
check('a session already there continues at once', signInStep(checking, { t: 'session', present: true }), { k: 'continuing', target: '/connect/authorize?client_id=x' })
const form = signInStep(checking, { t: 'session', present: false })
check('no session shows the form', form.k, 'form')
check('signing in continues', signInStep(form, { t: 'signed-in' }), { k: 'continuing', target: '/connect/authorize?client_id=x' })
check('the connect pages', ['/connect/signin', '/connect/consent', '/connect/authorize', '/'].map(isConnectPath), [true, true, false, false])

console.log('\n── sessions: who offers Release, and what the token says ────────────────────')
const tomb = 'mw-workflow~released~0192f1c2a3b44c5d8e9f00112233aabb'
check('a live name is not released', isReleased('mw-workflow'), false)
check('a tombstone is released', isReleased(tomb), true)
check('a tombstone shows the name it had', formerName(tomb), 'mw-workflow')
check('a live name offers Release', releasable({ session: { name: 'mw-workflow' } }), true)
check('a released row offers nothing', releasable({ session: { name: tomb } }), false)
check('every token state reads', ['none', 'live', 'revoked', 'expired'].map(tokenLabel), ['named by header, no token', 'token live', 'token revoked', 'token expired'])

console.log('\n── list keys: which keys move, when they stand down, where they land ───────')
const k = (over) => ({ key: 'ArrowDown', ctrlKey: false, metaKey: false, altKey: false, shiftKey: false, ...over })
check('ArrowDown is next, ArrowUp prev', [listMove(k({}), false, false), listMove(k({ key: 'ArrowUp' }), false, false)], ['next', 'prev'])
check('Home is first, End last', [listMove(k({ key: 'Home' }), false, false), listMove(k({ key: 'End' }), false, false)], ['first', 'last'])
check('other keys are not ours', [listMove(k({ key: 'j' }), false, false), listMove(k({ key: 'Enter' }), false, false)], [null, null])
check('focus in a text field: the key is typed', listMove(k({}), true, false), null)
check('the palette or a modal open: nothing moves', listMove(k({}), false, true), null)
check('any modifier held: nothing moves', ['ctrlKey', 'metaKey', 'altKey', 'shiftKey'].map((m) => listMove(k({ [m]: true }), false, false)), [null, null, null, null])
check('a key a handler already took is left alone', listMove(k({ defaultPrevented: true }), false, false), null)
check('typing: input, textarea, select', ['INPUT', 'TEXTAREA', 'SELECT'].map((tagName) => typingIn({ tagName })), [true, true, true])
check('typing: contenteditable', typingIn({ tagName: 'DIV', isContentEditable: true }), true)
check('not typing: a link, the body, no target', [typingIn({ tagName: 'A' }), typingIn({ tagName: 'BODY' }), typingIn(null)], [false, false, false])
const abc = ['a', 'b', 'c']
check('next and prev', [step(abc, 'b', 'next'), step(abc, 'b', 'prev')], ['c', 'a'])
check('first and last', [step(abc, 'b', 'first'), step(abc, 'b', 'last')], ['a', 'c'])
check('the ends hold: Down on the last, Up on the first', [step(abc, 'c', 'next'), step(abc, 'a', 'prev')], ['c', 'a'])
check('no selection: Down takes the first, Up the last', [step(abc, undefined, 'next'), step(abc, undefined, 'prev')], ['a', 'c'])
check('a selection not in the list counts as none', step(abc, 'z', 'next'), 'a')
check('an empty list: nowhere to go', [step([], undefined, 'next'), step([], 'a', 'first')], [undefined, undefined])
check('the run page walks heads and rows as drawn (All)', planOrder(plan(board, 'all')).map((r) => r.item.title), [
  'loose', 'P1', 'task', 'task', 'task', 'P2', 'task', 'subtask', 'task',
])
check('the run page walks the Open filter only', planOrder(plan(board, 'open')).map((r) => r.item.id), ['id-2', 'id-7', 'id-8', 'id-9', 'id-10'])
console.log('\n── Alt+Up/Down: run to run in the sidebar order ────────────────────────────')
check('Alt+ArrowDown is next, Alt+ArrowUp prev', [runMove(k({ altKey: true }), false, false), runMove(k({ key: 'ArrowUp', altKey: true }), false, false)], ['next', 'prev'])
check('Alt+ArrowLeft/Right stay the browser\'s', [runMove(k({ key: 'ArrowLeft', altKey: true }), false, false), runMove(k({ key: 'ArrowRight', altKey: true }), false, false)], [null, null])
check('plain arrows are the list\'s, not the runs\'', runMove(k({}), false, false), null)
check('Alt with another modifier is left alone', ['ctrlKey', 'metaKey', 'shiftKey'].map((m) => runMove(k({ altKey: true, [m]: true }), false, false)), [null, null, null])
check('Alt+Down in a field (a select opens) or a modal: not ours', [runMove(k({ altKey: true }), true, false), runMove(k({ altKey: true }), false, true)], [null, null])
const runRollup = (open) => ({ total: 2, open, inProgress: 0, review: 0, blocked: 0 })
const sideRun = (id, status = 'in_progress', open = 1) => ({ item: { id, status }, rollup: runRollup(open) })
const sideProject = (id, at, runs, archivedAt = null) => ({ item: { id, ref: id, title: id, kind: 'project' }, lastActivityAt: at, archivedAt, runs })
const sideAsOf = '2026-11-06T00:00:00Z'
const side = [
  sideProject('older', '2026-11-01T00:00:00Z', [sideRun('o1'), sideRun('o2', 'done'), sideRun('o3', 'in_progress', 0)]),
  sideProject('newer', '2026-11-05T00:00:00Z', [sideRun('n1'), sideRun('n2')]),
  sideProject('quiet', '2026-01-01T00:00:00Z', []),
  sideProject('old', '2026-01-01T00:00:00Z', [sideRun('a1')], '2026-02-01T00:00:00Z'),
]
const sideGroups = sideProjects(side, sideAsOf, '')
check('groups as the sidebar draws them', Object.values(sideGroups).map((g) => g.map((p) => p.item.id)), [['newer', 'older'], ['quiet'], ['old']])
check('a project lists its open runs only (done and finished out)', openRuns(side[0]).map((r) => r.item.id), ['o1'])
check('order across projects, archived collapsed', sideRuns(sideGroups, false).map((r) => r.item.id), ['n1', 'n2', 'o1'])
check('archived runs count once their group is shown', sideRuns(sideGroups, true).map((r) => r.item.id), ['n1', 'n2', 'o1', 'a1'])
check('the search box narrows the order', sideRuns(sideProjects(side, sideAsOf, 'older'), false).map((r) => r.item.id), ['o1'])
const order = sideRuns(sideGroups, false)
const id = (r) => r?.item.id
check('from a run: the next and the previous', [id(step(order, order[1], 'next')), id(step(order, order[1], 'prev'))], ['o1', 'n1'])
check('the ends hold: no wrap past the last or the first', [id(step(order, order[2], 'next')), id(step(order, order[0], 'prev'))], ['o1', 'n1'])
check('off a run page: Down the first run, Up the last', [id(step(order, undefined, 'next')), id(step(order, undefined, 'prev'))], ['n1', 'o1'])
check('the run a path is on', [runOnPath('/runs/r1'), runOnPath('/runs/r1/i2'), runOnPath('/projects/p'), runOnPath('/')], ['r1', 'r1', undefined, undefined])
console.log('\n── list keys on Backlog, Project, Activity: each page\'s row order ─────────')
const ev = (seq, at, item = { id: `i${seq}` }) => ({ seq, at, item, run: null, session: null, actor: 'a', role: 'worker', kind: 'note', summary: null })
const journal = [ev(1, '2026-11-04T12:00:00Z'), ev(2, '2026-11-04T13:00:00Z', null), ev(3, '2026-11-05T12:00:00Z'), ev(4, '2026-11-05T13:00:00Z')]
check('activity: newest day first, newest event first', activityDays(journal).map((d) => d.events.map((e) => e.seq)), [[4, 3], [2, 1]])
check('activity: only the events that link an item are rows', activityOrder(activityDays(journal)), ['ev:4', 'ev:3', 'ev:1'])
check('activity: nothing yet, no rows', activityOrder(activityDays([])), [])
const backlogPage = { groups: [
  { origin: { id: 'r1' }, items: [{ id: 'a' }, { id: 'b' }] },
  { origin: null, items: [{ id: 'c' }] },
] }
check('backlog: each origin link, then its items', backlogOrder(backlogPage, []), ['from:r1', 'item:a', 'item:b', 'item:c'])
check('backlog: the cancelled ones last, when shown', backlogOrder(backlogPage, [{ id: 'x' }]).slice(-2), ['item:c', 'item:x'])
check('backlog: empty', backlogOrder({ groups: [] }, []), [])
const proj = { runs: [sideRun('p1', 'done'), sideRun('p2'), sideRun('p3', 'in_progress', 0), sideRun('p4')] }
check('project: the panel\'s open and closed split', Object.values(projectRuns(proj)).map((rs) => rs.map((r) => r.item.id)), [['p2', 'p4'], ['p1', 'p3']])
check('project: open runs first, then the closed', projectOrder(proj), ['run:p2', 'run:p4', 'run:p1', 'run:p3'])
console.log('\n── keyboard help: ? opens it, one table for the dialog and doc 13 ──────────')
check('? opens the help', isHelpKey(k({ key: '?', shiftKey: true }), false, false), true)
check('? in a field is typed', isHelpKey(k({ key: '?', shiftKey: true }), true, false), false)
check('? with the palette or the help open: nothing', isHelpKey(k({ key: '?' }), false, true), false)
check('Ctrl+? and Alt+? are left alone', [isHelpKey(k({ key: '?', ctrlKey: true }), false, false), isHelpKey(k({ key: '?', altKey: true }), false, false)], [false, false])
check('/ is still the palette\'s, not the help\'s', isHelpKey(k({ key: '/' }), false, false), false)
const doc13 = readFileSync(new URL('../../../docs/13-dashboard.md', import.meta.url), 'utf8').replace(/\r\n/g, '\n')
check('doc 13\'s Keyboard table is keysTable() verbatim', doc13.includes(`\n${keysTable()}\n`), true)
check('the table names every key the code binds', ['Ctrl+P', '↓ / ↑', 'Home / End', 'Enter', 'Alt+↓', 'Esc', '?'].every((key) => KEYS.some((r) => r.keys.includes(key))), true)
check('the palette finds Keyboard shortcuts', matchCommands([...commands([]), KEYS_COMMAND], 'keyboard').map((c) => c.label), ['Keyboard shortcuts'])
check('… by "shortcuts" and by "help" too', [matchCommands([KEYS_COMMAND], 'shortcuts').length, matchCommands([KEYS_COMMAND], 'help').length], [1, 1])
check('it is an action, not a page', [KEYS_COMMAND.action, entryHref({ kind: 'command', command: KEYS_COMMAND })], ['keys', null])
console.log('\n── the command palette: what opens it, what matches, what Enter opens ───────')
const key = (over) => ({ key: 'p', ctrlKey: false, metaKey: false, altKey: false, shiftKey: false, ...over })
check('Ctrl+P opens', isPaletteChord(key({ ctrlKey: true })), true)
check('Cmd+P opens', isPaletteChord(key({ metaKey: true })), true)
check('Ctrl+Shift+P is left alone', isPaletteChord(key({ ctrlKey: true, shiftKey: true })), false)
check('a bare p is typing', isPaletteChord(key({})), false)
check('caps lock still opens', isPaletteChord(key({ key: 'P', ctrlKey: true })), true)
check('/ outside a field opens', isSlash(key({ key: '/' }), false), true)
check('/ in a field is typed', isSlash(key({ key: '/' }), true), false)
check('the hint on a Mac', chordLabel('Mozilla/5.0 (Macintosh; Intel Mac OS X 14_5)'), '⌘P')
check('the hint elsewhere', chordLabel('Mozilla/5.0 (Windows NT 10.0; Win64; x64)'), 'Ctrl P')

const projects = [
  { item: { id: 'p1', ref: 'workman', title: 'Workman — the tracker', kind: 'project' } },
  { item: { id: 'p2', ref: 'aw', title: 'Autowork', kind: 'project' } },
]
const all = commands(projects)
const labels = (cs) => cs.map((c) => c.label)
check('every command, in order', labels(all), [
  'Go to Overview', 'Go to Needs you', 'Go to Activity', 'Go to Sessions',
  'Open project Workman', 'Open Workman backlog', 'Open project Autowork', 'Open Autowork backlog',
  'Toggle theme',
])
check('a project and its backlog link through nav', all.slice(4, 6).map((c) => c.href), ['/projects/p1', '/projects/p1/backlog'])
check('empty query keeps them all', matchCommands(all, '  ').length, all.length)
check('a page by name', labels(matchCommands(all, 'activity')), ['Go to Activity'])
check('a label that starts with the query leads', labels(matchCommands(all, 'open project')), ['Open project Workman', 'Open project Autowork'])
check('every word must appear', labels(matchCommands(all, 'workman backlog')), ['Open Workman backlog'])
check('a project by its ref', labels(matchCommands(all, 'AW')), ['Open project Autowork', 'Open Autowork backlog'])
check('the theme by a keyword', labels(matchCommands(all, 'dark')), ['Toggle theme'])
check('a ref matches no command', labels(matchCommands(all, 'm/12')), [])

const hit = (id, match, run) => ({ item: { id, kind: 'task', ref: id, fullRef: id }, run, project: null, match, snippet: null })
const some = matchCommands(all, 'sessions')
const textOnly = arrange(some, [hit('t1', 'text', { id: 'r1' })])
check('commands lead without an exact ref', [textOnly.itemsFirst, textOnly.entries.map((e) => e.kind)], [false, ['command', 'item']])
const exact = arrange(some, [hit('t2', 'ref', { id: 'r1' }), hit('t3', 'prefix', null)])
check('an exact ref puts items first', [exact.itemsFirst, exact.entries.map((e) => e.kind)], [true, ['item', 'item', 'command']])
check('Enter on an item in a run opens its board', entryHref(exact.entries[0]), '/runs/r1/t2')
check('Enter on an item outside a run opens its page', entryHref(exact.entries[1]), '/items/t3')
check('Enter on a page command', entryHref(exact.entries[2]), '/sessions')
check('the theme navigates nowhere', entryHref({ kind: 'command', command: all.at(-1) }), null)

console.log('\n── admin: the rules the pages hint with, the switcher, the palette ──────────')
check('a password of 11 is short', passwordProblem('a'.repeat(11)), 'A password needs at least 12 characters.')
check('a password of 12 is enough', passwordProblem('a'.repeat(12)), null)
check('a blank name is refused', nameProblem('   ') !== null, true)
check('a name is trimmed before it is judged', nameProblem('  ada  '), null)
check('a control character is refused', nameProblem('ada\u0007') !== null, true)
check('slugs like default pass', ['default', 'team-two', 'a1'].map(slugProblem), [null, null, null])
check('slugs unlike them fail', ['Team', 'team--two', '-team', 'team-', 'tea m', ''].map((s) => slugProblem(s) !== null), [true, true, true, true, true, true])
check('a typed slug is lowered and hyphenated', slugFrom('  Team Two '), 'team-two')
check('roles read', ['admin', 'member', 'system-admin'].map(roleLabel), ['Admin', 'Member', 'System admin'])
check('one workspace shows no switcher', showSwitcher([{ id: 'a' }]), false)
check('two workspaces show it', showSwitcher([{ id: 'a' }, { id: 'b' }]), true)
const crew = [{ userId: 'u1', role: 'admin' }, { userId: 'u2', role: 'member' }]
check('the only admin is locked', isLastAdmin(crew, 'u1'), true)
check('a member is not', isLastAdmin(crew, 'u2'), false)
check('one of two admins is not', isLastAdmin([...crew, { userId: 'u3', role: 'admin' }], 'u1'), false)
const t0 = Date.parse('2026-10-28T12:00:00Z')
check('fourteen days out', expiresIn('2026-11-11T12:00:00Z', t0), 'in 14 days')
check('a day out reads in hours', expiresIn('2026-10-29T12:00:00Z', t0), 'in 24 hours')
check('minutes out', expiresIn('2026-10-28T12:05:00Z', t0), 'in 5 minutes')
check('past is expired', expiresIn('2026-10-28T11:00:00Z', t0), 'expired')

console.log('\n── the invite page: its path, and every step ───────────────────────────────')
const tok = 'Gx3c1oZk5mJ1VtJm6Vv3lR0yq8Jt4vWfZrjSx5i0bEk'
check('an invite path names its token', joinToken(`/join/${tok}`), tok)
check('not an invite path', ['/join/', '/join/a/b', '/joins/x', '/'].map(joinToken), [null, null, null, null])
check('the invite page is a link page', [isLinkPath(`/join/${tok}`), isLinkPath('/admin')], [true, false])
check('a link page is no connect page', isConnectPath(`/join/${tok}`), false)
const view = { tenantId: 't', tenant: 'team-two', role: 'member', expiresAt: '2026-11-11T12:00:00Z', signedInAs: null, member: false }
const invited = joinStep({ k: 'loading' }, { t: 'loaded', invite: view })
check('a usable invite shows the create form', [invited.k, invited.mode], ['shown', 'create'])
check('an unusable one says so in the server words', joinStep({ k: 'loading' }, { t: 'failed', status: 404, error: 'gone' }), { k: 'invalid', message: 'gone' })
check('no answer at all says so', joinStep({ k: 'loading' }, { t: 'failed', status: 0, error: null }).message, 'Workman did not answer. Check that it is running, then reload this page.')
check('sign in instead', joinStep(invited, { t: 'mode', mode: 'sign-in' }).mode, 'sign-in')
const accepting = joinStep(invited, { t: 'accept' })
check('accept is one at a time', [accepting.k, joinStep(accepting, { t: 'accept' }).k], ['accepting', 'accepting'])
check('a taken name goes back to the form with the reason', joinStep(accepting, { t: 'refused', status: 409, error: 'taken' }), { ...invited, error: 'taken' })
check('a link used meanwhile ends the page', joinStep(accepting, { t: 'refused', status: 404, error: null }).k, 'invalid')
check('throttled says to wait', joinStep(accepting, { t: 'refused', status: 429, error: null }).error, 'Too many attempts. Wait a minute, then try again.')
check('accepted', joinStep(accepting, { t: 'accepted' }), { k: 'joined', tenant: 'team-two' })

const withAccount = (account) => labels(commands(projects, account))
const reachable = [{ id: 'a', slug: 'default', current: true }, { id: 'b', slug: 'team-two', current: false }]
check('a member gets no admin command', withAccount({ administers: false, systemAdmin: false, tenants: reachable.slice(0, 1) }), labels(all))
check('an admin of several gets Admin and each switch', withAccount({ administers: true, systemAdmin: false, tenants: reachable }), [
  'Go to Overview', 'Go to Needs you', 'Go to Activity', 'Go to Sessions', 'Go to Admin',
  'Open project Workman', 'Open Workman backlog', 'Open project Autowork', 'Open Autowork backlog',
  'Switch to workspace team-two', 'Toggle theme',
])
const sys = commands(projects, { administers: true, systemAdmin: true, tenants: reachable })
check('a system administrator also gets Users', labels(sys).slice(4, 6), ['Go to Admin', 'Go to Users'])
check('admin commands open their pages', sys.slice(4, 6).map((c) => c.href), ['/admin', '/admin/users'])
const switchTo = sys.find((c) => c.action === 'tenant')
check('a switch names its workspace and opens nothing', [switchTo.tenant, entryHref({ kind: 'command', command: switchTo })], ['b', null])
check('a switch by the word workspace', labels(matchCommands(sys, 'workspace team')), ['Switch to workspace team-two'])
check('reads for the admin pages', ['/admin', '/admin/users', '/admin/tenants/T'].map(routeReads), [
  ['/api/admin/tenants'], ['/api/admin/users'], ['/api/admin/tenants', '/api/admin/tenants/T/members', '/api/admin/tenants/T/invites'],
])

console.log('\n── passwords: the reset page and the account page ─────────────────────────')
check('a reset path names its token', resetToken(`/reset/${tok}`), tok)
check('a reset page is a link page, an invite path no reset', [isLinkPath(`/reset/${tok}`), resetToken(`/join/${tok}`)], [true, null])
const resetShown = resetStep({ k: 'loading' }, { t: 'loaded', reset: { name: 'ada', expiresAt: '2026-10-29T12:00:00Z' } })
check('a usable reset link shows the form', resetShown, { k: 'shown', reset: { name: 'ada', expiresAt: '2026-10-29T12:00:00Z' }, error: null })
check('an unusable one says so', resetStep({ k: 'loading' }, { t: 'failed', status: 404, error: 'used' }), { k: 'invalid', message: 'used' })
const saving = resetStep(resetShown, { t: 'save' })
check('saving is one at a time', [saving.k, resetStep(saving, { t: 'save' }).k], ['saving', 'saving'])
check('a weak password goes back with the reason', resetStep(saving, { t: 'refused', status: 400, error: 'short' }).error, 'short')
check('a link used meanwhile ends the page', resetStep(saving, { t: 'refused', status: 404, error: null }).k, 'invalid')
check('saved names whose password it set', resetStep(saving, { t: 'saved' }), { k: 'done', name: 'ada' })
check('a change that worked says the other sessions ended', passwordMessage({ k: 'ok' }).startsWith('Password changed. Your other sessions are signed out'), true)
check('a wrong current password says nothing changed', passwordMessage({ k: 'wrong' }), 'The current password is not right. Nothing changed.')
check('a refusal is the server words', passwordMessage({ k: 'refused', error: 'A password needs at least 12 characters.' }), 'A password needs at least 12 characters.')
check('reads for the account page', routeReads('/account'), ['/api/session/me'])

console.log('\n── sharing: the overview marker, the Members panel, its palette command ────')
const mark = (sharing, me) => {
  const m = shareMarker(sharing, me)
  return [m.k, m.label, m.detail]
}
check('0 members: open to the workspace', mark({ members: 0, you: false, names: [] }, 'ada'), [
  'open', 'Open to the workspace', 'No one has joined yet, so everyone in the workspace can see it.',
])
check('1, you: private', mark({ members: 1, you: true, names: ['ada'] }, 'ada'), ['private', 'Private', 'Only you.'])
check('1, not you: private to them', mark({ members: 1, you: false, names: ['ben'] }), ['private', 'Private to ben', 'Only ben.'])
check('2, you: shared with the 1 other', mark({ members: 2, you: true, names: ['ada', 'ben'] }, 'ada'), ['shared', 'Shared with 1', 'You and ben.'])
check('3, you: shared with 2 others, named', mark({ members: 3, you: true, names: ['ada', 'ben', 'cy'] }, 'ben'), [
  'shared', 'Shared with 2', 'You, ada and cy.',
])
check('3, not you: the whole count', mark({ members: 3, you: false, names: ['ada', 'ben', 'cy'] }), ['shared', 'Shared with 3', 'ada, ben and cy.'])
check('you, own name not known yet: names everyone', mark({ members: 2, you: true, names: ['ada', 'ben'] }), ['shared', 'Shared with 1', 'ada and ben.'])
check('a member line: who added, when', memberLine({ addedBy: 'ada', disabledAt: null }, '29 Oct'), 'Added by ada 29 Oct')
check('a member line: no adder, disabled', memberLine({ addedBy: null, disabledAt: '2026-10-29T00:00:00Z' }, '29 Oct'), 'Added 29 Oct · disabled')
check('the Members anchor', [MEMBERS_ANCHOR, membersHref('P')], ['members', '/projects/P#members'])
check('a project page names its project, its backlog does not', ['/projects/P', '/projects/P/', '/projects/P/backlog', '/'].map(projectOnPath), [
  'P', 'P', null, null,
])
const onPage = commands(projects, undefined, 'P')
const manage = onPage.find((c) => c.label === 'Manage members')
check('Manage members on a project page opens its panel', manage && [manage.href, entryHref({ kind: 'command', command: manage })], [
  '/projects/P#members', '/projects/P#members',
])
check('no Manage members off a project page', commands(projects).some((c) => c.label === 'Manage members'), false)
check('Manage members by the word share', labels(matchCommands(onPage, 'share')), ['Manage members'])

console.log('── connected apps: the account page rows ───────────────────────────────────')
const app = (over) => ({
  id: 'a1',
  clientId: 'c1',
  name: 'Claude Code',
  scopes: ['mcp', 'offline_access'],
  createdAt: '2026-10-27T12:00:00Z',
  lastUsedAt: '2026-10-30T11:55:00Z',
  sessions: 1,
  ...over,
})
const asOf = '2026-10-30T12:00:00Z'
check('sessions: 0, 1, 2', [0, 1, 2].map(sessionsLabel), ['0 agent sessions', '1 agent session', '2 agent sessions'])
check('a used app: scopes, connected, last used, sessions', appLine(app({}), asOf), 'mcp offline_access · connected 3d ago · last used 5m ago · 1 agent session')
check('never used', appLine(app({ lastUsedAt: null, sessions: 0 }), asOf), 'mcp offline_access · connected 3d ago · not used yet · 0 agent sessions')
check('no creation date', appLine(app({ createdAt: null }), asOf), 'mcp offline_access · last used 5m ago · 1 agent session')
check('revoke, no sessions', revokeWarning(app({ sessions: 0 })), 'Claude Code will need to sign in again.')
check('revoke, one session', revokeWarning(app({})), 'Claude Code will need to sign in again, and its agent session stops.')
check('revoke, three sessions', revokeWarning(app({ sessions: 3 })), 'Claude Code will need to sign in again, and its 3 agent sessions stop.')

console.log('── boards: one read per refresh, never one per run ─────────────────────────')
const withRuns = (count, archived = false) => ({
  projects: [
    { archivedAt: null, runs: Array.from({ length: count }, (_, n) => ({ item: { id: `r${n}` } })) },
    { archivedAt: archived ? '2026-10-01T00:00:00Z' : null, runs: [{ item: { id: 'old' } }] },
  ],
})
const heldAll = (count) => Object.fromEntries([...Array.from({ length: count }, (_, n) => `r${n}`), 'old'].map((id) => [id, {}]))
check('opening the store: one read of every run, 75 runs', boardsRead(withRuns(75), 'all', {}), 'all')
check('a frame naming two runs reads those two', boardsRead(withRuns(75), ['r3', 'r9'], heldAll(75)), ['r3', 'r9'])
check('a run with no board yet is read with them', boardsRead(withRuns(3), ['r1'], { r0: {}, r1: {}, old: {} }), ['r1', 'r2'])
check('a run of an archived project is not read', boardsRead(withRuns(2, true), [], { r0: {}, r1: {} }), null)
check('nothing named, everything held: no read', boardsRead(withRuns(3), [], heldAll(3)), null)
check('past the cap: one read of every run', boardsRead(withRuns(BOARDS_MAX + 1, true), [], {}), 'all')

console.log('── inbox: what the person sees, and the controller housekeeping ────────────')
const runA = { id: 'ra', ref: 'run-a', title: 'Run A' }
const runB = { id: 'rb', ref: 'run-b', title: 'Run B' }
const inboxRow = (kind, audience, over = {}) => ({
  key: `${kind}:${over.id ?? kind}`, kind, audience, run: runA, item: { id: over.id ?? kind, title: 't' },
  what: 'w', since: '2026-11-06T10:00:00Z', call: 'c', detail: null, actor: null, tree: null, target: null, count: 1, url: null,
  ...over,
})
const mixed = [
  inboxRow('question', 'human'),
  inboxRow('blocked', 'human'),
  inboxRow('done_unverified', 'controller', { id: 'u1' }),
  inboxRow('done_unverified', 'controller', { id: 'u2' }),
  inboxRow('done_unverified', 'controller', { id: 'u3', run: runB }),
  inboxRow('expired_claim', 'controller', { id: 'c1' }),
]
check('needs you: question and blocked only', needsYou(mixed).map((r) => r.kind), ['question', 'blocked'])
check('an unknown audience is shown to the person', needsYou([inboxRow('question', 'someone')]).length, 1)
check('housekeeping: every controller row', forController(mixed).length, 4)
check('badge count: human rows only', needsYouCount({ rows: mixed }), 2)
check('badge count before the inbox loads: unknown', needsYouCount(undefined), undefined)
check('groups: by kind and run, kinds in order', groupHousekeeping(mixed).map((g) => g.label), [
  '1 claim expired in Run A',
  '2 not verified in Run A',
  '1 not verified in Run B',
])
check('a group keeps its rows', groupHousekeeping(mixed)[1].rows.map((r) => r.item.id), ['u1', 'u2'])
check('a group key is stable across reads', groupHousekeeping(mixed)[1].key, 'done_unverified:ra')
check('a row with no run groups without "in"', groupHousekeeping([inboxRow('stale_in_progress', 'controller', { run: null })])[0].label, '1 stalled')
check('no controller rows: no groups', groupHousekeeping(needsYou(mixed)), [])
check('card: closed by, for not verified', cardFacts(inboxRow('done_unverified', 'controller', { actor: 'worker-u1' })), ['closed by worker-u1'])
check('card: agent and tree, for an expired claim', cardFacts(inboxRow('expired_claim', 'controller', { actor: 'aw-1', tree: 'p:/trees/a' })), ['by aw-1', 'on p:/trees/a'])
check('card: note count only when several', cardFacts(inboxRow('unconsumed_note', 'controller', { count: 3 })), ['3 notes'])
check('card: a question has no extra facts', cardFacts(inboxRow('question', 'human')), [])

console.log('── inbox: the decision row (title first, muted kind and ref, the detail, never the call) ──')
{
  const inboxProjects = [{ item: { ref: 'acme', title: 'Acme Store — the shop' } }]
  const human = (kind, over = {}) =>
    inboxRow(kind, 'human', {
      item: { id: 'i1', title: 'Pick the cache', fullRef: 'acme/run-a/2.1' },
      call: 'work_status(ref:"run-a/2.1", to:"in_progress")',
      ...over,
    })
  const question = decisionRow(human('question', { actor: 'wm-1', detail: 'Which cache do we keep?' }), inboxProjects)
  check('question row: title, kind, full ref', [question.title, question.kind, question.fullRef], ['Pick the cache', 'Question', 'acme/run-a/2.1'])
  check('question row: who, project, run', question.facts, ['by wm-1', 'Acme Store', 'Run A'])
  check("question row: the intent's first line as given", question.detail, 'Which cache do we keep?')
  const blocked = decisionRow(human('blocked', { detail: 'waits on: run-a/1.2' }), inboxProjects)
  check('blocked row: the dot and kind', [blocked.dot, blocked.kind], ['stop', 'Blocked'])
  check('blocked row: waits on, as the server wrote it', blocked.detail, 'waits on: run-a/1.2')
  check('a null detail prints no line', decisionRow(human('blocked'), inboxProjects).detail, null)
  check('a blank detail prints no line', decisionRow(human('question', { detail: '  ' }), inboxProjects).detail, null)
  const odd = decisionRow(human('waiting_on_rain'), inboxProjects)
  check('unknown kind: its words, the stop dot', [odd.kind, odd.dot], ['waiting on rain', 'stop'])
  for (const [label, m] of [['question', question], ['blocked', blocked], ['unknown kind', odd]])
    check(`${label} row: no field carries the call`, JSON.stringify(m).includes('work_status'), false)
  check('project unknown to the overview: the ref segment', rowProject(human('question'), []), 'acme')
  check('project before the overview loads: the ref segment', rowProject(human('question'), undefined), 'acme')
  check('a row with no run names no run', decisionRow(human('question', { run: null }), inboxProjects).facts, ['Acme Store'])
}

console.log('── inbox: housekeeping, one line per kind and run; unread notes nowhere ──')
{
  const withNotes = [
    ...mixed,
    inboxRow('unconsumed_note', 'controller', { id: 'n1', count: 2 }),
    inboxRow('unconsumed_note', 'human', { id: 'n2' }),
    inboxRow('unconsumed_note', 'someone', { id: 'n3' }),
  ]
  check('group lines: kind count · run, kinds in order', groupHousekeeping(withNotes).map((g) => g.line), [
    '1 claim expired · Run A',
    '2 not verified · Run A',
    '1 not verified · Run B',
  ])
  check('a group line with no run has no separator', groupHousekeeping([inboxRow('stale_in_progress', 'controller', { run: null })])[0].line, '1 stalled')
  check('unread notes: no housekeeping group', groupHousekeeping(withNotes).some((g) => g.kind === 'unconsumed_note'), false)
  check('unread notes: not in the controller rows', forController(withNotes).length, 4)
  check('unread notes: never a person row, whatever the audience', needsYou(withNotes).map((r) => r.kind), ['question', 'blocked'])
  check('unread notes: not in the badge count', needsYouCount({ rows: withNotes }), 2)
  check(
    'chore row: ref, title, who, since',
    choreRow(inboxRow('done_unverified', 'controller', { item: { id: 'u9', qualifiedRef: 'run-a/3.1', title: 'Ship it' }, actor: 'wm-2' })),
    { ref: 'run-a/3.1', title: 'Ship it', actor: 'wm-2', since: '2026-11-06T10:00:00Z' },
  )
}

console.log(failures === 0 ? '\nAll checks passed.' : `\n${failures} check(s) failed.`)
if (failures > 0) process.exitCode = 1
