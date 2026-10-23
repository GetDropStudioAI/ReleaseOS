import AxeBuilder from '@axe-core/playwright'
import { expect, test } from '@playwright/test'
import { primaryNav, signIn } from './support'

// REOS-95: the Overview reads GET /overview; the four cards, the chart with its text alternatives, the activity table and the attention list are on it,
// the range control reloads everything, and it passes axe in both themes.
for (const scheme of ['light', 'dark'] as const) {
  test.describe(`${scheme} theme`, () => {
    test.use({ colorScheme: scheme })

    test('cards, chart, activity and attention show live data and pass axe', async ({ page }) => {
      await signIn(page, `overview-${scheme}@example.com`)
      await primaryNav(page).getByRole('link', { name: 'Overview' }).click()
      await expect(page.getByRole('heading', { level: 1, name: 'Overview' })).toBeVisible()
      for (const label of ['Active releases', 'Running now', 'Need attention', 'Deployment success']) await expect(page.getByRole('group', { name: label })).toBeVisible()
      const active = await page.getByRole('group', { name: 'Active releases' }).locator('.os-metric-value').innerText()
      const rows = await page.getByRole('table', { name: 'Release activity' }).locator('tbody tr').count()
      expect(Number(active.trim())).toBeGreaterThan(0)
      expect(rows).toBeGreaterThanOrEqual(Number(active.trim()))   // every open release has a row (closed ones follow)

      const chart = page.getByRole('group', { name: /^Release throughput: \d+ releases? completed in 14 days$/ })
      await expect(chart).toBeVisible()
      await expect(chart.getByRole('img')).toHaveCount(14)
      await chart.getByRole('img').last().focus()
      await expect(page.locator('.os-chart-readout')).toContainText(/no releases completed|successful|failed|with issues/)
      await page.getByText('View data').click()
      await expect(page.getByRole('table', { name: 'Releases completed per day (UTC)' }).locator('tbody tr')).toHaveCount(14)

      await expect(page.getByRole('heading', { name: 'Needs your attention' })).toBeVisible()
      await expect(page.getByRole('heading', { name: 'Environment health' })).toBeVisible()
      const serious = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze()).violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
      expect(serious.map(v => `${v.id}: ${v.nodes.map(n => n.target.join(' ')).join(' | ')}`)).toEqual([])
    })
  })
}

test('the range control reloads the chart, and an open release is reachable from the table', async ({ page }) => {
  await signIn(page, 'overview-range@example.com')
  await primaryNav(page).getByRole('link', { name: 'Overview' }).click()
  await expect(page.getByRole('group', { name: /in 14 days$/ })).toBeVisible()
  await page.getByLabel('Date range').selectOption('30')
  await expect(page.getByRole('group', { name: /in 30 days$/ })).toBeVisible()
  await page.getByRole('table', { name: 'Release activity' }).getByRole('button').first().click()
  await expect(page).toHaveURL(/\/trains\//)
})

test('a Viewer sees the Overview without the Create release button', async ({ page }) => {
  await signIn(page, 'overview-viewer@example.com', 'Viewer')
  await primaryNav(page).getByRole('link', { name: 'Overview' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Overview' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Create release' })).toHaveCount(0)
})
