import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { openPage, primaryNav, signIn } from './support'

// REOS-162: the API tokens screen (/api-tokens, Management group, RTE and Release Manager only). Make, rotate and revoke (the last two behind a confirmation), the secret shown once and
// gone afterwards, 409 and 422 in words, loading / empty / error / no-permission states, no checkbox or switch, and axe in both themes. Adds its own tokens (unique names) to the shared demo database.

const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']
const stamp = Date.now() % 1_000_000

async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  expect(bad, `axe on ${where}: ${bad.map(v => `${v.id}: ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join(', ')}`).join('; ')}`).toEqual([])
}

const table = (page: Page) => page.getByRole('table', { name: 'API tokens' })
const row = (page: Page, text: string | RegExp) => table(page).locator('tbody tr:not(:has(.os-table-empty))', { hasText: text })
const makeForm = (page: Page) => page.getByRole('form', { name: 'Make token' })
const detail = (page: Page) => page.getByRole('group', { name: 'Token detail' })
const secretOf = async (page: Page) => (await page.getByTestId('token-secret').innerText()).trim()
const announcer = (page: Page) => page.getByTestId('announcer')

async function openTokens(page: Page) {
  await openPage(page, '/api-tokens')
  await expect(page.getByRole('heading', { level: 1, name: 'API tokens' })).toBeVisible()
  await expect(table(page).or(page.getByText('No API tokens yet'))).toBeVisible()
}

interface Made { token: { id: string; version: number; name: string }; secret: string }
/** A token through the API as the signed-in person. */
async function token(page: Page, name: string, scopes = ['read']): Promise<Made> {
  return page.evaluate(async ([n, s]) => {
    const r = await fetch('/api/v1/api-tokens', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name: n, scopes: s }) })
    if (!r.ok) throw new Error(`token failed: ${r.status} ${await r.text()}`)
    return await r.json() as Made
  }, [name, scopes] as const)
}

/** Fills and submits the make form with the mouse. */
async function makeThroughForm(page: Page, name: string, access = 'Read and write (read, write)', expires = 'In 30 days') {
  await page.getByRole('button', { name: 'Make token' }).first().click()
  await makeForm(page).getByLabel('Name').fill(name)
  await makeForm(page).getByLabel('Access').selectOption({ label: access })
  await makeForm(page).getByLabel('Expires').selectOption({ label: expires })
  await makeForm(page).getByRole('button', { name: 'Make token' }).click()
  await expect(page.getByText('Copy this token now')).toBeVisible()
}

test('the sidebar has API tokens in the Management group for an RTE and opens the real screen', async ({ page }) => {
  await signIn(page, 'tk-nav-rte@example.com', 'RTE')
  await primaryNav(page).getByRole('link', { name: 'API tokens' }).click()
  await expect(page).toHaveURL(/\/api-tokens$/)
  await expect(page.getByRole('heading', { level: 1, name: 'API tokens' })).toBeVisible()
  await expect(page).toHaveTitle('API tokens · ReleaseOS')
  await expect(page.getByText('/api/public/v1')).toBeVisible()
  await expect(page.getByText('This screen is not built yet')).toHaveCount(0)
  // Management group: the item sits with Service catalog and Connectors
  const names = await primaryNav(page).locator('.os-nav-group-management a').allInnerTexts()
  expect(names.map(n => n.trim())).toContain('Service catalog')
  expect(names.map(n => n.trim())).toContain('API tokens')
})

test('a Release Manager makes a token: the secret is shown once, copied, and gone when the panel closes, after a reload and from every store', async ({ page, context }) => {
  await context.grantPermissions(['clipboard-read', 'clipboard-write'])
  const name = `E2E build server ${stamp}`
  await signIn(page, 'tk-make-rm@example.com', 'ReleaseManager')
  await openTokens(page)

  // Make stays off with the reason in words until the name is filled
  await page.getByRole('button', { name: 'Make token' }).first().click()
  await expect(makeForm(page).getByRole('button', { name: 'Make token' })).toBeDisabled()
  await expect(makeForm(page)).toContainText('Enter a name for the token.')
  await expect(makeForm(page).getByLabel('Access').locator('option')).toHaveText([
    'Read (read)', 'Read and write (read, write)', 'Read and approvals (read, approvals)', 'Read and deployments (read, deployments)',
    'Read, write and approvals (read, write, approvals)', 'Read, write and deployments (read, write, deployments)', 'Everything (read, write, approvals, deployments)',
  ])
  await expect(makeForm(page).getByLabel('Expires').locator('option')).toHaveText(['Never', 'In 30 days', 'In 90 days', 'In 1 year'])
  await page.getByRole('button', { name: 'Close the make token form' }).click()
  await page.getByRole('button', { name: 'Make token' }).first().click()
  await makeForm(page).getByLabel('Name').fill(name)
  await makeForm(page).getByLabel('Access').selectOption({ label: 'Read and write (read, write)' })
  await makeForm(page).getByLabel('Expires').selectOption({ label: 'In 30 days' })
  await makeForm(page).getByRole('button', { name: 'Make token' }).click()

  // the secret: once, in a selectable mono element, announced as a notice
  await expect(page.getByText('Copy this token now')).toBeVisible()
  await expect(page.getByText('shown once and cannot be recovered')).toBeVisible()
  const secret = await secretOf(page)
  expect(secret).toMatch(/^rt_/)
  expect(secret.length).toBeGreaterThan(20)
  await expect(announcer(page)).toContainText(`Token ${name} made`)
  await expect(announcer(page)).not.toContainText(secret)

  // the table has the row, with the prefix, access in words, State chip, owner and an expiry; the secret itself is not in it
  const r = row(page, name)
  await expect(r).toContainText('read, write')
  await expect(r.locator('.os-chip')).toHaveText('Active')
  const prefix = (await detail(page).locator('.os-prop', { hasText: 'Prefix' }).locator('dd').innerText()).trim()
  expect(prefix.length).toBeGreaterThan(3)
  expect(secret.startsWith(prefix), 'the prefix is the start of the secret').toBe(true)
  expect(secret).not.toBe(prefix)
  await expect(r).toContainText(prefix)
  await expect(r).toContainText('Never used')
  await expect(r).toContainText('UTC')   // the expiry, UTC labelled (D44)
  await expect(table(page)).not.toContainText(secret)
  await expect(detail(page)).toContainText('Never rotated')
  await expect(detail(page)).toContainText('Version')

  // Copy token: the clipboard holds the secret and "Copied" is announced
  await page.getByRole('button', { name: 'Copy token' }).click()
  await expect(announcer(page)).toHaveText('Copied')
  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(secret)

  // the secret is in no store and not in the URL
  const stores = await page.evaluate(() => JSON.stringify([{ ...localStorage }, { ...sessionStorage }, location.href]))
  expect(stores).not.toContain(secret)

  // closing the panel drops it; opening the row again shows the detail without it
  await page.getByRole('button', { name: `Close ${name}` }).click()
  await expect(page.getByText('Copy this token now')).toHaveCount(0)
  await expect(page.locator('main')).not.toContainText(secret)
  await row(page, name).click()
  await expect(detail(page)).toBeVisible()
  await expect(page.getByTestId('token-secret')).toHaveCount(0)

  // after a reload it is nowhere on the page
  await page.reload()
  await expect(table(page)).toBeVisible()
  await expect(row(page, name)).toBeVisible()
  expect(await page.locator('body').innerText()).not.toContain(secret)
  expect(await page.content()).not.toContain(secret)
})

test('Copy token says so in words when the clipboard cannot be written', async ({ page }) => {
  await signIn(page, 'tk-copyfail-rte@example.com', 'RTE')
  await page.addInitScript(() => { Object.defineProperty(navigator, 'clipboard', { value: { writeText: () => Promise.reject(new Error('refused')) }, configurable: true }) })
  await openTokens(page)
  await makeThroughForm(page, `E2E nocopy ${stamp}`, 'Read (read)', 'Never')
  await page.getByRole('button', { name: 'Copy token' }).click()
  await expect(page.getByText('Copying did not work here. Select the token above and copy it yourself.').first()).toBeVisible()
  await expect(page.getByTestId('token-secret')).toBeVisible()   // still there to select by hand
})

test('the make form works from the keyboard alone, and selecting another row drops the secret', async ({ page }) => {
  const name = `E2E keys ${stamp}`
  await signIn(page, 'tk-keys-rte@example.com', 'RTE')
  const other = await token(page, `E2E other ${stamp}`)
  await openTokens(page)
  await page.getByRole('button', { name: 'Make token' }).first().focus()
  await page.keyboard.press('Enter')
  await expect(makeForm(page).getByLabel('Name')).toBeVisible()
  await makeForm(page).getByLabel('Name').focus()
  await page.keyboard.type(name)
  await page.keyboard.press('Tab')                       // Access
  await expect(makeForm(page).getByLabel('Access')).toBeFocused()
  // ArrowDown on a focused native select opens the list on macOS and changes the value elsewhere, so the value is set the OS-independent way; reaching the control by Tab is the keyboard claim
  await makeForm(page).getByLabel('Access').selectOption({ label: 'Read and write (read, write)' })
  await page.keyboard.press('Tab')                       // Expires
  await expect(makeForm(page).getByLabel('Expires')).toBeFocused()
  await page.keyboard.press('Tab')                       // Make token
  await expect(makeForm(page).getByRole('button', { name: 'Make token' })).toBeFocused()
  await page.keyboard.press('Enter')
  await expect(page.getByText('Copy this token now')).toBeVisible()
  await expect(row(page, name)).toContainText('read, write')
  // a row is selected with the keyboard too, and the secret goes with the old selection
  await row(page, other.token.name).focus()
  await page.keyboard.press('Enter')
  await expect(page.getByText('Copy this token now')).toHaveCount(0)
  await expect(page.getByRole('heading', { name: other.token.name })).toBeVisible()
  // Enter in the name field submits a complete form
  await page.getByRole('button', { name: 'Make token' }).first().click()
  await makeForm(page).getByLabel('Name').fill(`${name} again`)
  await page.keyboard.press('Enter')
  await expect(page.getByText('Copy this token now')).toBeVisible()
})

test('rotate asks first (starts on Cancel, Escape cancels), then shows the new secret once', async ({ page }) => {
  const name = `E2E rotate ${stamp}`
  await signIn(page, 'tk-rotate-rte@example.com', 'RTE')
  const made = await token(page, name)
  await openTokens(page)
  await row(page, name).click()
  await detail(page).getByRole('button', { name: 'Rotate' }).click()
  const dialog = page.getByRole('dialog', { name: `Rotate ${name}?` })
  await expect(dialog).toBeVisible()
  await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused()
  await expect(dialog).toContainText('old secret')
  await expect(dialog).toContainText('stops working at once')
  await expect(dialog).toContainText('acts as')
  await page.keyboard.press('Escape')
  await expect(dialog).toBeHidden()
  await expect(detail(page)).toContainText('Never rotated')
  await detail(page).getByRole('button', { name: 'Rotate' }).click()
  await dialog.getByRole('button', { name: 'Cancel' }).click()
  await expect(dialog).toBeHidden()
  await expect(page.getByTestId('token-secret')).toHaveCount(0)

  await detail(page).getByRole('button', { name: 'Rotate' }).click()
  await dialog.getByLabel('Expires').selectOption({ label: 'In 90 days' })
  await dialog.getByRole('button', { name: 'Rotate token' }).click()
  await expect(page.getByText('Copy this token now')).toBeVisible()
  const fresh = await secretOf(page)
  expect(fresh).not.toBe(made.secret)
  expect(fresh).toMatch(/^rt_/)
  await expect(page.getByText('the old secret has stopped working')).toBeVisible()
  await expect(detail(page)).not.toContainText('Never rotated')
  await expect(row(page, name)).toContainText('UTC')   // the new expiry
  await expect(announcer(page)).toContainText(`Token ${name} rotated`)
  await page.reload()
  expect(await page.locator('body').innerText()).not.toContain(fresh)
  expect(await page.locator('body').innerText()).not.toContain(made.secret)
})

test('revoke asks first (starts on Cancel), then the state is Revoked and both actions are off with the reason in words', async ({ page }) => {
  const name = `E2E revoke ${stamp}`
  await signIn(page, 'tk-revoke-rte@example.com', 'RTE')
  await token(page, name)
  await openTokens(page)
  await row(page, name).click()
  await detail(page).getByRole('button', { name: 'Revoke' }).click()
  const dialog = page.getByRole('dialog', { name: `Revoke ${name}?` })
  await expect(dialog).toBeVisible()
  await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused()
  await expect(dialog).toContainText('stops working at once')
  await page.keyboard.press('Escape')
  await expect(dialog).toBeHidden()
  await expect(row(page, name).locator('.os-chip')).toHaveText('Active')
  await detail(page).getByRole('button', { name: 'Revoke' }).click()
  await dialog.getByRole('button', { name: 'Revoke token' }).click()
  await expect(announcer(page)).toContainText(`Token ${name} revoked`)
  await expect(row(page, name).locator('.os-chip')).toHaveText('Revoked')
  await expect(detail(page).getByRole('button', { name: 'Rotate' })).toBeDisabled()
  await expect(detail(page).getByRole('button', { name: 'Revoke' })).toBeDisabled()
  await expect(detail(page).getByText('This token is revoked; make a new one.')).toHaveCount(2)
  await expect(detail(page)).toContainText('Revoked')
  // revoked tokens sort last
  const states = await table(page).locator('tbody tr .os-chip').allInnerTexts()
  const firstRevoked = states.indexOf('Revoked')
  expect(states.slice(firstRevoked).every(s => s === 'Revoked')).toBe(true)
})

test('a token changed by someone else: Revoke shows the conflict notice and the list is refreshed; a refused make says why', async ({ page }) => {
  const name = `E2E conflict ${stamp}`
  await signIn(page, 'tk-conflict-rte@example.com', 'RTE')
  const made = await token(page, name)
  await openTokens(page)
  await row(page, name).click()
  // someone else rotates it first (version moves on)
  await page.evaluate(async ([id, v]) => {
    const r = await fetch(`/api/v1/api-tokens/${id}:rotate`, { method: 'POST', headers: { 'Content-Type': 'application/json', 'If-Match': String(v) }, body: '{}' })
    if (!r.ok) throw new Error(`rotate failed ${r.status}`)
  }, [made.token.id, made.token.version] as const)
  await detail(page).getByRole('button', { name: 'Revoke' }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'Revoke token' }).click()
  await expect(page.getByText('Someone else changed this first')).toBeVisible()
  await expect(row(page, name).locator('.os-chip')).toHaveText('Active')   // nothing was revoked
  await expect(detail(page)).toContainText('Version')
  await page.getByRole('button', { name: 'Use theirs' }).click()
  await expect(page.getByText('Someone else changed this first')).toHaveCount(0)
  // with the fresh version the revoke goes through
  await detail(page).getByRole('button', { name: 'Revoke' }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'Revoke token' }).click()
  await expect(row(page, name).locator('.os-chip')).toHaveText('Revoked')

  // a 422 is shown in words: the server refuses the make
  await page.route('**/api/v1/api-tokens', route => (route.request().method() === 'POST'
    ? route.fulfill({ status: 422, contentType: 'application/json', body: JSON.stringify({ guard: 'ApiTokenExpired', message: 'The token has expired; give it a new expiry.' }) })
    : route.continue()))
  await page.getByRole('button', { name: 'Make token' }).first().click()
  await makeForm(page).getByLabel('Name').fill('Refused')
  await makeForm(page).getByRole('button', { name: 'Make token' }).click()
  await expect(makeForm(page).getByRole('alert')).toContainText('The token has expired; give it a new expiry.')
})

for (const [role, email] of [['Viewer', 'tk-viewer@example.com'], ['GovernanceOfficer', 'tk-gov@example.com']] as const) {
  test(`a ${role} has no API tokens item, and /api-tokens says they cannot, without calling the API`, async ({ page }) => {
    const calls: string[] = []
    page.on('request', r => { if (new URL(r.url()).pathname.startsWith('/api/v1/api-tokens')) calls.push(`${r.method()} ${r.url()}`) })
    await signIn(page, email, role)
    await expect(primaryNav(page).getByRole('link', { name: 'API tokens' })).toHaveCount(0)
    await expect(primaryNav(page).getByRole('link', { name: 'Service catalog' })).toBeVisible()   // the rest of the group is there
    await page.goto('/api-tokens')
    await expect(page.getByRole('heading', { level: 1, name: 'API tokens' })).toBeVisible()
    await expect(page.getByText('You cannot manage API tokens')).toBeVisible()
    await expect(page.getByText('Release Train Engineers and Release Managers')).toBeVisible()
    await expect(page.getByRole('button', { name: 'Make token' })).toHaveCount(0)
    await expect(table(page)).toHaveCount(0)
    await page.waitForTimeout(500)
    expect(calls, 'the API must not be called for this role').toEqual([])
  })
}

test('a 403 from the API shows the no-permission page', async ({ page }) => {
  await signIn(page, 'tk-403-rte@example.com', 'RTE')
  await page.route('**/api/v1/api-tokens', route => route.fulfill({ status: 403, contentType: 'application/json', body: '{}' }))
  await page.goto('/api-tokens')
  await expect(page.getByText('You cannot manage API tokens')).toBeVisible()
})

test('loading, error with Retry, and the empty state', async ({ page }) => {
  await signIn(page, 'tk-states-rte@example.com', 'RTE')
  let release: () => void = () => undefined
  const gate = new Promise<void>(r => { release = r })
  await page.route('**/api/v1/api-tokens', async route => { await gate; await route.continue() })
  await page.goto('/api-tokens')
  await expect(page.getByText('Loading the API tokens…')).toBeVisible()
  release()
  await expect(table(page).or(page.getByText('No API tokens yet'))).toBeVisible()
  await page.unroute('**/api/v1/api-tokens')

  let failing = true
  await page.route('**/api/v1/api-tokens', route => (failing ? route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: 'The tokens are down.' }) }) : route.continue()))
  await page.goto('/api-tokens')
  const err = page.locator('.os-notice').filter({ hasText: 'could not be loaded' })
  await expect(err).toContainText('The tokens are down.')
  failing = false
  await err.getByRole('button', { name: 'Retry' }).click()
  await expect(table(page).or(page.getByText('No API tokens yet'))).toBeVisible()
  await page.unroute('**/api/v1/api-tokens')

  await page.route('**/api/v1/api-tokens', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ scopes: ['read', 'write', 'approvals', 'deployments'], tokens: [] }) }))
  await page.goto('/api-tokens')
  await expect(page.getByText('No API tokens yet')).toBeVisible()
  await expect(page.getByText('Make one to call the public API.')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Make token' })).toHaveCount(2)   // the heading action and the empty state's
  await page.getByRole('button', { name: 'Make token' }).last().click()
  await expect(makeForm(page)).toBeVisible()
})

test('there is no checkbox and no switch anywhere on the page', async ({ page }) => {
  const name = `E2E nochk ${stamp}`
  await signIn(page, 'tk-nochk-rte@example.com', 'RTE')
  await token(page, name)
  await openTokens(page)
  const none = async (where: string) => {
    expect(await page.getByRole('checkbox').count(), `checkbox on ${where}`).toBe(0)
    expect(await page.getByRole('switch').count(), `switch on ${where}`).toBe(0)
    expect(await page.locator('input[type=checkbox]').count(), `input checkbox on ${where}`).toBe(0)
  }
  await none('the list')
  await row(page, name).click()
  await none('the detail')
  await detail(page).getByRole('button', { name: 'Rotate' }).click()
  await none('the rotate dialog')
  await page.keyboard.press('Escape')
  await detail(page).getByRole('button', { name: 'Revoke' }).click()
  await none('the revoke dialog')
  await page.keyboard.press('Escape')
  await page.getByRole('button', { name: 'Make token' }).first().click()
  await none('the make form')
  await makeForm(page).getByLabel('Name').fill(name + ' two')
  await makeForm(page).getByRole('button', { name: 'Make token' }).click()
  await expect(page.getByText('Copy this token now')).toBeVisible()
  await none('the secret notice')
})

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`${scheme} mode`, () => {
    test.use({ colorScheme: scheme })
    test(`API tokens: the empty state, the list with a token selected, the make form, the one-time secret and the revoke dialog are axe clean (${scheme})`, async ({ page }) => {
      const name = `E2E a11y ${scheme} ${stamp}`
      await signIn(page, `tk-a11y-${scheme}@example.com`, 'RTE')
      await token(page, name, ['read', 'approvals'])

      await page.route('**/api/v1/api-tokens', route => (route.request().method() === 'GET'
        ? route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ scopes: ['read', 'write', 'approvals', 'deployments'], tokens: [] }) }) : route.continue()))
      await page.goto('/api-tokens')
      await expect(page.getByText('No API tokens yet')).toBeVisible()
      await scan(page, `API tokens, the empty state (${scheme})`)
      await page.unroute('**/api/v1/api-tokens')

      await openTokens(page)
      await scan(page, `API tokens, the list (${scheme})`)
      await row(page, name).click()
      await expect(detail(page)).toBeVisible()
      await scan(page, `API tokens, a token selected (${scheme})`)

      await page.getByRole('button', { name: 'Make token' }).first().click()
      await expect(makeForm(page)).toBeVisible()
      await scan(page, `API tokens, the make form with its reason (${scheme})`)
      await makeForm(page).getByLabel('Name').fill(`${name} new`)
      await makeForm(page).getByRole('button', { name: 'Make token' }).click()
      await expect(page.getByText('Copy this token now')).toBeVisible()
      await scan(page, `API tokens, the one-time secret (${scheme})`)

      await detail(page).getByRole('button', { name: 'Revoke' }).click()
      await expect(page.getByRole('dialog')).toBeVisible()
      await scan(page, `API tokens, the revoke dialog (${scheme})`)
      await page.getByRole('dialog').getByRole('button', { name: 'Cancel' }).click()
      await detail(page).getByRole('button', { name: 'Rotate' }).click()
      await expect(page.getByRole('dialog')).toBeVisible()
      await scan(page, `API tokens, the rotate dialog (${scheme})`)
      await page.getByRole('dialog').getByRole('button', { name: 'Cancel' }).click()

      await detail(page).getByRole('button', { name: 'Revoke' }).click()
      await page.getByRole('dialog').getByRole('button', { name: 'Revoke token' }).click()
      await expect(detail(page).getByText('This token is revoked; make a new one.').first()).toBeVisible()
      await scan(page, `API tokens, a revoked token (${scheme})`)
    })
  })
}

