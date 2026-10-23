import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { openPage, signIn, trainByTitle } from './support'

// REOS-53 (M8): accessibility pass over EVERY view in route.ts, in light and dark, driven and checked from the keyboard.
//   1. Keyboard-only tour: from the top of the page Tab to each primary-navigation link and press Enter, in navigation order; the view must open,
//      and axe must find no serious or critical violation on it.
//   2. On each view, Tab through the whole page: every stop has an accessible name, a visible focus indicator and a box, and focus is never trapped.
//   3. The train workspace in Plan, Rehearsal and Live mode, and its two right-hand drawers (Communicate, Paste tasks), opened from the keyboard.
// Read-only against the shared demo database: it signs in as its own users and changes nothing.
test.setTimeout(240_000)

const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']

async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  const detail = bad.map(v => `${v.id} (${v.impact}): ${v.help}\n   ${v.nodes.slice(0, 4).map(n => n.target.join(' ')).join('\n   ')}`).join('\n')
  expect(bad, `axe violations on ${where}:\n${detail}`).toEqual([])
}

/**
 * The sidebar's Primary navigation in DOM order (REOS-94, shell/nav.ts; an RTE sees every item), with what each page shows when it is ready. Sync health
 * (the control-plane card) and the Inbox (the bell) are reached outside the list and checked after the tour.
 */
const h1 = (text: string) => (p: Page) => expect(p.locator('main h1').first()).toHaveText(text)
const VIEWS: { link: RegExp; ready: (p: Page) => Promise<void>; name: string }[] = [
  { name: 'overview', link: /^Overview$/, ready: h1('Overview') },
  { name: 'calendar', link: /^Portfolio & calendar$/, ready: h1('Calendar') },
  { name: 'trains', link: /^Releases$/, ready: p => expect(p.locator('.stream-row').first()).toBeVisible() },
  { name: 'orchestration', link: /^Orchestration$/, ready: h1('Orchestration') },
  { name: 'approvals', link: /^Approvals/, ready: h1('Approvals') },
  { name: 'deployments', link: /^Deployments$/, ready: h1('Deployments') },
  { name: 'audit', link: /^Audit history$/, ready: h1('Audit') },
  { name: 'services', link: /^Service catalog$/, ready: h1('Service catalog') },
  { name: 'environments', link: /^Environments$/, ready: h1('Environments') },
  { name: 'admin', link: /^Teams & settings$/, ready: h1('Admin') },
  { name: 'templates', link: /^Templates$/, ready: h1('Train templates') },
  { name: 'library', link: /^Comm library$/, ready: h1('Comm library') },
  { name: 'connectors', link: /^Connectors$/, ready: h1('Connectors') },
  { name: 'work', link: /^My work$/, ready: h1('My work') },
  { name: 'analytics', link: /^Analytics$/, ready: h1('Analytics') },
  { name: 'importexport', link: /^Imports & exports$/, ready: h1('Imports & exports') },
]
const byName = (n: string) => VIEWS.find(v => v.name === n)!

const nameOf = () => {
  const el = document.activeElement as HTMLElement | null
  if (!el || el === document.body) return ''
  return (el.getAttribute('aria-label') || el.getAttribute('aria-labelledby') && document.getElementById(el.getAttribute('aria-labelledby')!)?.textContent || el.textContent || (el as HTMLInputElement).placeholder || el.getAttribute('title') || '').replace(/\s+/g, ' ').trim()
}

/** Press Tab until the focused element's name matches (a keyboard user reaching a control); fails after `max` presses. */
async function tabTo(page: Page, name: RegExp, max = 80) {
  for (let i = 0; i < max; i++) {
    await page.keyboard.press('Tab')
    if (name.test(await page.evaluate(nameOf))) return
  }
  throw new Error(`Tab never reached ${name} in ${max} presses`)
}

interface Stop { tag: string; name: string; hasName: boolean; visibleIndicator: boolean; hasBox: boolean; key: string }

/** Tab through the page from the top; returns every stop until focus leaves the document or comes round to the first stop. */
async function tabThrough(page: Page, max = 260): Promise<Stop[]> {
  // Start at the very top of the document: a view that moves focus to its heading on open (App.tsx) would otherwise start the walk mid-page.
  await page.evaluate(() => {
    (document.activeElement as HTMLElement | null)?.blur(); window.scrollTo(0, 0)
    let top = document.getElementById('tab-walk-start')
    if (!top) { top = document.createElement('span'); top.id = 'tab-walk-start'; top.tabIndex = -1; document.body.prepend(top) }
    top.focus({ preventScroll: true })
  })
  const stops: Stop[] = []
  for (let i = 0; i < max; i++) {
    await page.keyboard.press('Tab')
    const s = await page.evaluate((): Stop | null => {
      const el = document.activeElement as HTMLElement | null
      if (!el || el === document.body || el === document.documentElement) return null
      const cs = getComputedStyle(el), r = el.getBoundingClientRect()
      const outline = cs.outlineStyle !== 'none' && parseFloat(cs.outlineWidth) > 0
      const inputRule = el.matches('input.line, textarea.line') && parseFloat(cs.borderBottomWidth) >= 2
      const own = outline || cs.boxShadow !== 'none' || inputRule || el.matches('g.tl-gate')   // the timeline gate underlines its name instead (app.css)
      const label = (el.getAttribute('aria-label') || el.textContent || (el as HTMLInputElement).placeholder || el.getAttribute('title') || (el as HTMLInputElement).labels?.[0]?.textContent || '').replace(/\s+/g, ' ').trim()
      const path = `${el.tagName.toLowerCase()}${el.id ? '#' + el.id : ''}${el.className && typeof el.className === 'string' ? '.' + el.className.trim().split(/\s+/).join('.') : ''}`
      return { tag: el.tagName.toLowerCase(), name: label.slice(0, 40), hasName: label.length > 0, visibleIndicator: own, hasBox: r.width > 0 && r.height > 0, key: `${path}|${label.slice(0, 40)}|${Math.round(r.x)},${Math.round(r.y + window.scrollY)}` }
    })
    if (!s) break                                   // left the page (browser chrome) - a full cycle
    if (stops.length > 2 && s.key === stops[0].key) break   // came round to the start
    stops.push(s)
  }
  return stops
}

function checkStops(stops: Stop[], where: string) {
  expect(stops.length, `${where}: keyboard reached only ${stops.length} controls`).toBeGreaterThanOrEqual(3)
  const problems = [
    ...stops.filter(s => !s.hasName).map(s => `no accessible name: ${s.key}`),
    ...stops.filter(s => !s.visibleIndicator).map(s => `no visible focus indicator: ${s.key}`),
    ...stops.filter(s => !s.hasBox).map(s => `focused but has no box: ${s.key}`),
  ]
  expect(problems, `${where}:\n${problems.slice(0, 12).join('\n')}`).toEqual([])
  // a trap shows as the same few elements over and over
  const tail = stops.slice(-40).map(s => s.key)
  if (stops.length >= 60) expect(new Set(tail).size, `${where}: focus is cycling among ${new Set(tail).size} elements (keyboard trap?)`).toBeGreaterThan(3)
}

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`${scheme} theme`, () => {
    test.use({ colorScheme: scheme })

    test('keyboard-only tour of every view: open with Tab and Enter, axe clean, whole page reachable', async ({ page }) => {
      await signIn(page, `a11y-tour-${scheme}@example.com`)   // RTE: sees every nav item
      await expect(page.locator('nav[aria-label="Primary"] a')).toHaveCount(VIEWS.length)
      await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur())
      for (const v of VIEWS) {
        await tabTo(page, v.link)
        await page.keyboard.press('Enter')
        await v.ready(page)
        await expect(page.locator('nav[aria-label="Primary"] a[aria-current="page"]')).toHaveText(v.link)
        await scan(page, `${v.name} (${scheme})`)
        // the page just opened is fully reachable: walk it, then return focus to where the tour continues (the same nav link)
        checkStops(await tabThrough(page), `${v.name} (${scheme}) tab order`)
        await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur())
        await page.locator('nav[aria-label="Primary"] a[aria-current="page"]').focus()   // continue the keyboard tour from this link: Tab moves on to the next
      }
      // Sync health from the control-plane card, then the Inbox from the bell, from the keyboard as well
      for (const [name, link, heading] of [['sync', /Open Sync health/, 'Sync health'], ['inbox', /^Inbox/, 'Inbox']] as const) {
        await tabTo(page, link)
        await page.keyboard.press('Enter')
        await expect(page.locator('main h1').first()).toHaveText(heading)
        await scan(page, `${name} (${scheme})`)
        checkStops(await tabThrough(page), `${name} (${scheme}) tab order`)
        await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur())
      }
    })

    test('train workspace in Plan, Rehearsal and Live mode, gate Inspector and both drawers', async ({ page }) => {
      await signIn(page, `a11y-train-${scheme}@example.com`)
      const t = await trainByTitle(page, 'Card portal')
      const gate = t.gates.find(g => g.name === 'Compliance Sign-off')!
      for (const mode of ['', '/rehearsal', '/live']) {
        await openPage(page, `/trains/${t.id}${mode}`)
        await expect(page.locator('.stream-row').first()).toBeVisible()
        await page.waitForTimeout(400)   // let the readiness line, timeline and run data settle
        await scan(page, `train ${mode || 'plan'} (${scheme})`)
        checkStops(await tabThrough(page), `train ${mode || 'plan'} (${scheme}) tab order`)
      }

      // gate Inspector, then "Paste tasks" (the bulk drawer) opened with the keyboard
      await page.goto(`/trains/${t.id}/gates/${gate.id}`)
      await expect(page.locator('.inspector h2')).toHaveText('Compliance Sign-off')
      await scan(page, `gate inspector (${scheme})`)
      checkStops(await tabThrough(page), `gate inspector (${scheme}) tab order`)
      const paste = page.getByRole('button', { name: 'Paste tasks' })
      await paste.focus(); await page.keyboard.press('Enter')
      await expect(page.locator('.inspector')).toContainText(/paste|parse|line/i)
      await page.waitForTimeout(300)
      await scan(page, `bulk drawer (${scheme})`)
      checkStops(await tabThrough(page), `bulk drawer (${scheme}) tab order`)
      await page.keyboard.press('Escape')

      // Communicate drawer
      await page.goto(`/trains/${t.id}`)
      const communicate = page.getByRole('button', { name: 'Communicate' })
      await communicate.focus(); await page.keyboard.press('Enter')
      await expect(communicate).toHaveAttribute('aria-expanded', 'true')
      await page.waitForTimeout(500)
      await scan(page, `comms drawer (${scheme})`)
      checkStops(await tabThrough(page), `comms drawer (${scheme}) tab order`)
    })
  })
}

// The manual override (data-theme on <html>, UI.md) is a different code path from prefers-color-scheme: choose it with the keyboard and scan again.
for (const [os, pick] of [['light', 'Dark'], ['dark', 'Light']] as const) {
  test(`the ${pick} appearance choice, made from the keyboard on an OS set to ${os}, is axe clean`, async ({ browser }) => {
    const ctx = await browser.newContext({ colorScheme: os })
    const page = await ctx.newPage()
    await signIn(page, `a11y-theme-${pick.toLowerCase()}@example.com`)
    await tabTo(page, /account and appearance/)   // the user card opens the account panel (REOS-94)
    await page.keyboard.press('Enter')
    await tabTo(page, new RegExp(`^${pick}$`))
    await page.keyboard.press('Enter')
    await expect(page.locator('html')).toHaveAttribute('data-theme', pick.toLowerCase())
    await expect(page.getByRole('button', { name: pick, exact: true })).toHaveAttribute('aria-pressed', 'true')
    for (const v of [byName('trains'), byName('analytics'), byName('approvals')]) {   // Releases, Analytics (charts), Approvals (the redesign's page)
      await page.locator('nav[aria-label="Primary"]').getByRole('link', { name: v.link }).click()
      await v.ready(page)
      await page.waitForTimeout(300)
      await scan(page, `${v.name} with ${pick} chosen over an OS in ${os} mode`)
    }
    await ctx.close()
  })
}
