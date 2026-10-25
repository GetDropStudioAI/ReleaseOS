import { expect, type Page, type Request, type Response } from '@playwright/test'

export async function signIn(page: Page, email: string, role = 'RTE') {
  await page.goto('/')
  await page.fill('input[type=email]', email)
  await page.selectOption('select', role)
  await page.click('button:has-text("Sign in (development)")')
  await expect(releaseRow(page).first()).toBeVisible()
}

/** The Releases table (REOS-125, D50) and its rows: the rows that hold a release, not the "no match" line. */
export const releasesTable = (page: Page) => page.getByRole('table', { name: 'Releases' })
export const releaseRow = (page: Page, text?: string | RegExp) => releasesTable(page).locator('tbody tr:not(:has(.os-table-empty))', text === undefined ? {} : { hasText: text })

/** The shell (REOS-94): the sidebar's destinations, the Inbox bell in the utility bar, and the control-plane card that opens Sync health. */
export const primaryNav = (page: Page) => page.getByRole('navigation', { name: 'Primary' })
export const inboxBell = (page: Page) => page.getByRole('banner').getByRole('link', { name: /^Inbox/ })
export const syncHealthCard = (page: Page) => page.locator('aside[aria-label="Workspace"] a[href="/sync"]')

/**
 * Opens a page and waits for its heading. Pages show "Loading…" or an error in place of their h1, so on timeout the error says what main shows, which
 * API calls failed and which were still pending: an intermittent miss on a CI runner (macOS, 2026-10-23, the Rehearsal page) then explains itself.
 */
export async function openPage(page: Page, url: string) {
  const failed: string[] = [], pending = new Map<Request, string>()
  const name = (r: Request) => `${r.method()} ${new URL(r.url()).pathname}`
  const onRequest = (r: Request) => { if (r.url().includes('/api/')) pending.set(r, name(r)) }
  const onFinished = (r: Request) => { pending.delete(r) }
  const onFailed = (r: Request) => { pending.delete(r); failed.push(`${name(r)}: ${r.failure()?.errorText}`) }
  const onResponse = (r: Response) => { if (r.url().includes('/api/') && r.status() >= 400) failed.push(`${r.status()} ${name(r.request())}`) }
  page.on('request', onRequest); page.on('requestfinished', onFinished); page.on('requestfailed', onFailed); page.on('response', onResponse)
  try {
    await page.goto(url)
    await expect(page.locator('main h1')).toBeVisible()
  } catch (e) {
    const shown = await page.locator('main').first().innerText({ timeout: 1000 }).catch(() => '(no main element)')
    throw new Error(`No heading in main at ${page.url()}. Main shows: ${JSON.stringify(shown.slice(0, 300))}. Failed API calls: ${failed.join(', ') || 'none'}. `
      + `Still pending: ${[...pending.values()].join(', ') || 'none'}.\n${(e as Error).message}`)
  } finally {
    page.off('request', onRequest); page.off('requestfinished', onFinished); page.off('requestfailed', onFailed); page.off('response', onResponse)
  }
}

/** Sign out from the account panel the user card opens; waits for the sign-in page, so the logout request has finished. */
export async function signOut(page: Page) {
  await page.getByRole('button', { name: /account and appearance/ }).click()
  await page.getByRole('button', { name: 'Sign out', exact: true }).click()
  await expect(page.locator('input[type=email]')).toBeVisible()
}

/** Ids of a demo train and its gates, read through the signed-in page's own session. */
export async function trainByTitle(page: Page, title: string) {
  return page.evaluate(async t => {
    const list = await (await fetch('/api/v1/trains')).json() as { id: string; title: string }[]
    const row = list.find(r => r.title.includes(t))!
    const detail = await (await fetch(`/api/v1/trains/${row.id}`)).json() as { id: string; gates: { id: string; name: string; version: number; status: string }[] }
    return detail
  }, title)
}

/** The release page's tabs (REOS-97): Overview & readiness, Services, Dependencies, Approvals, Activity. Opens one by its name, from the tab strip. */
export async function openTab(page: Page, name: string) {
  const tab = page.getByRole('tab', { name: new RegExp(`^${name}`) })
  await tab.click()
  await expect(tab).toHaveAttribute('aria-selected', 'true')
}

/** The first runbook step of a train, adding one through the API when the demo train has none (REOS-124, REOS-143: a step Inspector needs a step to open). */
export async function firstStep(page: Page, trainId: string) {
  return page.evaluate(async id => {
    const json = { 'Content-Type': 'application/json' }
    const read = async () => (await (await fetch(`/api/v1/trains/${id}/steps`)).json()) as { id: string; title: string }[]
    let steps = await read()
    if (steps.length === 0) {
      const me = await (await fetch('/api/v1/me')).json() as { id: string }
      const r = await fetch(`/api/v1/trains/${id}/steps`, { method: 'POST', headers: json, body: JSON.stringify({ ownerUserId: me.id, title: 'Inspector check', section: 'Deploy', plannedStartAt: new Date(Date.now() + 7 * 86_400_000).toISOString(), plannedDurationMin: 15 }) })
      if (!r.ok) throw new Error(`could not add a step: ${r.status} ${await r.text()}`)
      steps = await read()
    }
    return steps[0]
  }, trainId)
}

/**
 * REOS-122 (RT7): the approver of a template is not its author, and a Governance Officer approves a Compliance gate. A spec that drafts a template through the API as the signed-in
 * person has it approved here, by a Governance Officer in a context of their own (shared demo database, one fixed e2e user).
 */
export async function approveAsGovernanceOfficer(page: Page, template: { id: string; version: number }) {
  const ctx = await page.context().browser()!.newContext()
  try {
    const gov = await ctx.newPage()
    await signIn(gov, 'e2e-template-gov@example.com', 'GovernanceOfficer')
    const status = await gov.evaluate(async t => (await fetch(`/api/v1/templates/${t.id}:approve`, { method: 'POST', headers: { 'Content-Type': 'application/json', 'If-Match': String(t.version) }, body: '{}' })).status, template)
    if (status !== 200) throw new Error(`approve failed: ${status}`)
  } finally { await ctx.close() }
}
