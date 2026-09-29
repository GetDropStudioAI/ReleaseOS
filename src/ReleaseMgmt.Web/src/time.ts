/**
 * Display time (D24): the server stores and returns UTC; the client formats in the organisation's zone (config Display:TimeZone).
 * `serverNow()` is the server's clock as last pushed over SignalR, so countdowns are right even when this computer's clock is wrong.
 */
let zone = 'America/Chicago'
let offsetMs = 0   // serverTime - browser clock, at the moment the last ServerTime arrived

export const setDisplayZone = (z: string) => { zone = z }
export const displayZone = () => zone
export const setServerTime = (iso: string) => { const t = Date.parse(iso); if (!Number.isNaN(t)) offsetMs = t - Date.now() }
export const serverNow = () => Date.now() + offsetMs
export const clockOffsetMs = () => offsetMs

const parts = (d: Date, opts: Intl.DateTimeFormatOptions) =>
  Object.fromEntries(new Intl.DateTimeFormat('en-GB', { timeZone: zone, hourCycle: 'h23', ...opts }).formatToParts(d).map(p => [p.type, p.value]))

/** 05:30 in the display zone. */
export const fmtHM = (d: Date | string | number) => { const p = parts(new Date(d), { hour: '2-digit', minute: '2-digit' }); return `${p.hour}:${p.minute}` }
export const fmtHMS = (d: Date | string | number) => { const p = parts(new Date(d), { hour: '2-digit', minute: '2-digit', second: '2-digit' }); return `${p.hour}:${p.minute}:${p.second}` }
/** Fri 30 Oct */
export const fmtDay = (d: Date | string | number) => { const p = parts(new Date(d), { weekday: 'short', day: 'numeric', month: 'short' }); return `${p.weekday} ${p.day} ${p.month}` }
export const fmtDayTime = (d: Date | string | number) => `${fmtDay(d)} ${fmtHM(d)}`
/** CT, CDT, GMT+1 ... */
export const zoneAbbr = (d: Date | string | number = Date.now()) =>
  new Intl.DateTimeFormat('en-US', { timeZone: zone, timeZoneName: 'short' }).formatToParts(new Date(d)).find(p => p.type === 'timeZoneName')?.value ?? zone   // en-US gives CDT/CST where en-GB gives GMT-5
/** h:mm:ss for a countdown; negative values keep their sign. */
export function fmtCountdown(totalSec: number): string {
  const s = Math.abs(Math.trunc(totalSec)); const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), ss = s % 60
  return `${totalSec < 0 ? '−' : ''}${h}:${String(m).padStart(2, '0')}:${String(ss).padStart(2, '0')}`
}
export const fmtMinSec = (totalSec: number) => { const s = Math.max(0, Math.trunc(totalSec)); return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}` }

/** UTC instant -> "YYYY-MM-DDTHH:mm" wall-clock in the display zone (what a datetime-local input shows). */
export function utcToZonedInput(iso: string): string {
  const p = parts(new Date(iso), { year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' })
  return `${p.year}-${p.month}-${p.day}T${p.hour}:${p.minute}`
}
/** "YYYY-MM-DDTHH:mm" read as wall-clock in the display zone -> UTC ISO (whole seconds). Handles DST by re-checking the offset. */
export function zonedInputToUtc(local: string): string {
  const [d, t] = local.split('T'); const [y, mo, da] = d.split('-').map(Number); const [h, mi] = t.split(':').map(Number)
  const asUtc = Date.UTC(y, mo - 1, da, h, mi)
  const offsetAt = (ms: number) => { const p = parts(new Date(ms), { year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit' }); return Date.UTC(+p.year, +p.month - 1, +p.day, +p.hour, +p.minute, +p.second) - ms }
  let ms = asUtc - offsetAt(asUtc)
  ms = asUtc - offsetAt(ms)
  return new Date(ms).toISOString().replace(/\.\d{3}Z$/, 'Z')
}
