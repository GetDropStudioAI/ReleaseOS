import { useEffect, useState } from 'react'
import { Plus } from 'lucide-react'
import { Button, DataTable, EmptyState, ErrorState, FilterSelect, LoadingState, MetricCard, PageHeading, Panel, StatusChip, TwoLine, type Column } from '../ui'
import { dueText } from './approvalsApi.ts'
import { RANGES, SEGMENT_FILTERS, attentionTone, clientDateLines, getOverview, rowsForSegment, successTone, windowText, type ActivityRow, type AttentionItem, type Overview as Data } from './overviewApi.ts'
import { ThroughputChart } from './ThroughputChart.tsx'
import { ChainBriefView } from '../versions/ChainStrip.tsx'
import { typeLabel } from '../mgmt/releaseTypesLogic.ts'
import { kindText } from '../calendarModel.ts'

/**
 * The Organization overview (REOS-95, Figma 6:4718): four metric cards, release throughput, the release activity table, what needs attention, and environment
 * health. Everything comes from GET /overview (REOS-108); the range control changes the cards, the chart and the closed rows together. Owner, team and
 * environment have no source yet (B3, D42), so those columns and the environment panel say so instead of showing made-up values (QUESTIONS Q-095a).
 */
export function Overview({ refreshKey, canCreate, onCreate, onOpen }: { refreshKey: number; canCreate: boolean; onCreate: () => void; onOpen: (trainId: string, gateId?: string) => void }) {
  const [days, setDays] = useState('14')
  const [segment, setSegment] = useState('all')   // RT5: the client-segment filter of the activity table
  const [data, setData] = useState<Data | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [tick, setTick] = useState(0)
  useEffect(() => {
    let live = true
    getOverview(Number(days)).then(d => { if (live) { setData(d); setError(null) } }).catch((e: Error) => { if (live) setError(e.message) })
    return () => { live = false }
  }, [days, refreshKey, tick])

  const actions = <>
    <FilterSelect label="Date range" value={days} options={RANGES} onChange={setDays} />
    {canCreate && <Button variant="primary" icon={Plus} onClick={onCreate}>Create release</Button>}
  </>
  const heading = <PageHeading trail={['Workspace', 'Overview']} title="Overview" description="Releases in flight, what needs attention, and how recent releases went." actions={actions} />
  if (error && !data) return <div className="os-page">{heading}<ErrorState what="The overview" message={error} onRetry={() => setTick(n => n + 1)} /></div>
  if (!data) return <div className="os-page">{heading}<LoadingState what="the overview" /></div>

  const { cards } = data
  const delta = cards.deploymentSuccess.deltaPts
  const columns: Column<ActivityRow>[] = [
    { key: 'release', header: 'Release', cell: r => <><TwoLine primary={<button type="button" className="os-link os-link-button" onClick={() => onOpen(r.trainId)}>{r.title}</button>} secondary={`${r.products} ${r.products === 1 ? 'product' : 'products'}${r.blockers ? ` · ${r.blockers} ${r.blockers === 1 ? 'blocker' : 'blockers'}` : ''}`} />{r.chain && <ChainBriefView brief={r.chain} currentId={r.trainId} />}</> },
    { key: 'type', header: 'Type', cell: r => r.type ? typeLabel(r.type, r.betaNumber) : <span className="os-muted">Not set</span> },
    { key: 'clients', header: 'Client dates', cell: r => { const lines = clientDateLines(r, segment); return lines.length ? <><span className="os-muted">{kindText(r.clientKind ?? '')}</span><ul className="os-clientdates">{lines.map(l => <li key={l}>{l}</li>)}</ul></> : <span className="os-muted">None</span> } },
    { key: 'status', header: 'Status', cell: r => <StatusChip>{r.displayStatus}</StatusChip> },
    { key: 'risk', header: 'Risk', cell: r => r.riskTier },
    { key: 'window', header: 'Window', cell: r => { const w = windowText(r); return <TwoLine primary={w.utc} secondary={w.local ?? undefined} /> } },
  ]
  const attention = data.attention
  const activity = rowsForSegment(data.activity, segment)
  return (
    <div className="os-page">
      {heading}
      {error && <ErrorState what="The overview" message={`${error} The figures below are from the last successful read.`} onRetry={() => setTick(n => n + 1)} />}
      <div className="os-metrics">
        <MetricCard label="Active releases" value={cards.activeReleases.value ?? 0} detail={cards.activeReleases.detail} />
        <MetricCard label="Running now" value={cards.runningNow.value ?? 0} detail={cards.runningNow.detail} />
        <MetricCard label="Need attention" value={cards.needAttention.value ?? 0} detail={cards.needAttention.detail} tone={attentionTone(cards.needAttention.value)} />
        <MetricCard label="Deployment success" value={cards.deploymentSuccess.value === null ? '—' : cards.deploymentSuccess.value} unit={cards.deploymentSuccess.value === null ? undefined : '%'}
          detail={cards.deploymentSuccess.detail} tone={successTone(delta)} />
      </div>
      <div className="os-overview-grid">
        <Panel title="Release throughput" subtitle={`Releases completed per day, ${data.from} to ${data.to} (UTC)`}><ThroughputChart days={data.throughput} /></Panel>
        <Panel title="Needs your attention" meta={<StatusChip tone={attention.length ? 'warning' : 'success'}>{attention.length ? `${attention.length} ${attention.length === 1 ? 'priority' : 'priorities'}` : 'All clear'}</StatusChip>}>
          {attention.length === 0 ? <EmptyState title="Nothing needs attention">Open releases have no failed gates, conflicts or decisions waiting.</EmptyState>
            : <ul className="os-attention">{attention.map(a => <AttentionRow key={`${a.kind}:${a.entityId}`} item={a} onOpen={onOpen} />)}</ul>}
        </Panel>
      </div>
      <Panel title="Release activity" subtitle="Open releases, then those completed or aborted in the range"
        meta={<FilterSelect label="Client segment" value={segment} options={SEGMENT_FILTERS} onChange={setSegment} />}
        footer={<span>Showing {activity.length} {activity.length === 1 ? 'release' : 'releases'} · as of {dueText({ dueAt: data.asOf, dueOn: null }).utc}</span>}>
        <DataTable caption="Release activity" columns={columns} rows={activity} rowKey={r => r.trainId} empty={segment === 'all' ? 'No releases are open and none completed in this range.' : 'No release in this range has dates for these clients.'} />
      </Panel>
      <Panel title="Environment health">
        <EmptyState title="Environments are not tracked yet">Availability and change freezes per environment appear here once environments and deployments are in ReleaseOS (REOS-110).</EmptyState>
      </Panel>
    </div>
  )
}

function AttentionRow({ item, onOpen }: { item: AttentionItem; onOpen: (trainId: string, gateId?: string) => void }) {
  const due = dueText({ dueAt: item.dueAt, dueOn: null })
  return (
    <li className="os-attention-item">
      <StatusChip tone={item.category === 'FailedGate' ? 'danger' : item.category === 'Conflict' ? 'warning' : 'info'}>{item.category === 'FailedGate' ? 'Failed' : item.category === 'Conflict' ? 'Conflict' : 'Approval'}</StatusChip>
      <div className="os-attention-text">
        <p className="os-attention-title">{item.title} · <button type="button" className="os-link os-link-button" onClick={() => onOpen(item.trainId, item.kind === 'CertifyGate' || item.kind === 'FailedGate' ? item.entityId : undefined)}>{item.trainTitle}</button></p>
        <p className="os-attention-detail">{item.detail}{item.actionBy ? ` · ${item.actionBy}` : ''}</p>
      </div>
      <p className="os-attention-due">Due {due.utc}{due.local ? <span className="os-muted"> · {due.local}</span> : null}</p>
    </li>
  )
}
