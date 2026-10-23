import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { primaryNav, signIn, trainByTitle } from './support'

// REOS-99: the Approvals page: the queue and the decision pane over GET /approvals and the existing action endpoints, the separation of duties,
// the decision note, due-time countdown, and the honest "not available yet" parts. The scenario is made here (a far-future freeze, an RTE's override
// request, a Governance Officer's decision) so it does not depend on, or change, the demo trains' own decisions.
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']
async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  const detail = bad.map(v => `${v.id} (${v.impact}): ${v.help}\n   ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join('\n   ')}`).join('\n')
  expect(bad, `axe violations on ${where}:\n${detail}`).toEqual([])
}

async function json(page: Page, method: string, url: string, body?: unknown) {
  return page.evaluate(async ([m, u, b]) => {
    const r = await fetch(u as string, { method: m as string, headers: { 'Content-Type': 'application/json' }, body: b === undefined ? undefined : JSON.stringify(b) })
    return { status: r.status, body: await r.json().catch(() => null) }
  }, [method, url, body] as const)
}

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`${scheme} theme`, () => {
    test.use({ colorScheme: scheme })

    test('a Governance Officer decides an RTE\'s freeze-override request: queue, decision pane, note rule, audit record, badge', async ({ page, browser }) => {
      const name = `Approvals e2e ${scheme} ${Date.now()}`
      await signIn(page, `approvals-gov-${scheme}@example.com`, 'GovernanceOfficer')
      const train = await trainByTitle(page, 'Card portal')
      const freeze = await json(page, 'POST', '/api/v1/freezes', { name, kind: 'Freeze', startsAt: '2031-03-01T00:00:00Z', endsAt: '2031-03-02T00:00:00Z', productPattern: null })
      expect(freeze.status, JSON.stringify(freeze.body)).toBe(200)
      const windowId = (freeze.body as { id: string }).id

      for (const who of ['rte', 'rte2']) {   // two requesters: two queue items, so Up and Down have somewhere to go
        const ctx = await browser.newContext({ colorScheme: scheme })
        const rte = await ctx.newPage()
        await signIn(rte, `approvals-${who}-${scheme}@example.com`, 'RTE')
        const asked = await json(rte, 'POST', `/api/v1/freezes/${windowId}/override-requests`, { trainId: train.id, reason: 'The card portal needs a fix inside the freeze', expiresAt: null })
        expect(asked.status, JSON.stringify(asked.body)).toBe(200)
        // the requester is never offered their own request (separation of duties): the RTE's queue is empty
        expect(((await json(rte, 'GET', '/api/v1/approvals')).body as { items: unknown[] }).items).toEqual([])
        await ctx.close()
      }

      await primaryNav(page).getByRole('link', { name: /^Approvals/ }).click()
      await expect(page).toHaveURL(/\/approvals$/)
      await expect(page.locator('main h1')).toHaveText('Approvals')

      // four metric cards; the one with no data says so instead of inventing a number
      for (const label of ['Pending approvals', 'Due within an hour', 'Open exceptions', 'Policy coverage']) await expect(page.getByRole('group', { name: label })).toBeVisible()
      await expect(page.getByRole('group', { name: 'Policy coverage' })).toContainText('Not available')

      // tabs with counts
      const tabs = page.getByRole('tablist', { name: 'Approvals' })
      await expect(tabs.getByRole('tab', { name: /^Pending \d+$/ })).toBeVisible()
      await expect(tabs.getByRole('tab', { name: /^Exceptions \d+$/ })).toBeVisible()

      // the queue item and its decision pane load without a page reload
      const items = page.getByRole('button', { name: new RegExp(`Freeze override: ${name}`) })
      await expect(items).toHaveCount(2)
      const item = items.first()
      await item.focus()
      await page.keyboard.press('ArrowDown')                               // Up and Down move through the queue
      await expect(items.nth(1)).toBeFocused()
      await page.keyboard.press('ArrowUp')
      await expect(item).toBeFocused()
      await expect(item).toContainText(/Due in \d+/)                      // a countdown, not a bare date
      await item.click()
      const pane = page.getByRole('region', { name: `Decision: Freeze override: ${name}` })
      await expect(pane).toBeVisible()
      await expect(pane).toContainText('Pending decision')
      await expect(pane).toContainText('Card portal')
      await expect(pane).toContainText('UTC')                              // times are UTC (D44)
      await expect(pane.getByRole('list', { name: 'Checks before this decision' })).toContainText('Independent approver')
      // Request changes does not exist for overrides yet: disabled, with its reason beside it
      await expect(pane.getByRole('button', { name: 'Request changes' })).toBeDisabled()
      await expect(pane).toContainText('Request changes needs REOS-113')

      await scan(page, `the approvals page with a decision open (${scheme})`)

      // the note is required, with a visible message
      const note = pane.getByLabel(/Decision note/)
      await pane.getByRole('button', { name: /Grant & record/ }).click()
      await expect(pane.getByRole('alert').filter({ hasText: 'at least 20 characters' })).toBeVisible()
      await expect(note).toHaveAttribute('aria-invalid', 'true')
      await scan(page, `the approvals page with the note message (${scheme})`)

      // grant it: the item leaves the queue and the decision is in Audit history
      await note.fill('The portal fix is time-critical and reviewed by two people')
      await pane.getByRole('button', { name: /Grant & record/ }).click()
      await expect(items).toHaveCount(1)                                   // the decided item left the queue; the other request is still waiting

      await primaryNav(page).getByRole('link', { name: /^Audit history$/ }).click()
      await page.getByRole('searchbox', { name: 'Search event names' }).fill('grant')
      const row = page.locator('table.au-table tbody tr').first()
      await expect(row).toContainText('Grant freeze override')
      await expect(row).toContainText(`approvals-gov-${scheme}`)          // the actor
    })

    test('the tabs are one tab stop moved with arrow keys, and the empty states say what to do', async ({ page }) => {
      await signIn(page, `approvals-kbd-${scheme}@example.com`, 'RTE')
      await page.goto('/approvals')
      await expect(page.locator('main h1')).toHaveText('Approvals')
      // an RTE holds no decision role, so the queue is empty: the empty state, never a blank page
      await expect(page.getByText('Nothing is waiting for your decision')).toBeVisible()
      await scan(page, `the empty approvals page (${scheme})`)

      const tabs = page.getByRole('tablist', { name: 'Approvals' })
      await tabs.getByRole('tab', { name: /^Pending/ }).focus()
      await page.keyboard.press('ArrowRight')
      await expect(tabs.getByRole('tab', { name: 'Decided' })).toHaveAttribute('aria-selected', 'true')
      await expect(page.getByRole('table', { name: /Decided approvals/ })).toBeVisible()      // an RTE may read the audit log
      await scan(page, `the Decided tab (${scheme})`)
      await page.keyboard.press('End')
      await expect(tabs.getByRole('tab', { name: 'Policy library' })).toHaveAttribute('aria-selected', 'true')
      await expect(page.getByText('No approval policies yet')).toBeVisible()
      await expect(page.getByText('REOS-113').first()).toBeVisible()
    })
  })
}

test('a due time counts down and flips to an overdue treatment without a reload', async ({ page }) => {
  await signIn(page, 'approvals-clock@example.com', 'ReleaseManager')
  const queue = (await json(page, 'GET', '/api/v1/approvals')).body as { items: { kind: string; dueAt: string | null; title: string }[] }
  const timed = queue.items.find(i => i.dueAt)
  test.skip(!timed, 'no timed decision is waiting for a Release Manager in this database')
  const due = Date.parse(timed!.dueAt!)
  await page.clock.install({ time: due - 2 * 60_000 })
  await page.goto('/approvals')
  const item = page.getByRole('button', { name: new RegExp(timed!.title) }).first()
  await expect(item).toContainText(/Due in [12]m/)
  await page.clock.fastForward('05:00')
  await expect(item).toContainText(/Overdue by/)
})

test('a Viewer holds no decision: an empty queue, and no audit tab content', async ({ page }) => {
  await signIn(page, 'approvals-viewer@example.com', 'Viewer')
  await page.goto('/approvals')
  await expect(page.getByText('Nothing is waiting for your decision')).toBeVisible()
  await page.getByRole('tab', { name: 'Decided' }).click()
  await expect(page.getByText('You cannot see decided approvals')).toBeVisible()
})
