import { expect, test } from '@playwright/test'

// SEC-E6: the page the app host serves (the built SPA in wwwroot, as production serves it) carries a Content-Security-Policy, and no screen breaks it.
// The Vite dev server on :6273 serves its own page without the policy (HMR needs inline script), so this spec talks to the app host on :6080 directly.
const APP = 'http://127.0.0.1:6080'

const SCREENS: { link: RegExp; heading: RegExp }[] = [
  { link: /^My work$/, heading: /^My work$/ },
  { link: /^Inbox/, heading: /^Inbox$/ },
  { link: /^Calendar$/, heading: /^Calendar$/ },
  { link: /^Analytics$/, heading: /^Analytics$/ },
  { link: /^Imports & exports$/, heading: /^Imports & exports$/ },
  { link: /^Sync health$/, heading: /^Sync health$/ },
  { link: /^Comm library$/, heading: /^Comm library$/ },
  { link: /^Connectors$/, heading: /^Connectors$/ },
  { link: /^Templates$/, heading: /^Train templates$/ },
  { link: /^Audit$/, heading: /^Audit$/ },
  { link: /^Admin$/, heading: /^Admin$/ },
]

test('the built app keeps its Content-Security-Policy and no screen violates it; the live connection still opens', async ({ page }) => {
  test.setTimeout(180_000)
  const home = await page.request.get(`${APP}/`)
  test.skip(!(home.headers()['content-type'] ?? '').includes('text/html'), 'no built web app in wwwroot: run npm run build in src/ReleaseMgmt.Web first')
  const csp = home.headers()['content-security-policy'] ?? ''
  expect(csp).toContain("script-src 'self'")
  expect(csp).toContain("frame-ancestors 'none'")

  const violations: string[] = []
  await page.addInitScript(() => document.addEventListener('securitypolicyviolation', e =>
    console.error(`CSP violation: ${e.violatedDirective} blocked ${e.blockedURI || '(inline)'} at ${e.sourceFile}:${e.lineNumber}`)))
  page.on('console', m => { if (/CSP violation|Content Security Policy/i.test(m.text())) violations.push(m.text()) })
  page.on('pageerror', e => violations.push(`page error: ${e.message}`))
  const sockets: string[] = []
  page.on('websocket', ws => sockets.push(ws.url()))

  await page.goto(`${APP}/`)
  await page.fill('input[type=email]', 'csp-rte@example.com')
  await page.selectOption('select', 'RTE')
  await page.click('button:has-text("Sign in (development)")')
  await expect(page.locator('.stream-row').first()).toBeVisible()

  await page.locator('.stream-row').first().click()   // the train workspace: gates, checklist, inspector
  await expect(page.locator('main h1')).toBeVisible()

  const nav = page.getByRole('navigation', { name: 'Primary' })
  for (const s of SCREENS) {
    await nav.getByRole('link', { name: s.link }).click()
    await expect(page.locator('main h1')).toHaveText(s.heading)
    if (s.heading.test('Analytics')) await expect(page.getByText('Loading chart…')).toHaveCount(0)
  }

  await expect.poll(() => sockets.some(u => u.includes('/hub/')), { message: 'SignalR opened its WebSocket under connect-src' }).toBe(true)
  expect(violations).toEqual([])

  // The detector itself works and the policy is enforced: injected inline script is refused, not run.
  const ran = await page.evaluate(() => {
    const s = document.createElement('script'); s.textContent = 'window.__injected = true'; document.body.appendChild(s)
    return (window as unknown as { __injected?: boolean }).__injected === true
  })
  expect(ran).toBe(false)
  await expect.poll(() => violations.filter(v => v.includes('script-src')).length).toBeGreaterThan(0)   // reported by the listener and by the browser console
})
