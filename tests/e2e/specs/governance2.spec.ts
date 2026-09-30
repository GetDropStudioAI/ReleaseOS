import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { signIn, trainByTitle } from './support'

// REOS-34/35/36: evidence upload from the gate Inspector, and the freeze / close-out panels pass axe in both themes.
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']
async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  expect(bad.map(v => `${v.id}: ${v.nodes[0]?.target.join(' ')}`), `axe violations on ${where}`).toEqual([])
}

test('evidence: attach a file, see its server-computed SHA-256, then delete it', async ({ page }) => {
  await signIn(page, 'evidence@example.com')
  const t = await trainByTitle(page, 'Card portal')
  const gate = t.gates.find(g => g.name === 'Compliance Sign-off')!
  await page.goto(`/trains/${t.id}/gates/${gate.id}`)
  const ev = page.locator('section[aria-label=Evidence]')
  await expect(ev).toBeVisible()
  await ev.locator('input[type=file]').setInputFiles({ name: 'abc.txt', mimeType: 'text/plain', buffer: Buffer.from('abc') })
  const row = ev.locator('tr', { hasText: 'abc.txt' })
  await expect(row).toBeVisible()
  await expect(row).toContainText('ba7816bf')                       // FIPS 180 test vector for "abc"
  await expect(row).toContainText('Open')
  await row.locator('button:text-is("Delete")').click()
  await ev.locator('button', { hasText: /^(Confirm|Yes|Delete)/ }).last().click()
  await expect(ev.locator('tr', { hasText: 'abc.txt' })).toHaveCount(0)
})

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`${scheme}: governance panels`, () => {
    test.use({ colorScheme: scheme })
    test('train view with freeze, waiver, close-out and evidence panels has no serious axe violations', async ({ page }) => {
      await signIn(page, `gov-${scheme}@example.com`, 'ReleaseManager')
      const t = await trainByTitle(page, 'Card portal')
      const gate = t.gates.find(g => g.name === 'Compliance Sign-off')!
      await page.goto(`/trains/${t.id}/gates/${gate.id}`)
      await expect(page.locator('section[aria-label="Freeze windows"]')).toBeVisible()
      await expect(page.locator('section[aria-label="Rollback rehearsal"]')).toBeVisible()
      await expect(page.locator('section[aria-label="Known issues and hypercare"]')).toBeVisible()
      await expect(page.locator('section[aria-label=Evidence]')).toBeVisible()
      await scan(page, `governance panels (${scheme})`)
    })
  })
}
