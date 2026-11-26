import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { inboxBell, primaryNav, signIn, syncHealthCard } from './support'

// REOS-142 (G20): the REOS-102 redesign QA carried across every restyled screen, both themes. Chrome only (Q-102a). Evidence for docs/QA_REPORT_REOS-142.md.
//   A. geometry   the screens that have a Figma frame, measured against the frame's numbers (Figma MCP read 2026-10-29, file uxMlQ7LTNoW8sIKWSIeY98)
//   B. times      "N min ago" (Sync health) and "Due in" (Approvals) are seeded through the dev endpoints / the demo's timed decision and watched moving under a fake clock
//   C. roles      Viewer, Governance Officer, RTE and Release Manager each open every screen: axe clean; a Viewer is offered no write action
//   D. states     loading, error (with a working Retry) and empty, on every screen, in light and dark, with axe on each state
// Service catalog and Environments are being built elsewhere (REOS-155/156) and are left out. Changes nothing in the demo data apart from what the tests add.
test.setTimeout(600_000)
test.use({ viewport: { width: 1600, height: 1000 } })
let seq = 0
const who = (tag: string) => `qa142.${tag}.${Date.now().toString(36)}.${seq++}@example.com`
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']

interface Screen { name: string; path: string; heading: string; open: (page: Page) => Promise<void> }
const viaNav = (link: string) => async (page: Page) => { await primaryNav(page).getByRole('link', { name: new RegExp(`^${link.replace(/[&]/g, '\\$&')}`) }).first().click() }
const SCREENS: Screen[] = [
  { name: 'Overview', path: '/overview', heading: 'Overview', open: viaNav('Overview') },
  { name: 'Portfolio & calendar', path: '/calendar', heading: 'Release portfolio', open: viaNav('Portfolio & calendar') },
  { name: 'Releases', path: '/', heading: 'Releases', open: viaNav('Releases') },
  { name: 'Orchestration', path: '/orchestration', heading: 'Orchestration pipeline', open: viaNav('Orchestration') },
  { name: 'Approvals', path: '/approvals', heading: 'Approvals', open: viaNav('Approvals') },
  { name: 'Deployments', path: '/deployments', heading: 'Deployments', open: viaNav('Deployments') },
  { name: 'Audit history', path: '/audit', heading: 'Audit history', open: viaNav('Audit history') },
  { name: 'Teams & settings', path: '/admin', heading: 'Teams & settings', open: viaNav('Teams & settings') },
  { name: 'Templates', path: '/templates', heading: 'Train templates', open: viaNav('Templates') },
  { name: 'Comm library', path: '/library', heading: 'Comm library', open: viaNav('Comm library') },
  { name: 'Connectors', path: '/connectors', heading: 'Connectors', open: viaNav('Connectors') },
  { name: 'My work', path: '/work', heading: 'My work', open: viaNav('My work') },
  { name: 'Analytics', path: '/analytics', heading: 'Analytics', open: viaNav('Analytics') },
  { name: 'Imports & exports', path: '/importexport', heading: 'Imports & exports', open: viaNav('Imports & exports') },
  { name: 'Sync health', path: '/sync', heading: 'Sync health', open: async page => { await syncHealthCard(page).click() } },
  { name: 'Inbox', path: '/inbox', heading: 'Inbox', open: async page => { await inboxBell(page).click() } },
]

async function settle(page: Page, heading?: string) {
  await expect(page.locator('main h1').first()).toBeVisible({ timeout: 20_000 })
  if (heading) await expect(page.locator('main h1').first()).toHaveText(heading)
  await page.locator('main [aria-busy="true"]').waitFor({ state: 'detached', timeout: 15_000 }).catch(() => undefined)
  await page.waitForTimeout(250)
}

async function axe(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  expect(bad.map(v => `${v.id} (${v.impact}): ${v.help}\n   ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join('\n   ')}`), `axe on ${where}`).toEqual([])
}

const api = (page: Page, method: string, url: string, body?: unknown) =>
  page.evaluate(async ([m, u, b]) => {
    const r = await fetch(u as string, { method: m as string, headers: { 'Content-Type': 'application/json' }, body: b === undefined ? undefined : JSON.stringify(b) })
    return { status: r.status, body: await r.json().catch(() => null) as unknown }
  }, [method, url, body] as const)

// ---- A. geometry against the Figma frames -------------------------------------------------------------------------------------------------------------
// Frames read with Figma metadata (frame, then its children, relative to the page content): Calendar 6:4990, Release detail 6:5279, Approvals 6:5838, Deployments 6:6058
// and Audit 6:6333 all use the shell of the tokens (216 px rail, 64 px bar), heading at x32 y28, content 1320 wide, and 20 to 22 px between blocks.
// Overview 6:4718 and Orchestration 6:5552 are drawn with a 248 px rail and 72 px bar (Q-102c). Block heights follow the frame's sample data, so they are reported, not asserted.
test.describe('A. geometry', () => {
  const FIGMA = [
    { name: 'Portfolio & calendar', path: '/calendar', frame: '6:4990' },
    { name: 'Approvals', path: '/approvals', frame: '6:5838' },
    { name: 'Deployments', path: '/deployments', frame: '6:6058' },
    { name: 'Audit history', path: '/audit', frame: '6:6333' },
    { name: 'Release detail', path: '/trains/', frame: '6:5279' },
  ]
  test('the five frames that use the token shell: content box, heading and block rhythm match the frame', async ({ page }) => {
    await signIn(page, who('geo'), 'RTE')
    const trainId = await page.evaluate(async () => ((await (await fetch('/api/v1/trains')).json()) as { id: string; title: string }[]).find(t => t.title.includes('Card portal'))!.id)
    const report: string[] = []
    for (const f of FIGMA) {
      await page.goto(f.path === '/trains/' ? `/trains/${trainId}` : f.path)
      await settle(page)
      await page.waitForTimeout(1000)
      const m = await page.evaluate(() => {
        const main = document.querySelector('main')!
        const root = main.firstElementChild!
        const kids = [...root.children].filter(c => c.getBoundingClientRect().height > 0 && c.tagName !== 'DIALOG')
        const box = (e: Element) => { const r = e.getBoundingClientRect(); return { x: r.x, y: r.y, w: r.width, h: r.height } }
        return { main: box(main), kids: kids.map(k => ({ ...box(k), cls: (k.className as string).split(' ')[0] })) }
      })
      const [head, ...rest] = m.kids
      expect(m.main.x, `${f.name}: rail width (frame ${f.frame}: 216)`).toBe(216)
      expect(m.main.y, `${f.name}: utility bar height (frame: 64)`).toBe(64)
      expect(head.x, `${f.name}: page gutter (frame: 32 from the rail edge)`).toBe(248)
      expect(head.y, `${f.name}: heading top (frame: 28 below the bar)`).toBe(92)
      if (f.name !== 'Release detail') expect(head.w, `${f.name}: content width (frame: 1320)`).toBe(1320)
      let prev = head
      const gaps: number[] = []
      for (const k of rest) { gaps.push(Math.round(k.y - (prev.y + prev.h))); prev = k }
      for (const g of gaps) expect(g, `${f.name}: space between page blocks (frame: 20 to 22)`).toBeGreaterThanOrEqual(20)
      for (const g of gaps) expect(g, `${f.name}: space between page blocks (frame: 20 to 22)`).toBeLessThanOrEqual(22)
      report.push(`PASS ${f.name} (frame ${f.frame}): rail 216, bar 64, heading x${head.x} y${head.y}${f.name === 'Release detail' ? '' : ` w${head.w}`}, block gaps ${gaps.join('/')}, heights ${m.kids.map(k => Math.round(k.h)).join('/')}`)
    }
    test.info().annotations.push({ type: 'geometry', description: report.join('\n') })
    console.log('GEOMETRY\n' + report.join('\n'))
  })

  test('the two frames drawn with the 248/72 shell are the owner decision Q-102c: the app keeps the token shell on those screens too', async ({ page }) => {
    await signIn(page, who('geo2'), 'RTE')
    for (const s of SCREENS.filter(s => ['Overview', 'Orchestration'].includes(s.name))) {
      await page.goto(s.path); await settle(page, s.heading)
      const m = await page.evaluate(() => ({ rail: document.querySelector('aside[aria-label="Workspace"]')!.getBoundingClientRect().width, bar: document.querySelector('header.os-utility')!.getBoundingClientRect().height }))
      expect(m, s.name).toEqual({ rail: 216, bar: 64 })   // frame: 248 / 72 (Q-102c)
    }
  })
})

// ---- B. relative times ---------------------------------------------------------------------------------------------------------------------------------
test.describe('B. relative times move', () => {
  test('Sync health: "N min ago" is seeded and moves forward with the clock without a reload', async ({ page }) => {
    await signIn(page, who('sync'), 'RTE')
    const dev = (path: string, body: unknown = {}) => api(page, 'POST', `/api/v1/dev/sync/${path}`, body)
    expect((await dev('reset')).status).toBe(200)
    expect((await dev('connector', { source: 'Jira', consecutiveFailures: 0, lastSuccessMinutesAgo: 5 })).status).toBe(200)
    expect((await dev('connector', { source: 'ServiceNow', consecutiveFailures: 0, lastSuccessMinutesAgo: 12 })).status).toBe(200)
    // The server pushes its own clock every 10 s and the page measures "ago" from it (D24). Under a fake browser clock that push would undo the three minutes the test
    // fast-forwards, so it is held back here; the page's own ticking is what is under test.
    await page.routeWebSocket(/\/hub\/trains/, ws => {
      const server = ws.connectToServer()
      ws.onMessage(m => server.send(m))
      server.onMessage(m => { if (!String(m).includes('"ServerTime"')) ws.send(m) })
    })
    await page.clock.install({ time: new Date() })
    await page.goto('/sync')
    await settle(page, 'Sync health')
    const table = page.getByRole('table', { name: 'Connectors' })
    const jira = table.locator('tbody tr', { hasText: 'Jira' })
    const snow = table.locator('tbody tr', { hasText: 'ServiceNow' })
    await expect(jira).toContainText(/(?<!\d)[56] min ago/)                   // seeded 5 min ago: 5 (or 6 if the minute rolled while the page loaded)
    await expect(snow).toContainText(/(?<!\d)1[23] min ago/)
    const first = (await jira.innerText()).match(/(?<!\d)(\d+) min ago/)![1]
    await page.clock.fastForward('03:00')                                // three minutes pass; the page ticks every 30 s and was never reloaded
    await expect(jira).toContainText(new RegExp(`(?<!\\d)${+first + 3} min ago`))
    await expect(snow).toContainText(/(?<!\d)1[56] min ago/)
    await dev('reset')
  })

  test('Approvals: "Due in" counts down 30 → 20 minutes and then flips to "Overdue by", with the "Due within an hour" card following', async ({ page }) => {
    await signIn(page, who('appr'), 'ReleaseManager')
    const queue = (await api(page, 'GET', '/api/v1/approvals')).body as { items: { kind: string; dueAt: string | null; title: string }[] }
    const timed = queue.items.find(i => i.dueAt)
    // The demo's Gated "Card portal" has a window and no decision, so the Release Manager has a timed Go/No-Go; this fails (not skips) if that seed is gone.
    expect(timed, 'the Release Manager has a timed decision waiting (the demo seeds one); without it "Due in" cannot be observed').toBeTruthy()
    const due = Date.parse(timed!.dueAt!)
    await page.clock.install({ time: due - 31 * 60_000 })
    await page.goto('/approvals')
    await settle(page, 'Approvals')
    const item = page.getByRole('button', { name: new RegExp(timed!.title) }).first()
    const hour = page.getByRole('group', { name: 'Due within an hour' })
    await expect(item).toContainText(/Due in (30|31)m/)
    const before = await hour.innerText()
    await page.clock.fastForward('10:00')
    await expect(item).toContainText(/Due in (20|21)m/)
    expect(await hour.innerText(), 'the card counts it while it is within the hour').toBe(before)
    await page.clock.fastForward('25:00')
    await expect(item).toContainText(/Overdue by (3|4|5)m/)
    await expect(item).toContainText('Overdue')                         // in words, not only the danger colour
    expect(await hour.innerText(), 'overdue is counted as overdue, no longer as due within the hour').not.toBe(before)
  })
})

// ---- C. role-limited views -----------------------------------------------------------------------------------------------------------------------------
// A Viewer reads. Anything that creates, changes, decides or removes must not be offered (hidden, or disabled with the reason): a visible, enabled control with one of these
// verbs fails the test. Two controls are allowed by decision: "Create link" (the person's own calendar feed, Policies.Read, Q-051) and "Resolve →" (navigates to the release).
const WRITE = /^(paste|new|create|add|plan|save|delete|remove|archive|restore|approve|reject|decide|certify|waive|edit|import|upload|start|pause|resume|end|stop|fail|rollback|resolve|send|record|commit|deactivate|activate|retire|publish|submit|revoke|rotate|grant|request|assign|invite|rename|duplicate|clone|mark all|apply and|set )\b/i
const ALLOWED_FOR_VIEWER = ['Create link', 'Resolve →']
const NAVIGATION_NAMES = /^(PreCheck|Deploy|Verify|Rollback|Cutover|Post), (Planned|Ready|Running|Done|Failed|Skipped|Blocked)/   // a runbook step card on Orchestration: opens the step, named by its section and state
const ROLES = ['Viewer', 'GovernanceOfficer', 'RTE', 'ReleaseManager'] as const

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`C. role-limited views (${scheme})`, () => {
    test.use({ colorScheme: scheme })
    for (const role of ROLES) {
      test(`${role}: every screen opens, axe is clean${role === 'Viewer' ? ', and no write action is offered' : ''}`, async ({ page }) => {
        await signIn(page, who(`role-${role}-${scheme}`), role)
        const trainId = await page.evaluate(async () => ((await (await fetch('/api/v1/trains')).json()) as { id: string; title: string }[]).find(t => t.title.includes('Card portal'))!.id)
        const rows: string[] = []
        const targets = [...SCREENS.map(s => ({ name: s.name, path: s.path, heading: s.heading as string | null })), { name: 'Release detail', path: `/trains/${trainId}`, heading: null }]
        for (const s of targets) {
          await page.goto(s.path)
          await settle(page, s.heading ?? undefined)
          await expect(page.locator('main [role="alert"]'), `${s.name}: an error alert for ${role}`).toHaveCount(0)
          await axe(page, `${s.name} as ${role} (${scheme})`)
          const offered = await page.evaluate(() => {
            const name = (e: Element) => ((e.getAttribute('aria-label') ?? e.textContent) ?? '').trim().replace(/\s+/g, ' ')
            return [...document.querySelectorAll('main button, main [role="button"], main a[href], main [role="menuitem"], main [role="tab"]')]
              .filter(e => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0 && !e.closest('dialog:not([open])') })
              .map(e => ({ text: name(e), disabled: (e as HTMLButtonElement).disabled || e.getAttribute('aria-disabled') === 'true' }))
          })
          const writes = offered.filter(o => WRITE.test(o.text) && !o.disabled && !ALLOWED_FOR_VIEWER.includes(o.text) && !NAVIGATION_NAMES.test(o.text))
          rows.push(`${role} ${s.name}: ${offered.length} controls, ${writes.length} enabled write verb(s)${writes.length ? ': ' + writes.map(w => w.text).join(' | ') : ''}`)
          if (role === 'Viewer') expect(writes.map(w => w.text), `${s.name}: write actions offered to a Viewer`).toEqual([])
          if (role === 'RTE' && s.name === 'Releases') expect(writes.map(w => w.text), 'the RTE is offered New train').toContain('New train')
        }
        test.info().annotations.push({ type: 'roles', description: rows.join('\n') })
        console.log(`ROLES ${scheme}\n` + rows.join('\n'))
      })
    }
  })
}

// ---- D. loading, error and empty on every screen -------------------------------------------------------------------------------------------------------
for (const scheme of ['light', 'dark'] as const) {
  test.describe(`D. states (${scheme})`, () => {
    test.use({ colorScheme: scheme })

    test('loading and error on every screen: words, a polite status, an alert with Retry that works; axe on both states', async ({ page }) => {
      await signIn(page, who(`state-${scheme}`), 'RTE')
      const mode = { fail: false, slow: false }
      const shell = /^\/api\/v1\/(me|workspaces|sync\/state|notifications|inbox|health|server-time|release-types|clients)\/?$/
      await page.route('**/api/v1/**', async route => {
        const u = new URL(route.request().url())
        if ((!mode.fail && !mode.slow) || route.request().method() !== 'GET' || shell.test(u.pathname)) return route.continue()
        if (mode.slow) { await new Promise(r => setTimeout(r, 2500)); return route.continue() }
        await route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ title: 'QA forced failure', detail: 'QA forced failure' }) })
      })
      const report: string[] = []
      for (const s of SCREENS) {
        Object.assign(mode, { fail: false, slow: false })
        await page.goto('/overview'); await expect(page.locator('main h1').first()).toBeVisible()
        // loading
        let loading = false
        if (!['Releases', 'Orchestration'].includes(s.name)) {   // these read the shell's release list, loaded once at start
          mode.slow = true
          await s.open(page)
          const busy = page.locator('main [role="status"][aria-busy="true"]').first()
          loading = await busy.waitFor({ timeout: 2000 }).then(() => true, () => false)
          if (loading) {
            expect((await busy.innerText()).trim().length, `${s.name}: the loading state says something`).toBeGreaterThan(3)
            await axe(page, `${s.name} loading (${scheme})`)
          }
          mode.slow = false
        }
        // error, then Retry
        await page.goto('/overview'); await expect(page.locator('main h1').first()).toBeVisible()
        mode.fail = true
        if (['Releases', 'Orchestration'].includes(s.name)) await page.goto(s.path); else await s.open(page)
        const alert = page.locator('main [role="alert"], main .os-notice.danger').first()
        const shown = await alert.waitFor({ timeout: 8000 }).then(() => true, () => false)
        const retry = page.locator('main').getByRole('button', { name: /retry/i }).first()
        const hasRetry = shown && await retry.isVisible().catch(() => false)
        let recovered = false
        if (shown) {
          await axe(page, `${s.name} error (${scheme})`)
          mode.fail = false
          if (hasRetry) {
            await retry.click()
            recovered = await expect(page.locator('main [role="alert"]')).toHaveCount(0, { timeout: 10_000 }).then(() => true, () => false)
          }
        }
        mode.fail = false
        report.push(`${loading || ['Releases', 'Orchestration'].includes(s.name) ? 'ok  ' : 'MISS'} ${shown && hasRetry && recovered ? 'PASS' : 'FAIL'}  ${s.name}: loading ${loading ? 'shown' : ['Releases', 'Orchestration'].includes(s.name) ? 'n/a (shell load)' : 'NOT SEEN'}; error ${shown ? 'shown' : 'NOT SHOWN'}, Retry ${hasRetry ? 'yes' : 'no'}, ${recovered ? 'recovers' : 'does not recover'}`)
      }
      test.info().annotations.push({ type: 'states', description: report.join('\n') })
      console.log(`STATES ${scheme}\n` + report.join('\n'))
      expect(report.filter(r => r.includes('FAIL') || r.startsWith('MISS')), report.join('\n')).toEqual([])
    })

    test('empty: a workspace with no data shows every screen in words, no error, no broken numbers; axe on each', async ({ page }) => {
      const id = Date.now().toString(36)
      await signIn(page, who(`empty-${scheme}`), 'RTE')
      const made = await api(page, 'POST', '/api/v1/workspaces', { name: `Empty ${scheme} ${id}`, slug: `empty-${scheme}-${id}`, region: 'EU' })
      expect(made.status, JSON.stringify(made.body)).toBe(200)
      await page.reload(); await settle(page)
      await page.locator('aside[aria-label="Workspace"]').getByRole('button', { name: /Switch workspace/ }).click()
      await page.getByRole('menuitemradio', { name: new RegExp(`^Empty ${scheme} ${id}`) }).click()
      await expect(page.locator('aside[aria-label="Workspace"] .os-workspace-name')).toHaveText(`Empty ${scheme} ${id}`)
      const report: string[] = []
      const garbage = /\b(NaN|undefined|null|Infinity)\b|\[object|Invalid Date/
      for (const s of SCREENS) {
        await page.goto(s.path); await settle(page, s.heading)
        await page.waitForTimeout(600)
        const text = (await page.locator('main').innerText()).replace(/\s+/g, ' ')
        expect(await page.locator('main [role="alert"]').count(), `${s.name}: error in an empty workspace`).toBe(0)
        expect(text, `${s.name}: broken value in the empty state`).not.toMatch(garbage)
        await axe(page, `${s.name} empty (${scheme})`)
        const says = text.match(/\b(No [a-z]+[^.]{0,50}|Nothing[^.]{0,50}|none[^.]{0,30}|not yet[^.]{0,30}|empty)/i)
        report.push(`${says ? 'words' : 'data '} ${s.name}: ${says ? '"' + says[0].trim().slice(0, 60) + '"' : 'no empty phrase (the screen shows its own controls or settings)'}`)
      }
      test.info().annotations.push({ type: 'empty', description: report.join('\n') })
      console.log(`EMPTY ${scheme}\n` + report.join('\n'))
    })
  })
}
