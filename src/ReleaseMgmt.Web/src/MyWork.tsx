import { useEffect, useMemo, useState, type MouseEvent } from 'react'
import { getMyWork, type Me, type MyWorkData, type WorkVia } from './api'
import { pirReason } from './Closeout'
import type { Route } from './route'
import { useRowNav } from './rowNav'
import { dueText } from './shell/approvalsApi'
import { Button, DataTable, EmptyState, ErrorState, LoadingState, MetricCard, PageHeading, Panel, StatusChip, TwoLine, type Column, type Tone } from './ui'

/**
 * REOS-38, restyled with REOS-131 (G9): everything assigned to me (or to a team I am in) in one table, soonest due first. Rows open the item in its train.
 * The decisions among them (Go/No-Go conditions) also link to Approvals, where they are closed (REOS-94). Times are UTC with my own time beside them (D44);
 * a date-only due value is a calendar day and shows without a zone.
 */
interface Part { tone: Tone; text: string }
interface Item {
  key: string; kind: string; title: string; detail: string | null; trainTitle: string
  due: string; dueMs: number; dueIsDate: boolean; status: Part[]; via: WorkVia | null; target: Partial<Route>; decision?: boolean
}

// A date-only value (DueOn) is a calendar day, not an instant: read it at noon UTC so any display zone shows the same day.
const dayMs = (d: string) => Date.parse(`${d}T12:00:00Z`)
const STATUS: Record<string, Part> = {
  Pending: { tone: 'warning', text: 'Pending' }, InProgress: { tone: 'info', text: 'In progress' }, Failed: { tone: 'danger', text: 'Failed' },
  Scheduled: { tone: 'neutral', text: 'Scheduled' }, Running: { tone: 'info', text: 'Running' },
}
const OVERDUE: Part = { tone: 'danger', text: 'Overdue' }
const OPEN: Part = { tone: 'neutral', text: 'Open' }

function buildItems(w: MyWorkData): Item[] {
  const items: Item[] = []
  for (const t of w.tasks) items.push({
    key: `task:${t.id}`, kind: 'Task', title: t.description, detail: t.gateName, trainTitle: t.trainTitle,
    due: t.dueOn, dueMs: dayMs(t.dueOn), dueIsDate: true, status: [t.overdue ? OVERDUE : OPEN], via: t.via,
    target: { trainId: t.trainId, selection: { kind: 'task', gateId: t.gateId, id: t.id } },
  })
  for (const g of w.gates) items.push({
    key: `gate:${g.id}`, kind: 'Gate', title: g.name, detail: g.openTasks ? `${g.openTasks} open task${g.openTasks === 1 ? '' : 's'}` : null, trainTitle: g.trainTitle,
    due: g.dueOn, dueMs: dayMs(g.dueOn), dueIsDate: true, status: [...(g.overdue ? [OVERDUE] : []), STATUS[g.status] ?? { tone: 'neutral', text: g.status }], via: g.via,
    target: { trainId: g.trainId, selection: { kind: 'gate', id: g.id } },
  })
  for (const s of w.steps) items.push({
    key: `step:${s.executionId}`, kind: 'Step', title: `${s.stepCode} ${s.title}`, detail: s.runMode === 'Rehearsal' ? 'rehearsal' : null, trainTitle: s.trainTitle,
    due: s.plannedStartAt, dueMs: Date.parse(s.plannedStartAt), dueIsDate: false, status: [...(s.late ? [{ tone: 'warning' as const, text: 'Late' }] : []), STATUS[s.status] ?? { tone: 'neutral', text: s.status }], via: s.via,
    target: { trainId: s.trainId, mode: s.runMode === 'Rehearsal' ? 'rehearsal' : 'live', selection: { kind: 'step', id: s.stepId } },
  })
  for (const c of w.conditions) items.push({
    key: `cond:${c.id}`, kind: 'Condition', title: c.text, detail: null, trainTitle: c.trainTitle,
    due: c.expiresAt, dueMs: Date.parse(c.expiresAt), dueIsDate: false, status: [c.expired ? { tone: 'danger', text: 'Expired' } : OPEN], via: null,
    target: { trainId: c.trainId }, decision: true,
  })
  for (const p of w.pirActions) items.push({
    key: `pir:${p.id}`, kind: 'PIR action', title: p.text, detail: null, trainTitle: p.trainTitle,
    due: p.dueOn, dueMs: dayMs(p.dueOn), dueIsDate: true, status: [p.overdue ? OVERDUE : OPEN], via: null,
    target: { trainId: p.trainId },
  })
  for (const r of w.reviews ?? []) items.push({
    key: `review:${r.pirId}`, kind: 'Review', title: 'Post-implementation review', detail: pirReason(r.reason), trainTitle: r.trainTitle,
    due: r.dueOn, dueMs: dayMs(r.dueOn), dueIsDate: true, status: [r.overdue ? OVERDUE : r.status === 'Scheduled' ? { tone: 'info', text: 'Scheduled' } : OPEN], via: null,
    target: { trainId: r.trainId },
  })
  return items.sort((a, b) => a.dueMs - b.dueMs)
}

const plural = (n: number, one: string) => `${n} ${one}${n === 1 ? '' : 's'}`

export function MyWork({ onOpen, onOpenApprovals, refreshKey = 0 }: { me: Me; onOpen?: (r: Partial<Route>) => void; onOpenApprovals?: () => void; refreshKey?: number }) {
  const [data, setData] = useState<MyWorkData | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [tick, setTick] = useState(0)
  useEffect(() => { getMyWork().then(d => { setData(d); setError(null) }).catch(e => setError((e as Error).message)) }, [refreshKey, tick])
  const items = useMemo(() => (data ? buildItems(data) : []), [data])
  const [sel, setSel, nav] = useRowNav<HTMLDivElement>(items.length, i => { const it = items[i]; if (it) onOpen?.(it.target) })
  const c = data?.counts

  const heading = (
    <PageHeading trail={['Workspace', 'My work']} title="My work" description="Tasks, gates, steps, conditions and reviews assigned to you or your teams, soonest due first."
      actions={onOpenApprovals && <Button onClick={onOpenApprovals}>Open Approvals</Button>} />
  )
  if (error && !data) return <div className="os-page">{heading}<ErrorState what="Your work" message={error} onRetry={() => setTick(n => n + 1)} /></div>
  if (!data || !c) return <div className="os-page">{heading}<LoadingState what="your work" /></div>

  const summary = c.total === 0 ? 'Nothing assigned to you.'
    : `${plural(c.total, 'item')} assigned to you (${plural(c.tasks, 'task')}, ${plural(c.gates, 'gate')}, ${plural(c.steps, 'step')}, ${plural(c.conditions, 'condition')}, ${plural(c.pirActions, 'PIR action')}${c.reviews ? `, ${plural(c.reviews, 'review')}` : ''})`

  const columns: Column<Item>[] = [
    { key: 'due', header: 'Due (UTC)', cell: it => { const d = it.dueIsDate ? dueText({ dueOn: it.due, dueAt: null }) : dueText({ dueOn: null, dueAt: it.due }); return <TwoLine primary={d.utc} secondary={d.local ?? undefined} /> } },
    { key: 'kind', header: 'Kind', cell: it => it.kind },
    {
      key: 'item', header: 'Item',
      cell: (it) => (
        <TwoLine primary={<button type="button" className="os-link os-link-button" data-rownav aria-current={it === items[sel ?? -1] ? 'true' : undefined}>{it.title}</button>}
          secondary={it.decision
            ? <>{it.detail ? `${it.detail} · ` : ''}Go/No-Go condition · <button type="button" className="os-link os-link-button" aria-label={`Review in Approvals: ${it.title}`}
              onClick={(e: MouseEvent) => { e.stopPropagation(); onOpenApprovals?.() }}>Review in Approvals</button></>
            : it.detail ?? undefined} />
      ),
    },
    { key: 'train', header: 'Release', cell: it => it.trainTitle },
    { key: 'status', header: 'Status', cell: it => <span className="os-chips">{it.status.map(p => <StatusChip key={p.text} tone={p.tone}>{p.text}</StatusChip>)}</span> },
    { key: 'via', header: 'Assigned via', cell: it => (it.via?.kind === 'team' ? `Team ${it.via.teamName ?? ''}` : 'Me') },
  ]
  const openRow = (e: MouseEvent<HTMLDivElement>) => {
    const tr = (e.target as HTMLElement).closest('tbody tr'); if (!tr) return
    const i = Array.from(nav.ref.current?.querySelectorAll('tbody tr') ?? []).indexOf(tr)
    const it = items[i]; if (it) { setSel(i); onOpen?.(it.target) }
  }

  return (
    <div className="os-page">
      {heading}
      {error && <ErrorState what="Your work" message={`${error} The list below is from the last successful read.`} onRetry={() => setTick(n => n + 1)} />}
      <div className="os-metrics">
        <MetricCard label="Assigned to you" value={c.total} detail={c.total === 0 ? 'Nothing open' : `${c.tasks + c.gates} tasks and gates, ${plural(c.steps, 'step')}`} />
        <MetricCard label="Overdue" value={c.overdue} detail={c.overdue > 0 ? 'Needs action' : 'Nothing overdue'} tone={c.overdue > 0 ? 'danger' : 'success'} />
        <MetricCard label="Decisions" value={c.conditions} detail={c.conditions > 0 ? 'Go/No-Go conditions, closed in Approvals' : 'No conditions open'} tone={c.conditions > 0 ? 'warning' : 'neutral'} />
      </div>
      <Panel title="Items assigned to me" subtitle={<span data-testid="work-counts">{summary}</span>}>
        {items.length === 0
          ? <EmptyState title="No open items">No open tasks, gates, steps, conditions or PIR actions are assigned to you or your teams. Items appear here as trains move.</EmptyState>
          : (
            <div {...nav} onClick={openRow}>
              <DataTable columns={columns} rows={items} rowKey={it => it.key} caption="Items assigned to me, by due date" selectedKey={sel !== null ? items[sel]?.key : null}
                footer={<><span>In the table, j / k moves the selection and Enter opens the item in its train.</span><span>{plural(items.length, 'item')}</span></>} />
            </div>
          )}
      </Panel>
    </div>
  )
}
