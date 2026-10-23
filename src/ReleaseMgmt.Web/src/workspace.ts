/**
 * The workspace the person is working in (REOS-106, D45, QUESTIONS Q-106b). The server resolves it per request from `X-Workspace` (a slug), a route
 * segment, `?workspace=` on the live channel, or, for requests that cannot carry a header (links, downloads), the `reos.workspace` cookie (Q-105a).
 * The SPA names it on every `/api/` call, keeps the choice per person in this browser, and switching reloads the page so nothing of the old
 * workspace stays on screen.
 */
export const HEADER = 'X-Workspace'
export const COOKIE = 'reos.workspace'
const STORAGE_PREFIX = 'reos.workspace.'

export interface WorkspaceChoice { id: string; name: string; slug: string; region: string | null; role: string }

let current: string | null = null
export const currentWorkspace = () => current

/** The workspace's name for the live channel's query string: `?workspace=<slug>`, or nothing before one is chosen. */
export const hubQuery = () => (current ? `?workspace=${encodeURIComponent(current)}` : '')

/** Storage can be blocked (private window, site data off): a choice then lasts for the page load only, and never throws. */
function store(): Pick<Storage, 'getItem' | 'setItem'> | null {
  try { return typeof localStorage === 'undefined' ? null : localStorage } catch { return null }
}

/** The slug this person last chose in this browser, or null. */
export function remembered(userId: string, s: Pick<Storage, 'getItem'> | null = store()): string | null {
  try { return s?.getItem(STORAGE_PREFIX + userId) ?? null } catch { return null }
}

/**
 * Which of the person's workspaces to open: the one they chose last time, else the one the server resolved for the first request, else the default
 * workspace, else the first by name. Null when they belong to none.
 */
export function pick(list: WorkspaceChoice[], rememberedSlug: string | null, resolvedId: string | null | undefined): WorkspaceChoice | null {
  const bySlug = (slug: string | null) => (slug ? list.find(w => w.slug.toLowerCase() === slug.toLowerCase()) : undefined)
  return bySlug(rememberedSlug) ?? list.find(w => w.id === resolvedId) ?? bySlug('default') ?? list[0] ?? null
}

/** Names the workspace for this page load, remembers it for this person, and sets the cookie plain navigations fall back on. */
export function choose(slug: string, userId?: string, s: Pick<Storage, 'setItem'> | null = store()) {
  current = slug
  if (userId) { try { s?.setItem(STORAGE_PREFIX + userId, slug) } catch { /* the choice lasts for this page load only */ } }
  if (typeof document !== 'undefined') {
    try { document.cookie = `${COOKIE}=${encodeURIComponent(slug)}; Path=/; Max-Age=31536000; SameSite=Lax` } catch { /* a blocked cookie only costs plain links the fallback */ }
  }
}

/**
 * Where switching to another workspace lands. A page keeps its path (the same page in the other workspace); a train, gate or step belongs to the
 * workspace it was made in, so it would not exist in the other one and the path falls back to Releases (Q-106c). Query and hash are dropped: they
 * name rows of the old workspace too.
 */
export function pathAfterSwitch(pathname: string): string {
  const seg = pathname.split('/').filter(Boolean)
  return seg[0] === 'trains' ? '/' : pathname || '/'
}

/** Switches workspace: remembers it, then reloads at the adapted path so every screen and the live channel start again in the new workspace. */
export function switchTo(slug: string, userId: string) {
  choose(slug, userId)
  window.location.assign(pathAfterSwitch(window.location.pathname))
}

const isApi = (url: string, base: string) => {
  try { const u = new URL(url, base); return u.origin === new URL(base).origin && u.pathname.startsWith('/api/') } catch { return false }
}

/** Adds X-Workspace to every same-origin /api/ call (the app's own, the specs' page.evaluate calls and the signalr negotiate alike). Installed once. */
export function installWorkspaceHeader() {
  const w = window as Window & { __reosWorkspaceFetch?: boolean }
  if (w.__reosWorkspaceFetch) return
  w.__reosWorkspaceFetch = true
  const original = window.fetch.bind(window)
  window.fetch = (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url
    if (!current || !isApi(url, window.location.href)) return original(input, init)
    const headers = new Headers(init?.headers ?? (input instanceof Request ? input.headers : undefined))
    if (!headers.has(HEADER)) headers.set(HEADER, current)
    return original(input, { ...init, headers })
  }
}
