import AxeBuilder from '@axe-core/playwright'
import { expect, test } from '@playwright/test'
import { signIn, trainByTitle } from './support'

// REOS-83: an admin adds a person before their first sign-in (inline row form on Admin > Users), and that person can at once be picked as a task owner.
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']

test('a Release Manager adds a user, a duplicate is refused inline, and the new user is offered as a task owner', async ({ page }) => {
  const email = `quinn.added.${Date.now()}@example.com`
  await signIn(page, 'users-rm@example.com', 'ReleaseManager')
  await page.click('nav[aria-label=Primary] a:has-text("Admin")')
  await expect(page.locator('main h1')).toHaveText('Admin')

  await page.locator('button:text-is("Add user")').click()
  const form = page.locator('[role=group][aria-label="Add user"]')
  await expect(form.locator('button:text-is("Add user")')).toBeDisabled()
  await expect(form).toContainText('Enter the email they sign in with and their name.')   // a disabled action says why
  await form.locator('label:has-text("Email") input').fill(`  ${email.toUpperCase()} `)
  await form.locator('label:has-text("Name") input').fill('Quinn Added')
  await form.locator('label:has-text("Role") select').selectOption({ label: 'RTE' })
  for (const scheme of ['light', 'dark'] as const) {
    await page.emulateMedia({ colorScheme: scheme })
    const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
    expect(r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical'), `add user form (${scheme})`).toEqual([])
  }
  await form.locator('button:text-is("Add user")').click()
  await expect(page.locator('main [role=status]', { hasText: 'Quinn Added' })).toContainText(`Quinn Added (${email}) added`)
  const row = page.locator('table.grid tr', { hasText: email })                   // stored trimmed and in lower case
  await expect(row).toContainText('Quinn Added')
  await expect(row).toContainText('RTE')
  await expect(row).toContainText('● active')

  // the same email again, in another case: refused inline with the reason, nothing added
  await page.locator('button:text-is("Add user")').click()
  await form.locator('label:has-text("Email") input').fill(email.replace('quinn', 'QUINN'))
  await form.locator('label:has-text("Name") input').fill('Quinn Twice')
  await form.locator('button:text-is("Add user")').click()
  await expect(form.locator('[role=alert]')).toContainText('is already a user')
  await expect(page.locator('table.grid tr', { hasText: 'Quinn Twice' })).toHaveCount(0)
  await form.locator('button:text-is("Cancel")').click()

  // pick them as a task owner on a train's checklist
  const t = await trainByTitle(page, 'Payments platform')
  await page.goto(`/trains/${t.id}`)
  const checklist = page.locator('section[aria-label="Checklist"]')
  await checklist.locator('.section-head button:has-text("Add task")').click()
  await checklist.locator('input[placeholder="What has to happen"]').fill('Brief Quinn on the cutover')
  await checklist.locator('label:has-text("Owner") select').selectOption({ label: 'Quinn Added' })
  await checklist.locator('button:text-is("Add")').click()
  await expect(checklist.locator('tr', { hasText: 'Brief Quinn on the cutover' })).toContainText('Quinn Added')
})

test('a Viewer sees the users table without Add user', async ({ page }) => {
  await signIn(page, 'users-viewer@example.com', 'Viewer')
  await page.goto('/admin')
  await expect(page.locator('main h1')).toHaveText('Admin')
  await expect(page.locator('table.grid')).toBeVisible()
  await expect(page.locator('button:text-is("Add user")')).toHaveCount(0)
})
