import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { openTab, signIn, trainByTitle } from './support'

// REOS-97 / REOS-126 / REOS-127: the release detail page in the redesign's tab structure, bound to the real data.
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']
const TABS = ['Overview & readiness', 'Services', 'Dependencies', 'Approvals', 'Activity']

async function drill(page: Page) {
  return page.evaluate(async () => (await fetch('/api/v1/dev/live-drill', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' })).json()) as Promise<{ trainId: string }>
}
/** Records a Go/No-Go decision through the API as the signed-in Release Manager. */
async function record(page: Page, trainId: string, decision: string, notes: string, conditions: { text: string; ownerUserId: string; expiresAt: string }[] = []) {
  const res = await page.evaluate(async ([id, d, n, c]) => {
    const t = await (await fetch(`/api/v1/trains/${id}`)).json() as { version: number }
    const r = await fetch(`/api/v1/trains/${id}/gonogo`, { method: 'POST', headers: { 'Content-Type': 'application/json', 'If-Match': String(t.version) }, body: JSON.stringify({ decision: d, notes: n, newTargetReleaseDate: null, conditions: c }) })
    return { ok: r.ok, status: r.status, text: await r.text() }
  }, [trainId, decision, notes, conditions] as const)
  expect(res.ok, `record ${decision}: ${res.status} ${res.text}`).toBe(true)
}
const scan = async (page: Page, where: string) => {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  expect(bad.map(v => `${v.id}: ${v.nodes[0]?.target.join(' ')}`), `axe violations on ${where}`).toEqual([])
}

test('the release page has the design\'s five tabs, operable from the keyboard, and the counts match the lists under them', async ({ page }) => {
  await signIn(page, 'rd-tabs@example.com', 'RTE')
  const t = await trainByTitle(page, 'Card portal')
  await page.goto(`/trains/${t.id}`)
  await expect(page.getByRole('tab')).toHaveText([/^Overview & readiness$/, /^Services\s*\d+$/, /^Dependencies\s*\d+$/, /^Approvals\s*\d+$/, /^Activity$/])
  const count = async (name: string) => Number((await page.getByRole('tab', { name: new RegExp(`^${name}`) }).innerText()).replace(/\D/g, ''))

  const [services, deps, approvals] = [await count('Services'), await count('Dependencies'), await count('Approvals')]
  await openTab(page, 'Services')
  await expect(page.getByRole('table', { name: 'Service manifest' }).locator('tbody tr')).toHaveCount(services)
  await openTab(page, 'Dependencies')
  expect(await page.getByRole('table', { name: 'Overlapping windows' }).locator('tbody tr:not(:has(.os-table-empty))').count()).toBe(deps)
  await openTab(page, 'Approvals')
  expect(await page.getByRole('table', { name: 'Open decisions' }).locator('tbody tr:not(:has(.os-table-empty))').count()).toBe(approvals)

  // keyboard: one tab stop, arrows, Home and End move and select
  await page.getByRole('tab', { name: /^Overview/ }).focus()
  await page.keyboard.press('ArrowRight')
  await expect(page.getByRole('tab', { name: /^Services/ })).toBeFocused()
  await expect(page.getByRole('tab', { name: /^Services/ })).toHaveAttribute('aria-selected', 'true')
  await page.keyboard.press('End')
  await expect(page.getByRole('tab', { name: /^Activity/ })).toHaveAttribute('aria-selected', 'true')
  await expect(page.getByRole('tabpanel')).toContainText('Recent activity')
  await page.keyboard.press('Home')
  await expect(page.getByRole('tab', { name: /^Overview/ })).toHaveAttribute('aria-selected', 'true')
})

test('quality gates: score, progress and table agree; pending gates name their owner; Review blockers filters to the outstanding gates', async ({ page }) => {
  await signIn(page, 'rd-gates@example.com', 'RTE')
  const t = await trainByTitle(page, 'Card portal')
  await page.goto(`/trains/${t.id}`)
  const panel = page.locator('section[aria-label="Quality gates"]')
  const rows = panel.getByRole('table', { name: 'Quality gates' }).locator('tbody tr')
  const total = t.gates.length
  await expect(rows).toHaveCount(total)
  const passed = await rows.filter({ hasText: /Passed|Waived/ }).count()
  const outstanding = total - passed
  await expect(panel.locator('.rd-score')).toHaveText(`${passed} / ${total}`)
  const pct = Math.round(100 * passed / total)
  await expect(panel).toContainText(`${pct}% ready`)
  await expect(panel.getByRole('progressbar', { name: 'Gates ready' })).toHaveAttribute('aria-valuenow', String(pct))
  await expect(panel).toContainText(outstanding === 0 ? 'All passed' : `${outstanding} outstanding`)
  await expect(page.getByRole('group', { name: 'Gates passed' })).toContainText(`${passed} / ${total}`)
  await expect(panel).toContainText(/Evaluated .* UTC/)                                   // times are UTC and labelled (D44)

  // a pending gate says what it waits for and who owns it
  const pending = rows.filter({ hasText: /Pending|In progress/ }).first()
  await expect(pending).toContainText(/Waiting for|Needs at least one check/)
  await expect(pending).toContainText('Gina Ortiz')

  await page.getByRole('link', { name: /Review blockers/ }).or(page.getByRole('button', { name: /Review blockers/ })).click()
  await expect(panel.getByRole('combobox', { name: 'Show gates' })).toHaveValue('outstanding')
  await expect(rows).toHaveCount(outstanding)
  await expect(rows.first()).toBeFocused()
  await expect(panel).toContainText(`${outstanding} gate${outstanding === 1 ? '' : 's'} shown`)

  // selecting a gate opens its checklist and the Inspector, from the keyboard
  await page.keyboard.press('Enter')
  await expect(page.locator('.inspector h2')).toHaveText(await rows.first().locator('.primary').first().innerText())
})

test('fields the app does not record are shown as not recorded, and the manifest says when it is locked', async ({ page }) => {
  await signIn(page, 'rd-props@example.com', 'RTE')
  const d = await drill(page)                                                             // an Executing release: its manifest is locked
  await page.goto(`/trains/${d.trainId}`)
  const ctx = page.getByRole('group', { name: 'Release context' })
  await expect(ctx).toContainText('Team Not recorded')
  await expect(ctx).toContainText('Owner Not recorded')
  await expect(ctx).toContainText(/Window .* UTC/)
  const props = page.locator('section[aria-label="Release properties"]')
  for (const k of ['Target environments', 'Release branch', 'On-call']) await expect(props.locator('.os-prop', { hasText: k })).toContainText('Not recorded')
  await expect(props.locator('.os-prop', { hasText: 'Service manifest' })).toContainText('Locked')
  await openTab(page, 'Services')
  const manifest = page.locator('section[aria-label="Bundled products"]')
  await expect(manifest).toContainText('Locked')
  await expect(manifest).toContainText(/Manifest v\d+/)
  await expect(manifest.getByRole('button', { name: 'Add product' })).toHaveCount(0)    // edits are blocked while locked
  await expect(manifest.getByRole('button', { name: /^Edit / })).toHaveCount(0)
})

test('the Go/No-Go card has its own treatment for Not yet decided, Go, Conditional go and No-go, and Review approvals opens the Approvals tab', async ({ page }) => {
  await signIn(page, 'rd-go@example.com', 'ReleaseManager')
  const d = await drill(page)
  await page.goto(`/trains/${d.trainId}`)
  const card = page.locator('section[aria-label="Go/No-Go summary"]')
  await expect(card).toContainText('Not yet decided')
  await expect(card.locator('[data-decision]')).toHaveAttribute('data-decision', 'None')

  const me = await page.evaluate(async () => (await (await fetch('/api/v1/me')).json()).id as string)
  await record(page, d.trainId, 'Go', 'All gates passed')
  await page.reload()
  await expect(card).toContainText('Go')
  await expect(card).toContainText('Cleared to deploy')
  await expect(card).toContainText('All gates passed')
  await expect(card).toContainText(/Decided by .* UTC/)

  await record(page, d.trainId, 'GoWithConditions', 'Watch the queue', [{ text: 'Raise the limit', ownerUserId: me, expiresAt: '2035-01-01T09:00:00Z' }])
  await page.reload()
  await expect(card.locator('[data-decision]')).toHaveAttribute('data-decision', 'GoWithConditions')
  await expect(card).toContainText('Conditional go')
  await expect(card).toContainText('Cleared with 1 open condition')
  await expect(card).toContainText('Raise the limit')
  await expect(page.getByRole('tab', { name: /^Approvals/ })).toContainText(/\d/)

  await record(page, d.trainId, 'NoGo', 'Gateway outage')
  await page.reload()
  await expect(card.locator('[data-decision]')).toHaveAttribute('data-decision', 'NoGo')
  await expect(card).toContainText('No-go')
  await expect(card).toContainText('Deployment is on hold')
  await scan(page, 'no-go card')

  await card.getByRole('button', { name: 'Review approvals' }).click()
  await expect(page.getByRole('tab', { name: /^Approvals/ })).toHaveAttribute('aria-selected', 'true')
  await expect(page.locator('section[aria-label="Go/No-Go"]')).toBeVisible()
})

test('a manifest of 20 or more services scrolls inside its panel with its headings kept, and sorts', async ({ page }) => {
  await signIn(page, 'rd-many@example.com', 'RTE')
  const id = await page.evaluate(async () => {
    const json = { 'Content-Type': 'application/json' }
    const made = await (await fetch('/api/v1/trains', { method: 'POST', headers: json, body: JSON.stringify({ title: `R26.99 Many services ${Date.now()}`, targetReleaseDate: '2026-12-15' }) })).json() as { train?: { id: string }; id?: string }
    const trainId = (made.train?.id ?? made.id)!
    for (let i = 1; i <= 22; i++) await fetch(`/api/v1/trains/${trainId}/products`, { method: 'POST', headers: json, body: JSON.stringify({ name: `Service ${String(23 - i).padStart(2, '0')}`, versionTag: `1.${i}.0` }) })
    return trainId
  })
  await page.goto(`/trains/${id}`)
  await expect(page.getByRole('tab', { name: /^Services/ })).toContainText('22')
  await openTab(page, 'Services')
  const manifest = page.locator('section[aria-label="Bundled products"]')
  const rows = manifest.getByRole('table', { name: 'Service manifest' }).locator('tbody tr')
  await expect(rows).toHaveCount(22)
  const scroller = manifest.locator('.rd-scroll')
  expect(await scroller.evaluate(el => el.scrollHeight > el.clientHeight)).toBe(true)     // it scrolls inside the panel, the page does not grow with it
  await expect(rows.first()).toContainText('Service 01')                                  // sorted by name by default
  await manifest.getByRole('combobox', { name: 'Sort services by' }).selectOption('version')
  await expect(rows.first()).toContainText('Service 22')                                  // version 1.1.0 is the first
  await scroller.evaluate(el => { el.scrollTop = el.scrollHeight })
  const [head, box] = await Promise.all([manifest.getByRole('columnheader', { name: 'Service', exact: true }).boundingBox(), scroller.boundingBox()])
  expect(Math.abs(head!.y - box!.y), 'the headings stay at the top of the scrolling panel while the rows scroll').toBeLessThan(2)
  await scan(page, 'manifest with 22 services')
})

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`${scheme}: every tab`, () => {
    test.use({ colorScheme: scheme })
    test('has no serious axe violations and every control is reachable by keyboard', async ({ page }) => {
      await signIn(page, `rd-axe-${scheme}@example.com`, 'ReleaseManager')
      const t = await trainByTitle(page, 'Card portal')
      await page.goto(`/trains/${t.id}`)
      await expect(page.getByRole('tab', { name: /^Overview/ })).toBeVisible()
      for (const tab of TABS) {
        await openTab(page, tab.split(' ')[0])
        await page.waitForTimeout(500)
        await scan(page, `${tab} (${scheme})`)
        // Tab from the panel's top reaches only visible, enabled controls: nothing is skipped into a hidden panel
        await page.getByRole('tab', { name: new RegExp(`^${tab.split(' ')[0]}`) }).focus()
        for (let i = 0; i < 6; i++) {
          await page.keyboard.press('Tab')
          const visible = await page.evaluate(() => { const a = document.activeElement as HTMLElement | null; return !a || a === document.body || a.getClientRects().length > 0 })   // a tab with nothing to operate lets focus leave the page
          expect(visible, `${tab} (${scheme}): tab stop ${i + 1} is visible or has left the page`).toBe(true)
        }
      }
    })
  })
}
