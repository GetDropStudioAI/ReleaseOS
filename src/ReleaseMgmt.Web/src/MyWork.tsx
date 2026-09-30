import { useEffect, useMemo, useState } from 'react'
import { getMyWork, type Me, type MyWorkData, type WorkVia } from './api'
import type { Route } from './route'
import { useRowNav } from './rowNav'
import { fmtDay, fmtDayTime, zoneAbbr } from './time'

/** REOS-38: everything assigned to me (or to a team I am in) in one table, soonest due first. Rows open the item in its train. */
interface Part { cls: string; text: string }
interface Item {
  key: string; kind: string; title: string; detail: string | null; trainTitle: string
  due: string; dueMs: number; dueIsDate: boolean; status: Part[]; via: WorkVia | null; target: Partial<Route>
}

// A date-only value (DueOn) is a calendar day, not an instant: read it at noon UTC so any display zone shows the same day.
const dayMs = (d: string) => Date.parse(`${d}T12:00:00Z`)
const STATUS: Record<string, Part> = {
  Pending: { cls: 'muted', text: '○ pending' }, InProgress: { cls: 'accent', text: '◐ in progress' }, Failed: { cls: 'bad', text: '✗ failed' },
  Scheduled: { cls: 'muted', text: '○ scheduled' }, Running: { cls: 'ok', text: '● running' },
}
const OVERDUE: Part = { cls: 'bad', text: '✗ overdue' }
const OPEN: Part = { cls: 'muted', text: '○ open' }

function buildItems(w: MyWorkData): Item[] {
  const items: Item[] = []
  for (const t of w.tasks) items.push({
    key: `task:${t.id}`, kind: 'Task', title: t.description, detail: t.gateName, trainTitle: t.trainTitle,
    due: t.dueOn, dueMs: dayMs(t.dueOn), dueIsDate: true, status: [t.overdue ? OVERDUE : OPEN], via: t.via,
    target: { trainId: t.trainId, selection: { kind: 'task', gateId: t.gateId, id: t.id } },
  })
  for (const g of w.gates) items.push({
    key: `gate:${g.id}`, kind: 'Gate', title: g.name, detail: g.openTasks ? `${g.openTasks} open task${g.openTasks === 1 ? '' : 's'}` : null, trainTitle: g.trainTitle,
    due: g.dueOn, dueMs: dayMs(g.dueOn), dueIsDate: true, status: [...(g.overdue ? [OVERDUE] : []), STATUS[g.status] ?? { cls: 'muted', text: g.status }], via: g.via,
    target: { trainId: g.trainId, selection: { kind: 'gate', id: g.id } },
  })
  for (const s of w.steps) items.push({
    key: `step:${s.executionId}`, kind: 'Step', title: `${s.stepCode} ${s.title}`, detail: s.runMode === 'Rehearsal' ? 'rehearsal' : null, trainTitle: s.trainTitle,
    due: s.plannedStartAt, dueMs: Date.parse(s.plannedStartAt), dueIsDate: false, status: [...(s.late ? [{ cls: 'warn', text: '▲ late' }] : []), STATUS[s.status] ?? { cls: 'muted', text: s.status }], via: s.via,
    target: { trainId: s.trainId, mode: s.runMode === 'Rehearsal' ? 'rehearsal' : 'live', selection: { kind: 'step', id: s.stepId } },
  })
  for (const c of w.conditions) items.push({
    key: `cond:${c.id}`, kind: 'Condition', title: c.text, detail: null, trainTitle: c.trainTitle,
    due: c.expiresAt, dueMs: Date.parse(c.expiresAt), dueIsDate: false, status: [c.expired ? { cls: 'bad', text: '✗ expired' } : OPEN], via: null,
    target: { trainId: c.trainId },
  })
  for (const p of w.pirActions) items.push({
    key: `pir:${p.id}`, kind: 'PIR action', title: p.text, detail: null, trainTitle: p.trainTitle,
    due: p.dueOn, dueMs: dayMs(p.dueOn), dueIsDate: true, status: [p.overdue ? OVERDUE : OPEN], via: null,
    target: { trainId: p.trainId },
  })
  return items.sort((a, b) => a.dueMs - b.dueMs)
}

const plural = (n: number, one: string) => `${n} ${one}${n === 1 ? '' : 's'}`

export function MyWork({ onOpen, refreshKey = 0 }: { me: Me; onOpen?: (r: Partial<Route>) => void; refreshKey?: number }) {
  const [data, setData] = useState<MyWorkData | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => { getMyWork().then(d => { setData(d); setError(null) }).catch(e => setError((e as Error).message)) }, [refreshKey])
  const items = useMemo(() => (data ? buildItems(data) : []), [data])
  const [sel, setSel, nav] = useRowNav(items.length, i => { const it = items[i]; if (it) onOpen?.(it.target) })
  const c = data?.counts

  return (
    <section>
      <h1>My work</h1>
      {error && <p className="bad" role="alert">✗ {error}</p>}
      {!data && !error && <p className="muted">Loading…</p>}
      {c && (
        <p className="muted" data-testid="work-counts">
          {c.total === 0 ? 'Nothing assigned to you.' : `${plural(c.total, 'item')} assigned to you (${plural(c.tasks, 'task')}, ${plural(c.gates, 'gate')}, ${plural(c.steps, 'step')}, ${plural(c.conditions, 'condition')}, ${plural(c.pirActions, 'PIR action')})`}
          {c.overdue > 0 && <> · <span className="bad">✗ {c.overdue} overdue</span></>}
        </p>
      )}
      {data && items.length === 0 && <p className="muted">No open tasks, gates, steps, conditions or PIR actions are assigned to you or your teams. Items appear here as trains move.</p>}
      {items.length > 0 && (
        <table className="grid" aria-label="Items assigned to me, by due date" {...nav}>
          <thead><tr><th>Due</th><th>Kind</th><th>Item</th><th>Train</th><th>Status</th><th>Assigned via</th></tr></thead>
          <tbody>
            {items.map((it, i) => (
              <tr key={it.key} className={sel === i ? 'selected' : undefined} onClick={() => { setSel(i); onOpen?.(it.target) }}>
                <td className="nowrap">{it.dueIsDate ? fmtDay(`${it.due}T12:00:00Z`) : `${fmtDayTime(it.due)} ${zoneAbbr(it.due)}`}</td>
                <td>{it.kind}</td>
                <td><button type="button" className="text plainlink" data-rownav aria-current={sel === i ? 'true' : undefined}>{it.title}</button>{it.detail && <span className="muted"> · {it.detail}</span>}</td>
                <td>{it.trainTitle}</td>
                <td>{it.status.map((p, k) => <span key={k} className={p.cls}>{k > 0 && ' '}{p.text}</span>)}</td>
                <td className="muted">{it.via?.kind === 'team' ? `team ${it.via.teamName ?? ''}` : 'me'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {items.length > 0 && <p className="muted">In the table, j / k moves the selection and Enter opens the item in its train.</p>}
    </section>
  )
}
