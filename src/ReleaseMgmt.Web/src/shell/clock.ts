/**
 * The utility bar's clock (D44, REOS-94): UTC is the standard, labelled, and the person's own zone (from the browser) and local time stand beside it.
 * Pure functions of an instant and a zone, so the tests do not depend on the machine's zone.
 */
const MONTHS = ['JAN', 'FEB', 'MAR', 'APR', 'MAY', 'JUN', 'JUL', 'AUG', 'SEP', 'OCT', 'NOV', 'DEC']
const pad = (n: number) => String(n).padStart(2, '0')

/** "23 OCT 2026 · 14:32 UTC", as the design writes it. */
export function utcClock(ms: number): string {
  const d = new Date(ms)
  return `${pad(d.getUTCDate())} ${MONTHS[d.getUTCMonth()]} ${d.getUTCFullYear()} · ${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())} UTC`
}

const isUtc = (zone: string) => ['UTC', 'Etc/UTC', 'Etc/GMT', 'GMT', 'Etc/Universal', 'Universal', 'Zulu', 'Etc/Zulu'].includes(zone)

/** "09:32 CDT" in the given IANA zone, or null when the person works in UTC (no second clock). The date is added when it differs from UTC's. */
export function localClock(ms: number, zone: string): string | null {
  if (isUtc(zone)) return null
  const p = Object.fromEntries(new Intl.DateTimeFormat('en-US', { timeZone: zone, hourCycle: 'h23', hour: '2-digit', minute: '2-digit', day: '2-digit', month: 'short', timeZoneName: 'short' })
    .formatToParts(new Date(ms)).map(x => [x.type, x.value]))
  const utcDay = new Date(ms).getUTCDate()
  const day = Number(p.day) !== utcDay ? `${p.day} ${String(p.month).toUpperCase()} ` : ''
  return `${day}${p.hour === '24' ? '00' : p.hour}:${p.minute} ${p.timeZoneName}`
}

/** What a screen reader hears: the full UTC date and time, then the person's time with the zone's name spelt out. */
export function clockLabel(ms: number, zone: string): string {
  const utc = new Intl.DateTimeFormat('en-GB', { timeZone: 'UTC', dateStyle: 'long', timeStyle: 'short' }).format(new Date(ms))
  if (isUtc(zone)) return `${utc} UTC`
  const local = new Intl.DateTimeFormat('en-GB', { timeZone: zone, timeStyle: 'short' }).format(new Date(ms))
  const name = new Intl.DateTimeFormat('en-US', { timeZone: zone, timeZoneName: 'long' }).formatToParts(new Date(ms)).find(x => x.type === 'timeZoneName')?.value ?? zone
  return `${utc} UTC; your time ${local} ${name}`
}

/** The browser's IANA zone ("America/Chicago"); UTC when the browser does not say. */
export const browserZone = () => { try { return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC' } catch { return 'UTC' } }
