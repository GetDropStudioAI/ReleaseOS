import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { openPage, primaryNav, signIn } from './support'

// REOS-174 (D56, D57, D63): add-on modules per workspace. Teams & settings has a Modules tab: each module is off until an RTE or Release Manager presses "Enable <module>" and confirms what turns on
// (never a toggle switch, rule 9); disabling asks again and says the data is kept. A module's navigation entry appears and disappears live in every open session of the workspace, and its page is
// Page not found while it is off. The shared demo database must end with every module off: later specs run with the modules off.

const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']

async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  expect(bad, `axe on ${where}: ${bad.map(v => `${v.id}: ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join(', ')}`).join('; ')}`).toEqual([])
}

/** Puts every module back to off through the API as the signed-in admin, whatever state a failed run left. */
async function allOff(page: Page) {
  await page.evaluate(async () => {
    const list = await (await fetch('/api/v1/modules')).json() as { key: string; enabled: boolean; version: number }[]
    for (const m of list.filter(x => x.enabled)) {
      const r = await fetch(`/api/v1/modules/${m.key}:disable`, { method: 'POST', headers: { 'Content-Type': 'application/json', 'If-Match': `"${m.version}"` }, body: '{}' })
      if (!r.ok) throw new Error(`disable ${m.key} failed: ${r.status}`)
    }
  })
}

const table = (page: Page) => page.getByRole('table', { name: 'Modules' })
const row = (page: Page, name: string | RegExp) => table(page).locator('tbody tr', { hasText: name })
const cabEntry = (page: Page) => primaryNav(page).getByRole('link', { name: 'Change advisory board' })
const aiEntry = (page: Page) => primaryNav(page).getByRole('link', { name: 'AI assistant' })

async function openModules(page: Page) {
  await openPage(page, '/admin?tab=modules')
  await expect(page.getByRole('tab', { name: 'Modules' })).toHaveAttribute('aria-selected', 'true')
  await expect(table(page)).toBeVisible()
}

// Whatever a test did or failed to do, the next spec starts with every module off: a separate session switches them off.
test.afterEach(async ({ browser }) => {
  const ctx = await browser.newContext()
  try {
    const page = await ctx.newPage()
    await signIn(page, 'mod-cleanup-rte@example.com', 'RTE')
    await allOff(page)
  } finally { await ctx.close() }
})

test('every module is off by default: no navigation entry, a page that is not found, and the Modules tab says so in words', async ({ page }) => {
  await signIn(page, 'mod-off-rte@example.com', 'RTE')
  await expect(primaryNav(page).getByRole('link', { name: 'Approvals' })).toBeVisible()
  await expect(cabEntry(page)).toHaveCount(0)
  await expect(aiEntry(page)).toHaveCount(0)

  await openModules(page)
  await expect(row(page, 'CAB module')).toContainText('Not enabled')
  await expect(row(page, 'AI module')).toContainText('Not enabled')
  await expect(page.getByRole('button', { name: 'Enable CAB module' })).toBeVisible()
  expect(await page.getByRole('switch').count()).toBe(0)       // rule 9: no toggle switch, no checkbox
  expect(await page.getByRole('checkbox').count()).toBe(0)

  await page.goto('/cab')
  await expect(page.getByRole('heading', { name: 'Page not found' })).toBeVisible()
  await page.goto('/ai')
  await expect(page.getByRole('heading', { name: 'Page not found' })).toBeVisible()
})

test('only an RTE or Release Manager reaches the Modules tab; the API refuses the rest', async ({ page }) => {
  for (const role of ['Viewer', 'GovernanceOfficer']) {
    await signIn(page, `mod-${role}@example.com`, role)
    await expect(primaryNav(page).getByRole('link', { name: 'Teams & settings' })).toHaveCount(0)
    await openPage(page, '/admin?tab=modules')
    await expect(page.getByRole('tab', { name: 'Modules' })).toHaveCount(0)
    const status = await page.evaluate(async () => (await fetch('/api/v1/modules/cab:enable', { method: 'POST', headers: { 'Content-Type': 'application/json', 'If-Match': '"0"' }, body: '{}' })).status)
    expect(status).toBe(403)
    expect(await page.evaluate(async () => (await fetch('/api/v1/modules')).status)).toBe(200)   // everyone can read the state (the navigation needs it)
    await page.getByRole('button', { name: /account and appearance/ }).click()
    await page.getByRole('button', { name: 'Sign out', exact: true }).click()
    await expect(page.locator('input[type=email]')).toBeVisible()
  }
})

test('enabling asks first and says what turns on; the entry appears live in another open session; disabling asks again, says data is kept, and removes it live', async ({ page, browser }) => {
  // another person's open session, in the same workspace, on an ordinary page
  const other = await browser.newPage()
  await signIn(other, 'mod-watch@example.com', 'Viewer')
  await expect(cabEntry(other)).toHaveCount(0)

  await signIn(page, 'mod-admin-rm@example.com', 'ReleaseManager')
  const versionOf = () => page.evaluate(async () => (await (await fetch('/api/v1/modules')).json() as { key: string; version: number }[]).find(m => m.key === 'cab')!.version)
  const before = await versionOf()   // another spec may have enabled and disabled the module on this shared server (the CAB screens' spec does)
  await openModules(page)

  // Cancel changes nothing
  await page.getByRole('button', { name: 'Enable CAB module' }).click()
  const dialog = page.getByRole('dialog', { name: 'Enable CAB module?' })
  await expect(dialog).toContainText('turns on')
  await expect(dialog).toContainText('Change advisory board entry appears in the navigation')
  await expect(dialog).toContainText('recorded in the audit history')
  await dialog.getByRole('button', { name: 'Cancel' }).click()
  await expect(dialog).toBeHidden()
  await expect(row(page, 'CAB module')).toContainText('Not enabled')
  await expect(cabEntry(page)).toHaveCount(0)

  // Confirm
  await page.getByRole('button', { name: 'Enable CAB module' }).click()
  await page.getByRole('dialog', { name: 'Enable CAB module?' }).getByRole('button', { name: 'Enable CAB module' }).click()
  await expect(row(page, 'CAB module')).toContainText('Enabled')
  await expect(page.getByTestId('announcer')).toContainText('CAB module enabled')
  await expect(page.getByRole('button', { name: 'Disable CAB module' })).toBeVisible()
  await expect(cabEntry(page)).toBeVisible()
  await expect(aiEntry(page)).toHaveCount(0)               // the other module is unaffected
  await expect(page.getByText('Enabled by')).toBeVisible()
  await expect(cabEntry(other)).toBeVisible()              // live: the other session never reloaded
  await cabEntry(other).click()
  await expect(other.getByRole('heading', { name: 'Change advisory board' })).toBeVisible()
  await expect(other.getByRole('table', { name: 'Risk rules' })).toBeVisible()   // REOS-176: the CAB module's own page, no longer a placeholder

  // Disable: asks again and says the data is kept
  await page.getByRole('button', { name: 'Disable CAB module' }).click()
  const off = page.getByRole('dialog', { name: 'Disable CAB module?' })
  await expect(off).toContainText('data is kept')
  await off.getByRole('button', { name: 'Cancel' }).click()
  await expect(cabEntry(page)).toBeVisible()
  await page.getByRole('button', { name: 'Disable CAB module' }).click()
  await page.getByRole('dialog', { name: 'Disable CAB module?' }).getByRole('button', { name: 'Disable CAB module' }).click()
  await expect(row(page, 'CAB module')).toContainText('Not enabled')
  await expect(cabEntry(page)).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Enable CAB module' })).toBeVisible()
  // the other session was on the module's own page: it goes (Page not found) without a reload, and its navigation entry is gone
  await expect(other.getByRole('heading', { name: 'Page not found' })).toBeVisible()
  await other.close()

  // both changes are in the audit history, versioned
  expect(await versionOf()).toBe(before + 2)
})

test('the Modules tab and its confirmation dialog have no serious axe violation, light or dark', async ({ page }) => {
  await signIn(page, 'mod-axe-rte@example.com', 'RTE')
  for (const scheme of ['light', 'dark'] as const) {
    await page.emulateMedia({ colorScheme: scheme })
    await openModules(page)
    await scan(page, `Modules (${scheme})`)
    await page.getByRole('button', { name: 'Enable CAB module' }).click()
    await expect(page.getByRole('dialog', { name: 'Enable CAB module?' })).toBeVisible()
    await scan(page, `Enable dialog (${scheme})`)
    await page.getByRole('dialog').getByRole('button', { name: 'Cancel' }).click()
    await expect(page.getByRole('dialog')).toHaveCount(0)
  }
  // a module that is on: the detail, the Disable dialog and the module's own page
  // (the version is read, not assumed to be 0: another spec may already have enabled and disabled the module on this shared server)
  await page.evaluate(async () => {
    const ai = (await (await fetch('/api/v1/modules')).json() as { key: string; version: number }[]).find(m => m.key === 'ai')!
    const r = await fetch('/api/v1/modules/ai:enable', { method: 'POST', headers: { 'Content-Type': 'application/json', 'If-Match': `"${ai.version}"` }, body: '{}' })
    if (!r.ok) throw new Error(`enable ai failed: ${r.status}`)
  })
  for (const scheme of ['light', 'dark'] as const) {
    await page.emulateMedia({ colorScheme: scheme })
    await openModules(page)
    await row(page, 'AI module').click()                       // the detail panel starts on the first module
    await page.getByRole('button', { name: 'Disable AI module' }).click()
    await expect(page.getByRole('dialog', { name: 'Disable AI module?' })).toBeVisible()
    await scan(page, `Disable dialog (${scheme})`)
    await page.getByRole('dialog').getByRole('button', { name: 'Cancel' }).click()
    await openPage(page, '/ai')
    await scan(page, `AI module page (${scheme})`)
  }
})
