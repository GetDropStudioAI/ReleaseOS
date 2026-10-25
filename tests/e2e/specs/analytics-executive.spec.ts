import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { primaryNav, signIn } from './support'

// REOS-121 (RT6, D49): the VP and Director views of Analytics read across the workspaces the person belongs to. The e2e app runs the demo seed, so this asserts SHAPES
// (which workspaces are named, that grouping changes the table, that the report is requested), never exact numbers: those are pinned by the API tests and the oracle.
// Each test signs in as its own admin and creates its own second workspace, so the shared demo database is only added to.
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']

async function scan(page: Page, where: string) {
  for (const scheme of ['light', 'dark'] as const) {
    await page.emulateMedia({ colorScheme: scheme })
    const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
    const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
    expect(bad, `axe violations on ${where} (${scheme}):\n${bad.map(v => `${v.id}: ${v.help} ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join(' | ')}`).join('\n')}`).toEqual([])
  }
}

/** Signs in as an admin and gives them a second workspace (they become its member), as the workspaces spec does. Returns that workspace's name. */
async function twoWorkspaces(page: Page, id: string) {
  await signIn(page, `an-exec-${id}@example.com`, 'RTE')
  await primaryNav(page).getByRole('link', { name: 'Teams & settings' }).click()
  await page.getByRole('tab', { name: 'Workspace' }).click()
  const name = `Payments ${id}`
  const form = page.getByRole('group', { name: 'Create a workspace' })
  await form.getByLabel('Workspace name').fill(name)
  await form.getByLabel('Region').fill('EU')
  await form.getByRole('button', { name: 'Create workspace' }).click()
  await expect(page.locator('.os-notice-title', { hasText: `${name} created` })).toBeVisible()
  return name
}

async function openView(page: Page, view: 'vp' | 'director') {
  await primaryNav(page).getByRole('link', { name: 'Analytics' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Analytics' })).toBeVisible()
  await page.getByRole('tab', { name: view === 'vp' ? 'VP' : 'Director' }).click()
  await expect(page.getByRole('tab', { name: view === 'vp' ? 'VP' : 'Director' })).toHaveAttribute('aria-selected', 'true')
  // loaded = the headline figures have left their placeholders and every panel is drawn
  await expect(page.locator('[data-testid=kpi]').first()).toBeVisible()
  await expect(page.locator('[data-testid=exec-panel], [data-testid=chart]').first()).toBeVisible()
  await expect(page.getByText('Loading chart…')).toHaveCount(0)
}

test('the VP view names only the person\'s own workspaces, groups the rows, and asks for the portfolio report', async ({ page }) => {
  const id = Date.now().toString(36)
  const second = await twoWorkspaces(page, id)

  const asked: string[] = []
  page.on('request', r => { if (r.url().includes('/api/v1/analytics/M')) asked.push(decodeURIComponent(new URL(r.url()).search)) })
  await openView(page, 'vp')

  const heading = page.getByTestId('an-scope-heading')
  await expect(heading).toContainText(second)
  await expect(heading).toContainText('(2 of 2 of your workspaces)')
  await expect(heading).toContainText(/Calendar quarters|Fiscal quarters/)
  expect(asked.some(q => q.includes('workspaces=all') && q.includes('groupBy=workspace,quarter'))).toBe(true)
  await expect(page.getByTestId('kpi')).toHaveCount(6)
  await expect(page.getByTestId('kpi').locator('.os-metric-label')).toHaveText(['On time, CERT', 'On time, Production', 'Change fail', 'Time to restore', 'Exceptions', 'Open releases'])
  expect(page.url()).toContain('view=vp')

  // the workspace list offers only this person's own workspaces: all, plus each of the two
  const workspaces = page.getByRole('combobox', { name: 'Workspaces', exact: true })
  await expect(workspaces).toBeVisible()
  const options = await workspaces.evaluate((el: HTMLSelectElement) => Array.from(el.options).map(o => o.text))
  expect(options).toEqual(['All my workspaces (2)', expect.any(String), expect.any(String)])
  expect(options.join('|')).toContain(second)

  // grouping by quarter is asked of the API, and drops the workspace column when the demo data has rows to show (a fresh workspace has none, so the table can be the empty note)
  const m17 = page.locator('[data-testid=exec-panel][data-metric=M17]')
  await expect(m17).toBeVisible()
  const heads = m17.locator('thead th')
  const hasRows = (await heads.count()) > 0
  if (hasRows) await expect(heads.first()).toHaveText('Workspace')
  await page.getByRole('combobox', { name: 'Group by', exact: true }).selectOption({ label: 'Quarter' })
  await expect.poll(() => asked.some(q => q.includes('groupBy=quarter') && !q.includes('groupBy=workspace')), { message: 'the grouping is sent to the API' }).toBe(true)
  if (hasRows) {
    await expect(heads.first()).not.toHaveText('Workspace')
    expect(await heads.allInnerTexts()).toContain('Track')
  }

  // choosing one workspace narrows the heading
  const secondValue = await workspaces.evaluate((el: HTMLSelectElement, name: string) => Array.from(el.options).find(o => o.text.includes(name))!.value, second)
  await workspaces.selectOption(secondValue)
  await expect(heading).toContainText('(1 of 2 of your workspaces)')
  await workspaces.selectOption({ label: 'All my workspaces (2)' })

  // the portfolio report is requested for the chosen workspaces and arrives as a PDF
  const pdf = page.waitForResponse(r => r.url().includes('/api/v1/analytics/portfolio.pdf'))
  await page.getByRole('button', { name: 'Download the portfolio report as a PDF' }).click()
  const res = await pdf
  expect(new URL(res.url()).searchParams.get('workspaces')).toBe('all')
  expect(res.status()).toBe(200)
  expect(res.headers()['content-type']).toContain('application/pdf')

  await scan(page, 'the VP view')
})

test('the Director view shows team and version operations, blocker ageing and the slip-reason note, without serious axe violations', async ({ page }) => {
  const id = Date.now().toString(36) + 'd'
  await twoWorkspaces(page, id)
  await openView(page, 'director')
  await expect(page.getByTestId('kpi')).toHaveCount(5)
  await expect(page.locator('[data-testid=exec-panel][data-metric=M22]')).toBeVisible()
  await expect(page.locator('[data-testid=chart][data-metric=M3]')).toBeVisible()
  await expect(page.locator('[data-testid=chart][data-metric=M9]')).toBeVisible()
  await expect(page.locator('[data-testid=exec-panel][data-metric=M20]')).toContainText('Manual moves keep no reason code yet')
  const group = page.getByRole('combobox', { name: 'Group by', exact: true })
  await expect(group).toBeVisible()
  expect(await group.evaluate((el: HTMLSelectElement) => el.selectedOptions[0]?.text)).toBe('Team and release type')
  await scan(page, 'the Director view')
})

test('a person with one workspace is told there is nothing to combine, and the Operations view still reads their workspace', async ({ page }) => {
  await signIn(page, `an-exec-single-${Date.now().toString(36)}@example.com`, 'Viewer')
  await primaryNav(page).getByRole('link', { name: 'Analytics' }).click()
  await expect(page.getByTestId('an-scope-single')).toContainText('You belong to one workspace')
  await expect(page.getByTestId('an-scope-heading')).toContainText('(1 of 1 of your workspaces)')
  await expect(page.locator('[data-testid=kpi]').first()).toBeVisible()
  await scan(page, 'Operations with one workspace')
})
