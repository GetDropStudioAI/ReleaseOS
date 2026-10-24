import { useCallback, useEffect, useId, useMemo, useRef, useState } from 'react'
import { ChevronLeft, ChevronRight, Download, Plus } from 'lucide-react'
import { ApiError, get, getStream, type Me, type StreamRow } from './api'
import { buildPath, ROOT, type Route } from './route'
import { CalendarSubscribe } from './CalendarSubscribe'
import {
  addDays, addMonths, applyFilters, conflictsOf, filtered, dayLabel, durationText, entriesByDay, freezeDays, NO_FILTER, parseDay, RISKS, rangeTitle, releaseRows, riskLabel, STATUSES,
  startOfWeek, stamp, utcDay, visibleDays, type CalendarPayload, type CalFreeze, type Conflict, type Entry, type Filters, type Mode,
} from './calendarModel'
import './calendar.css'
import { listReleaseTypes } from './mgmt/releaseTypesApi'
import { sortTypes, type ReleaseTypeRow } from './mgmt/releaseTypesLogic'
import { browserZone } from './shell/clock'
import { serverNow } from './time'
import { Button, DataTable, EmptyState, ErrorState, FilterSelect, LoadingState, Notice, PageHeading, Panel, PropertyList, StatusChip, TabPanel, Tabs, TwoLine, type Column } from './ui'

/**
 * Screen "Portfolio & calendar" (REOS-96, REOS-134, REOS-51 underneath; docs/UI.md, docs/proposals/redesign-mapping.md). Tabs: Week calendar (the designed one), Month, Timeline and
 * Release list; filters; upcoming windows; capacity and constraints; the Subscribe panel for the ICS feed. Every day is a UTC day and every time is UTC with the person's own time
 * beside it (D44). The grids are role="grid" with a roving tabindex: arrow keys move the day (and, in the week grid, the release row), Page Up and Page Down the week or month,
 * Home and End the week's edges, Enter opens the first item in the cell; every entry is also a real button. Status is a word in a chip, never colour alone; a freeze or chill is
 * tinted and named in text.
 */
export type { CalTrain, CalGate, CalFreeze, CalMilestone } from './calendarModel'
type TabId = 'week' | 'month' | 'timeline' | 'list'
const TIMELINE_DAYS = 28
const UPCOMING_DAYS = 30
const FREEZE_ROW = '__freeze'
const EMPTY_ROW = '__empty'   // the grid keeps its header (and today's column) when the week has nothing in it

const errText = (e: unknown) => (e instanceof ApiError ? (e.body?.message ?? e.message) : (e as Error).message)
const todayIso = () => utcDay(serverNow())

/** Open a train. App wires `onOpen`; without it the screen changes the URL itself (history + popstate, the same path useRoute already listens to). */
function openRoute(r: Partial<Route>, onOpen?: (r: Partial<Route>) => void) {
  if (onOpen) return onOpen(r)
  window.history.pushState(null, '', buildPath({ ...ROOT, ...r }))
  window.dispatchEvent(new PopStateEvent('popstate'))
}

/** One request for a range of UTC days; a day of margin each side. Keeps the last payload while the next loads. */
function useRange(from: string | null, to: string | null) {
  const [data, setData] = useState<CalendarPayload | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [nonce, setNonce] = useState(0)
  useEffect(() => {
    if (!from || !to) return
    let live = true
    get<CalendarPayload>(`/api/v1/calendar?from=${addDays(from, -1)}&to=${addDays(to, 1)}`)
      .then(d => { if (live) { setData(d); setError(null) } })
      .catch(e => { if (live) setError(errText(e)) })
    return () => { live = false }
  }, [from, to, nonce])
  const fresh = data !== null && from !== null && to !== null && data.from === addDays(from, -1) && data.to === addDays(to, 1)   // the payload is for the range now shown
  return { data, error, fresh, retry: () => { setError(null); setNonce(n => n + 1) } }
}

function EntryCard({ e, onOpen, compact }: { e: Entry; onOpen: () => void; compact?: boolean }) {
  return (
    <button type="button" className={`os-cal-card ${e.tone}${e.conflict ? ' conflict' : ''}${compact ? ' compact' : ''} ${e.kind}`} title={e.title} data-testid={`cal-${e.kind}`} onClick={onOpen}>
      <span className="os-cal-card-name">{e.name}</span>
      <span className="os-cal-card-detail">{e.detail}</span>
      <StatusChip tone={e.tone}>{e.label}</StatusChip>
      {e.conflict && <span className="os-cal-card-conflict">▲ {e.conflict}</span>}
    </button>
  )
}

function FreezeLabel({ f, visible }: { f: CalFreeze; visible: boolean }) {
  const glyph = f.kind === 'Freeze' ? '▲' : '◐'
  return (
    <div className={`os-cal-freeze ${f.kind === 'Freeze' ? 'danger' : 'warning'}`} data-testid="freeze-label">
      {visible && <span aria-hidden="true">{glyph} {f.kind} · {f.name}</span>}
      <span className="sr-only">{f.kind}: {f.name}, {f.scope}</span>
    </div>
  )
}

export function Calendar({ onOpen, onPlan, trainId: current }: { me: Me; onOpen?: (r: Partial<Route>) => void; onPlan?: () => void; trainId?: string | null }) {
  const [tab, setTab] = useState<TabId>('week')
  const [focus, setFocus] = useState<string>(() => todayIso())
  const [focusRow, setFocusRow] = useState<string | null>(null)
  const [filters, setFilters] = useState<Filters>(NO_FILTER)
  const [types, setTypes] = useState<ReleaseTypeRow[]>([])
  useEffect(() => { listReleaseTypes(true).then(r => setTypes(sortTypes(r))).catch(() => setTypes([])) }, [])   // REOS-116: the choices are the workspace's types, not only those with a release in view
  const today = todayIso()
  const zone = browserZone()
  const titleId = useId()
  const cells = useRef(new Map<string, HTMLElement>())
  const kb = useRef(false)   // focus moved by keyboard: move DOM focus after the render

  const mode: Mode = tab === 'month' ? 'month' : 'week'
  const days = useMemo(() => tab === 'timeline' ? Array.from({ length: TIMELINE_DAYS }, (_, i) => addDays(startOfWeek(focus), i)) : visibleDays(focus, mode), [focus, mode, tab])
  const first = days[0], last = days[days.length - 1]
  const view = useRange(tab === 'list' ? null : first, tab === 'list' ? null : last)
  const upcoming = useRange(today, addDays(today, UPCOMING_DAYS))
  const [stream, setStream] = useState<StreamRow[] | null>(null)
  const [streamError, setStreamError] = useState<string | null>(null)
  const [ahead, setAhead] = useState<{ id: string; name: string; kind: string; startsAt: string; endsAt: string }[] | null>(null)
  const loadStream = useCallback(() => { setStreamError(null); getStream().then(setStream).catch(e => setStreamError(errText(e))) }, [])
  useEffect(() => { getStream().then(setStream).catch(e => setStreamError(errText(e))) }, [])
  useEffect(() => { get<typeof ahead>('/api/v1/freeze-windows/ahead').then(setAhead).catch(() => setAhead([])) }, [])   // the panel says "none" if this fails; the calendar itself shows the error

  const data = useMemo(() => view.data && applyFilters(view.data, filters), [view.data, filters])
  const byDay = useMemo(() => (data ? entriesByDay(data, today) : new Map<string, Entry[]>()), [data, today])
  const fz = useMemo(() => (data ? freezeDays(data, last) : new Map<string, CalFreeze[]>()), [data, last])
  const rows = useMemo(() => (data ? releaseRows(data, byDay, days) : []), [data, byDay, days])
  const gridRows = useMemo(() => { const g = [...(days.some(d => fz.has(d)) ? [FREEZE_ROW] : []), ...rows.map(r => r.id)]; return g.length ? g : [EMPTY_ROW] }, [rows, days, fz])
  const activeRow = focusRow && gridRows.includes(focusRow) ? focusRow : gridRows[0] ?? ''
  // Focus follows a keyboard move. The cell may not exist yet (the next week is still loading, or its rows differ): keep trying until it does.
  useEffect(() => {
    if (!kb.current || !view.fresh) return   // wait for the new range: its rows decide which cell can take focus
    const el = cells.current.get(tab === 'week' ? `${activeRow}|${focus}` : `|${focus}`)
    if (el) { el.focus(); kb.current = false }
  }, [focus, activeRow, tab, view.data, view.fresh])

  const open = (e: Entry) => openRoute({ view: 'trains', trainId: e.trainId, selection: e.gateId ? { kind: 'gate', id: e.gateId } : null }, onOpen)
  const openTrain = (id: string) => openRoute({ view: 'trains', trainId: id, selection: null }, onOpen)
  const step = (dir: 1 | -1) => setFocus(f => (tab === 'month' ? addMonths(f, dir) : addDays(f, (tab === 'timeline' ? TIMELINE_DAYS : 7) * dir)))
  const moveTo = (d: string, row?: string) => { kb.current = true; setFocus(d); if (row !== undefined) setFocusRow(row) }
  const openFirst = (day: string, row?: string) => {
    if (row === FREEZE_ROW || row === EMPTY_ROW) return
    const list = (byDay.get(day) ?? []).filter(e => row === undefined || e.trainId === row)
    if (list[0]) open(list[0])
  }
  const onKey = (e: React.KeyboardEvent, day: string, row?: string) => {
    if (e.target !== e.currentTarget || e.ctrlKey || e.metaKey || e.altKey) return
    const k = e.key
    const ri = row === undefined ? -1 : gridRows.indexOf(row)
    if (row !== undefined && (k === 'ArrowUp' || k === 'ArrowDown')) {
      const nr = gridRows[ri + (k === 'ArrowDown' ? 1 : -1)]
      e.preventDefault(); if (nr !== undefined) moveTo(day, nr); return
    }
    const to = k === 'ArrowLeft' ? addDays(day, -1) : k === 'ArrowRight' ? addDays(day, 1) : k === 'ArrowUp' ? addDays(day, -7) : k === 'ArrowDown' ? addDays(day, 7)
      : k === 'Home' ? startOfWeek(day) : k === 'End' ? addDays(startOfWeek(day), 6)
      : k === 'PageUp' ? (mode === 'month' ? addMonths(day, -1) : addDays(day, -7)) : k === 'PageDown' ? (mode === 'month' ? addMonths(day, 1) : addDays(day, 7)) : null
    if (to) { e.preventDefault(); moveTo(to) }   // the row is kept as it was: it may not exist in the next week, and the person's own row comes back when it does
    else if (k === 'Enter') { e.preventDefault(); openFirst(day, row) }
  }

  // ---- derived panels ----
  const conflicts: Conflict[] = useMemo(() => (upcoming.data ? conflictsOf(upcoming.data) : []), [upcoming.data])
  const upcomingTrains = useMemo(() => {
    const d = upcoming.data && applyFilters(upcoming.data, filters)
    if (!d) return null
    const clash = new Map<string, string[]>()
    for (const c of conflictsOf(d)) clash.set(c.trainId, [...(clash.get(c.trainId) ?? []), c.text])
    return d.trains.filter(t => t.status !== 'Complete' && t.status !== 'Aborted')
      .map(t => ({ t, at: t.window ? t.window.startsAt : `${t.targetDate}T00:00:00Z`, why: clash.get(t.id) ?? [] }))
      .filter(x => x.at >= `${today}T00:00:00Z`).sort((a, b) => a.at.localeCompare(b.at) || a.t.title.localeCompare(b.t.title)).slice(0, 8)
  }, [upcoming.data, filters, today])
  const nextFreeze = ahead?.[0]
  const windowCount = (d: CalendarPayload | null) => d?.trains.filter(t => t.window).length ?? 0
  const shownEntries = days.flatMap(d => byDay.get(d) ?? [])
  const summary = data
    ? `${shownEntries.filter(e => e.kind === 'window').length} release windows · ${shownEntries.filter(e => e.kind === 'target').length} target dates · ${shownEntries.filter(e => e.kind === 'milestone').length} milestones · ${new Set(data.freezeWindows.map(f => f.id)).size} freeze or chill windows`
    : ''

  const upcomingCols: Column<NonNullable<typeof upcomingTrains>[number]>[] = [
    { key: 'release', header: 'Release', cell: x => <TwoLine primary={<button type="button" className="os-link os-link-button" onClick={() => openTrain(x.t.id)}>{x.t.title}</button>} secondary={`${riskLabel(x.t.riskTier)} risk`} /> },
    { key: 'window', header: 'Window · UTC', cell: x => {
      if (!x.t.window) return <TwoLine primary={`Target ${dayLabel(x.t.targetDate)}`} secondary="No deployment window" />
      const s = stamp(x.t.window.startsAt, zone)
      return <TwoLine primary={s.utc} secondary={[durationText(x.t.window.startsAt, x.t.window.endsAt), s.local && `${s.local} for you`].filter(Boolean).join(' · ')} />
    } },
    { key: 'status', header: 'Status', cell: x => <StatusChip>{x.t.status}</StatusChip> },
    { key: 'conflicts', header: 'Conflicts', cell: x => x.why.length ? <span className="os-cal-conflict-text">▲ {x.why[0]}{x.why.length > 1 ? ` (+${x.why.length - 1} more)` : ''}</span> : <span className="os-muted">None</span> },
  ]

  const listRows = useMemo(() => (stream ?? []).filter(r => r.status !== 'Complete' && r.status !== 'Aborted' || filters.status === r.status)
    .filter(r => (filters.status === 'all' || r.status === filters.status) && (filters.risk === 'all' || r.riskTier === filters.risk) && (filters.type === 'all' || r.type?.id === filters.type)), [stream, filters])
  const listCols: Column<StreamRow>[] = [
    { key: 'release', header: 'Release', cell: r => <TwoLine primary={r.title} secondary={`${riskLabel(r.riskTier)} risk`} /> },
    { key: 'target', header: 'Target date · UTC', cell: r => <TwoLine primary={dayLabel(r.targetReleaseDate)} secondary={r.daysToTarget === 0 ? 'today' : r.daysToTarget > 0 ? `in ${r.daysToTarget} business days` : `${-r.daysToTarget} business days ago`} /> },
    { key: 'status', header: 'Status', cell: r => <StatusChip>{r.status}</StatusChip> },
    { key: 'blockers', header: 'Readiness', cell: r => r.blockers ? <StatusChip tone="warning">{`${r.blockers} ${r.blockers === 1 ? 'blocker' : 'blockers'}`}</StatusChip> : <StatusChip tone="success">No blockers</StatusChip> },
  ]

  const dayNum = (day: string, withMonth: boolean) => new Date(parseDay(day)).toLocaleDateString('en-GB', withMonth ? { day: 'numeric', month: 'short', timeZone: 'UTC' } : { day: 'numeric', timeZone: 'UTC' })
  const dayFull = (day: string) => new Date(parseDay(day)).toLocaleDateString('en-GB', { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric', timeZone: 'UTC' })
  const dow = (day: string) => new Date(parseDay(day)).toLocaleDateString('en-GB', { weekday: 'short', timeZone: 'UTC' })

  const exportCalendar = () => { const el = document.getElementById('subscribe'); el?.scrollIntoView({ block: 'start' }); el?.focus() }
  const title = tab === 'timeline' ? `${dayLabel(first)} – ${dayLabel(last)} ${last.slice(0, 4)}` : rangeTitle(focus, mode)
  const unit = tab === 'month' ? 'month' : tab === 'timeline' ? '4 weeks' : 'week'

  const nav = (
    <div className="os-cal-nav">
      <span className="os-cal-zone">UTC</span>
      <button type="button" className="os-button secondary os-cal-step" aria-label={`Previous ${unit}`} onClick={() => step(-1)}><ChevronLeft aria-hidden="true" size={16} strokeWidth={1.6} /></button>
      <Button onClick={() => setFocus(todayIso())}>Today</Button>
      <button type="button" className="os-button secondary os-cal-step" aria-label={`Next ${unit}`} onClick={() => step(1)}><ChevronRight aria-hidden="true" size={16} strokeWidth={1.6} /></button>
    </div>
  )

  const loading = view.data === null && !view.error
  const body = view.error && !view.data ? <ErrorState what="The calendar" message={view.error} onRetry={view.retry} /> : loading ? <LoadingState what="the calendar" /> : null
  const emptyFilter = filtered(filters)

  return (
    <div className="os-page">
      <PageHeading trail={['Workspace', 'Portfolio & calendar']} title="Release portfolio" description="Coordinate release windows, freezes and milestones in one shared plan."
        actions={<><Button icon={Download} onClick={exportCalendar}>Export calendar</Button>{onPlan && <Button variant="primary" icon={Plus} onClick={onPlan}>Plan release</Button>}</>} />

      <div className="os-cal-toolbar">
        <Tabs label="Portfolio views" group="portfolio" value={tab} onChange={id => setTab(id as TabId)}
          tabs={[{ id: 'week', label: 'Week calendar' }, { id: 'month', label: 'Month' }, { id: 'timeline', label: 'Timeline' }, { id: 'list', label: 'Release list', count: stream?.filter(r => r.status !== 'Complete' && r.status !== 'Aborted').length }]} />
        <div className="os-cal-filters" role="group" aria-label="Filters">
          <FilterSelect label="Status" value={filters.status} onChange={status => setFilters(f => ({ ...f, status }))} options={[{ value: 'all', label: 'All statuses' }, ...STATUSES.map(s => ({ value: s, label: s }))]} />
          <FilterSelect label="Risk tier" value={filters.risk} onChange={risk => setFilters(f => ({ ...f, risk }))} options={[{ value: 'all', label: 'All risk tiers' }, ...RISKS.map(r => ({ value: r.value, label: r.label }))]} />
          <FilterSelect label="Release type" value={filters.type} onChange={type => setFilters(f => ({ ...f, type }))} options={[{ value: 'all', label: 'All release types' }, ...types.map(t => ({ value: t.id, label: t.name }))]} />
        </div>
      </div>

      {tab !== 'list' && (
        <TabPanel id={tab} group="portfolio">
          <Panel title={<span id={titleId} data-testid="cal-title" aria-live="polite">{title}</span>} meta={nav}
            footer={data && <div className="os-cal-foot"><span data-testid="cal-summary">{summary}</span><span className="os-cal-legend" aria-label="Status legend">{STATUSES.map(s => <StatusChip key={s}>{s}</StatusChip>)}</span></div>}>
            {body ?? (view.error && <Notice tone="warning" title="The calendar may be out of date" live action={<Button onClick={view.retry}>Retry</Button>}>{view.error}</Notice>)}
            {data && tab === 'week' && (
              (
                  <div className="os-cal-scroll">
                    <table className="os-cal week" role="grid" aria-labelledby={titleId} data-testid="calendar-grid">
                      <thead>
                        <tr>
                          <th scope="col" className="os-cal-corner">Release</th>
                          {days.map(d => (
                            <th key={d} scope="col" abbr={dayFull(d)} className={d === today ? 'today' : undefined} aria-current={d === today ? 'date' : undefined}>
                              {dow(d)} {dayNum(d, false)}{d === today && <span className="os-cal-today"> Today</span>}
                            </th>
                          ))}
                        </tr>
                      </thead>
                      <tbody>
                        {gridRows.map(rid => {
                          const r = rows.find(x => x.id === rid)
                          return (
                            <tr key={rid}>
                              <th scope="row" className="os-cal-rowhead">
                                {r ? <TwoLine primary={r.title} secondary={[r.status, r.riskTier && `${riskLabel(r.riskTier)} risk`].filter(Boolean).join(' · ')} />
                                  : rid === EMPTY_ROW ? <TwoLine primary={emptyFilter ? 'No releases match these filters' : 'No releases this week'} secondary={emptyFilter ? 'Change the filters, or go to another week' : 'Go to another week, or plan a release'} />
                                  : <TwoLine primary="Freeze and chill" secondary="Windows that restrict changes" />}
                              </th>
                              {days.map(day => {
                                const list = r ? (byDay.get(day) ?? []).filter(e => e.trainId === r.id) : []
                                const fzs = r ? [] : fz.get(day) ?? []
                                const kind = r ? (fz.get(day)?.some(f => f.kind === 'Freeze') ? 'freeze' : fz.get(day)?.length ? 'chill' : '') : (fzs.some(f => f.kind === 'Freeze') ? 'freeze' : fzs.length ? 'chill' : '')
                                const here = rid === activeRow && day === focus
                                return (
                                  <td key={day} role="gridcell" ref={el => { const k = `${rid}|${day}`; if (el) cells.current.set(k, el); else cells.current.delete(k) }}
                                    className={['os-cal-cell', kind && `tint-${kind}`, day === today && 'today'].filter(Boolean).join(' ')} data-date={day} data-row={rid}
                                    tabIndex={here ? 0 : -1} aria-selected={here} aria-label={`${r ? r.title : rid === EMPTY_ROW ? 'No releases' : 'Freeze and chill'}, ${dayFull(day)}, ${list.length + fzs.length} ${list.length + fzs.length === 1 ? 'item' : 'items'}`}
                                    onKeyDown={e => onKey(e, day, rid)} onClick={e => { if (e.target === e.currentTarget) { setFocus(day); setFocusRow(rid) } }}>
                                    {fzs.map(f => <FreezeLabel key={f.id} f={f} visible />)}
                                    {list.map(e => <EntryCard key={e.key} e={e} onOpen={() => open(e)} />)}
                                  </td>
                                )
                              })}
                            </tr>
                          )
                        })}
                      </tbody>
                    </table>
                  </div>
                )
            )}
            {data && tab === 'month' && (
              <div className="os-cal-scroll">
                <table className="os-cal month" role="grid" aria-labelledby={titleId} data-testid="calendar-grid">
                  <thead><tr>{['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'].map(d => <th key={d} scope="col" abbr={d}>{d.slice(0, 3)}</th>)}</tr></thead>
                  <tbody>
                    {Array.from({ length: days.length / 7 }, (_, w) => days.slice(w * 7, w * 7 + 7)).map(wk => (
                      <tr key={wk[0]}>
                        {wk.map((day, col) => {
                          const entries = byDay.get(day) ?? []
                          const fs = fz.get(day) ?? []
                          const kind = fs.some(f => f.kind === 'Freeze') ? 'freeze' : fs.length ? 'chill' : ''
                          const out = day.slice(0, 7) !== focus.slice(0, 7)
                          const MAX = 3
                          return (
                            <td key={day} role="gridcell" ref={el => { const k = `|${day}`; if (el) cells.current.set(k, el); else cells.current.delete(k) }}
                              className={['os-cal-cell', kind && `tint-${kind}`, out && 'out', day === focus && 'sel', day === today && 'today'].filter(Boolean).join(' ')}
                              tabIndex={day === focus ? 0 : -1} aria-selected={day === focus} data-date={day} onKeyDown={e => onKey(e, day)} onClick={e => { if (e.target === e.currentTarget) setFocus(day) }}>
                              <div className="os-cal-daynum">
                                <span aria-hidden="true">{day.endsWith('-01') ? dayNum(day, true) : dayNum(day, false)}</span>
                                {/* every day carries its full date for screen readers: a bare "30" under an "October" title would be read as 30 October */}
                                <span className="sr-only">{dayFull(day)}</span>
                                {day === today && <span className="os-cal-today"> Today</span>}
                              </div>
                              {fs.map(f => <FreezeLabel key={f.id} f={f} visible={day === utcDay(f.startsAt) || col === 0 || day === first} />)}
                              {entries.slice(0, MAX).map(e => <EntryCard key={e.key} e={e} compact onOpen={() => open(e)} />)}
                              {entries.length > MAX && <button type="button" className="os-link os-link-button" onClick={() => { setFocus(day); setTab('week') }}>+{entries.length - MAX} more (week view)</button>}
                            </td>
                          )
                        })}
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            {data && tab === 'timeline' && <Timeline data={data} days={days} today={today} rows={rows} byDay={byDay} onOpen={openTrain} emptyFilter={emptyFilter} />}
            {data && tab !== 'timeline' && <p className="os-muted os-cal-help">Arrow keys move by day{tab === 'week' ? ' and release' : ''}, Page Up and Page Down by {mode}, Home and End to the week's edges. Enter opens the first item in the cell; each item is also a button.</p>}
          </Panel>
        </TabPanel>
      )}

      {tab === 'list' && (
        <TabPanel id="list" group="portfolio">
          <Panel title="Release list" subtitle="Every open release, with its target date and readiness" meta={stream ? <span>{listRows.length} {listRows.length === 1 ? 'release' : 'releases'}</span> : undefined}>
            {streamError ? <ErrorState what="The release list" message={streamError} onRetry={loadStream} />
              : stream === null ? <LoadingState what="the release list" />
              : <DataTable caption="Open releases" columns={listCols} rows={listRows} rowKey={r => r.id} onSelect={r => openTrain(r.id)}
                  empty={emptyFilter ? 'No releases match these filters.' : 'No open releases yet. Import releases from a CSV, or plan one.'} />}
          </Panel>
        </TabPanel>
      )}

      <div className="os-cal-lower">
        <Panel title="Upcoming release windows" subtitle={`Next ${UPCOMING_DAYS} days · sorted by scheduled start`} meta={<button type="button" className="os-link os-link-button" onClick={() => setTab('list')}>View portfolio →</button>}>
          {upcoming.error ? <ErrorState what="The upcoming windows" message={upcoming.error} onRetry={upcoming.retry} />
            : upcomingTrains === null ? <LoadingState what="the upcoming windows" />
            : <DataTable caption="Upcoming release windows" columns={upcomingCols} rows={upcomingTrains} rowKey={x => x.t.id}
                empty={emptyFilter ? 'No upcoming releases match these filters.' : `No release is scheduled in the next ${UPCOMING_DAYS} days.`} />}
        </Panel>
        <Panel title="Capacity & constraints">
          <div className="os-stack">
            {upcoming.data && (conflicts.length
              ? <Notice tone="warning" title={`${conflicts.length} scheduling ${conflicts.length === 1 ? 'conflict' : 'conflicts'}`}
                  action={<button type="button" className="os-link os-link-button" onClick={() => openTrain(conflicts[0].trainId)}>Resolve →</button>}>
                  {conflicts[0].text}{conflicts.length > 1 ? `, and ${conflicts.length - 1} more.` : '.'}
                </Notice>
              : <Notice tone="success" title="No scheduling conflicts">No release window overlaps another release or a freeze in the next {UPCOMING_DAYS} days.</Notice>)}
            <PropertyList items={[
              { label: `Release windows, next ${UPCOMING_DAYS} days`, value: String(windowCount(upcoming.data)) },
              { label: 'Overlapping or frozen windows', value: String(new Set(conflicts.map(c => c.trainId)).size) },
              { label: 'Next change freeze', value: nextFreeze ? `${nextFreeze.kind} · ${nextFreeze.name}: ${dayLabel(utcDay(nextFreeze.startsAt))} → ${dayLabel(utcDay(Date.parse(nextFreeze.endsAt) - 1000))}` : ahead ? 'None in the next 45 days' : '…' },
            ]} />
            <p className="os-muted">A freeze stops a release starting in its range unless an override is approved. A chill only warns. Overlapping windows are flagged when they share a configuration item or product.</p>
          </div>
        </Panel>
      </div>

      <CalendarSubscribe current={current} />
    </div>
  )
}

/** The Timeline tab (REOS-134): one bar per release across four weeks, from its first dated item to its last, with freezes as a band on top. */
function Timeline({ data, days, today, rows, byDay, onOpen, emptyFilter }: {
  data: CalendarPayload; days: string[]; today: string; rows: { id: string; title: string; status: string; riskTier?: string }[]; byDay: Map<string, Entry[]>
  onOpen: (id: string) => void; emptyFilter: boolean
}) {
  const col = (d: string) => days.indexOf(d) + 2   // 1 is the row label
  const spans = rows.map(r => {
    const mine = days.filter(d => (byDay.get(d) ?? []).some(e => e.trainId === r.id))
    return { r, from: mine[0], to: mine[mine.length - 1], n: mine.reduce((s, d) => s + (byDay.get(d) ?? []).filter(e => e.trainId === r.id).length, 0) }
  })
  const freezes = data.freezeWindows.map(f => ({ f, a: utcDay(f.startsAt) < days[0] ? days[0] : utcDay(f.startsAt), b: utcDay(Date.parse(f.endsAt) - 1000) > days[days.length - 1] ? days[days.length - 1] : utcDay(Date.parse(f.endsAt) - 1000) })).filter(x => x.b >= x.a)
  if (spans.length === 0 && freezes.length === 0) return <EmptyState title={emptyFilter ? 'No releases match these filters in this period' : 'No releases in this period'}>Go to another period, or plan a release.</EmptyState>
  return (
    <div className="os-cal-scroll">
      <div className="os-timeline" role="table" aria-label="Release timeline" style={{ gridTemplateColumns: `minmax(160px, 220px) repeat(${days.length}, minmax(26px, 1fr))` }}>
        <div role="row" className="os-timeline-row head">
          <div role="columnheader" className="os-timeline-label" style={{ gridColumn: 1 }}>Release</div>
          {days.map((d, i) => <div key={d} role="columnheader" className={`os-timeline-day${d === today ? ' today' : ''}`} style={{ gridColumn: i + 2 }} aria-label={new Date(parseDay(d)).toLocaleDateString('en-GB', { weekday: 'long', day: 'numeric', month: 'long', timeZone: 'UTC' })}>
            <span aria-hidden="true">{Number(d.slice(8))}</span>
          </div>)}
        </div>
        {freezes.map(({ f, a, b }) => (
          <div role="row" key={f.id} className="os-timeline-row">
            <div role="rowheader" className="os-timeline-label" style={{ gridColumn: 1 }}>{f.kind === 'Freeze' ? '▲' : '◐'} {f.kind} · {f.name}</div>
            <div role="cell" className={`os-timeline-bar ${f.kind === 'Freeze' ? 'danger' : 'warning'}`} style={{ gridColumn: `${col(a)} / ${col(b) + 1}` }}>
              {dayLabel(utcDay(f.startsAt))} → {dayLabel(utcDay(Date.parse(f.endsAt) - 1000))}
            </div>
          </div>
        ))}
        {spans.map(({ r, from, to, n }) => (
          <div role="row" key={r.id} className="os-timeline-row">
            <div role="rowheader" className="os-timeline-label"><TwoLine primary={r.title} secondary={[r.status, r.riskTier && `${riskLabel(r.riskTier)} risk`].filter(Boolean).join(' · ')} /></div>
            <div role="cell" className="os-timeline-cell" style={{ gridColumn: `${col(from)} / ${col(to) + 1}` }}>
              <button type="button" className={`os-timeline-bar ${r.status === 'Executing' ? 'info' : r.status === 'Complete' ? 'success' : r.status === 'Aborted' ? 'danger' : 'neutral'}`} data-testid="timeline-bar"
                title={`${r.title}: ${n} dated ${n === 1 ? 'item' : 'items'}, ${dayLabel(from)} to ${dayLabel(to)}`} onClick={() => onOpen(r.id)}>
                {dayLabel(from)}{to !== from ? ` → ${dayLabel(to)}` : ''} · {n} {n === 1 ? 'item' : 'items'}
              </button>
            </div>
          </div>
        ))}
      </div>
      <p className="os-muted os-cal-help">Each bar runs from the release's first dated item (window, target date, gate or milestone) to its last in this period. Select a bar to open the release.</p>
    </div>
  )
}
