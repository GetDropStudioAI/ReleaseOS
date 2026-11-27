import { useCallback, useEffect, useState } from 'react'
import { ApiError } from '../api.ts'
import { errMsg } from '../format.ts'
import { onCabChanged } from '../live.ts'
import { DataTable, EmptyState, ErrorState, LoadingState, Notice, Panel, StatusChip, TimeRange, type Column } from '../ui'
import { statusTone } from './cabLogic.ts'
import { conflictCount, groupByType, marker } from './conflictLogic.ts'
import { getCabCalendar, getConflicts, getMeetingConflicts, type AgendaConflictItem, type CabCalendar, type CabCalendarChange, type Conflict, type ConflictReport } from './conflictsApi.ts'
import './conflicts.css'

/**
 * Conflict views of the CAB module (REOS-180; D56, D9). What a change collides with is derived by the server on every read, so these components only ask again: when they open, when the change
 * is saved (its version moves), when the CAB module pushes a change, and when the tab becomes visible again (a freeze window may have moved meanwhile). A conflict warns and never blocks; each one
 * is shown in words with the rule that fired and a diamond marker, and the count is a word in a chip: nothing relies on colour. Times are UTC with the person's own time beside them (D44).
 */

/** Asks again whenever the page is shown again, so a window that moved elsewhere is not shown stale. */
function useRefetchOnShow(load: () => void) {
  useEffect(() => {
    const show = () => { if (document.visibilityState === 'visible') load() }
    document.addEventListener('visibilitychange', show)
    window.addEventListener('focus', show)
    return () => { document.removeEventListener('visibilitychange', show); window.removeEventListener('focus', show) }
  }, [load])
}

function ConflictRow({ c }: { c: Conflict }) {
  return (
    <li className="cab-conflict" data-rule={c.rule} data-type={c.type}>
      <span className="cab-conflict-head"><span className="cab-conflict-marker" aria-hidden="true">◆</span><span className="cab-conflict-rule">{c.ruleLabel}</span><StatusChip tone="warning">Conflict</StatusChip></span>
      <p className="cab-conflict-why">{c.summary}</p>
      {c.startsAt && c.endsAt && c.type !== 'Blackout' && <p className="cab-conflict-when"><span className="cab-conflict-label">{c.refLabel ?? 'Other window'}:</span> <TimeRange from={c.startsAt} to={c.endsAt} /></p>}
    </li>
  )
}

/** The conflicts list, grouped by kind in a fixed order. */
export function ConflictList({ conflicts }: { conflicts: Conflict[] }) {
  return (
    <div className="mg-stack">
      {groupByType(conflicts).map(g => (
        <section key={g.type} aria-label={g.label}>
          <h3 className="cab-conflict-group">{g.label}</h3>
          <ul className="cab-conflicts">{g.items.map((c, i) => <ConflictRow key={`${c.rule}-${c.refId ?? c.startsAt}-${i}`} c={c} />)}</ul>
        </section>
      ))}
    </div>
  )
}

/**
 * "Conflicts" on the change detail. `version` is the change's version: it moves when the change is saved, which asks again. `onLoaded` gives the screen the conflicts, so the decision form can say a
 * reason is needed before the person presses the button (the server asks for it all the same).
 */
export function ConflictsPanel({ changeId, version, onLoaded }: { changeId: string; version: number; onLoaded?: (conflicts: Conflict[]) => void }) {
  const [report, setReport] = useState<ConflictReport | null>(null)
  const [error, setError] = useState<string | null>(null)
  const load = useCallback(() => {
    getConflicts(changeId).then(r => { setReport(r); setError(null); onLoaded?.(r.conflicts) }).catch(e => setError(e instanceof ApiError && e.status === 404 ? 'The conflicts are not available for this change.' : errMsg(e)))
  }, [changeId, onLoaded])
  useEffect(() => { load() }, [load, version])
  useEffect(() => onCabChanged(id => { if (!id || id === changeId) load() }), [changeId, load])
  useRefetchOnShow(load)

  return (
    <Panel title="Conflicts" subtitle="Freeze windows, blackout days, other changes and release windows" meta={report?.checked ? <StatusChip tone={report.conflicts.length ? 'warning' : 'success'}>{conflictCount(report.conflicts.length)}</StatusChip> : undefined}>
      {error && !report ? <ErrorState what="The conflicts" message={error} onRetry={load} />
        : !report ? <LoadingState what="the conflicts" />
        : !report.checked ? <EmptyState title="Conflicts are not checked">{report.reason}</EmptyState>
        : (
          <div className="mg-stack">
            {report.conflicts.length === 0
              ? <Notice tone="success" title="No conflicts">No freeze window, blackout day, other change or release window collides with the planned window.</Notice>
              : <Notice tone="warning" title={`${conflictCount(report.conflicts.length)}, shown for the CAB`} live>A conflict warns and never blocks. Approving this change over one needs a reason, recorded with the decision.</Notice>}
            {report.conflicts.length > 0 && <ConflictList conflicts={report.conflicts} />}
            <p className="mg-note">Worked out again each time you open this page, so a window that moves is reflected here. Other changes and releases count when they are within {report.bufferMinutes} minutes of this window.</p>
          </div>
        )}
    </Panel>
  )
}

/** A count and a diamond for one agenda item or calendar change: "◆! 2 conflicts" or "◆ No conflicts". Used wherever a change is listed. */
export function ConflictBadge({ conflicts, checked = true }: { conflicts: Conflict[]; checked?: boolean }) {
  if (!checked) return <span className="cab-badge off"><span aria-hidden="true">◆</span> Not checked</span>
  return <StatusChip tone={conflicts.length ? 'warning' : 'success'}>{`${marker(conflicts.length)} ${conflictCount(conflicts.length)}`}</StatusChip>
}

/**
 * The conflicts of each agenda item of a meeting (REOS-180): one row per change in agenda order with its count and the rules that fired. The CAB workbench (REOS-179) places this beside its agenda.
 * Asks again when the CAB module pushes a change.
 */
export function AgendaConflictsPanel({ meetingId, onOpenChange }: { meetingId: string; onOpenChange?: (changeId: string) => void }) {
  const [items, setItems] = useState<AgendaConflictItem[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const load = useCallback(() => { getMeetingConflicts(meetingId).then(r => { setItems(r.items); setError(null) }).catch(e => setError(errMsg(e))) }, [meetingId])
  useEffect(() => { load() }, [load])
  useEffect(() => onCabChanged(() => load()), [load])
  useRefetchOnShow(load)
  const columns: Column<AgendaConflictItem>[] = [
    { key: 'change', header: 'Change', cell: i => <span>{i.key} · {i.title}</span> },
    { key: 'status', header: 'Status', cell: i => <StatusChip tone={statusTone(i.status)}>{i.status}</StatusChip> },
    { key: 'conflicts', header: 'Conflicts', cell: i => <ConflictBadge conflicts={i.conflicts} checked={i.checked} /> },
    { key: 'rules', header: 'Rules that fired', cell: i => i.conflicts.length ? [...new Set(i.conflicts.map(c => c.ruleLabel))].join('; ') : <span className="os-muted">None</span> },
  ]
  return (
    <Panel title="Conflicts on the agenda" subtitle="Each change checked against freeze windows, blackout days, other changes and release windows">
      {error && !items ? <ErrorState what="The agenda conflicts" message={error} onRetry={load} />
        : !items ? <LoadingState what="the agenda conflicts" />
        : <DataTable caption="Agenda conflicts" columns={columns} rows={items} rowKey={i => i.changeId} onSelect={onOpenChange ? i => onOpenChange(i.changeId) : undefined} empty="No changes are on this agenda." />}
    </Panel>
  )
}

/**
 * The change overlay of the calendar (REOS-180): the changes planned in the days shown, each with a diamond marker (not a colour), its window in UTC with local time, its status as a word and
 * its conflicts in words, and the blackout days of the period. It adds nothing while the CAB module is off: the API answers 404 and the panel renders nothing at all.
 */
export function ChangeCalendarOverlay({ from, to, onOpenChange }: { from: string; to: string; onOpenChange: (changeId: string) => void }) {
  const [data, setData] = useState<CabCalendar | null>(null)
  const [off, setOff] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const load = useCallback(() => {
    getCabCalendar(from, to).then(d => { setData(d); setError(null); setOff(false) })
      .catch(e => { if (e instanceof ApiError && e.status === 404) { setOff(true); setData(null) } else setError(errMsg(e)) })
  }, [from, to])
  useEffect(() => { load() }, [load])
  useEffect(() => onCabChanged(() => load()), [load])
  useRefetchOnShow(load)
  if (off) return null
  const columns: Column<CabCalendarChange>[] = [
    { key: 'change', header: 'Change', cell: c => <span className="cab-cal-name"><span className="cab-conflict-marker" aria-hidden="true">{marker(c.conflicts.length)}</span><span>{c.key} · {c.title}</span></span> },
    { key: 'window', header: 'Planned window', cell: c => <TimeRange from={c.startsAt} to={c.endsAt} /> },
    { key: 'status', header: 'Status', cell: c => <StatusChip tone={statusTone(c.status)}>{c.status}</StatusChip> },
    { key: 'conflicts', header: 'Conflicts', cell: c => (
      <div className="cab-cal-conflicts">
        <ConflictBadge conflicts={c.conflicts} checked={c.checked} />
        {c.conflicts.length > 0 && <ul className="cab-cal-why">{[...new Set(c.conflicts.map(x => x.ruleLabel))].map(l => <li key={l}>{l}</li>)}</ul>}
      </div>) },
  ]
  return (
    <Panel title="Changes in this period" subtitle="Change requests with a planned window in the days shown (◆ marks a change, ◆! one with conflicts)">
      {error && !data ? <ErrorState what="The changes" message={error} onRetry={load} />
        : !data ? <LoadingState what="the changes" />
        : (
          <div className="mg-stack">
            {data.blackouts.length > 0 && <p className="cab-cal-blackouts" data-testid="cab-blackouts"><strong>Blackout days:</strong> {data.blackouts.map(b => `${b.day} ${b.name}`).join('; ')}</p>}
            <DataTable caption="Changes in this period" columns={columns} rows={data.changes} rowKey={c => c.id} onSelect={c => onOpenChange(c.id)} empty="No change request has a planned window in these days." />
          </div>
        )}
    </Panel>
  )
}
