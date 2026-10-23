import { useEffect, useState } from 'react'
import { DataTable, PageHeading, Panel, StatusChip, TwoLine, type Column } from '../ui'
import { KIND, dueText, getApprovals, type ApprovalItem, type ApprovalQueue } from './approvalsApi.ts'

/**
 * The Approvals destination (REOS-94): the decisions waiting for the signed-in person, from GET /approvals, the same queue whose total is the sidebar badge.
 * The designed Approvals screen (evidence, several approvers, policies) is REOS-99; this lists the queue and opens the release where each decision is made.
 */
export function Approvals({ refreshKey, onOpen }: { refreshKey: number; onOpen: (trainId: string, gateId?: string) => void }) {
  const [queue, setQueue] = useState<ApprovalQueue | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => { getApprovals().then(q => { setQueue(q); setError(null) }).catch((e: Error) => setError(e.message)) }, [refreshKey])
  const columns: Column<ApprovalItem>[] = [
    { key: 'decision', header: 'Decision', cell: i => <TwoLine primary={`${KIND[i.kind]}: ${i.title}`} secondary={i.detail ?? undefined} /> },
    { key: 'release', header: 'Release', cell: i => <button type="button" className="os-link os-link-button" onClick={() => onOpen(i.trainId, i.kind === 'CertifyGate' ? i.id : undefined)}>{i.trainTitle}</button> },
    { key: 'due', header: 'Due', cell: i => { const d = dueText(i); return <TwoLine primary={d.utc} secondary={d.local ?? undefined} /> } },
    { key: 'state', header: 'Status', cell: i => <StatusChip tone={i.overdue ? 'danger' : 'warning'}>{i.overdue ? 'Overdue' : 'Pending'}</StatusChip> },
  ]
  const total = queue?.counts.total ?? 0
  return (
    <div className="os-page">
      <PageHeading trail={['Workspace', 'Approvals']} title="Approvals"
        description="Decisions waiting for you: gates to certify, waivers and freeze overrides to approve, Go/No-Go decisions and Go conditions to close." />
      {error && <p className="bad" role="alert">✗ The approval queue could not be read: {error}</p>}
      <Panel title="Waiting for your decision" meta={queue ? <span>{total} {total === 1 ? 'decision' : 'decisions'}</span> : undefined}>
        {queue === null && !error ? <p className="os-muted">Loading…</p>
          : <DataTable caption="Decisions waiting for you" columns={columns} rows={queue?.items ?? []} rowKey={i => `${i.kind}:${i.id}`} empty="Nothing is waiting for your decision." />}
      </Panel>
    </div>
  )
}
