import { useCallback, useEffect, useState } from 'react'
import { errMsg } from '../format.ts'
import type { Me } from '../api.ts'
import { Button, DataTable, EmptyState, ErrorState, FilterSelect, LoadingState, PageHeading, Panel, ReadOnlyNotice, StatusChip, TimeRange, TwoLine, type Column } from '../ui'
import { onCabChanged } from '../live.ts'
import { getRiskRules, listChanges, type ChangeRow, type RiskRules } from './cabApi.ts'
import { STATUSES, TYPES, canDecide, canPlan, riskTone, riskWord, statusTone, summary } from './cabLogic.ts'
import { RiskRulesPanel } from './RiskPanels.tsx'
import './cab.css'

const ALL = ''

/** The change request list (REOS-176): type, status, risk (as a word), planned window in UTC with the person's own time beside it, requester and release. Select a row to open the change. */
export function ChangeList({ me, onOpen, onNew, onMeetings }: { me: Me; onOpen: (id: string) => void; onNew: () => void; onMeetings?: () => void }) {
  const [rows, setRows] = useState<ChangeRow[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [rules, setRules] = useState<RiskRules | null>(null)
  const [status, setStatus] = useState(ALL)
  const [type, setType] = useState(ALL)
  const reload = useCallback(() => {
    listChanges({ status: status || undefined, type: type || undefined }).then(r => { setRows(r); setError(null) }).catch(e => setError(errMsg(e)))
  }, [status, type])
  useEffect(() => { reload() }, [reload])
  useEffect(() => onCabChanged(reload), [reload])
  useEffect(() => { getRiskRules().then(setRules).catch(() => setRules(null)) }, [])

  const plan = canPlan(me)
  const columns: Column<ChangeRow>[] = [
    { key: 'change', header: 'Change', cell: r => <TwoLine primary={r.title} secondary={r.key} /> },
    { key: 'type', header: 'Type', cell: r => r.changeType },
    { key: 'status', header: 'Status', cell: r => <StatusChip tone={statusTone(r.status)}>{r.status}</StatusChip> },
    { key: 'risk', header: 'Risk', cell: r => <StatusChip tone={riskTone(r.riskLevel)}>{riskWord(r.riskLevel)}</StatusChip> },
    { key: 'window', header: 'Planned window', cell: r => r.plannedStartAt && r.plannedEndAt ? <TimeRange from={r.plannedStartAt} to={r.plannedEndAt} /> : <span className="os-muted">Not set</span> },
    { key: 'requester', header: 'Requested by', cell: r => r.requestedBy.name },
    { key: 'release', header: 'Release', cell: r => r.release?.title ?? <span className="os-muted">None</span> },
  ]
  return (
    <div className="os-page">
      <PageHeading trail={['Workspace', 'Change advisory board']} title="Change advisory board" description="Change requests, each with a risk level you can read the reasons for."
        actions={<span className="mg-actions">{onMeetings && <Button onClick={onMeetings}>CAB meetings</Button>}{plan && <Button variant="primary" onClick={onNew}>New change request</Button>}</span>} />
      {!plan && <ReadOnlyNotice>{canDecide(me) ? 'You can read change requests and record decisions on them. An RTE or Release Manager raises and edits them.' : 'You can read change requests. An RTE or Release Manager raises and edits them.'}</ReadOnlyNotice>}
      <Panel title="Change requests" subtitle="Newest first" meta={rows ? summary(rows) : undefined}>
        <div className="mg-stack">
          <div className="cab-filters" role="group" aria-label="Filter changes">
            <FilterSelect label="Status" value={status} onChange={setStatus} options={[{ value: ALL, label: 'All statuses' }, ...STATUSES.map(s => ({ value: s, label: s }))]} />
            <FilterSelect label="Type" value={type} onChange={setType} options={[{ value: ALL, label: 'All types' }, ...TYPES.map(t => ({ value: t, label: t }))]} />
          </div>
          {error && !rows ? <ErrorState what="Change requests" message={error} onRetry={reload} />
            : !rows ? <LoadingState what="change requests" />
            : rows.length === 0 && !status && !type ? <EmptyState title="No change requests yet" action={plan ? <Button variant="primary" onClick={onNew}>New change request</Button> : undefined}>Raise one to see its risk rules at work.</EmptyState>
            : <DataTable caption="Change requests" columns={columns} rows={rows} rowKey={r => r.id} onSelect={r => onOpen(r.id)} empty="No change request matches these filters." />}
        </div>
      </Panel>
      <RiskRulesPanel rules={rules} />
    </div>
  )
}
