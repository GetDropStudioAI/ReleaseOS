// REOS-82 pure logic for the train's Links section: the URL of an external key and the words for a link's sync state.
// No imports from components, so `npm test` (node --test, types stripped) can load it directly.

/** The key shapes the server accepts (ExternalKeys.cs). Anything else is never made into a URL. */
const JIRA_ISSUE = /^[A-Z][A-Z0-9_]{1,19}-[0-9]{1,9}$/
const SN_NUMBER = /^(CHG|CTASK)[0-9]{4,12}$/

/**
 * SEC-C: a URL is built only from the connector's configured base URL (an admin setting, https) and a key that matches the strict shape above,
 * with the key percent-encoded. Never from link data alone. Null means "show the key as text": no base URL, a base URL that is not plain https,
 * a Jira fix version (no stable URL without its numeric id), or a key of any other shape.
 */
export function externalUrl(source: string, baseUrl: string | null | undefined, key: string): string | null {
  if (!baseUrl) return null
  let base: URL
  try { base = new URL(baseUrl) } catch { return null }
  if (base.protocol !== 'https:' || base.username || base.password || base.search || base.hash) return null
  const root = base.origin + base.pathname.replace(/\/+$/, '')
  if (source === 'Jira' && JIRA_ISSUE.test(key)) return `${root}/browse/${encodeURIComponent(key)}`
  if (source === 'ServiceNow' && SN_NUMBER.test(key)) {
    const table = key.startsWith('CTASK') ? 'change_task' : 'change_request'
    return `${root}/${table}.do?sysparm_query=${encodeURIComponent(`number=${key}`)}`
  }
  return null
}

export type Freshness = { glyph: string; word: string; cls: 'ok' | 'warn' | 'bad' | 'muted' }

/**
 * One of four words (Q-082a): broken (the source said not found or refused access), not yet checked (never read), stale (not refreshed for three
 * polling intervals), fresh. A mismatch is a disagreement, not a freshness state: the screen adds it beside the word.
 */
export function freshness(l: { syncState: string; lastSyncedAt: string | null; stale: boolean }): Freshness {
  if (l.syncState === 'NotFound' || l.syncState === 'AuthFailed') return { glyph: '✗', word: 'broken', cls: 'bad' }
  if (!l.lastSyncedAt) return { glyph: '○', word: 'not yet checked', cls: 'muted' }
  if (l.stale) return { glyph: '▲', word: 'stale', cls: 'warn' }
  return { glyph: '●', word: 'fresh', cls: 'ok' }
}

/** Why a broken link is broken, in words. */
export const brokenReason = (syncState: string) =>
  syncState === 'NotFound' ? 'not found in the source system' : syncState === 'AuthFailed' ? 'the connector was refused access' : ''

/** What the key looks like for the chosen system (the input's placeholder and hint). */
export const keyHint = (source: string) =>
  source === 'Jira' ? 'PAY-123 or PAY/4.5.0' : 'CHG0030001 or CTASK0010001'
