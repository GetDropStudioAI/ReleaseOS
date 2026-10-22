import { ApiError } from './api'

export const tMinus = (d: number) => d > 0 ? `T−${d}` : d === 0 ? 'T−0' : `T+${-d}`
export const day = (iso: string) => new Date(iso + 'T00:00:00Z').toLocaleDateString('en-GB', { weekday: 'short', day: 'numeric', month: 'short', timeZone: 'UTC' })
export const splitId = (title: string) => { const m = /^(R\d+\.\d+)\s+(.*)$/.exec(title); return m ? { id: m[1], name: m[2] } : { id: '', name: title } }
export const plural = (n: number, w: string) => `${n} ${w}${n === 1 ? '' : 's'}`
/** A duration in minutes as "2 h 30 m", "2 h" or "45 m". */
export const dur = (min: number) => { const m = Math.abs(Math.round(min)); return m >= 60 ? `${Math.floor(m / 60)} h${m % 60 ? ` ${m % 60} m` : ''}` : `${m} m` }
export const errMsg = (e: unknown, fallback = 'Something went wrong.') =>
  e instanceof ApiError ? (e.body?.message ?? e.message) : fallback
