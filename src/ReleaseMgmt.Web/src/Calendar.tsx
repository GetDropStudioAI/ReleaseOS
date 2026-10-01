import { useCallback, useEffect, useId, useMemo, useRef, useState } from 'react'
import { ApiError, get, getStream, post, type Me, type StreamRow } from './api'
import { buildPath, ROOT, type Route } from './route'
import { announce } from './announce'
import { useFocusWhen } from './focus'
import { displayZone, fmtDayTime, fmtHM, serverNow, zoneAbbr } from './time'
import { useAction } from './useAction'

/**
 * Screen "Calendar" (REOS-51, docs/UI.md row "Calendar", Q-051*): month and week grids of trains (target date, deployment window, gate due dates, milestones) with freeze
 * and chill windows as tinted date ranges, and the "Subscribe" section for the ICS feed link.
 * Semantics: one role="grid" table (labelled by the month heading above it, column headers, gridcells) with a roving tabindex. Arrow keys move the day, Home/End the start/end of the week,
 * Page Up/Down the month (the week in week view), Enter opens the first train of the day; every entry is also a real button. Status is glyph + word +
 * colour; freeze days are tinted AND named in text (visible on the first day of each row, and for screen readers on every day). Dates are shown in Display:TimeZone.
 */
export interface CalTrain { id: string; title: string; status: string; riskTier: string; targetDate: string; window: { startsAt: string; endsAt: string } | null }
export interface CalGate { id: string; name: string; class: string; status: string; dueOn: string; trainId: string; trainTitle: string; trainStatus: string }
export interface CalFreeze { id: string; name: string; kind: 'Freeze' | 'Chill'; startsAt: string; endsAt: string; scope: string }
export interface CalMilestone { id: string; name: string; dueOn: string; done: boolean; trainId: string; trainTitle: string; trainStatus: string }
interface CalendarPayload { from: string; to: string; trains: CalTrain[]; gates: CalGate[]; freezeWindows: CalFreeze[]; milestones?: CalMilestone[] }
interface IcsTokenRow { id: string; createdAt: string; revokedAt: string | null; active: boolean; lastUsedAt: string | null }
interface IcsIssued { id: string; token: string; path: string; scope: string; createdAt: string }

// ---- date arithmetic on 'YYYY-MM-DD' strings (calendar days, no zone) ----
const pad = (n: number) => String(n).padStart(2, '0')
const parse = (s: string) => { const [y, m, d] = s.split('-').map(Number); return Date.UTC(y, m - 1, d) }
const iso = (ms: number) => { const d = new Date(ms); return `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())}` }
export const addDays = (s: string, n: number) => iso(parse(s) + n * 86_400_000)
export function addMonths(s: string, n: number) {
  const d = new Date(parse(s)); const y = d.getUTCFullYear(), m = d.getUTCMonth() + n
  return iso(Date.UTC(y, m, Math.min(d.getUTCDate(), new Date(Date.UTC(y, m + 1, 0)).getUTCDate())))
}
const weekday = (s: string) => (new Date(parse(s)).getUTCDay() + 6) % 7   // Monday = 0
export const startOfWeek = (s: string) => addDays(s, -weekday(s))
/** The calendar day an instant falls on in the display zone. */
const dayInZone = (t: string | number) => new Intl.DateTimeFormat('en-CA', { timeZone: displayZone(), year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date(t))
const todayIso = () => dayInZone(serverNow())

export type Mode = 'month' | 'week'
/** The days shown for a focus date: whole weeks covering the month, or the focus week. */
export function visibleDays(focus: string, mode: Mode): string[] {
  let first: string, last: string
  if (mode === 'week') { first = startOfWeek(focus); last = addDays(first, 6) }
  else { const m1 = `${focus.slice(0, 7)}-01`; first = startOfWeek(m1); last = addDays(startOfWeek(addDays(addMonths(m1, 1), -1)), 6) }
  const out: string[] = []
  for (let d = first; d <= last; d = addDays(d, 1)) out.push(d)
  return out
}

const TRAIN: Record<string, { glyph: string; cls: string; word: string }> = {
  Planning: { glyph: '○', cls: 'muted', word: 'planning' }, Gated: { glyph: '◐', cls: 'accent', word: 'gated' }, Executing: { glyph: '●', cls: 'accent', word: 'executing' },
  Complete: { glyph: '✓', cls: 'ok', word: 'complete' }, Aborted: { glyph: '✗', cls: 'bad', word: 'aborted' },
}
const GATE: Record<string, { glyph: string; cls: string; word: string }> = {
  Pending: { glyph: '○', cls: 'muted', word: 'pending' }, InProgress: { glyph: '◐', cls: 'accent', word: 'in progress' }, Certified: { glyph: '✓', cls: 'ok', word: 'certified' },
  Failed: { glyph: '✗', cls: 'bad', word: 'failed' }, Waived: { glyph: '✓', cls: 'ok', word: 'waived' },
}
const trainSt = (s: string) => TRAIN[s] ?? { glyph: '○', cls: 'muted', word: s.toLowerCase() }
const gateSt = (s: string) => GATE[s] ?? { glyph: '○', cls: 'muted', word: s.toLowerCase() }
const KIND = { Freeze: { glyph: '▲', cls: 'bad' }, Chill: { glyph: '◐', cls: 'warn' } } as const

interface Entry { key: string; kind: 'target' | 'window' | 'gate' | 'milestone'; trainId: string; gateId?: string; label: string; title: string; st?: { glyph: string; cls: string; word: string } }

const errText = (e: unknown) => (e instanceof ApiError ? (e.body?.message ?? e.message) : (e as Error).message)

/** Open a train. App wires `onOpen`; without it the screen changes the URL itself (history + popstate, the same path useRoute already listens to). */
function openRoute(r: Partial<Route>, onOpen?: (r: Partial<Route>) => void) {
  if (onOpen) return onOpen(r)
  window.history.pushState(null, '', buildPath({ ...ROOT, ...r }))
  window.dispatchEvent(new PopStateEvent('popstate'))
}

export function Calendar({ onOpen, trainId: current }: { me: Me; onOpen?: (r: Partial<Route>) => void; trainId?: string | null }) {
  const [mode, setMode] = useState<Mode>('month')
  const [focus, setFocus] = useState<string>(() => todayIso())
  const [data, setData] = useState<CalendarPayload | null>(null)
  const [error, setError] = useState<string | null>(null)
  const days = useMemo(() => visibleDays(focus, mode), [focus, mode])
  const first = days[0], last = days[days.length - 1]
  const captionId = useId()
  const cells = useRef(new Map<string, HTMLElement>())
  const kb = useRef(false)   // focus moved by keyboard: move DOM focus after the render
  const today = todayIso()

  // One request per visible range; a day of margin each side, because the server cuts days at UTC midnight and the screen at the display zone's.
  useEffect(() => {
    let live = true
    get<CalendarPayload>(`/api/v1/calendar?from=${addDays(first, -1)}&to=${addDays(last, 1)}`)
      .then(d => { if (live) { setData(d); setError(null) } })
      .catch(e => { if (live) setError(errText(e)) })
    return () => { live = false }
  }, [first, last])
  useEffect(() => { if (kb.current) { cells.current.get(focus)?.focus(); kb.current = false } }, [focus, mode, data])

  const byDay = useMemo(() => {
    const m = new Map<string, Entry[]>()
    const add = (d: string, e: Entry) => { const a = m.get(d); if (a) a.push(e); else m.set(d, [e]) }
    for (const t of data?.trains ?? []) {
      const st = trainSt(t.status)
      add(t.targetDate, { key: `t:${t.id}`, kind: 'target', trainId: t.id, label: `Target · ${t.title}`, title: `${t.title}, target release date, ${st.word}`, st })
      if (t.window) add(dayInZone(t.window.startsAt), { key: `w:${t.id}`, kind: 'window', trainId: t.id, label: `${fmtHM(t.window.startsAt)}–${fmtHM(t.window.endsAt)} ${zoneAbbr(t.window.startsAt)} window · ${t.title}`, title: `${t.title}, deployment window` })
    }
    for (const g of data?.gates ?? []) {
      const st = gateSt(g.status)
      add(g.dueOn, { key: `g:${g.id}`, kind: 'gate', trainId: g.trainId, gateId: g.id, label: `${g.name} due · ${g.trainTitle}`, title: `${g.name} gate of ${g.trainTitle}, due, ${st.word}`, st })
    }
    // Q-0844: milestones are ◆ entries; the state word says done, open or overdue (never a guard, so never "blocked").
    for (const m of data?.milestones ?? []) {
      const st = m.done ? { glyph: '✓', cls: 'ok', word: 'done' } : m.dueOn < today ? { glyph: '▲', cls: 'bad', word: 'overdue' } : { glyph: '○', cls: 'muted', word: 'open' }
      add(m.dueOn, { key: `m:${m.id}`, kind: 'milestone', trainId: m.trainId, label: `◆ ${m.name} · ${m.trainTitle}`, title: `Milestone ${m.name} of ${m.trainTitle}, ${st.word}`, st })
    }
    return m
  }, [data, today])

  const freezeDays = useMemo(() => {
    const m = new Map<string, CalFreeze[]>()
    for (const f of data?.freezeWindows ?? []) {
      const a = dayInZone(f.startsAt), b = dayInZone(Date.parse(f.endsAt) - 1000)
      for (let d = a; d <= b && d <= addDays(last, 1); d = addDays(d, 1)) { const arr = m.get(d); if (arr) arr.push(f); else m.set(d, [f]) }
    }
    return m
  }, [data, last])

  const step = useCallback((dir: 1 | -1) => setFocus(f => (mode === 'month' ? addMonths(f, dir) : addDays(f, 7 * dir))), [mode])
  const moveTo = (d: string) => { kb.current = true; setFocus(d) }
  const openFirst = (day: string) => {
    const e = (byDay.get(day) ?? [])[0]
    if (e) openRoute({ view: 'trains', trainId: e.trainId, selection: e.gateId ? { kind: 'gate', id: e.gateId } : null }, onOpen)
  }
  const onKey = (e: React.KeyboardEvent, day: string) => {
    if (e.target !== e.currentTarget || e.ctrlKey || e.metaKey || e.altKey) return
    const k = e.key
    const to = k === 'ArrowLeft' ? addDays(day, -1) : k === 'ArrowRight' ? addDays(day, 1) : k === 'ArrowUp' ? addDays(day, -7) : k === 'ArrowDown' ? addDays(day, 7)
      : k === 'Home' ? startOfWeek(day) : k === 'End' ? addDays(startOfWeek(day), 6)
      : k === 'PageUp' ? (mode === 'month' ? addMonths(day, -1) : addDays(day, -7)) : k === 'PageDown' ? (mode === 'month' ? addMonths(day, 1) : addDays(day, 7)) : null
    if (to) { e.preventDefault(); moveTo(to) }
    else if (k === 'Enter') { e.preventDefault(); openFirst(day) }
  }

  const title = mode === 'month'
    ? new Date(parse(focus)).toLocaleDateString('en-GB', { month: 'long', year: 'numeric', timeZone: 'UTC' })
    : `Week of ${new Date(parse(first)).toLocaleDateString('en-GB', { day: 'numeric', month: 'long', year: 'numeric', timeZone: 'UTC' })}`
  const weeks: string[][] = []
  for (let i = 0; i < days.length; i += 7) weeks.push(days.slice(i, i + 7))
  const MAX = 4

  return (
    <section>
      <h1>Calendar</h1>
      <div className="cal-bar">
        <span className="tabs" role="group" aria-label="View">
          <button type="button" className="choice" aria-pressed={mode === 'month'} onClick={() => setMode('month')}>Month</button>
          <button type="button" className="choice" aria-pressed={mode === 'week'} onClick={() => setMode('week')}>Week</button>
        </span>
        <span>
          <button type="button" className="text" onClick={() => step(-1)}><span aria-hidden="true">‹</span> Previous {mode}</button>{' '}
          <button type="button" className="text" onClick={() => setFocus(todayIso())}>Today</button>{' '}
          <button type="button" className="text" onClick={() => step(1)}>Next {mode} <span aria-hidden="true">›</span></button>
        </span>
        <span className="muted">Dates in {displayZone()} ({zoneAbbr()})</span>
      </div>
      {error && <p className="bad" role="alert">✗ {error}</p>}
      {!data && !error && <p className="muted" role="status">Loading…</p>}

      <div id={captionId} className="cal-caption" aria-live="polite">{title}</div>   {/* outside the grid: a role=grid table may not contain a caption (axe aria-required-children) */}
      <table className={`cal ${mode}`} role="grid" aria-labelledby={captionId} data-testid="calendar-grid">
        <thead>
          <tr>{['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'].map(d => <th key={d} scope="col" abbr={d}>{d.slice(0, 3)}</th>)}</tr>
        </thead>
        <tbody>
          {weeks.map(w => (
            <tr key={w[0]}>
              {w.map((day, col) => {
                const entries = byDay.get(day) ?? []
                const fz = freezeDays.get(day) ?? []
                const kind = fz.some(f => f.kind === 'Freeze') ? 'freeze' : fz.length ? 'chill' : ''
                const out = mode === 'month' && day.slice(0, 7) !== focus.slice(0, 7)
                const shown = mode === 'month' ? entries.slice(0, MAX) : entries
                const num = Number(day.slice(8))
                const cls = ['cal-day', kind && `cal-${kind}`, kind && 'cal-tint', out && 'cal-out', day === focus && 'cal-sel', day === today && 'cal-today'].filter(Boolean).join(' ')
                return (
                  <td key={day} role="gridcell" ref={el => { if (el) cells.current.set(day, el); else cells.current.delete(day) }}
                    className={cls} tabIndex={day === focus ? 0 : -1} aria-selected={day === focus} data-date={day}
                    onKeyDown={e => onKey(e, day)} onClick={e => { if (e.target === e.currentTarget) setFocus(day) }}>
                    <div className={`cal-daynum${day === today ? ' today' : ''}`}>
                      <span aria-hidden="true">{num === 1 || (mode === 'week' && col === 0) ? new Date(parse(day)).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', timeZone: 'UTC' }) : num}</span>
                      {/* every day carries its full date for screen readers: a bare "30" under an "October" caption would be read as 30 October */}
                      <span className="sr-only">{new Date(parse(day)).toLocaleDateString('en-GB', { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric', timeZone: 'UTC' })}</span>
                      {day === today && <> · today</>}
                    </div>
                    {fz.map(f => {
                      const k = KIND[f.kind]
                      const visible = day === dayInZone(f.startsAt) || col === 0 || day === first
                      return (
                        <div key={f.id} className={`cal-freeze-label ${k.cls}`} data-testid="freeze-label">
                          {visible && <span aria-hidden="true">{k.glyph} {f.kind} · {f.name}</span>}
                          <span className="sr-only">{f.kind}: {f.name}, {f.scope}</span>
                          {mode === 'week' && <span className="muted"> ({f.scope})</span>}
                        </div>
                      )
                    })}
                    {shown.map(e => (
                      <button key={e.key} type="button" className={`cal-entry ${e.kind}`} title={e.title} data-testid={`cal-${e.kind}`}
                        onClick={() => openRoute({ view: 'trains', trainId: e.trainId, selection: e.gateId ? { kind: 'gate', id: e.gateId } : null }, onOpen)}>
                        <span className="cal-what">{e.label}</span>
                        {e.st && <> <span className={e.st.cls}>{e.st.glyph} {e.st.word}</span></>}
                      </button>
                    ))}
                    {mode === 'month' && entries.length > MAX && (
                      <button type="button" className="text cal-more" onClick={() => { setFocus(day); setMode('week') }}>+{entries.length - MAX} more (week view)</button>
                    )}
                  </td>
                )
              })}
            </tr>
          ))}
        </tbody>
      </table>
      <p className="muted cal-help">
        <span className="bad">▲ Freeze</span> and <span className="warn">◐ Chill</span> days are shaded. Arrow keys move by day, Page Up and Page Down by {mode}, Home and End to the week edges; Enter opens the first item on the day and each item is also a button.
      </p>

      <Subscribe current={current} />
    </section>
  )
}

/** The ICS feed link: created, rotated and revoked here; the secret is shown once, straight after it is issued, and is not recoverable (only its hash is stored). */
function Subscribe({ current }: { current?: string | null }) {
  const [rows, setRows] = useState<IcsTokenRow[] | null>(null)
  const [issued, setIssued] = useState<IcsIssued | null>(null)
  const [confirm, setConfirm] = useState<{ id: string; action: 'rotate' | 'revoke' } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const { run: guard, pending } = useAction()
  const busy = pending !== null
  const [copied, setCopied] = useState<string | null>(null)
  const heading = useRef<HTMLHeadingElement>(null)
  const firstLink = useRef<HTMLInputElement>(null)
  const createRef = useRef<HTMLButtonElement>(null)
  const confirmRef = useFocusWhen<HTMLButtonElement>(confirm !== null)
  const triggers = useRef(new Map<string, HTMLButtonElement>())   // Rotate / Revoke per row, to give focus back on Cancel
  const [trains, setTrains] = useState<StreamRow[]>([])
  const [trainId, setTrainId] = useState('')
  const load = useCallback(() => get<IcsTokenRow[]>('/api/v1/me/ics-tokens').then(r => { setRows(r); setError(null) }).catch(e => setError(errText(e))), [])
  useEffect(() => { load() }, [load])
  // One-train subscriptions default to the train last opened in the Stream, else the first.
  useEffect(() => { getStream().then(t => { setTrains(t); setTrainId(id => id || (current && t.some(x => x.id === current) ? current : t[0]?.id) || '') }).catch(() => setTrains([])) }, [])   // eslint-disable-line react-hooks/exhaustive-deps

  const active = rows?.find(r => r.active) ?? null
  // One request at a time: a double click on Create or Confirm is ignored. After each, focus goes where the next step is, never to <body>.
  const run = (label: string, fn: () => Promise<void>, then: () => void) => guard(label, async () => {
    setError(null)
    try { await fn(); await load(); requestAnimationFrame(then) } catch (e) { setError(errText(e)) }
  })
  const ready = () => { announce('Link ready. Copy it now: it is shown only once.'); firstLink.current?.focus() }
  const create = () => run('Creating…', async () => { setIssued(await post<IcsIssued>('/api/v1/me/ics-tokens', { scope: 'all' })); setCopied(null) }, ready)
  const rotate = (id: string) => run('Rotating…', async () => { setIssued(await post<IcsIssued>(`/api/v1/me/ics-tokens/${id}:rotate`, { scope: 'all' })); setConfirm(null); setCopied(null) }, ready)
  const revoke = (id: string) => run('Revoking…', async () => { await post(`/api/v1/me/ics-tokens/${id}:revoke`, {}); setIssued(null); setConfirm(null) },
    () => { announce('Link revoked.'); createRef.current?.focus() })
  const cancel = () => { const c = confirm; setConfirm(null); requestAnimationFrame(() => c && triggers.current.get(`${c.id}:${c.action}`)?.focus()) }
  const hide = () => { setIssued(null); requestAnimationFrame(() => heading.current?.focus()) }

  const origin = window.location.origin
  const urls = (t: string) => [
    { key: 'all', label: 'All trains', url: `${origin}/api/v1/ics/${t}.ics` },
    { key: 'mine', label: 'My work (my gates, steps and milestones)', url: `${origin}/api/v1/ics/${t}/mine.ics` },
    { key: 'freezes', label: 'Freeze and chill windows', url: `${origin}/api/v1/ics/${t}/freezes.ics` },
    ...(trainId ? [{ key: 'train', label: 'One train', url: `${origin}/api/v1/ics/${t}/trains/${encodeURIComponent(trainId)}.ics` }] : []),
  ]
  const copy = async (key: string, url: string) => {
    try { await navigator.clipboard.writeText(url); setCopied(key); announce('Link copied.') }
    catch { setCopied(null); setError('The browser did not allow copying. Select the address and copy it by hand.') }
  }

  return (
    <section aria-labelledby="subscribe-h">
      <h2 id="subscribe-h" className="cap" ref={heading} tabIndex={-1}>Subscribe</h2>
      <p className="muted">Add the calendar to Outlook, Apple Calendar or Google Calendar with a link. Anyone who has the link can see gate, milestone, window and freeze titles and times, so keep it private. You have one link; it works until you rotate or revoke it.</p>
      {error && <p className="bad" role="alert">✗ {error}</p>}
      {rows === null && !error && <p className="muted">Loading…</p>}

      {issued && (
        <div className="cal-issued" role="region" aria-label="Your new calendar link">
          <p className="ok">✓ Link ready. Copy it now: it is shown only once and cannot be shown again. If you lose it, rotate to get a new one.</p>
          <table className="grid" aria-label="Calendar feed links">
            <thead><tr><th scope="col">Feed</th><th scope="col">Link</th><th scope="col">Action</th></tr></thead>
            <tbody>
              {urls(issued.token).map((u, i) => (
                <tr key={u.key}>
                  <td>{u.key === 'train'
                    ? <label>One train <select className="line" value={trainId} onChange={e => setTrainId(e.target.value)} aria-label="Train for the one-train feed">{trains.map(t => <option key={t.id} value={t.id}>{t.title}</option>)}</select></label>
                    : u.label}</td>
                  <td className="wrap"><input ref={i === 0 ? firstLink : undefined} className="line wide cal-mono" readOnly value={u.url} aria-label={`${u.label} link`} onFocus={e => e.currentTarget.select()} /></td>
                  <td><button type="button" className="text" onClick={() => copy(u.key, u.url)}>{copied === u.key ? '✓ Copied' : 'Copy'}</button></td>
                </tr>
              ))}
            </tbody>
          </table>
          <p><button type="button" className="text" onClick={hide}>I have copied it, hide the link</button></p>
        </div>
      )}

      {rows && !active && (
        <p>You have no calendar link. <button type="button" className="text strong" ref={createRef} disabled={busy} onClick={() => void create()}>{pending === 'Creating…' ? pending : 'Create link'}</button></p>
      )}

      {rows && rows.length > 0 && (
        <table className="grid" aria-label="Calendar links">
          <thead><tr><th scope="col">Created</th><th scope="col">Last used</th><th scope="col">State</th><th scope="col">Actions</th></tr></thead>
          <tbody>
            {rows.map(r => (
              <tr key={r.id} data-testid="ics-row">
                <td className="cal-mono">{fmtDayTime(r.createdAt)}</td>
                <td className="cal-mono">{r.lastUsedAt ? fmtDayTime(r.lastUsedAt) : <span className="muted">not since the server started</span>}</td>
                <td>{r.active ? <span className="ok">● active</span> : <span className="muted">○ revoked {r.revokedAt ? fmtDayTime(r.revokedAt) : ''}</span>}</td>
                <td className="wrap">
                  {r.active && confirm?.id !== r.id && (
                    <>
                      <button type="button" className="text" ref={el => { if (el) triggers.current.set(`${r.id}:rotate`, el); else triggers.current.delete(`${r.id}:rotate`) }} disabled={busy} onClick={() => setConfirm({ id: r.id, action: 'rotate' })}>Rotate</button>{' '}
                      <button type="button" className="text" ref={el => { if (el) triggers.current.set(`${r.id}:revoke`, el); else triggers.current.delete(`${r.id}:revoke`) }} disabled={busy} onClick={() => setConfirm({ id: r.id, action: 'revoke' })}>Revoke</button>
                    </>
                  )}
                  {r.active && confirm?.id === r.id && (
                    <span role="group" aria-label={confirm.action === 'rotate' ? 'Confirm rotate' : 'Confirm revoke'}>
                      <span className="warn">{confirm.action === 'rotate'
                        ? '▲ The current link stops working at once. Calendars using it will stop updating until you paste the new link.'
                        : '▲ The link stops working at once and you will have no calendar link until you create one.'}</span>{' '}
                      <button type="button" className="text strong" ref={confirmRef} disabled={busy} onClick={() => void (confirm.action === 'rotate' ? rotate(r.id) : revoke(r.id))}>{busy ? pending : `Confirm ${confirm.action}`}</button>{' '}
                      <button type="button" className="text" disabled={busy} onClick={cancel}>Cancel</button>
                    </span>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  )
}
