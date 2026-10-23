import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { primaryNav, signIn } from './support'

// REOS-106 (D45): the workspace switcher, creating a workspace and managing its members. Each test signs in as its own admin so the shared demo database
// is only added to. A person with one workspace sees its name without a menu; with several, the switcher is a keyboard-operable menu that reloads
// the app in the chosen workspace and remembers the choice.
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']
const sidebar = (page: Page) => page.locator('aside[aria-label="Workspace"]')
const switcher = (page: Page) => sidebar(page).getByRole('button', { name: /Switch workspace/ })

async function scan(page: Page, where: string) {
  for (const scheme of ['light', 'dark'] as const) {
    await page.emulateMedia({ colorScheme: scheme })
    const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
    expect(r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical'), `${where} (${scheme})`).toEqual([])
  }
}

async function openWorkspaceTab(page: Page) {
  await primaryNav(page).getByRole('link', { name: 'Teams & settings' }).click()
  await page.getByRole('tab', { name: 'Workspace' }).click()
  await expect(page.getByRole('heading', { name: 'Members', exact: true })).toBeVisible()
}

async function createWorkspace(page: Page, name: string, slug: string, region: string) {
  const form = page.getByRole('group', { name: 'Create a workspace' })
  await form.getByLabel('Workspace name').fill(name)
  await expect(form.getByLabel('Short name')).toHaveValue(slug)                 // suggested from the name
  await form.getByLabel('Region').fill(region)
  await form.getByRole('button', { name: 'Create workspace' }).click()
  await expect(page.locator('.os-notice-title', { hasText: `${name} created` })).toBeVisible()
}

test('a person with one workspace sees its name and region without a menu', async ({ page }) => {
  await signIn(page, `ws-single-${Date.now()}@example.com`, 'Viewer')
  await expect(sidebar(page).locator('.os-workspace-name')).not.toHaveText('Loading workspace…')
  await expect(sidebar(page).locator('.os-workspace-detail')).toContainText('Workspace')
  await expect(switcher(page)).toHaveCount(0)
})

test('an admin creates a workspace, switches to it from the keyboard, and the choice is remembered', async ({ page }) => {
  const id = Date.now().toString(36)
  await signIn(page, `ws-admin-${id}@example.com`, 'RTE')
  await openWorkspaceTab(page)
  await scan(page, 'Workspace tab')
  await createWorkspace(page, `Payments ${id}`, `payments-${id}`, 'EU')
  await expect(switcher(page)).toBeVisible()                                     // two workspaces now: a menu

  // keyboard: open with Enter, the current workspace is first checked, Down moves, Escape closes and returns focus
  await switcher(page).focus()
  await page.keyboard.press('Enter')
  const menu = page.getByRole('menu', { name: 'Your workspaces' })
  await expect(menu).toBeVisible()
  await expect(menu.getByRole('menuitemradio', { checked: true })).toHaveCount(1)
  await scan(page, 'workspace menu open')
  await page.keyboard.press('Escape')
  await expect(menu).toHaveCount(0)
  await expect(switcher(page)).toBeFocused()

  await page.keyboard.press('ArrowDown')
  await expect(menu).toBeVisible()
  await page.keyboard.press('End')
  await expect(menu.getByRole('menuitemradio').last()).toBeFocused()
  const target = await menu.getByRole('menuitemradio').last().innerText()
  const choosing = page.waitForURL(url => url.pathname === '/admin' && url.search === '')   // the page is kept (the query is dropped), the app reloads
  await page.keyboard.press('Enter')
  await choosing
  const newName = target.includes(`Payments ${id}`) ? `Payments ${id}` : null
  if (newName) await expect(sidebar(page).locator('.os-workspace-name')).toHaveText(newName)
  await expect(sidebar(page).locator('.os-workspace-detail')).toContainText('EU')

  // an empty workspace: no releases, and a reload stays in it
  await primaryNav(page).getByRole('link', { name: 'Releases' }).click()
  await expect(page.getByText('No trains yet')).toBeVisible()
  await page.reload()
  await expect(sidebar(page).locator('.os-workspace-name')).toHaveText(`Payments ${id}`)

  // and back, by the mouse
  await switcher(page).click()
  await page.getByRole('menuitemradio', { name: /^Default/ }).click()
  await expect(page.locator('.stream-row').first()).toBeVisible()
})

test('members: add, change role and remove with a confirmation; the last admin is refused in words', async ({ page }) => {
  const id = Date.now().toString(36)
  const admin = `ws-members-${id}@example.com`
  await signIn(page, admin, 'RTE')
  await openWorkspaceTab(page)
  await createWorkspace(page, `Team ${id}`, `team-${id}`, 'US')
  await switcher(page).click()
  await page.getByRole('menuitemradio', { name: new RegExp(`^Team ${id}`) }).click()
  await openWorkspaceTab(page)

  const members = page.getByRole('table', { name: 'Workspace members' })
  await expect(members.getByRole('row', { name: new RegExp(admin) })).toContainText('RTE')

  // the only admin cannot leave
  await members.getByRole('row', { name: new RegExp(admin) }).click()
  await page.getByRole('button', { name: 'Remove from workspace' }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'Remove from workspace' }).click()
  await expect(page.getByRole('alert').filter({ hasText: 'last RTE or Release Manager' })).toBeVisible()

  // add a person
  const add = page.getByRole('group', { name: 'Add a member' })
  await expect(add.getByRole('button', { name: 'Add member' })).toBeDisabled()
  await add.getByLabel('Email').fill(`quinn-${id}@example.com`)
  await add.getByLabel('Name').fill('Quinn Member')
  await add.getByLabel('Role in this workspace').selectOption('Viewer')
  await add.getByRole('button', { name: 'Add member' }).click()
  const quinn = members.getByRole('row', { name: /Quinn Member/ })
  await expect(quinn).toContainText('Viewer')
  await expect(quinn).toContainText('UTC')                                      // times are UTC, labelled
  await scan(page, 'member selected')

  // change the role
  await quinn.click()
  await expect(page.getByRole('button', { name: 'Change role' })).toBeDisabled()   // the same role: says why
  await page.getByLabel('Role in this workspace').first().selectOption('ReleaseManager')
  await page.getByRole('button', { name: 'Change role' }).click()
  await expect(quinn).toContainText('Release Manager')

  // with a second admin the first may go; here, remove Quinn: the dialog starts on Cancel
  await page.getByRole('button', { name: 'Remove from workspace' }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused()
  await dialog.getByRole('button', { name: 'Cancel' }).click()
  await expect(quinn).toBeVisible()
  await page.getByRole('button', { name: 'Remove from workspace' }).click()
  await dialog.getByRole('button', { name: 'Remove from workspace' }).click()
  await expect(quinn).toHaveCount(0)
})
