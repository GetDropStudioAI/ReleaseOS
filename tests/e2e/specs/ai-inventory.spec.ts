import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { openPage, primaryNav, signIn } from './support'

// REOS-184 (D58): the AI inventory screen and the audit trail of AI-assisted actions. Read-only facts (kill switches, features and whether each can run, models used, the data rules) and the trail;
// a pending draft is decided once by the person who asked or an administrator (accept, edit and accept, reject behind a confirmation). The draft and the human edit both show on the Audit history screen.
// No AI feature exists yet, so drafts are made with the development-only /dev/ai/draft, which records one exactly as the governed client does. The shared demo server is left as it was found.

const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']

async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  expect(bad, `axe on ${where}: ${bad.map(v => `${v.id}: ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join(', ')}`).join('; ')}`).toEqual([])
}

/** Makes a pending draft for the signed-in person and returns its id. */
async function makeDraft(page: Page, text: string): Promise<string> {
  return page.evaluate(async t => {
    const r = await fetch(`/api/v1/dev/ai/draft?text=${encodeURIComponent(t)}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' })
    return ((await r.json()) as { id: string }).id
  }, text)
}

const trail = (page: Page) => page.getByRole('table', { name: 'AI actions' })
const detail = (page: Page) => page.getByRole('region', { name: 'Release notes draft' })

async function openInventory(page: Page) {
  await openPage(page, '/ai-inventory')
  await expect(page.getByRole('heading', { level: 1, name: 'AI inventory' })).toBeVisible()
  await expect(trail(page).or(page.getByText('No AI action has been recorded yet')).first()).toBeVisible()   // an empty trail shows both the table and its empty text: either is enough
}

test('an RTE finds AI inventory in the sidebar, and it states the kill switches, features, models and data rules in words', async ({ page }) => {
  await signIn(page, 'ai-inv-rte@example.com', 'RTE')
  await openPage(page, '/')
  await primaryNav(page).getByRole('link', { name: 'AI inventory' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'AI inventory' })).toBeVisible()
  await expect(page).toHaveTitle('AI inventory · ReleaseOS')
  await expect(page.getByRole('region', { name: 'Kill switches' })).toContainText('Global kill switch')
  await expect(page.getByRole('region', { name: 'Kill switches' })).toContainText('This workspace')
  const features = page.getByRole('table', { name: 'AI features' })
  await expect(features.locator('tbody tr')).toHaveCount(7)
  for (const name of ['Release risk score', 'Release notes draft', 'Post-implementation review draft']) await expect(features).toContainText(name)
  await expect(features.getByText(/^(Available|Not available)$/).first()).toBeVisible()   // a word, never colour alone
  const data = page.getByRole('region', { name: 'Data sent to a model' })
  await expect(data).toContainText('CustomerData')
  await expect(data).toContainText('TicketSummary')
  await expect(data).toContainText('Hashes and sizes of what was sent, never the text')
  await expect(page.getByRole('checkbox')).toHaveCount(0)   // rule 9: no checkboxes and no toggle switches
  await expect(page.getByRole('switch')).toHaveCount(0)
  await scan(page, '/ai-inventory')
})

test('a Viewer has no AI inventory item and the page says they cannot see it, without calling the API', async ({ page }) => {
  await signIn(page, 'ai-inv-viewer@example.com', 'Viewer')
  await openPage(page, '/')
  await expect(primaryNav(page).getByRole('link', { name: 'AI inventory' })).toHaveCount(0)
  let called = false
  page.on('request', r => { if (r.url().includes('/api/v1/ai/inventory')) called = true })
  await page.goto('/ai-inventory')
  await expect(page.getByRole('heading', { level: 1, name: 'AI inventory' })).toBeVisible()
  await expect(page.getByText('You cannot see the AI inventory')).toBeVisible()
  expect(called).toBe(false)
})

test('the requester edits a draft: both the draft and the human edit are shown, the decision is final, and the Audit history shows both', async ({ page }) => {
  await signIn(page, 'ai-inv-editor@example.com', 'RTE')
  await openPage(page, '/')
  const id = await makeDraft(page, 'Draft: R26.12 ships the gateway fix.')
  await openInventory(page)
  const row = trail(page).locator('tbody tr', { hasText: 'Awaiting decision' }).first()
  await row.click()
  await expect(detail(page)).toBeVisible()
  await expect(detail(page).getByLabel('Draft')).toHaveValue('Draft: R26.12 ships the gateway fix.')
  await expect(detail(page)).toContainText('Matches the audit chain')
  await detail(page).getByRole('button', { name: 'Edit and accept' }).click()
  const save = detail(page).getByRole('button', { name: 'Save edit and accept' })
  await expect(save).toBeDisabled()   // the draft unchanged is not an edit
  await detail(page).getByRole('textbox', { name: 'Your edit' }).fill('Edited: R26.12 ships the gateway fix on Friday.')
  await save.click()
  await expect(detail(page).getByText('Edited and accepted').first()).toBeVisible()
  await expect(detail(page).getByLabel('Draft')).toHaveValue('Draft: R26.12 ships the gateway fix.')
  await expect(detail(page).getByLabel('Human edit')).toHaveValue('Edited: R26.12 ships the gateway fix on Friday.')
  await expect(detail(page)).toContainText('The decision is final')
  await expect(detail(page).getByRole('button', { name: 'Accept draft' })).toHaveCount(0)   // nothing more to decide
  await expect(detail(page)).toContainText('Matches the audit chain')
  await scan(page, 'an edited AI action')

  // the same event on the Audit history screen: the draft (removed) and the edit (added)
  const eventNo = await page.evaluate(async actionId => {
    const r = await (await fetch(`/api/v1/audit?entity=AiAction&entityId=${actionId}`)).json() as { items: { id: number; action: string }[] }
    return r.items.find(i => i.action === 'Edited')!.id
  }, id)
  await page.goto(`/audit?event=${eventNo}`)
  const change = page.getByRole('group', { name: 'Change' })
  await expect(change).toContainText('Draft: R26.12 ships the gateway fix.')
  await expect(change).toContainText('Edited: R26.12 ships the gateway fix on Friday.')
  const integrity = await page.evaluate(async () => (await (await fetch('/api/v1/audit/integrity?recheck=true')).json()) as { status: string })
  expect(integrity.status).toBe('Verified')
})

test('rejecting a draft asks first, keeps the draft on the trail, and cannot be taken back', async ({ page }) => {
  await signIn(page, 'ai-inv-rejecter@example.com', 'RTE')
  await openPage(page, '/')
  await makeDraft(page, 'Draft to throw away.')
  await openInventory(page)
  await trail(page).locator('tbody tr', { hasText: 'Awaiting decision' }).first().click()
  await detail(page).getByRole('button', { name: 'Reject draft' }).click()
  const dialog = page.getByRole('dialog', { name: 'Reject this draft?' })
  await expect(dialog).toBeVisible()
  await dialog.getByRole('button', { name: 'Cancel' }).click()
  await expect(detail(page).getByText('Awaiting decision').first()).toBeVisible()   // cancelling decided nothing
  await detail(page).getByRole('button', { name: 'Reject draft' }).click()
  await dialog.getByRole('textbox').fill('Wrong release')
  await dialog.getByRole('button', { name: 'Reject draft' }).click()
  await expect(detail(page).getByText('Rejected').first()).toBeVisible()
  await expect(detail(page)).toContainText('Note: Wrong release')
  await expect(detail(page).getByLabel('Draft')).toHaveValue('Draft to throw away.')
  await expect(detail(page).getByRole('button', { name: 'Reject draft' })).toHaveCount(0)
})

test('a Governance Officer reads the inventory and the draft but cannot decide it', async ({ browser }) => {
  const rteCtx = await browser.newContext(); const govCtx = await browser.newContext()
  try {
    const rte = await rteCtx.newPage(); const gov = await govCtx.newPage()
    await signIn(rte, 'ai-inv-asker@example.com', 'RTE'); await openPage(rte, '/')
    await makeDraft(rte, 'A draft only its requester may decide.')
    await signIn(gov, 'ai-inv-gov@example.com', 'GovernanceOfficer')
    await openInventory(gov)
    await expect(primaryNav(gov).getByRole('link', { name: 'AI inventory' })).toBeVisible()
    await trail(gov).locator('tbody tr', { hasText: 'Awaiting decision' }).first().click()
    await expect(detail(gov)).toBeVisible()
    await expect(detail(gov).getByLabel('Draft')).toHaveValue('A draft only its requester may decide.')
    await expect(detail(gov).getByRole('button', { name: 'Accept draft' })).toHaveCount(0)
    await expect(detail(gov)).toContainText('Only the person who asked for this draft, or an RTE or Release Manager, can record the decision')
  } finally { await rteCtx.close(); await govCtx.close() }
})
