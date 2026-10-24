import { useEffect, useMemo, useState } from 'react'
import { CircleAlert, CircleCheck, CircleDashed, CircleX, type LucideIcon } from 'lucide-react'
import {
  advanceTrain, get, getChangeRecord, getDecisions, getOwners, getProducts,
  type Advisories, type GateRow, type Readiness, type TrainDetail,
} from '../api'
import { useDraft } from '../session'
import { ChangeRecordPanel, GoNoGo, emptyGoNoGo, goNoGoKey, type GoNoGoDraft } from '../Governance'
import { FreezesPanel } from '../Freezes'
import { Links } from '../Links'
import { Runbook } from '../Runbook'
import { Closeout } from '../Closeout'
import { commsKey, type CommsDraft } from '../CommsDrawer'
import { ExportsPanel } from '../ExportsPanel'
import { Checklist, Products, Timeline, WindowLine, type Selection } from '../Planning'
import { scrollAndFocus } from '../focus'
import { MilestonesSection, milestoneState, useMilestones } from '../Milestones'
import { CloseDialog, type Closing } from '../TrainClose'
import { AdvisoryItems, ContingencyLine } from '../Advisories'
import { day, errMsg, plural, splitId, tMinus } from '../format'
import { announce } from '../announce'
import { typeLabel } from '../mgmt/releaseTypesLogic'
import { browserZone } from '../shell/clock'
import { serverNow } from '../time'
import type { Mode } from '../route'
import {
  Button, DataTable, DisabledAction, EmptyState, ErrorState, FilterSelect, LoadingState, MetricCard, NoPermissionState, Notice, PageHeading, Panel, ProgressBar,
  PropertyList, StatusChip, TabPanel, Tabs, TwoLine, type Column, type TabItem,
} from '../ui'
import {
  NOT_RECORDED, TAB_LABEL, TABS, goCard, isPassed, gateState, manifestLocked, openDecisions, releaseNotice, releaseProperties, riskChip, stamp, statusChip,
  summarizeGates, waitingFor, windowStamp, type GoKey, type OpenDecision, type TabId,
} from './model'
import { useLoad } from './useLoad'
import './release.css'

const ORDER = ['Planning', 'Gated', 'Executing', 'Complete']
const GO_ICON: Record<GoKey, LucideIcon> = { Go: CircleCheck, GoWithConditions: CircleAlert, NoGo: CircleX, None: CircleDashed }

/** A time in UTC, labelled, with the person's own time beside it (D44). */
export function Stamp({ iso }: { iso: string }) {
  const s = stamp(iso, browserZone())
  return <span className="rd-stamp"><span className="mono">{s.utc}</span>{s.local && <span className="rd-local"> ({s.local} your time)</span>}</span>
}

/** Waits for an element a tab switch is about to mount (its data arrives after the switch), then scrolls to it and moves focus there. */
function focusWhenPresent(find: () => HTMLElement | null, frames = 90) {
  const tick = (n: number) => {
    const el = find()
    if (el) {
      scrollAndFocus(el)
      // the panels above may still be loading and push the target down: settle, then bring it back into view
      setTimeout(() => { const r = el.getBoundingClientRect(); if (el.isConnected && (r.bottom < 0 || r.top > window.innerHeight)) el.scrollIntoView({ block: 'start' }) }, 700)
    } else if (n > 0) requestAnimationFrame(() => tick(n - 1))
  }
  requestAnimationFrame(() => tick(frames))
}

export interface ReleaseDetailProps {
  t: TrainDetail; r: Readiness | null; rErr: string | null; adv: Advisories | null; err: string | null; refreshKey: number
  onChanged: () => void; retry: () => void; selection: Selection; onSelect: (s: Selection) => void; canPlan: boolean; canDecide: boolean; onMode: (m: Mode) => void
  chosenGate: string | null; onPickGate: (gateId: string) => void
}

/**
 * The release workspace in the redesign's structure (REOS-97, Figma 6:5279): page heading, context row, tabs (Overview & readiness, Services, Dependencies,
 * Approvals, Activity). It binds to the real data: gates and their checks are the quality gates, products are the service manifest, the Go/No-Go decisions
 * are the dark card. Fields the design shows that the app does not record (team, owner, environments, release branch, on-call; REOS-107) say so.
 * The planning sections (G4, REOS-126) are on Overview; the governance and close-out sections (G5, REOS-127) are on Approvals and Activity.
 */
export function ReleaseDetail(p: ReleaseDetailProps) {
  const { t, r, adv, refreshKey, onChanged, selection, onSelect, canPlan, canDecide } = p
  const [tab, setTab] = useState<TabId>('overview')
  const [closing, setClosing] = useState<Closing | null>(null)
  const [busy, setBusy] = useState(false)
  const [actionErr, setActionErr] = useState<string | null>(null)
  const [showOnly, setShowOnly] = useState<'all' | 'outstanding'>('all')
  const [goDraft, setGoDraft] = useDraft<GoNoGoDraft>(goNoGoKey(t.id))   // the header button and the Go/No-Go section share one draft
  const [comms, setComms] = useDraft<CommsDraft>(commsKey(t.id))   // REOS-45: App swaps the right drawer to the Comms drawer while this is open
  const milestones = useMilestones(t.id, refreshKey)   // Q-0843: the timeline's ◆ markers and the Milestones section share one fetch
  const decisions = useLoad(() => getDecisions(t.id), [t.id, refreshKey])
  const products = useLoad(() => getProducts(t.id), [t.id, refreshKey])
  const owners = useLoad(() => getOwners(), [])
  const changeRecord = useLoad(() => getChangeRecord(t.id).catch(() => null), [t.id, refreshKey])
  const model = useLoad(() => get<{ type: { id: string; name: string } | null; betaNumber: number | null }>(`/api/v1/trains/${t.id}/release`).catch(() => null), [t.id, refreshKey])   // REOS-116: the release's type

  const { id: relId, name } = splitId(t.title)
  const open = t.status === 'Planning' || t.status === 'Gated' || t.status === 'Executing'
  const blockers = r?.blockers.length ?? 0
  const gatesBefore = t.nextStatus ? t.gates.filter(g => ORDER.indexOf(g.requiredBeforeStatus) <= ORDER.indexOf(t.nextStatus!)) : []
  const others = (r?.blockers ?? []).filter(b => b.failure.guard !== 'GateLockout')
  const guardsTotal = gatesBefore.length + others.length
  const summary = useMemo(() => summarizeGates(t.gates), [t.gates])
  const evaluatedAt = useMemo(() => new Date(serverNow()).toISOString(), [t, r])   // eslint-disable-line react-hooks/exhaustive-deps
  const go = useMemo(() => goCard(decisions.data ?? []), [decisions.data])
  const decisionsOpen = useMemo(() => openDecisions(t.gates, decisions.data ?? [], t.status), [t.gates, decisions.data, t.status])
  const locked = manifestLocked(t.status)
  const ownerName = (id: string) => (owners.data ?? []).find(o => o.id === id && o.kind === 'user')?.name ?? 'Unknown user'
  const checklistGate = t.gates.find(g => g.id === p.chosenGate) ?? t.gates.find(g => !isPassed(g.status)) ?? t.gates[t.gates.length - 1]

  const counts: Record<TabId, number | undefined> = {
    overview: undefined, services: products.data?.products.length, dependencies: adv ? adv.collisions.length : undefined,
    approvals: decisions.data ? decisionsOpen.length : undefined, activity: undefined,
  }
  const tabs: TabItem[] = TABS.map(id => ({ id, label: TAB_LABEL[id], count: counts[id] }))

  const advance = async () => {
    if (!t.nextStatus) return
    setBusy(true); setActionErr(null)
    try { await advanceTrain(t.id, t.nextStatus, t.version); announce(`Advanced to ${t.nextStatus}.`); onChanged() }
    catch (e) { setActionErr(errMsg(e, 'Could not advance the release.')); onChanged() }
    finally { setBusy(false) }
  }
  const toApprovals = () => { setTab('approvals'); focusWhenPresent(() => document.querySelector<HTMLElement>('#release-panel-approvals h2')) }
  const recordGo = () => {
    if (goDraft) { setGoDraft(undefined); return }
    setGoDraft(emptyGoNoGo())
    setTab('approvals')   // the form opens on the Approvals tab: take the person there (UX review 11)
    focusWhenPresent(() => document.querySelector<HTMLElement>('section[aria-label="Go/No-Go"] h2'))
  }
  const exportPanel = () => { setTab('activity'); focusWhenPresent(() => document.querySelector<HTMLElement>('#exports h2')) }
  const reviewBlockers = () => {
    setTab('overview'); setShowOnly('outstanding')
    focusWhenPresent(() => document.querySelector<HTMLElement>('#rd-gates tbody tr'))
  }
  const noticeInfo = releaseNotice({ status: t.status, nextStatus: t.nextStatus, ready: r ? r.ready : null, guardsMet: Math.max(0, guardsTotal - blockers), guardsTotal, blockers })

  const lifecycle = t.nextStatus === 'Complete'
    ? canPlan && (blockers > 0
      ? <DisabledAction variant="primary" reason={`Complete is off: ${plural(blockers, 'check')} not met, listed under “To reach ${t.nextStatus}”.`}>Complete…</DisabledAction>
      : <Button variant="primary" onClick={() => setClosing('complete')}>Complete…</Button>)
    : t.nextStatus && (blockers > 0
      ? <DisabledAction variant="primary" reason={`Advance to ${t.nextStatus} is off: ${plural(blockers, 'check')} not met, listed under “To reach ${t.nextStatus}”.`}>{`Advance to ${t.nextStatus}`}</DisabledAction>
      : <Button variant="primary" disabled={busy} onClick={advance}>{busy ? 'Advancing…' : `Advance to ${t.nextStatus}`}</Button>)

  const sc = statusChip(t.status, t.closeCode), rc = riskChip(t.riskTier)
  return (
    <div className="os-page rd">
      <PageHeading trail={['Releases', relId || name]} title={relId ? `${relId} ${name}` : name}
        description={<>{t.status} · Target <strong>{day(t.targetReleaseDate)} {t.targetReleaseDate.slice(0, 4)}</strong> · <span className="mono">{tMinus(t.daysToTarget)}</span> business days · release <span className="mono">v{t.version}</span></>}
        actions={<><Button onClick={() => p.onMode('live')}>Live runbook</Button>{lifecycle}</>} />
      <div className="rd-context" aria-label="Release context" role="group">
        <StatusChip tone={sc.tone}>{sc.word}</StatusChip>
        <StatusChip tone={rc.tone}>{rc.word}</StatusChip>
        <span className="rd-fact"><span className="rd-k">Type</span> {model.data?.type ? <span>{typeLabel(model.data.type, model.data.betaNumber)}</span> : <span className="rd-na">Not set</span>}</span>
        <span className="rd-fact"><span className="rd-k">Team</span> <span className="rd-na">{NOT_RECORDED}</span></span>
        <span className="rd-fact"><span className="rd-k">Owner</span> <span className="rd-na">{NOT_RECORDED}</span></span>
        {t.changeTicketNumber && <span className="rd-fact"><span className="rd-k">Change</span> <span className="mono">{t.changeTicketNumber}</span></span>}
        <WindowLine trainId={t.id} canEdit={canPlan} refreshKey={refreshKey} onChanged={onChanged} collisions={adv?.collisions} />
      </div>
      <div className="rd-actions" role="group" aria-label="Release actions">
        <Button onClick={() => p.onMode('rehearsal')}>Rehearsal</Button>
        {canDecide && <Button onClick={recordGo}>{goDraft ? 'Cancel Go/No-Go' : 'Record Go/No-Go'}</Button>}
        <Button data-comms-trigger aria-expanded={!!comms?.open} onClick={() => setComms(comms?.open ? undefined : { ...comms, open: true, tab: comms?.tab ?? 'message' })}>Communicate</Button>
        <Button onClick={exportPanel}>Export</Button>
        {canPlan && open && <Button variant="destructive" onClick={() => setClosing('abort')}>Abort…</Button>}
      </div>
      {(actionErr || p.err) && <div role="alert"><Notice tone="danger" title="That did not work">{actionErr ?? p.err}</Notice></div>}
      <Tabs tabs={tabs} value={tab} onChange={id => setTab(id as TabId)} label="Release sections" group="release" />

      {tab === 'overview' && <TabPanel id="overview" group="release">
        <Overview {...p} summary={summary} evaluatedAt={evaluatedAt} go={go} decisions={decisions} ownerName={ownerName} changeRecord={changeRecord.data}
          notice={noticeInfo} guardsTotal={guardsTotal} gatesBefore={gatesBefore} others={others} showOnly={showOnly} setShowOnly={setShowOnly}
          checklistGate={checklistGate} milestones={milestones} onReviewBlockers={reviewBlockers} onReviewApprovals={toApprovals} locked={locked} />
      </TabPanel>}
      {tab === 'services' && <TabPanel id="services" group="release">
        <Products trainId={t.id} refreshKey={refreshKey} selection={selection} onSelect={onSelect} canPlan={canPlan} trainStatus={t.status} onChanged={onChanged} manifestVersion={t.version} />
      </TabPanel>}
      {tab === 'dependencies' && <TabPanel id="dependencies" group="release">
        <Dependencies adv={adv} />
      </TabPanel>}
      {tab === 'approvals' && <TabPanel id="approvals" group="release">
        <div className="rd-stack">
          <OpenDecisions rows={decisionsOpen} loaded={!!decisions.data} error={decisions.error} onRetry={decisions.reload}
            onOpen={row => { if (row.gateId) { setTab('overview'); p.onPickGate(row.gateId) } else if (row.kind === 'GoNoGo' && canDecide) recordGo() }} />
          <GoNoGo trainId={t.id} trainVersion={t.version} canDecide={canDecide} refreshKey={refreshKey} onChanged={onChanged} />
          <ChangeRecordPanel trainId={t.id} canEdit={canPlan} refreshKey={refreshKey} onChanged={onChanged} />
          <FreezesPanel trainId={t.id} refreshKey={refreshKey} onChanged={onChanged} />
          <Closeout train={t} canPlan={canPlan} refreshKey={refreshKey} onChanged={onChanged} part="rollback" />
        </div>
      </TabPanel>}
      {tab === 'activity' && <TabPanel id="activity" group="release">
        <div className="rd-stack">
          <Activity trainId={t.id} refreshKey={refreshKey} />
          <Links key={t.id} trainId={t.id} canEdit={canPlan} refreshKey={refreshKey} onChanged={onChanged} />
          <Closeout train={t} canPlan={canPlan} refreshKey={refreshKey} onChanged={onChanged} part="review" />
          <ExportsPanel trainId={t.id} refreshKey={refreshKey} />
        </div>
      </TabPanel>}
      <CloseDialog kind={closing} trainId={t.id} version={t.version} status={t.status} onCancel={() => setClosing(null)} onDone={() => { setClosing(null); onChanged() }} />
    </div>
  )
}

// ---- Overview & readiness -----------------------------------------------------------------------------------------------------------------
type Loaded<T> = { data: T | null; error: string | null; reload: () => void }

function Overview(p: ReleaseDetailProps & {
  summary: ReturnType<typeof summarizeGates>; evaluatedAt: string; go: ReturnType<typeof goCard>; decisions: Loaded<import('../api').DecisionView[]>
  ownerName: (id: string) => string; changeRecord: import('../api').ChangeRecord | null; notice: ReturnType<typeof releaseNotice>; guardsTotal: number
  gatesBefore: GateRow[]; others: Readiness['blockers']; showOnly: 'all' | 'outstanding'; setShowOnly: (v: 'all' | 'outstanding') => void
  checklistGate: GateRow | undefined; milestones: ReturnType<typeof useMilestones>; onReviewBlockers: () => void; onReviewApprovals: () => void; locked: boolean
}) {
  const { t, r, adv, summary, go } = p
  const outstandingOnly = p.showOnly === 'outstanding'
  const rows = outstandingOnly ? t.gates.filter(g => !isPassed(g.status)) : t.gates
  const pct = Math.round(summary.ready * 100)
  const gateColumns: Column<GateRow>[] = [
    { key: 'gate', header: 'Gate group', cell: g => <TwoLine primary={g.name} secondary={isPassed(g.status) ? `${g.class} · ${g.status === 'Waived' ? 'waived' : 'certified'}${g.certifiedBy ? ` by ${g.certifiedBy}` : ''}` : `${g.class} · due ${day(g.dueOn)} · ${tMinus(g.tMinus)}`} /> },
    { key: 'checks', header: 'Checks', cell: g => <span className="mono">{g.tasksDone} / {g.tasksTotal}</span> },
    { key: 'owner', header: 'Owner', cell: g => g.ownerName ?? <span className="rd-na">Not set</span> },
    { key: 'state', header: 'State', cell: g => { const s = gateState(g.status); return isPassed(g.status) ? <StatusChip tone={s.tone}>{s.word}</StatusChip> : <TwoLine primary={<StatusChip tone={s.tone}>{s.word}</StatusChip>} secondary={waitingFor(g)} /> } },
  ]
  const GoIcon = GO_ICON[go.key]
  const decider = go.decision ? p.ownerName(go.decision.decidedByUserId) : null
  const props = releaseProperties({ changeTicket: t.changeTicketNumber, backoutPlan: p.changeRecord?.backoutPlan ?? null, rollbackRehearsed: t.rollbackRehearsed, riskTier: t.riskTier, cabDate: p.changeRecord?.cabDate ?? null, locked: p.locked })
  return (
    <div className="rd-stack">
      <Notice tone={p.notice.tone} title={p.notice.title} action={p.notice.review && <button type="button" className="os-link os-link-button" onClick={p.onReviewBlockers}>Review blockers →</button>}>{p.notice.body}</Notice>
      {p.rErr && t.nextStatus && <ErrorState what={`The readiness for ${t.nextStatus}`} message={p.rErr} onRetry={p.retry} />}
      <div className="rd-metrics">
        <MetricCard label="Gates passed" value={`${summary.passed} / ${summary.total}`} detail={summary.outstanding === 0 ? 'All gates passed' : `${plural(summary.outstanding, 'gate')} outstanding`} tone={summary.outstanding === 0 ? 'success' : 'warning'} />
        <MetricCard label="Checks done" value={`${summary.checksDone} / ${summary.checksTotal}`} detail="Across every gate" />
        <MetricCard label="Business days to target" value={t.daysToTarget} detail={day(t.targetReleaseDate)} />
        <MetricCard label={t.nextStatus ? `Blocking ${t.nextStatus}` : 'Blocking'} value={p.r ? r!.blockers.length : '—'} detail={t.nextStatus ? (r ? (r.ready ? 'Ready' : 'Checks not met') : 'Checking') : 'No next status'} tone={r && !r.ready ? 'warning' : r ? 'success' : undefined} />
      </div>

      <Panel label="Quality gates" title="Quality gates" subtitle={<>Evaluated <Stamp iso={p.evaluatedAt} /></>}
        meta={<><span className="rd-score mono" aria-label={`${summary.passed} of ${summary.total} gates passed`}>{summary.passed} / {summary.total}</span>
          <StatusChip tone={summary.outstanding === 0 ? 'success' : 'warning'}>{summary.outstanding === 0 ? 'All passed' : `${summary.outstanding} outstanding`}</StatusChip>
          <FilterSelect label="Show gates" value={p.showOnly} onChange={v => p.setShowOnly(v as 'all' | 'outstanding')} options={[{ value: 'all', label: `All gates (${summary.total})` }, { value: 'outstanding', label: `Outstanding (${summary.outstanding})` }]} /></>}
        footer={<>{plural(rows.length, 'gate')} shown · {pct}% ready</>}>
        <div className="rd-ready"><ProgressBar value={summary.ready} label="Gates ready" tone={summary.outstanding === 0 ? 'success' : 'info'} /><span className="mono rd-pct">{pct}% ready</span></div>
        <div id="rd-gates" className="rd-scroll">
          <DataTable caption="Quality gates" columns={gateColumns} rows={rows} rowKey={g => g.id} selectedKey={p.checklistGate?.id} onSelect={g => p.onPickGate(g.id)}
            empty={t.gates.length === 0 ? 'This release has no gates yet.' : 'Every gate has passed.'} />
        </div>
      </Panel>

      <div className="rd-pair">
        <Panel inverse label="Go/No-Go summary" title="Go / no-go" meta={<StatusChip tone={go.tone}>{go.label}</StatusChip>}>
          <div className={`rd-go rd-go-${go.key}`} data-decision={go.key}>
            <p className="rd-go-head"><GoIcon aria-hidden="true" size={20} strokeWidth={1.6} /><strong>{go.headline}</strong></p>
            {go.decision
              ? <>
                  <p className="rd-go-rationale">{go.decision.notes ?? 'No rationale recorded.'}</p>
                  <p className="rd-go-meta">Decided by {decider} · <Stamp iso={go.decision.decidedAt} /></p>
                  {go.decision.newTargetReleaseDate && <p className="rd-go-meta">New target date {go.decision.newTargetReleaseDate}</p>}
                  {go.openConditions.length > 0 && <ul className="rd-go-conds">{go.openConditions.map(c => <li key={c.id}>{c.text} · expires <Stamp iso={c.expiresAt} /></li>)}</ul>}
                </>
              : <p className="rd-go-rationale">{p.decisions.error ? `The decisions could not be loaded: ${p.decisions.error}` : p.decisions.data ? 'Executing needs a Go from the Release Manager.' : 'Loading…'}</p>}
            <Button onClick={p.onReviewApprovals}>Review approvals</Button>
          </div>
        </Panel>
        <Panel label="Release properties" title="Release properties"><PropertyList items={props} /></Panel>
      </div>

      {t.nextStatus && r && (
        <Panel label="Readiness" title={`To reach ${t.nextStatus}`} subtitle={`${p.guardsTotal - r.blockers.length} of ${p.guardsTotal} checks met`}>
          <ul className="rd-checks">
            {p.gatesBefore.map(g => {
              const ok = isPassed(g.status)
              return <li key={g.id}><span className={ok ? 'ok' : 'bad'} aria-hidden="true">{ok ? '✓' : '✗'}</span> {g.name}<span className="sr-only">{ok ? ': met' : ': not met'}</span>{!ok && <span className="rd-na"> due {day(g.dueOn)}</span>}</li>
            })}
            {p.others.map((b, i) => <li key={i}><span className="bad" aria-hidden="true">✗</span><span className="sr-only">Not met:</span> {b.failure.message}</li>)}
            {r.ready && <li className="ok">✓ Ready for {t.nextStatus}</li>}
            {adv && <AdvisoryItems contingency={adv.contingency} collisions={adv.collisions} item="li" />}
          </ul>
        </Panel>
      )}

      <h2 className="rd-group">Planning</h2>
      <Timeline gates={t.gates} todayT={t.daysToTarget} targetDate={t.targetReleaseDate} selectedId={p.checklistGate?.id ?? null} onSelect={p.onPickGate} trainId={t.id} trainStatus={t.status} canPlan={p.canPlan} onChanged={p.onChanged}
        milestones={(p.milestones.rows ?? []).map(m => ({ id: m.id, name: m.name, dueOn: m.dueOn, tMinus: m.tMinus, state: milestoneState(m) }))} />
      {p.checklistGate && <Checklist gateId={p.checklistGate.id} trainId={t.id} refreshKey={p.refreshKey} selection={p.selection} onSelect={p.onSelect} onChanged={p.onChanged} />}
      <MilestonesSection trainId={t.id} rows={p.milestones.rows} error={p.milestones.error} canPlan={p.canPlan} onChanged={p.onChanged} />
      <Runbook trainId={t.id} canPlan={p.canPlan} refreshKey={p.refreshKey} selection={p.selection} onSelect={p.onSelect} onChanged={p.onChanged} contingency={adv?.contingency ?? null} />
    </div>
  )
}

// ---- Dependencies ---------------------------------------------------------------------------------------------------------------------------
/** Cross-release dependencies are out of v1 (PROJECT_SCOPE section 2); what the app does know is the windows that overlap this one and what they share. */
function Dependencies({ adv }: { adv: Advisories | null }) {
  const collisions = adv?.collisions ?? []
  const columns: Column<NonNullable<Advisories['collisions']>[number]>[] = [
    { key: 'other', header: 'Release', cell: c => <TwoLine primary={c.otherTitle} secondary={c.otherStatus} /> },
    { key: 'window', header: 'Window (UTC)', cell: c => { const w = windowStamp(c.otherStartsAt, c.otherEndsAt, browserZone()); return <TwoLine primary={<span className="mono">{w.utc}</span>} secondary={w.local ? `${w.local} your time` : undefined} /> } },
    { key: 'shared', header: 'Shared', cell: c => [...c.sharedCis.map(x => `CI ${x}`), ...c.sharedProducts].join(', ') || '—' },
  ]
  return (
    <div className="rd-stack">
      <Panel label="Overlapping windows" title="Overlapping windows" subtitle="Releases whose deployment window overlaps this one and shares a service or CI. Advisory: nothing is blocked."
        meta={<StatusChip tone={collisions.length ? 'warning' : 'success'}>{collisions.length ? plural(collisions.length, 'overlap') : 'No overlaps'}</StatusChip>}>
        {adv === null ? <LoadingState what="the overlapping windows" /> : <DataTable caption="Overlapping windows" columns={columns} rows={collisions} rowKey={c => c.otherTrainId} empty="No other release overlaps this window." />}
      </Panel>
      {adv?.contingency && <Panel label="Contingency" title="Rollback contingency"><ContingencyLine c={adv.contingency} /></Panel>}
      <Panel label="External dependencies" title="External dependencies">
        <EmptyState title="Not tracked in this version">Dependencies between releases and on external services (a payments API at a minimum version, a Redis cluster) are not recorded yet. Overlapping windows above are the only dependency signal.</EmptyState>
      </Panel>
    </div>
  )
}

// ---- Approvals ---------------------------------------------------------------------------------------------------------------------------
function OpenDecisions({ rows, loaded, error, onRetry, onOpen }: { rows: OpenDecision[]; loaded: boolean; error: string | null; onRetry: () => void; onOpen: (r: OpenDecision) => void }) {
  const columns: Column<OpenDecision>[] = [
    { key: 'what', header: 'Decision', cell: r => <TwoLine primary={r.title} secondary={r.detail} /> },
    { key: 'kind', header: 'Kind', cell: r => r.kind === 'Gate' ? 'Gate certification' : r.kind === 'GoNoGo' ? 'Go/No-Go' : 'Go condition' },
  ]
  return (
    <Panel label="Open decisions" title="Open decisions" subtitle="What is still waiting for a decision on this release."
      meta={loaded ? <StatusChip tone={rows.length ? 'warning' : 'success'}>{rows.length ? `${rows.length} open` : 'Nothing open'}</StatusChip> : undefined}>
      {error && !loaded ? <ErrorState what="The open decisions" message={error} onRetry={onRetry} />
        : !loaded ? <LoadingState what="the open decisions" />
        : <DataTable caption="Open decisions" columns={columns} rows={rows} rowKey={r => r.key} onSelect={onOpen} empty="Nothing is waiting for a decision." />}
    </Panel>
  )
}

// ---- Activity ---------------------------------------------------------------------------------------------------------------------------------
interface AuditItem { id: number; occurredAt: string; actorName: string | null; entityType: string; action: string }

/** The latest audit events for this release (GET /audit?train=…): Release Managers, RTEs and Governance Officers read it; anyone else is told whose it is. */
function Activity({ trainId, refreshKey }: { trainId: string; refreshKey: number }) {
  const [state, setState] = useState<{ items: AuditItem[] | null; error: string | null; denied: boolean }>({ items: null, error: null, denied: false })
  const [tick, setTick] = useState(0)
  useEffect(() => {
    let live = true
    get<{ items: AuditItem[] }>(`/api/v1/audit?train=${encodeURIComponent(trainId)}&limit=25`)
      .then(d => { if (live) setState({ items: d.items, error: null, denied: false }) })
      .catch((e: unknown) => { if (live) setState(s => ({ ...s, error: errMsg(e), denied: (e as { status?: number }).status === 403 })) })
    return () => { live = false }
  }, [trainId, refreshKey, tick])
  const columns: Column<AuditItem>[] = [
    { key: 'when', header: 'When (UTC)', cell: a => <Stamp iso={a.occurredAt} /> },
    { key: 'event', header: 'Event', cell: a => <TwoLine primary={a.action} secondary={a.entityType} /> },
    { key: 'actor', header: 'Actor', cell: a => a.actorName ?? 'System' },
  ]
  return (
    <Panel label="Recent activity" title="Recent activity" subtitle="The latest audit events on this release; the Audit page has the full history and the before and after of each change.">
      {state.denied ? <NoPermissionState what="read the audit log" who="a Release Manager, RTE or Governance Officer" />
        : state.error && !state.items ? <ErrorState what="The activity" message={state.error} onRetry={() => setTick(n => n + 1)} />
        : !state.items ? <LoadingState what="the activity" />
        : <div className="rd-scroll" tabIndex={0} role="region" aria-label="Recent activity events"><DataTable caption="Recent activity" columns={columns} rows={state.items} rowKey={a => String(a.id)} empty="No activity recorded yet." /></div>}
    </Panel>
  )
}

