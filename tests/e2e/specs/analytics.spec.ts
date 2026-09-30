import AxeBuilder from '@axe-core/playwright'
import { readFileSync } from 'node:fs'
import { expect, test, type Page } from '@playwright/test'
import { signIn } from './support'

// REOS-47: Analytics screen. The e2e app runs the real demo seed, so this asserts SHAPES (six headline figures, a finding per chart, downloads that are real files,
// both themes), never exact numbers. Exact numbers are pinned by the API tests (AnalyticsTests) and the web unit tests (npm test).
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']

async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  const detail = bad.map(v => `${v.id} (${v.impact}): ${v.help}\n   ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join('\n   ')}`).join('\n')
  expect(bad, `axe violations on ${where}:\n${detail}`).toEqual([])
}

async function openAnalytics(page: Page, email: string) {
  await signIn(page, email)
  await page.click('nav[aria-label=Primary] a[href="/analytics"]')
  await expect(page.getByRole('heading', { level: 1, name: 'Analytics' })).toBeVisible()
  // loaded = no chart still says "Loading…" and the six headline figures have left their placeholder
  await expect(page.locator('[data-testid=chart-title]').first()).not.toHaveText('Loading…')
  await expect(page.locator('[data-testid=kpi] .an-kpi-value').first()).not.toHaveText('…')
  await expect(page.locator('[data-testid=chart-title]', { hasText: 'Loading…' })).toHaveCount(0)
  // Charts are lazy: the renderer loads after the data ("Loading chart…"), then draws. Wait for that too, or a "no data" skip below
  // can fire before the first SVG exists and silently turn a real check into a skip.
  await expect(page.getByText('Loading chart…')).toHaveCount(0)
  const drawable = page.locator('[data-testid=chart]:not(:has([data-testid=chart-empty])) .an-canvas')
  if (await drawable.count() > 0) await expect(page.locator('[data-testid=chart] [role=img] svg').first()).toBeAttached()
}

/** First chart card that has an export line (a chart with no data has none). */
const withExports = (page: Page) => page.locator('[data-testid=chart]', { has: page.locator('.an-export') }).first()

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`${scheme} theme`, () => {
    test.use({ colorScheme: scheme })

    test('six headline figures, a finding on every chart, no serious axe violations', async ({ page }) => {
      await openAnalytics(page, `analytics-${scheme}@example.com`)

      const kpis = page.locator('[data-testid=kpi]')
      await expect(kpis).toHaveCount(6)
      await expect(kpis.locator('.group-label')).toHaveText(['On time', 'Slip when late', 'Change fail', 'Rollback', 'Comms on time', 'Waived gates'])
      for (const k of await kpis.all()) {
        await expect(k.locator('.an-kpi-value')).toHaveText(/^(—|\d+(\.\d)?( d|%)?)$/)   // a figure, or an honest dash
        await expect(k.locator('.an-kpi-sub')).not.toBeEmpty()
      }

      // every chart is titled with a finding (or says there is no data), never blank, and names itself for assistive technology
      const charts = page.locator('[data-testid=chart]')
      expect(await charts.count()).toBeGreaterThanOrEqual(10)
      for (const c of await charts.all()) {
        const title = (await c.locator('[data-testid=chart-title]').innerText()).trim()
        expect(title.length, 'chart title').toBeGreaterThan(8)
        expect(title).not.toBe('Could not load this chart')
        if (title === 'No data in this window') await expect(c.getByTestId('chart-empty')).toBeVisible()
        else if (await c.locator('[role=img]').count()) await expect(c.locator('[role=img]')).toHaveAttribute('aria-label', title)
      }
      // ECharts drew SVG (no canvas anywhere on the screen)
      await expect(page.locator('canvas')).toHaveCount(0)

      // View data reveals a table with non-empty headers, and hides again
      const drawn = page.locator('[data-testid=chart]', { has: page.locator('[role=img] svg') }).first()
      if (await drawn.count()) {
        await drawn.getByRole('button', { name: 'View data' }).click()
        const heads = drawn.locator('table th')
        expect(await heads.count()).toBeGreaterThan(1)
        for (const h of await heads.all()) expect((await h.innerText()).trim().length).toBeGreaterThan(0)
        await drawn.getByRole('button', { name: 'Hide data' }).click()
        await expect(drawn.locator('table')).toHaveCount(0)
      }

      // rule 9: no checkboxes, radios or switches on the screen
      await expect(page.locator('input[type=checkbox], input[type=radio], [role=switch]')).toHaveCount(0)
      await scan(page, `analytics (${scheme})`)
    })

    test('the drawn chart takes its colours from the tokens of the active theme', async ({ page }) => {
      await openAnalytics(page, `analytics-theme-${scheme}@example.com`)
      const svg = page.locator('[data-testid=chart] [role=img] svg').first()
      test.skip((await svg.count()) === 0, 'the demo seed has no chart data in this window')
      const bg = await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--bg').trim().toLowerCase())
      expect(bg).toMatch(/^#[0-9a-f]{6}$/)
      expect((await svg.evaluate(e => e.outerHTML)).toLowerCase()).toContain(bg)
    })
  })
}

test('switching the theme redraws the charts with the other palette', async ({ page }) => {
  await page.emulateMedia({ colorScheme: 'light' })
  await openAnalytics(page, 'analytics-switch@example.com')
  const svg = page.locator('[data-testid=chart] [role=img] svg').first()
  test.skip((await svg.count()) === 0, 'the demo seed has no chart data in this window')
  const bg = () => page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--bg').trim().toLowerCase())
  const light = await bg()
  expect((await svg.evaluate(e => e.outerHTML)).toLowerCase()).toContain(light)
  await page.emulateMedia({ colorScheme: 'dark' })
  const dark = await bg()
  expect(dark).not.toBe(light)
  await expect.poll(async () => (await page.locator('[data-testid=chart] [role=img] svg').first().evaluate(e => e.outerHTML)).toLowerCase()).toContain(dark)
})

test('CSV, XLSX and SVG exports download real files; a failed export says so inline', async ({ page }) => {
  await openAnalytics(page, 'analytics-export@example.com')
  const card = withExports(page)
  test.skip((await card.count()) === 0, 'the demo seed has no chart data in this window')
  const title = await card.locator('[data-testid=chart-title]').innerText()

  const grab = async (label: RegExp) => {
    const [dl] = await Promise.all([page.waitForEvent('download'), card.getByRole('button', { name: label }).click()])
    const path = await dl.path()
    const bytes = readFileSync(path)
    expect(bytes.length, `${dl.suggestedFilename()} is not empty`).toBeGreaterThan(0)
    return { name: dl.suggestedFilename(), bytes }
  }

  const csv = await grab(/as CSV$/)
  expect(csv.name).toMatch(/^analytics-m\d+-.*\.csv$/)
  const text = csv.bytes.toString('utf8').replace(/^﻿/, '')
  expect(text.split('\r\n')[0].length).toBeGreaterThan(0)     // header row
  expect(text.split('\r\n').length).toBeGreaterThan(2)        // header + at least one row + trailing CRLF

  const xlsx = await grab(/as XLSX$/)
  expect(xlsx.name).toMatch(/^analytics-m\d+-.*\.xlsx$/)
  expect(xlsx.bytes.subarray(0, 2).toString('latin1')).toBe('PK')   // a zip container

  if (await card.getByRole('button', { name: /as SVG$/ }).count()) {
    const svg = await grab(/as SVG$/)
    expect(svg.name).toMatch(/^analytics-m\d+-.*\.svg$/)
    const s = svg.bytes.toString('utf8')
    expect(s).toContain('<svg')
    expect(s).toContain(`<title>`)   // the finding travels with the file
    expect(s.replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"')).toContain(title)
  }

  // failure is shown inline: make the XLSX endpoint fail once
  await page.route('**/api/v1/analytics/*/xlsx*', r => r.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: 'boom' }) }))
  await card.getByRole('button', { name: /as XLSX$/ }).click()
  await expect(card.getByRole('alert')).toContainText('XLSX export failed')
})

test('applying a period with from after to is refused inline; a valid period reloads', async ({ page }) => {
  await openAnalytics(page, 'analytics-period@example.com')
  const from = page.getByLabel('Period from'), to = page.getByLabel('to', { exact: true })
  await from.fill('2030-01-02'); await to.fill('2030-01-01')
  await expect(page.getByRole('alert').filter({ hasText: 'From must not be after to' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Apply' })).toBeDisabled()
  await from.fill('2030-01-01'); await to.fill('2030-01-31')
  await page.getByRole('button', { name: 'Apply' }).click()
  // a window with no history: every chart says so honestly, and the headline figures show a dash with the reason
  await expect(page.locator('[data-testid=chart-title]', { hasText: 'No data in this window' }).first()).toBeVisible()
  await expect(page.locator('[data-testid=kpi] .an-kpi-value').first()).toHaveText('—')
})
