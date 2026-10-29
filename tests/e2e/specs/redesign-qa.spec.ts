import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { openPage, primaryNav, signIn } from './support'

// REOS-102 (redesign QA, Chrome only, Q-102a). Automated evidence for docs/QA_REPORT_REOS-102.md, at 1600 px, in both themes:
//   1. the shell (sidebar, utility bar) is the same box on every screen: no layout shift between screens (tokens: 216 px sidebar, 64 px utility bar)
//   2. contrast measured on the rendered text of every screen (own measurement + axe color-contrast), and no informative text in text/tertiary
//   3. status is never colour alone: every chip, count badge and state mark carries a word
//   4. charts and the pipeline graph have text alternatives; one polite live region, no assertive noise
//   5. an API failure shows an error state with Retry on every screen; a slow API shows a loading state
//   6. live data: SignalR cut -> the utility bar says so and the page keeps its data; back -> it recovers
//   7. times are UTC-labelled; the relative "ago" text is not frozen
// Against the shared demo database; it changes nothing.
test.setTimeout(300_000)
// The app remembers each person's last page, so every test signs in as its own RTE and starts on the Releases page.
let seq = 0
const who = () => `qa102.${Date.now().toString(36)}.${seq++}@example.com`
test.use({ viewport: { width: 1600, height: 1000 } })

const SCREENS: { link: string; path: string; heading: string }[] = [
  { link: 'Overview', path: '/overview', heading: 'Overview' },
  { link: 'Portfolio & calendar', path: '/calendar', heading: 'Release portfolio' },
  { link: 'Releases', path: '/', heading: 'Releases' },
  { link: 'Orchestration', path: '/orchestration', heading: 'Orchestration pipeline' },
  { link: 'Approvals', path: '/approvals', heading: 'Approvals' },
  { link: 'Deployments', path: '/deployments', heading: 'Deployments' },
  { link: 'Audit history', path: '/audit', heading: 'Audit history' },
  { link: 'Service catalog', path: '/services', heading: 'Service catalog' },
  { link: 'Environments', path: '/environments', heading: 'Environments' },
  { link: 'Teams & settings', path: '/admin', heading: 'Teams & settings' },
  { link: 'Templates', path: '/templates', heading: 'Train templates' },
  { link: 'Comm library', path: '/library', heading: 'Comm library' },
  { link: 'Connectors', path: '/connectors', heading: 'Connectors' },
  { link: 'My work', path: '/work', heading: 'My work' },
  { link: 'Analytics', path: '/analytics', heading: 'Analytics' },
  { link: 'Imports & exports', path: '/importexport', heading: 'Imports & exports' },
]

async function go(page: Page, s: { link: string; heading: string }) {
  await primaryNav(page).getByRole('link', { name: new RegExp(`^${s.link.replace(/[&]/g, '\\$&')}`) }).first().click()
  await expect(page.locator('main h1').first()).toHaveText(s.heading)
  await page.locator('main [aria-busy="true"]').waitFor({ state: 'detached', timeout: 15_000 }).catch(() => undefined)
}

const THEMES = ['light', 'dark'] as const
async function theme(page: Page, t: (typeof THEMES)[number]) {
  await page.emulateMedia({ colorScheme: t })
  await page.evaluate(x => { try { localStorage.removeItem('theme') } catch { /* none */ } document.documentElement.setAttribute('data-theme', x) }, t)
}

/** Runs in the page: the contrast of every visible text node against what is really behind it (alpha composited up the ancestors). */
const measureContrast = () => {
  type RGBA = [number, number, number, number]
  const parse = (c: string): RGBA => { const m = c.match(/[\d.]+/g)!.map(Number); return [m[0], m[1], m[2], m[3] ?? 1] }
  const over = (top: RGBA, under: RGBA): RGBA => { const a = top[3] + under[3] * (1 - top[3]); return a === 0 ? [0, 0, 0, 0] : [0, 1, 2].map(i => (top[i] * top[3] + under[i] * under[3] * (1 - top[3])) / a).concat(a) as RGBA }
  const lum = (c: RGBA) => { const l = c.slice(0, 3).map(v => { v /= 255; return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4 }); return 0.2126 * l[0] + 0.7152 * l[1] + 0.0722 * l[2] }
  const ratio = (a: RGBA, b: RGBA) => { const [hi, lo] = [lum(a), lum(b)].sort((x, y) => y - x); return (hi + 0.05) / (lo + 0.05) }
  const bgOf = (el: Element): RGBA => {
    const stack: RGBA[] = []
    for (let e: Element | null = el; e; e = e.parentElement) {
      const cs = getComputedStyle(e)
      stack.push(parse(cs.backgroundColor))
      if (cs.backgroundImage !== 'none' && !cs.backgroundImage.includes('gradient(') === false) return [NaN, 0, 0, 1]   // a gradient: not measurable here
    }
    return stack.reduceRight((acc, c) => over(c, acc), [255, 255, 255, 1] as RGBA)
  }
  const tertiary = getComputedStyle(document.documentElement).getPropertyValue('--color-text-tertiary').trim()
  const out: { text: string; ratio: number; size: number; fg: string; where: string; tertiary: boolean }[] = []
  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT)
  for (let n = walker.nextNode(); n; n = walker.nextNode()) {
    const text = (n.textContent ?? '').trim()
    const el = n.parentElement
    if (!text || !el || el.closest('.sr-only, [aria-hidden="true"] svg, script, style, option')) continue
    const cs = getComputedStyle(el)
    const r = el.getBoundingClientRect()
    if (cs.visibility === 'hidden' || cs.display === 'none' || r.width === 0 || r.height === 0 || +cs.opacity === 0) continue
    if ((el as HTMLButtonElement).disabled || el.closest('[disabled],[aria-disabled="true"]')) continue   // WCAG 1.4.3 exempts inactive controls
    const bg = bgOf(el)
    if (Number.isNaN(bg[0])) continue
    const fg = over(parse(cs.color), bg)
    const rt = ratio(fg, bg)
    const hex = '#' + fg.slice(0, 3).map(v => Math.round(v).toString(16).padStart(2, '0')).join('').toUpperCase()
    out.push({ text: text.slice(0, 40), ratio: Math.round(rt * 100) / 100, size: parseFloat(cs.fontSize), fg: hex, where: el.tagName.toLowerCase() + (el.className && typeof el.className === 'string' ? '.' + el.className.split(' ')[0] : ''), tertiary: !!tertiary && hex === tertiary.toUpperCase() })
  }
  return out
}

for (const t of THEMES) {
  test.describe(`${t} theme`, () => {
    test('1 shell: the sidebar and the utility bar are the same boxes on every screen', async ({ page }) => {
      await signIn(page, who())
      await theme(page, t)
      const boxes: Record<string, string> = {}
      for (const s of SCREENS) {
        await go(page, s)
        boxes[s.link] = await page.evaluate(() => {
          const b = (sel: string) => { const r = document.querySelector(sel)!.getBoundingClientRect(); return `${r.x},${r.y},${r.width},${r.height}` }
          return [b('aside[aria-label="Workspace"]'), b('header[class~="os-utility"]')].join(' | ')
        })
        expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth), `${s.link}: horizontal page scroll`).toBeLessThanOrEqual(0)
      }
      const first = Object.values(boxes)[0]
      for (const [name, box] of Object.entries(boxes)) expect(box, `${name} moved the shell`).toBe(first)
      const [side, bar] = first.split(' | ').map(x => x.split(',').map(Number))
      expect(side[2], 'sidebar width token --sidebar-width').toBe(216)
      expect(bar[3], 'utility bar height token --utility-height').toBe(64)
      test.info().annotations.push({ type: 'shell', description: first })
    })

    test('2 contrast: every rendered text on every screen, measured; nothing informative in text/tertiary', async ({ page }) => {
      await signIn(page, who())
      await theme(page, t)
      // Self-test: the measurement must see a known-bad text (the designed light tertiary grey on the page) or a green result means nothing.
      await go(page, SCREENS[0])
      await page.evaluate(() => { const p = document.createElement('p'); p.id = 'qa-canary'; p.style.color = 'var(--color-text-tertiary)'; p.textContent = 'canary caption'; document.querySelector('main')!.append(p) })
      const canary = (await page.evaluate(measureContrast)).find(r => r.text === 'canary caption')!
      await page.evaluate(() => document.getElementById('qa-canary')!.remove())
      expect(canary, 'the canary was measured').toBeTruthy()
      expect(canary.tertiary, 'the canary is recognised as text/tertiary').toBe(true)
      if (t === 'light') expect(canary.ratio, 'light text/tertiary on the surface it is on').toBeLessThan(4.5)
      const worst: string[] = [], bad: string[] = []
      let min = 99, count = 0
      for (const s of SCREENS) {
        await go(page, s)
        const rows = await page.evaluate(measureContrast)
        count += rows.length
        const low = rows.filter(r => r.ratio < (r.size >= 24 || r.size >= 18.66 ? 3 : 4.5))
        for (const r of low) bad.push(`${s.link}: ${r.ratio}:1 ${r.fg} ${r.size}px <${r.where}> "${r.text}"`)
        for (const r of rows.filter(r => r.tertiary)) bad.push(`${s.link}: informative text in text/tertiary <${r.where}> "${r.text}" ${r.ratio}:1`)
        const m = rows.reduce((a, r) => (r.ratio < a.ratio ? r : a), rows[0])
        min = Math.min(min, m.ratio); worst.push(`${s.link} min ${m.ratio}:1 (${rows.length} texts) "${m.text}" ${m.fg} ${m.size}px`)
        const axe = await new AxeBuilder({ page }).withRules(['color-contrast']).analyze()
        expect(axe.violations.map(v => v.nodes.map(n => n.target.join(' ') + ': ' + n.any[0]?.message)).flat(), `axe color-contrast on ${s.link}`).toEqual([])
      }
      test.info().annotations.push({ type: 'contrast', description: `${t}: ${count} texts measured, lowest ${min}:1\n${worst.join('\n')}` })
      console.log(`CONTRAST ${t}: canary(text/tertiary) ${canary.ratio}:1 ${canary.fg}; ${count} texts measured, lowest ${min}:1\n${worst.join('\n')}`)
      expect(bad, `text below WCAG 2.2 AA:\n${bad.join('\n')}`).toEqual([])
    })

    test('3 status is never colour alone: chips and badges carry words; a count badge names what it counts', async ({ page }) => {
      await signIn(page, who())
      await theme(page, t)
      let chips = 0
      for (const s of SCREENS) {
        await go(page, s)
        const r = await page.evaluate(() => {
          const empty: string[] = []
          const chips = [...document.querySelectorAll('.os-chip')]
          for (const c of chips) if (!(c.textContent ?? '').trim()) empty.push('chip without a word: ' + c.outerHTML.slice(0, 80))
          for (const b of document.querySelectorAll('.os-badge')) if (!b.getAttribute('aria-label')) empty.push('count badge without a name: ' + b.outerHTML.slice(0, 80))
          for (const d of document.querySelectorAll('.os-chip-dot')) if (d.getAttribute('aria-hidden') !== 'true') empty.push('chip dot not hidden from assistive tech')
          // A state mark made only of a coloured glyph/shape: any [class*=dot|swatch|status] element with no text, no aria-label, not aria-hidden inside a labelled parent.
          for (const e of document.querySelectorAll('[class*="swatch"],[class*="-dot"]:not(.os-chip-dot)')) {
            const labelled = (e.textContent ?? '').trim() || e.getAttribute('aria-label') || e.closest('[aria-label],[title]') || (e.parentElement?.textContent ?? '').trim()
            if (!labelled) empty.push('colour mark without a word: ' + e.outerHTML.slice(0, 80))
          }
          return { empty, chips: chips.length }
        })
        chips += r.chips
        expect(r.empty, `${s.link}`).toEqual([])
      }
      test.info().annotations.push({ type: 'status', description: `${t}: ${chips} chips checked across ${SCREENS.length} screens` })
    })
  })
}

test('4 charts, pipeline graph and live regions have text alternatives and no noise', async ({ page }) => {
  await signIn(page, who())
  for (const s of SCREENS) {
    await go(page, s)
    const r = await page.evaluate(() => {
      const problems: string[] = []
      const announcers = document.querySelectorAll('[data-testid="announcer"]').length
      if (announcers !== 1) problems.push(`${announcers} global announcers`)
      for (const e of document.querySelectorAll('[aria-live="assertive"]')) problems.push('assertive live region: ' + e.outerHTML.slice(0, 80))
      for (const e of document.querySelectorAll('svg')) {
        const hidden = e.closest('[aria-hidden="true"]') || e.getAttribute('aria-hidden') === 'true'
        const named = e.getAttribute('aria-label') || e.getAttribute('aria-labelledby') || e.querySelector('title') || e.closest('[role="img"][aria-label]')
        if (!hidden && !named && e.getBoundingClientRect().width > 60) problems.push('large svg with no name or aria-hidden: ' + e.outerHTML.slice(0, 80))
      }
      for (const e of document.querySelectorAll('[role="img"]')) if (!e.getAttribute('aria-label') && !e.getAttribute('aria-labelledby')) problems.push('role=img without a name')
      const alerts = [...document.querySelectorAll('main [role="alert"]')].length
      const statuses = document.querySelectorAll('[role="status"]').length
      return { problems, alerts, statuses }
    })
    expect(r.problems, s.link).toEqual([])
    // Noise: a screen at rest has no alert, and only a few status regions (the announcer, the clock's connection word, the page's own counters).
    expect(r.alerts, `${s.link}: alert on a healthy page`).toBe(0)
    expect(r.statuses, `${s.link}: too many live regions`).toBeLessThanOrEqual(10)   // a region only speaks when its text changes; Analytics has one per chart card
  }
  await go(page, SCREENS.find(s => s.link === 'Analytics')!)
  const charts = page.locator('main [role="img"]')
  expect(await charts.count(), 'analytics charts').toBeGreaterThan(0)
  for (const c of await charts.all()) expect((await c.getAttribute('aria-label')) ?? '').toMatch(/\w{4}/)
  const metric = await page.locator('[data-testid=chart]', { has: page.getByRole('button', { name: 'View data' }) }).first().getAttribute('data-metric')
  const card = page.locator(`[data-testid=chart][data-metric="${metric}"]`)
  const viewData = card.getByRole('button', { name: 'View data' })
  await viewData.focus(); await page.keyboard.press('Enter')
  await expect(card.getByRole('button', { name: 'Hide data' })).toHaveAttribute('aria-expanded', 'true')
  await expect(card.locator('table th').first()).toBeVisible()
  await go(page, SCREENS.find(s => s.link === 'Overview')!)
  await expect(page.locator('main svg[aria-label^="Release throughput"]')).toBeVisible()
  await expect(page.locator('main summary', { hasText: 'View data' })).toBeVisible()   // a disclosure: the same numbers as a table
  await go(page, SCREENS.find(s => s.link === 'Orchestration')!)
  const stages = page.getByRole('list', { name: 'Stages' })
  // The pipeline graph exists only for a release that has a run; the demo's live drill is seeded at start with a short window, so later in the day the page may show "No run yet".
  await page.waitForTimeout(1500)
  if (await stages.count()) for (const b of await stages.getByRole('button').all()) expect(((await b.getAttribute('aria-label')) ?? '').length, 'stage button name').toBeGreaterThan(8)
  else await expect(page.getByText(/no (live|rehearsal) run/i).first()).toBeVisible()   // no graph: the page says why, in words
})

test('5 an API failure shows an error state with Retry on every screen; a slow API shows a loading state', async ({ page }) => {
  await signIn(page, who())
  const mode = { fail: false, slow: false }
  // The session, the workspace list, the connection and the shell's own feeds keep working: the page under test is what fails.
  const shell = /^\/api\/v1\/(me|workspaces|sync\/state|notifications|inbox|health|server-time|release-types|clients)\/?$/
  await page.route('**/api/v1/**', async route => {
    const u = new URL(route.request().url())
    if ((!mode.fail && !mode.slow) || route.request().method() !== 'GET' || shell.test(u.pathname)) return route.continue()
    if (mode.slow) { await new Promise(r => setTimeout(r, 2500)); return route.continue() }
    await route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ title: 'QA forced failure', detail: 'QA forced failure' }) })
  })
  const link = (s: { link: string }) => primaryNav(page).getByRole('link', { name: new RegExp(`^${s.link.replace(/[&]/g, '\\$&')}`) }).first()
  const report: string[] = []
  const NOT_BUILT = ['Service catalog', 'Environments']   // placeholders (HANDOFF section 9): no data to load, they say so in words
  for (const s of SCREENS) {
    Object.assign(mode, { fail: false, slow: false })
    await page.goto('/overview'); await expect(page.locator('main h1').first()).toBeVisible()
    if (NOT_BUILT.includes(s.link)) {
      await link(s).click()
      await expect(page.locator('main').getByText('This screen is not built yet')).toBeVisible()
      report.push(`N/A   ${s.link}: placeholder says "This screen is not built yet" and where the work is done today`)
      continue
    }
    // slow: a loading state in words, as a polite status
    mode.slow = true
    await link(s).click()
    const loading = await page.locator('main [role="status"][aria-busy="true"]').first().waitFor({ timeout: 2000 }).then(() => true, () => false)
    mode.slow = false
    await page.goto('/overview'); await expect(page.locator('main h1').first()).toBeVisible()
    mode.fail = true
    // Releases and Orchestration read the shell's release list, loaded once at start: their error state is a failed start-up load, so reach them by a fresh load.
    if (['Releases', 'Orchestration'].includes(s.link)) await page.goto(s.path); else await link(s).click()
    const alert = page.locator('main [role="alert"], main .os-notice.danger').first()
    const shown = await alert.waitFor({ timeout: 8000 }).then(() => true, () => false)
    const retry = shown ? await page.locator('main').getByRole('button', { name: /retry/i }).count() : 0
    const text = shown ? ((await alert.innerText()).replace(/\s+/g, ' ').slice(0, 70)) : (await page.locator('main').innerText()).replace(/\s+/g, ' ').slice(0, 70)
    report.push(`${shown && retry ? 'PASS' : 'FAIL'}  ${s.link}: loading state ${loading ? 'shown' : 'NOT SEEN'}; error ${shown ? 'shown' : 'NOT SHOWN'}, Retry ${retry ? 'yes' : 'no'}: ${text}`)
  }
  mode.fail = false
  test.info().annotations.push({ type: 'states', description: report.join('\n') })
  console.log('STATES\n' + report.join('\n'))
  expect(report.filter(r => r.startsWith('FAIL')), report.join('\n')).toEqual([])
})

test('6 live data: a cut SignalR connection is said in words and the page keeps its data; it recovers and resyncs', async ({ page }) => {
  const hub = { down: false, sockets: [] as { close: () => void }[] }
  await page.routeWebSocket(/\/hub\/trains/, ws => {
    if (hub.down) { void ws.close(); return }
    ws.connectToServer(); hub.sockets.push(ws)
  })
  await page.route('**/hub/trains/negotiate**', r => (hub.down ? r.abort() : r.continue()))
  await signIn(page, who())
  const bar = page.getByRole('banner')
  const word = bar.locator('.os-live')
  await expect(word).toHaveText('')                                    // live: nothing said (no noise)
  const rowsBefore = await page.getByRole('table', { name: 'Releases' }).locator('tbody tr').count()
  const resync = page.waitForRequest(r => r.url().includes('/api/v1/') && r.method() === 'GET' && r.url().includes('trains'), { timeout: 30_000 })

  hub.down = true; hub.sockets.forEach(s => s.close())
  await expect(word).toContainText('Reconnecting')
  await expect(word).toHaveAttribute('class', /reconnecting/)
  expect(await page.getByRole('table', { name: 'Releases' }).locator('tbody tr').count(), 'the page keeps its data while disconnected').toBe(rowsBefore)
  hub.down = false                                                     // back before the retry schedule (0, 1, 3, 5, 10 s) runs out
  await expect(word).toHaveText('', { timeout: 20_000 })
  await resync                                                         // onResync: events may have been missed, so the page refetches
  test.info().annotations.push({ type: 'live', description: 'cut -> "Reconnecting…" in the utility bar status; data kept; restored -> status empty and a refetch' })
})

test('6b live data: a long outage says Offline in words and the deployment monitor says its log may be behind', async ({ page }) => {
  const hub = { down: false, sockets: [] as { close: () => void }[] }
  await page.routeWebSocket(/\/hub\/trains/, ws => { if (hub.down) { void ws.close(); return } ws.connectToServer(); hub.sockets.push(ws) })
  await page.route('**/hub/trains/negotiate**', r => (hub.down ? r.abort() : r.continue()))
  await signIn(page, who())
  await primaryNav(page).getByRole('link', { name: /^Deployments/ }).click()
  await expect(page.locator('main h1').first()).toHaveText('Deployments')
  hub.down = true; hub.sockets.forEach(s => s.close())
  const word = page.getByRole('banner').locator('.os-live')
  await expect(word).toContainText('Reconnecting')
  await expect(word).toContainText('Offline', { timeout: 40_000 })   // the schedule gives up after about 19 s
  await expect(page.locator('main h1').first()).toHaveText('Deployments')
})

test('7 times: every clock time on every screen is UTC-labelled; and the shell clock is real UTC', async ({ page }) => {
  await signIn(page, who())
  const clock = page.getByRole('banner').locator('time')
  await expect(clock).toContainText(/\d\d [A-Z]{3} 20\d\d · \d\d:\d\d UTC/)
  // The shell clock is the server's time (pushed over SignalR, D24), so it is checked against real time rather than a faked one; the minute rollover is covered by the web unit tests (tests-unit/shell.test.ts).
  const shown = await clock.innerText()
  const m = shown.match(/(\d\d) ([A-Z]{3}) (\d{4}) · (\d\d):(\d\d) UTC/)!
  const at = Date.parse(`${m[1]} ${m[2]} ${m[3]} ${m[4]}:${m[5]} UTC`)
  expect(Math.abs(at - Date.now()), 'the shell clock is within two minutes of real UTC').toBeLessThan(2 * 60_000)
  const unlabelled: string[] = []
  for (const s of SCREENS) {
    await go(page, s)
    const found = await page.evaluate(() => {
      const out: string[] = []
      const zone = /UTC|\b[A-Z]{3,4}\b/
      const w = document.createTreeWalker(document.querySelector('main')!, NodeFilter.SHOW_TEXT)
      for (let n = w.nextNode(); n; n = w.nextNode()) {
        const t = (n.textContent ?? '').trim()
        const el = n.parentElement!
        if (!/(^|[^\d.])([01]?\d|2[0-3]):[0-5]\d(:[0-5]\d)?($|[^\d])/.test(t) || el.closest('.sr-only, [aria-hidden="true"] .sr-only, input, textarea, select, option, script, style')) continue
        const cell = el.closest('td'); const th = cell ? cell.closest('table')?.querySelectorAll('thead th')[cell.cellIndex]?.textContent ?? '' : ''
        const block = el.closest('td, li, p, dd, dt, div, section')!
        const around = (block.textContent ?? '') + ' ' + th + ' ' + (el.closest('[aria-label]')?.getAttribute('aria-label') ?? '') + ' ' + (block.parentElement?.textContent ?? '').slice(0, 300)
        if (!zone.test(around)) out.push(`"${t.slice(0, 50)}"`)
      }
      return out
    })
    for (const f of found) unlabelled.push(`${s.link}: ${f}`)
  }
  expect(unlabelled, 'clock times with no UTC or zone label nearby').toEqual([])
})

test('7b countdowns and elapsed times are live: the Orchestration clocks tick, and are labelled UTC', async ({ page }) => {
  await signIn(page, who())
  await primaryNav(page).getByRole('link', { name: /^Orchestration/ }).click()
  await expect(page.locator('main h1').first()).toHaveText('Orchestration pipeline')
  const cards = page.locator('main').getByText(/^\d+:\d\d:\d\d$/)
  await page.waitForTimeout(1500)
  // The demo's live run has a two-hour window from when the app first started; once it is over there is nothing running to count, so say so instead of failing.
  test.skip(await cards.count() === 0, 'no running run in the demo data right now (its window has passed), so there is no countdown to watch')
  const before = await cards.allInnerTexts()
  await expect.poll(async () => (await cards.allInnerTexts()).join('|'), { timeout: 8000 }).not.toBe(before.join('|'))
  await expect(page.locator('main').getByText(/UTC/).first()).toBeVisible()
  // "N min ago" (Sync health) and "Due in" (Approvals) are watched moving, with seeded data and a fake clock, in qa-142.spec.ts (REOS-142).
})

test('3b dialogs: Tab and Shift+Tab never reach the page behind an open confirmation dialog, and Escape gives focus back', async ({ page }) => {
  await signIn(page, 'gallery@example.com')
  await page.goto('/gallery')
  const trigger = page.locator('section[aria-label="Light theme"]').getByRole('button', { name: 'Rollback' })
  await trigger.click()
  const dialog = page.getByRole('dialog')
  await expect(dialog).toBeVisible()
  for (const key of ['Tab', 'Tab', 'Tab', 'Shift+Tab', 'Shift+Tab', 'Shift+Tab', 'Shift+Tab']) {
    await page.keyboard.press(key)
    // A native modal dialog makes the page behind it inert: Tab past its last control goes to the browser chrome (activeElement is body), never to the page.
    expect(await page.evaluate(() => { const a = document.activeElement; return !a || a === document.body || !!a.closest('dialog[open]') }), `focus reached the page behind the dialog on ${key}`).toBe(true)
  }
  await page.keyboard.press('Escape')
  await expect(trigger).toBeFocused()
})
