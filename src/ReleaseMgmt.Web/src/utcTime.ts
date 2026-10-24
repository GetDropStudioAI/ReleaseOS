/**
 * Times on screen (D44, REOS-136): UTC is the standard and is always labelled; the person's own zone (from the browser) and local time stand beside it,
 * and are left out when the person works in UTC. Pure functions of an instant and a zone, so the tests do not depend on the machine's zone.
 * `Display:TimeZone` (time.ts) is not used for showing a time; it stays for business-day maths, PDFs, ICS and the zone datetime-local fields edit in.
 */
import { browserZone, clockLabel, localClock } from './shell/clock.ts'

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']
const DAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']
const pad = (n: number) => String(n).padStart(2, '0')
const ms = (t: Date | string | number) => (t instanceof Date ? t.getTime() : typeof t === 'number' ? t : Date.parse(t))

/** 14:32, in UTC. */
export const utcHM = (t: Date | string | number) => { const d = new Date(ms(t)); return `${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}` }
/** Fri 30 Oct, the UTC day. */
export const utcDayLabel = (t: Date | string | number) => { const d = new Date(ms(t)); return `${DAYS[d.getUTCDay()]} ${d.getUTCDate()} ${MONTHS[d.getUTCMonth()]}` }

export interface TimeParts {
  /** "14:32 UTC", or "Fri 30 Oct 14:32 UTC" with the date. */
  utc: string
  /** "09:32 CDT" (the date is added when it differs from UTC's), null when the person works in UTC. */
  local: string | null
  /** The full text, "14:32 UTC (09:32 CDT)", for places that take a string only (titles, options, sentences). */
  text: string
  /** What a screen reader hears, from clockLabel. */
  label: string
}

/** One instant as UTC with the person's time beside it. `date` adds the UTC day. `zone` is the person's IANA zone (default: the browser's). */
export function timeParts(t: Date | string | number, opts: { date?: boolean; zone?: string } = {}): TimeParts {
  const at = ms(t), zone = opts.zone ?? browserZone()
  const utc = `${opts.date ? `${utcDayLabel(at)} ` : ''}${utcHM(at)} UTC`
  const local = localClock(at, zone)
  return { utc, local, text: local ? `${utc} (${local})` : utc, label: clockLabel(at, zone) }
}

/** "Fri 30 Oct 02:00–04:00 UTC (Thu 29 Oct 21:00 CDT – 23:00 CDT)": a window as UTC, with a second UTC date when it ends on another day. */
export function rangeParts(from: Date | string | number, to: Date | string | number, opts: { zone?: string } = {}): TimeParts {
  const a = ms(from), b = ms(to), zone = opts.zone ?? browserZone()
  const sameDay = utcDayLabel(a) === utcDayLabel(b)
  const utc = sameDay ? `${utcDayLabel(a)} ${utcHM(a)}–${utcHM(b)} UTC` : `${utcDayLabel(a)} ${utcHM(a)} – ${utcDayLabel(b)} ${utcHM(b)} UTC`
  const la = localClock(a, zone), lb = localClock(b, zone)
  const local = la && lb ? `${la} – ${lb}` : null
  return { utc, local, text: local ? `${utc} (${local})` : utc, label: `${clockLabel(a, zone)} to ${clockLabel(b, zone)}` }
}

/** The text form of timeParts, for strings. */
export const timeText = (t: Date | string | number, opts: { date?: boolean; zone?: string } = {}) => timeParts(t, opts).text
export const rangeText = (from: Date | string | number, to: Date | string | number, opts: { zone?: string } = {}) => rangeParts(from, to, opts).text

/** "Entered in CDT (America/Chicago)", the note beside a datetime-local field; with a value, what it comes to in UTC ("= Tue 1 Dec 20:00 UTC"). */
export function inputZoneNote(zone: string, abbr: string, utcIso?: string): string {
  const isUtc = ['UTC', 'Etc/UTC', 'Etc/GMT', 'GMT', 'Etc/Universal', 'Universal', 'Zulu', 'Etc/Zulu'].includes(zone)
  const base = isUtc ? 'Entered in UTC' : `Entered in ${abbr} (${zone}), not UTC`
  return utcIso && !isUtc ? `${base} · = ${timeText(utcIso, { date: true, zone: 'UTC' })}` : base
}
