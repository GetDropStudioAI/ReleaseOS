import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { signIn } from './support'

// REOS-80: "New train" on the Stream opens the right drawer (no modal) with three ways to start: Blank, From template, Copy of a train.
// Each way creates a Planning train and opens its workspace. Adds its own trains and template to the shared demo database (unique names).

const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']
const stamp = Date.now() % 1_000_000
const day = (n: number) => { const d = new Date(Date.now() + n * 86_400_000); return d.toISOString().slice(0, 10) }

async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  expect(bad, `axe on ${where}: ${bad.map(v => `${v.id}: ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join(', ')}`).join('; ')}`).toEqual([])
}

/** An Approved template with two gates (one team-less) and one step, through the API as the signed-in Release Manager. */
async function approvedTemplate(page: Page, name: string) {
  return page.evaluate(async n => {
    const json = { 'Content-Type': 'application/json' }
    const t = await (await fetch('/api/v1/templates', { method: 'POST', headers: json, body: JSON.stringify({
      name: n, defaultRiskTier: 'High',
      gates: [{ gateName: 'E2E Freeze', gateClass: 'Standard', offsetDays: 5, requiredBeforeStatus: 'Gated' }, { gateName: 'E2E CAB', gateClass: 'Compliance', offsetDays: 1, requiredBeforeStatus: 'Executing' }],
      steps: [{ stepCode: 'PRE-1', section: 'PreCheck', title: 'E2E health check', offsetMinutes: -30, plannedDurationMin: 15 }], schedule: [],
    }) })).json() as { id: string; version: number }
    const ok = await fetch(`/api/v1/templates/${t.id}:approve`, { method: 'POST', headers: { ...json, 'If-Match': String(t.version) }, body: '{}' })
    if (!ok.ok) throw new Error(`approve failed: ${ok.status}`)
    return t.id
  }, name)
}

const drawer = (page: Page) => page.locator('aside[aria-label=Inspector]')

async function openDrawer(page: Page) {
  await page.locator('aside[aria-label=Stream] button:text-is("New train")').click()
  await expect(drawer(page).locator('h2', { hasText: 'New train' })).toBeVisible()
}

async function fillCommon(page: Page, title: string, target: string) {
  await drawer(page).getByLabel('Title', { exact: true }).fill(title)
  await drawer(page).getByLabel('Target release date', { exact: true }).fill(target)
}

async function expectOpened(page: Page, title: string) {
  await expect(drawer(page).locator('[role=status]', { hasText: `✓ Created ${title}` })).toBeVisible()
  await expect(page.locator('main h1')).toHaveText(title)
  await expect(page).toHaveURL(/\/trains\/[0-9a-f-]{36}$/)
  await expect(page.locator('main')).toContainText('Planning')
  await expect(page.locator('aside[aria-label=Stream] .stream-row', { hasText: title })).toHaveAttribute('aria-current', 'true')
}

test('a Release Manager creates a train blank, from an approved template and as a copy, and each opens', async ({ page }) => {
  const tplName = `E2E quarterly ${stamp}`
  await signIn(page, 'newtrain-rm@example.com', 'ReleaseManager')
  await approvedTemplate(page, tplName)

  // 1. Blank: Create is off, with the reason on the line below, until the form is complete.
  const blank = `E2E blank ${stamp}`
  await openDrawer(page)
  await expect(drawer(page).locator('button:text-is("Create train")')).toBeDisabled()
  await expect(drawer(page)).toContainText('Enter a title.')
  await fillCommon(page, blank, day(60))
  await drawer(page).getByLabel('Risk tier').selectOption('Low')
  await drawer(page).locator('button:text-is("Create train")').click()
  await expectOpened(page, blank)
  await expect(page.locator('main')).toContainText('Risk Low')

  // 2. From template: the gates come from the template; a window plans its step.
  const fromTpl = `E2E from template ${stamp}`
  await drawer(page).locator('button:text-is("Create another")').click()
  await drawer(page).locator('button.choice:text-is("From template")').click()
  await expect(drawer(page).locator('button.choice:text-is("From template")')).toHaveAttribute('aria-pressed', 'true')
  await drawer(page).getByLabel('Template', { exact: true }).selectOption({ label: tplName })
  await expect(drawer(page)).toContainText('2 gates · 1 runbook step')
  await fillCommon(page, fromTpl, day(61))
  await drawer(page).getByLabel('Starts').fill(`${day(61)}T02:00`)
  await drawer(page).getByLabel('Ends').fill(`${day(61)}T06:00`)
  await drawer(page).locator('button:text-is("Create train")').click()
  await expectOpened(page, fromTpl)
  await expect(page.locator('main')).toContainText('Risk High')   // the template's default tier
  await expect(page.locator('main')).toContainText('E2E Freeze')
  await expect(page.locator('main')).toContainText('E2E CAB')
  await expect(drawer(page)).toContainText('1 runbook step')

  // 3. Copy of a train: the template-built train, moved a week later.
  const copy = `E2E copy ${stamp}`
  await drawer(page).locator('button:text-is("Create another")').click()
  await drawer(page).locator('button.choice:text-is("Copy of a train")').click()
  await drawer(page).getByLabel('Copy of').selectOption({ label: `${fromTpl} · Planning` })
  await fillCommon(page, copy, day(68))
  await drawer(page).locator('button:text-is("Create copy")').click()
  await expectOpened(page, copy)
  await expect(page.locator('main')).toContainText('E2E Freeze')
  await expect(drawer(page)).toContainText('2 gates')

  // A refusal is shown inline, in words: the title is taken.
  await drawer(page).locator('button:text-is("Create another")').click()
  await drawer(page).locator('button.choice:text-is("Blank")').click()
  await fillCommon(page, blank.toUpperCase(), day(60))
  await drawer(page).locator('button:text-is("Create train")').click()
  await expect(drawer(page).locator('[role=alert]')).toContainText('already exists')

  // Discard closes the drawer and drops the form.
  await drawer(page).locator('button:text-is("Discard")').click()
  await expect(drawer(page).locator('h2', { hasText: 'New train' })).toHaveCount(0)
})

test('a Viewer has no New train button', async ({ page }) => {
  await signIn(page, 'newtrain-viewer@example.com', 'Viewer')
  await expect(page.locator('aside[aria-label=Stream] button:text-is("New train")')).toHaveCount(0)
})

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`${scheme} mode`, () => {
    test.use({ colorScheme: scheme })
    test(`the New train drawer is axe clean in every mode (${scheme})`, async ({ page }) => {
      await signIn(page, `newtrain-a11y-${scheme}@example.com`, 'RTE')
      await openDrawer(page)
      for (const mode of ['Blank', 'From template', 'Copy of a train']) {
        await drawer(page).locator(`button.choice:text-is("${mode}")`).click()
        await scan(page, `New train · ${mode} · ${scheme}`)
      }
      await page.keyboard.press('Escape')   // Esc closes the drawer (UI.md)
      await expect(drawer(page).locator('h2', { hasText: 'New train' })).toHaveCount(0)
    })
  })
}
