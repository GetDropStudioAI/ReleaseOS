import { useId, type ReactNode } from 'react'
import { displayZone, zoneAbbr, zonedInputToUtc } from '../time'
import { inputZoneNote, rangeParts, timeParts } from '../utcTime.ts'

/** A time on screen (D44): "14:32 UTC" with the person's own time beside it ("(09:32 CDT)"; left out for a person who works in UTC). Screen readers hear the full label once. */
export function Time({ at, date, zone }: { at: string | number | Date; date?: boolean; zone?: string }) {
  const p = timeParts(at, { date, zone })
  return (
    <time className="os-time" dateTime={new Date(at).toISOString()}>
      <span aria-hidden="true">{p.utc}{p.local && <span className="os-time-local"> ({p.local})</span>}</span>
      <span className="sr-only">{p.label}</span>
    </time>
  )
}

/** A window: "Fri 30 Oct 02:00–04:00 UTC (…local…)". */
export function TimeRange({ from, to, zone }: { from: string | number | Date; to: string | number | Date; zone?: string }) {
  const p = rangeParts(from, to, { zone })
  return (
    <span className="os-time">
      <span aria-hidden="true">{p.utc}{p.local && <span className="os-time-local"> ({p.local})</span>}</span>
      <span className="sr-only">{p.label}</span>
    </span>
  )
}

/** The note under or beside a datetime-local field that says which zone it edits in (the display zone, not UTC), and what the value comes to in UTC. Link it with `aria-describedby={id}`. */
export function InputZoneNote({ id, value, children }: { id: string; value?: string; children?: ReactNode }) {
  let utc: string | undefined
  try { utc = value ? zonedInputToUtc(value) : undefined } catch { utc = undefined }
  return <span id={id} className="os-zone-note">{inputZoneNote(displayZone(), zoneAbbr(utc ?? Date.now()), utc)}{children}</span>
}

/** A stable id for an InputZoneNote. */
export const useZoneNoteId = () => `zn${useId().replace(/:/g, '')}`
