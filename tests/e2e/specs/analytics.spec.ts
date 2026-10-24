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

async function openAnalytics(page: Page, email: string, role = 'RTE') {
  await signIn(page, email, role)
  await page.click('nav[aria-label=Primary] a[href="/analytics"]')
  await expect(page.getByRole('heading', { level: 1, name: 'Analytics' })).toBeVisible()
  // loaded = the chart cards are drawn and none is still busy (while loading each titles itself with its subtitle), and the six headline figures have left their placeholder
  await expect(page.locator('[data-testid=chart]').first()).toBeVisible()
  await expect(page.locator('[data-testid=chart][aria-busy=true]')).toHaveCount(0)
  await expect(page.locator('[data-testid=kpi] .os-metric-value').first()).not.toHaveText('…')
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
      await expect(kpis.locator('.os-metric-label')).toHaveText(['On time', 'Slip when late', 'Change fail', 'Rollback', 'Comms on time', 'Waived gates'])
      for (const k of await kpis.all()) {
        await expect(k.locator('.os-metric-value')).toHaveText(/^(—|\d+(\.\d)?( d|%)?)$/)   // a figure, or an honest dash
        await expect(k.locator('.os-metric-detail')).not.toBeEmpty()
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
      const bg = await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--color-bg-surface').trim().toLowerCase())
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
  const bg = () => page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--color-bg-surface').trim().toLowerCase())
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
  await expect(page.locator('[data-testid=kpi] .os-metric-value').first()).toHaveText('—')
  // the reload is spoken once through the app's live region (WCAG 4.1.3)
  await expect(page.getByTestId('announcer')).toHaveText(/^Loaded \d+ charts for 2030-01-01 to 2030-01-31/)
})

// REOS-132: the redesign's screen.
test('the six headline figures are metric cards and each says its state in words', async ({ page }) => {
  await openAnalytics(page, 'analytics-cards@example.com')
  await expect(page.locator('[data-testid=kpi] .os-metric')).toHaveCount(6)
  for (const k of await page.locator('[data-testid=kpi]').all()) {
    const detail = (await k.locator('.os-metric-detail').innerText()).trim()
    // a toned figure leads its detail with a glyph and a word, so colour is never the only cue
    if (await k.locator('.os-metric-detail.success, .os-metric-detail.warning, .os-metric-detail.danger').count()) expect(detail).toMatch(/^(✓ on track|▲ needs attention|✗ off track) · /)
  }
  await expect(page.getByRole('heading', { level: 1, name: 'Analytics' })).toBeVisible()
  await expect(page.getByText(/Data as of .* UTC/)).toBeVisible()
})

test('a quick period fills the dates, reloads and is announced', async ({ page }) => {
  await openAnalytics(page, 'analytics-preset@example.com')
  await page.getByRole('button', { name: 'Last 30 days' }).click()
  await expect(page.getByTestId('announcer')).toHaveText(/^Loaded \d+ charts for \d{4}-\d\d-\d\d to \d{4}-\d\d-\d\d/)
  const from = await page.getByLabel('Period from').inputValue(), to = await page.getByLabel('to', { exact: true }).inputValue()
  expect((Date.parse(to) - Date.parse(from)) / 86_400_000).toBe(30)
})

test('keyboard: the period, a chart\'s exports and View data are reachable and operable without a mouse', async ({ page }) => {
  await openAnalytics(page, 'analytics-keys@example.com')
  await page.getByLabel('Period from').focus(); await expect(page.getByLabel('Period from')).toBeFocused()
  await page.getByLabel('to', { exact: true }).focus(); await expect(page.getByLabel('to', { exact: true })).toBeFocused()
  await page.getByRole('button', { name: 'Apply' }).focus()
  await page.keyboard.press('Tab'); await expect(page.getByRole('button', { name: 'Last 30 days' })).toBeFocused()
  const first = page.locator('[data-testid=chart]', { has: page.getByRole('button', { name: 'View data' }) }).first()
  test.skip((await first.count()) === 0, 'the demo seed has no chart data in this window')
  const card = page.locator(`[data-testid=chart][data-metric=${await first.getAttribute('data-metric')}]`)   // stable once the button's name flips
  const view = card.getByRole('button', { name: 'View data' })
  await view.focus(); await page.keyboard.press('Enter')
  await expect(card.getByRole('button', { name: 'Hide data' })).toHaveAttribute('aria-expanded', 'true')
  await expect(card.locator('table')).toBeVisible()
  const outline = await card.getByRole('button', { name: 'Hide data' }).evaluate(e => getComputedStyle(e).outlineStyle)
  expect(outline).not.toBe('none')   // the visible focus ring
  const [dl] = await Promise.all([page.waitForEvent('download'), (async () => { await card.getByRole('button', { name: /as CSV$/ }).focus(); await page.keyboard.press('Enter') })()])
  expect(dl.suggestedFilename()).toMatch(/\.csv$/)
})

test('a Viewer reads every chart and downloads the exports', async ({ page }) => {
  await openAnalytics(page, 'analytics-viewer@example.com', 'Viewer')
  await expect(page.locator('[data-testid=kpi]')).toHaveCount(6)
  const card = withExports(page)
  test.skip((await card.count()) === 0, 'the demo seed has no chart data in this window')
  const [dl] = await Promise.all([page.waitForEvent('download'), card.getByRole('button', { name: /as XLSX$/ }).click()])
  expect(readFileSync(await dl.path()).subarray(0, 2).toString('latin1')).toBe('PK')
  await scan(page, 'analytics (Viewer)')
})

test('the exported SVG uses only colours that are token values of the active theme', async ({ page }) => {
  await openAnalytics(page, 'analytics-svgtokens@example.com')
  const card = page.locator('[data-testid=chart]', { has: page.getByRole('button', { name: /as SVG$/ }) }).first()
  test.skip((await card.count()) === 0, 'the demo seed has no chart data in this window')
  const [dl] = await Promise.all([page.waitForEvent('download'), card.getByRole('button', { name: /as SVG$/ }).click()])
  const svg = readFileSync(await dl.path(), 'utf8')
  const tokens = await page.evaluate(() => {
    const cs = getComputedStyle(document.documentElement)
    return [...document.styleSheets].flatMap(sh => [...sh.cssRules]).flatMap(r => [...((r as CSSStyleRule).style ?? [])]).filter(n => n.startsWith('--color-'))
      .map(n => cs.getPropertyValue(n).trim().toLowerCase())
  })
  const used = new Set((svg.match(/#[0-9a-fA-F]{6}\b|#[0-9a-fA-F]{3}\b/g) ?? []).map(c => c.toLowerCase()))
  expect(used.size, 'the drawing has colours').toBeGreaterThan(0)
  for (const c of used) expect(tokens, `${c} is not a design token`).toContain(c)
})

// REOS-141 (G19): one workbook with a parameters sheet and a sheet per metric, in both themes, with axe clean around the button, its busy state and its failure notice
for (const scheme of ['light', 'dark'] as const) {
  test.describe(`workbook, ${scheme} theme`, () => {
    test.use({ colorScheme: scheme })

    test('the Workbook (XLSX) button downloads a real .xlsx with a Parameters sheet and M1..M15, and axe is clean', async ({ page }) => {
      await openAnalytics(page, `analytics-workbook-${scheme}@example.com`)
      const button = page.getByRole('button', { name: 'Export all metrics as one XLSX workbook' })
      await expect(button).toBeVisible()
      await expect(button).toHaveText('Workbook (XLSX)')
      await scan(page, `analytics with the workbook button (${scheme})`)

      const [dl] = await Promise.all([page.waitForEvent('download'), button.click()])
      expect(dl.suggestedFilename()).toMatch(/^analytics-workbook-\d{4}-\d\d-\d\d_\d{4}-\d\d-\d\d\.xlsx$/)
      const bytes = readFileSync(await dl.path())
      expect(bytes.subarray(0, 2).toString('latin1')).toBe('PK')                    // a zip container
      const names = bytes.toString('latin1')                                        // zip entry names are stored in the clear
      expect(names).toContain('xl/workbook.xml')
      expect(names).toContain('xl/worksheets/sheet16.xml')                          // Parameters + 15 metrics
      expect(names).not.toContain('xl/worksheets/sheet17.xml')
      await expect(button).toBeEnabled()
      await scan(page, `analytics after the workbook download (${scheme})`)

      // a failed download says so inline, as an alert, and still passes axe
      await page.route('**/api/v1/analytics/xlsx*', r => r.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: 'boom' }) }))
      await button.click()
      await expect(page.getByRole('alert').filter({ hasText: 'Workbook export failed' })).toBeVisible()
      await scan(page, `analytics with the workbook failure (${scheme})`)
    })
  })
}

test('a Viewer downloads the workbook too', async ({ page }) => {
  await openAnalytics(page, 'analytics-workbook-viewer@example.com', 'Viewer')
  const [dl] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Export all metrics as one XLSX workbook' }).click()])
  expect(readFileSync(await dl.path()).subarray(0, 2).toString('latin1')).toBe('PK')
})
