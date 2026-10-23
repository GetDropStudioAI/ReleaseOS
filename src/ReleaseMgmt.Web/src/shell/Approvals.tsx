import { useCallback, useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react'
import { get } from '../api'
import {
  Button, DataTable, EmptyState, ErrorState, LoadingState, MetricCard, NoPermissionState, PageHeading, Panel, StatusChip, TabPanel, Tabs, TwoLine, type Column,
} from '../ui'
import { KIND, dueText, getApprovals, type ApprovalItem, type ApprovalQueue } from './approvalsApi.ts'
import { EXCEPTION_KINDS, countdown, dueWithinHour, itemKey, overdueCount, shortRef } from './approvalsLogic.ts'
import { DecisionPane } from './ApprovalDecision.tsx'
import { announce } from '../announce'
import './approvals.css'

/**
 * The Approvals page (REOS-99, Figma 6:5838): four metric cards, the Pending / Decided / Exceptions / Policy library tabs, the queue on the left and the
 * decision pane on the right. The queue is GET /approvals (what waits for the signed-in person; the sidebar badge is its total). Parts of the design
 * that need data the app lacks (several required approvers, policies, policy coverage) say so on the screen; see Q-0990 to Q-0993 in docs/QUESTIONS.md.
 */
type Tab = 'pending' | 'decided' | 'exceptions' | 'policies'

/** A clock that re-renders the page every `ms`, so "Due in 28m" counts down and flips to "Overdue by …" without a reload. */
function useNow(ms = 30000) {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => { const t = setInterval(() => setNow(Date.now()), ms); return () => clearInterval(t) }, [ms])
  return now
}

export function Approvals({ refreshKey, onOpen, onViewAudit, canAudit = false }: {
  refreshKey: number; onOpen: (trainId: string, gateId?: string) => void; onViewAudit?: (eventId: number) => void; canAudit?: boolean
}) {
  const [queue, setQueue] = useState<ApprovalQueue | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [tab, setTab] = useState<Tab>('pending')
  const [selected, setSelected] = useState<string | null>(null)
  const [rev, setRev] = useState(0)
  const now = useNow()
  const paneRef = useRef<HTMLDivElement>(null)

  const loadQueue = useCallback(() => {
    getApprovals().then(q => { setQueue(q); setError(null) }).catch((e: Error) => setError(e.message))
  }, [])
  useEffect(loadQueue, [loadQueue, refreshKey, rev])

  const items = useMemo(() => queue?.items ?? [], [queue])
  const shown = useMemo(() => tab === 'exceptions' ? items.filter(i => EXCEPTION_KINDS.includes(i.kind)) : items, [items, tab])
  // keep a valid selection: the first item by default (the design opens with one selected), and the next one once the selected item is decided
  useEffect(() => {
    if (tab !== 'pending' && tab !== 'exceptions') return
    if (selected === null || !shown.some(i => itemKey(i) === selected)) setSelected(shown[0] ? itemKey(shown[0]) : null)
  }, [shown, selected, tab])
  const item = shown.find(i => itemKey(i) === selected) ?? null

  const exceptions = items.filter(i => EXCEPTION_KINDS.includes(i.kind)).length
  const tabs = [
    { id: 'pending', label: 'Pending', count: items.length },
    { id: 'decided', label: 'Decided' },
    { id: 'exceptions', label: 'Exceptions', count: exceptions },
    { id: 'policies', label: 'Policy library' },
  ]
  const overdue = overdueCount(items, now), soon = dueWithinHour(items, now)
  const decided = (what: string) => { announce(`${what}. It is recorded in the audit log.`); setRev(n => n + 1) }

  return (
    <div className="os-page">
      <PageHeading trail={['Workspace', 'Approvals']} title="Approvals"
        description="Make accountable release decisions with evidence, and a complete decision trail."
        actions={<Button variant="primary" disabled={items.length === 0} onClick={() => { setTab('pending'); setSelected(items[0] ? itemKey(items[0]) : null); requestAnimationFrame(() => paneRef.current?.querySelector<HTMLElement>('button, a, textarea, input')?.focus()) }}>Review queue</Button>} />
      {error && <ErrorState what="The approval queue" message={error} onRetry={loadQueue} />}

      <div className="ap-metrics">
        <MetricCard label="Pending approvals" value={queue ? items.length : '–'} detail={overdue > 0 ? `${overdue} overdue` : 'None overdue'} tone={overdue > 0 ? 'danger' : 'success'} />
        <MetricCard label="Due within an hour" value={queue ? soon : '–'} detail="Timed decisions only" tone={soon > 0 ? 'warning' : 'neutral'} />
        <MetricCard label="Open exceptions" value={queue ? exceptions : '–'} detail="Waivers and freeze overrides waiting for you" tone={exceptions > 0 ? 'warning' : 'neutral'} />
        <MetricCard label="Policy coverage" value="Not available" detail="No approval policies exist yet (REOS-113)" tone="neutral" />
      </div>

      <div>
        <Tabs tabs={tabs} value={tab} onChange={id => setTab(id as Tab)} label="Approvals" group="ap" />
        <TabPanel id={tab} group="ap">
          {tab === 'decided' && <Decided canAudit={canAudit} onViewAudit={onViewAudit} refreshKey={rev + refreshKey} />}
          {tab === 'policies' && (
            <Panel title="Policy library">
              <EmptyState title="No approval policies yet">
                Who must approve what, by environment and risk tier, is not configurable in this version: the rules are built in (a Governance Officer certifies Compliance
                gates and approves waivers, a Release Manager or Governance Officer approves freeze overrides, a Release Manager records Go/No-Go). Configurable policies are REOS-113.
              </EmptyState>
            </Panel>
          )}
          {(tab === 'pending' || tab === 'exceptions') && queue && (
            items.length === 0
              ? <Panel title="Approval queue"><EmptyState title="Nothing is waiting for your decision">Decisions appear here when a gate is ready to certify, a waiver or freeze override is requested, or a Go/No-Go is due. Your role decides which ones you see.</EmptyState></Panel>
              : <div className="ap-split">
                  <Queue items={shown} selected={selected} now={now} onSelect={setSelected} empty={tab === 'exceptions' ? 'No waivers or freeze overrides are waiting for you.' : ''} />
                  <div ref={paneRef} className="ap-pane-wrap">
                    {item ? <DecisionPane item={item} onDecided={decided} onOpen={onOpen} /> : <Panel title="Decision"><EmptyState title="Select a decision">Pick an item in the queue to see the release, the evidence and what you can do.</EmptyState></Panel>}
                  </div>
                </div>
          )}
          {(tab === 'pending' || tab === 'exceptions') && !queue && !error && <LoadingState what="the approval queue" />}
        </TabPanel>
      </div>
    </div>
  )
}

function Queue({ items, selected, now, onSelect, empty }: { items: ApprovalItem[]; selected: string | null; now: number; onSelect: (key: string) => void; empty: string }) {
  const keys = (e: KeyboardEvent<HTMLButtonElement>) => {
    if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return
    e.preventDefault()
    const li = e.currentTarget.closest('li')
    const next = (e.key === 'ArrowDown' ? li?.nextElementSibling : li?.previousElementSibling)?.querySelector<HTMLElement>('button')
    next?.focus()
  }
  return (
    <section className="os-panel ap-queue-panel" aria-labelledby="ap-queue-title">
      <header className="os-panel-head"><h2 id="ap-queue-title" className="os-panel-title">Approval queue</h2><div className="os-panel-meta">Due soonest</div></header>
      {items.length === 0 ? <p className="os-muted ap-empty">{empty}</p> : (
        <ul className="ap-queue">
          {items.map(i => {
            const c = countdown(i, now), key = itemKey(i), on = key === selected
            return (
              <li key={key}>
                <button type="button" className={`ap-item${on ? ' selected' : ''}`} aria-current={on ? 'true' : undefined} onClick={() => onSelect(key)} onKeyDown={keys}>
                  <span className="ap-item-top"><span className="mono ap-ref">{shortRef(i)}</span>
                    <span className={`ap-due ${c.overdue ? 'danger' : c.soon ? 'warning' : ''}`}>{c.overdue ? '▲ ' : ''}{c.text}</span></span>
                  <span className="ap-item-title">{i.title}</span>
                  <span className="ap-item-sub">{KIND[i.kind]} · {i.trainTitle}</span>
                </button>
              </li>
            )
          })}
        </ul>
      )}
      <footer className="os-panel-foot">Separation of duties is enforced: you are only offered decisions you did not request or work on.</footer>
    </section>
  )
}

interface AuditEvent { id: number; occurredAt: string; actorName: string | null; trainTitle: string | null; entityType: string; action: string }
const DECISIONS: Record<string, string> = {
  'StageGate:Certify': 'Gate certified', 'StageGate:Waive': 'Gate waived', 'GateWaiver:Approve': 'Waiver approved', 'FreezeOverride:Grant': 'Override granted',
  'FreezeOverride:Renew': 'Override renewed', 'GoNoGo:Record': 'Go/No-Go recorded', 'GoNoGoCondition:Close': 'Condition closed',
}

/** The Decided tab: the decisions this app records, read from the audit log (so only people who may read it), newest first. */
function Decided({ canAudit, onViewAudit, refreshKey }: { canAudit: boolean; onViewAudit?: (id: number) => void; refreshKey: number }) {
  const [rows, setRows] = useState<AuditEvent[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const load = useCallback(() => {
    get<{ items: AuditEvent[] }>('/api/v1/audit?limit=500').then(p => { setRows(p.items.filter(e => DECISIONS[`${e.entityType}:${e.action}`]).slice(0, 50)); setError(null) }).catch((e: Error) => setError(e.message))
  }, [])
  useEffect(() => { if (canAudit) load() }, [canAudit, load, refreshKey])
  if (!canAudit) return <NoPermissionState what="see decided approvals" who="Release Train Engineers, Release Managers and Governance Officers" />
  if (error) return <ErrorState what="Decided approvals" message={error} onRetry={load} />
  if (!rows) return <LoadingState what="decided approvals" />
  const columns: Column<AuditEvent>[] = [
    { key: 'decision', header: 'Decision', cell: e => <TwoLine primary={DECISIONS[`${e.entityType}:${e.action}`]} secondary={e.trainTitle ?? undefined} /> },
    { key: 'actor', header: 'Decided by', cell: e => e.actorName ?? 'System' },
    { key: 'when', header: 'Recorded (UTC)', cell: e => { const t = dueText({ dueAt: e.occurredAt, dueOn: null }); return <TwoLine primary={t.utc} secondary={t.local ?? undefined} /> } },
    { key: 'outcome', header: 'Outcome', cell: () => <StatusChip tone="success">Recorded</StatusChip> },
    { key: 'audit', header: <span className="sr-only">Audit record</span>, cell: e => onViewAudit ? <button type="button" className="os-link os-link-button" onClick={() => onViewAudit(e.id)} aria-label={`Open audit record ${e.id}`}>Audit record</button> : null },
  ]
  return (
    <Panel title="Decided" subtitle="Decisions recorded in this workspace, from the audit log (the newest 50 of the latest events).">
      <DataTable caption="Decided approvals, newest first" columns={columns} rows={rows} rowKey={e => String(e.id)} empty="No decisions have been recorded yet." />
    </Panel>
  )
}
