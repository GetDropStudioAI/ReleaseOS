import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Plus, RefreshCw } from 'lucide-react'
import { ApiError, type Me } from '../api.ts'
import { announce } from '../announce.tsx'
import { errMsg } from '../format.ts'
import { useAction } from '../useAction.ts'
import { isConflict } from '../Conflict.tsx'
import { canAdmin } from '../shell/nav.ts'
import { Button, ConfirmDialog, ConflictNotice, DataTable, DisabledAction, EmptyState, ErrorState, FilterSelect, LoadingState, NoPermissionState, Notice, PageHeading, Panel, PropertyList, SidePanel, StatusChip, TabPanel, Tabs, Time, TwoLine, type Column } from '../ui'
import { Field, Problem, TextInput } from '../mgmt/forms.tsx'
import '../mgmt/mgmt.css'
import '../tokens/tokens.css'
import './webhooks.css'
import { createSubscription, deleteSubscription, getDelivery, getOverview, listDeliveries, pauseSubscription, replayDelivery, resumeSubscription, rotateSecret, sendTest, updateSubscription } from './webhooksApi.ts'
import {
  ALL_EVENTS, COPY_FALLBACK, allowListNotice, attemptText, attemptsText, deleteImpact, deliveryFooter, deliveryTone, deliveryWord, eventsText, footerText, formProblem, normaliseChoice, prettyPayload,
  queueText, replayReason, resultText, rotateImpact, sortDeliveries, sortSubscriptions, statusTone, type Delivery, type DeliveryDetail, type Subscription, type WebhookOverview,
} from './webhooksLogic.ts'

/**
 * Webhooks (REOS-164, D61, Management group): the signed events ReleaseOS posts to other systems. RTE and Release Manager only (the API's Admin policy); anyone else gets the no-permission page and
 * the API is not called. A webhook is a name, an https address (checked against the administrator's host list) and the events it wants. Create and Rotate secret return the HMAC secret once; it lives in
 * this component's state only, is shown with a Copy button and is dropped when the panel is closed or another row is selected (never stored, logged or put in the URL). Rotate secret and Delete ask first;
 * every change sends the row's Version as If-Match. The delivery log lists every try and can replay a delivery.
 */
type Panelled = { kind: 'none' } | { kind: 'add' } | { kind: 'row'; id: string } | { kind: 'edit'; id: string }
interface Shown { id: string; secret: string; how: 'made' | 'rotated' }
const WHO = 'Release Train Engineers and Release Managers'
const SHOWN_LOG = 100

export function Webhooks({ me }: { me: Me }) {
  if (!canAdmin(me.roles)) {
    return <div className="os-page"><PageHeading trail={['Management', 'Webhooks']} title="Webhooks" /><NoPermissionState what="manage webhooks" who={WHO} /></div>
  }
  return <WebhooksScreen />
}

function WebhooksScreen() {
  const [data, setData] = useState<WebhookOverview | null>(null)
  const [log, setLog] = useState<Delivery[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [logError, setLogError] = useState<string | null>(null)
  const [denied, setDenied] = useState(false)
  const [tab, setTab] = useState<'subscriptions' | 'deliveries'>('subscriptions')
  const [panel, setPanel] = useState<Panelled>({ kind: 'none' })
  const [openDelivery, setOpenDelivery] = useState<string | null>(null)
  const [shown, setShown] = useState<Shown | null>(null)
  const [confirm, setConfirm] = useState<{ kind: 'rotate' | 'delete'; row: Subscription } | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [conflict, setConflict] = useState<ApiError | null>(null)
  const [filterSub, setFilterSub] = useState('')
  const [filterStatus, setFilterStatus] = useState('')
  const { run, pending } = useAction()
  const addButton = useRef<HTMLButtonElement>(null)

  const reload = useCallback(() => getOverview().then(o => { setData({ ...o, subscriptions: sortSubscriptions(o.subscriptions) }); setError(null) })
    .catch(e => { if (e instanceof ApiError && e.status === 403) setDenied(true); else setError(errMsg(e, 'Could not load the webhooks.')) }), [])
  const reloadLog = useCallback(() => listDeliveries({ subscriptionId: filterSub || undefined, status: filterStatus || undefined, take: SHOWN_LOG })
    .then(l => { setLog(sortDeliveries(l.deliveries)); setLogError(null) }).catch(e => setLogError(errMsg(e, 'Could not load the delivery log.'))), [filterSub, filterStatus])
  useEffect(() => { void reload() }, [reload])
  useEffect(() => { void reloadLog() }, [reloadLog])
  // While something is still being tried and the log is open, look again every few seconds: the person watches a test event arrive.
  const anyPending = (log ?? []).some(d => d.status === 'Pending')
  useEffect(() => {
    if (tab !== 'deliveries' || !anyPending) return
    const t = window.setInterval(() => { void reloadLog() }, 4000)
    return () => window.clearInterval(t)
  }, [tab, anyPending, reloadLog])

  const subs = data?.subscriptions ?? []
  const catalogue = data?.events ?? []
  const put = (s: Subscription) => setData(d => d && { ...d, subscriptions: sortSubscriptions(d.subscriptions.some(x => x.id === s.id) ? d.subscriptions.map(x => (x.id === s.id ? { ...x, ...s, hasSecret: true } : x)) : [...d.subscriptions, s]) })
  const sel = panel.kind === 'row' || panel.kind === 'edit' ? subs.find(r => r.id === panel.id) ?? null : null
  /** Every way out of a panel drops the secret: closing, opening Add, selecting another row. */
  const open = (p: Panelled) => {
    setProblem(null); setConflict(null)
    if (!((p.kind === 'row' || p.kind === 'edit') && (panel.kind === 'row' || panel.kind === 'edit') && p.id === panel.id)) setShown(null)
    setPanel(p)
  }
  const close = () => { setShown(null); setProblem(null); setConflict(null); setPanel({ kind: 'none' }) }
  const fail = (e: unknown) => { if (isConflict(e)) { setConflict(e); setProblem(null); void reload() } else { setConflict(null); setProblem(errMsg(e)) } }
  const refreshAll = () => { void reload(); void reloadLog() }

  const add = (name: string, url: string, events: string[]) => run('Adding…', async () => {
    setProblem(null)
    try {
      const r = await createSubscription({ name: name.trim(), url: url.trim(), eventTypes: events })
      put(r.subscription); setShown({ id: r.subscription.id, secret: r.secret, how: 'made' }); setPanel({ kind: 'row', id: r.subscription.id })
      announce(`Webhook ${r.subscription.name} added. Copy the secret now: it is shown once.`)
    } catch (e) { fail(e) }
  })
  const save = (s: Subscription, name: string, url: string, events: string[]) => run('Saving…', async () => {
    setProblem(null); setConflict(null)
    try { const r = await updateSubscription(s.id, { name: name.trim(), url: url.trim() || undefined, eventTypes: events }, s.version); put(r); setPanel({ kind: 'row', id: s.id }); announce(`Webhook ${r.name} saved`); void reload() }
    catch (e) { fail(e) }
  })
  const toggle = (s: Subscription) => run(s.status === 'Active' ? 'Pausing…' : 'Resuming…', async () => {
    setProblem(null); setConflict(null)
    try { const r = await (s.status === 'Active' ? pauseSubscription : resumeSubscription)(s.id, s.version); put(r); announce(`Webhook ${r.name} ${r.status === 'Active' ? 'resumed' : 'paused'}`); void reloadLog() }
    catch (e) { fail(e) }
  })
  const test = (s: Subscription) => run('Sending…', async () => {
    setProblem(null); setConflict(null)
    try { await sendTest(s.id); announce(`Test event queued for ${s.name}. See the delivery log.`); await reloadLog(); void reload(); setTab('deliveries') }
    catch (e) { fail(e) }
  })
  const rotate = (s: Subscription) => run('Rotating…', async () => {
    setProblem(null); setConflict(null)
    try { const r = await rotateSecret(s.id, s.version); put(r.subscription); setShown({ id: r.subscription.id, secret: r.secret, how: 'rotated' }); setConfirm(null); announce(`Secret of ${s.name} rotated. Copy the new secret now: it is shown once.`) }
    catch (e) { setConfirm(null); setShown(null); fail(e) }
  })
  const remove = (s: Subscription) => run('Deleting…', async () => {
    setProblem(null); setConflict(null)
    try { await deleteSubscription(s.id, s.version); setData(d => d && { ...d, subscriptions: d.subscriptions.filter(x => x.id !== s.id) }); setConfirm(null); close(); announce(`Webhook ${s.name} deleted`); void reloadLog() }
    catch (e) { setConfirm(null); fail(e) }
  })

  const columns: Column<Subscription>[] = [
    { key: 'name', header: 'Name', cell: s => <TwoLine primary={s.name} secondary={<span className="mg-mono">{s.host}</span>} /> },
    { key: 'events', header: 'Events', cell: s => eventsText(s, catalogue) },
    { key: 'status', header: 'State', cell: s => <StatusChip tone={statusTone(s.status)}>{s.status}</StatusChip> },
    { key: 'queue', header: 'Queue', cell: s => queueText(s) },
    { key: 'secret', header: 'Secret', cell: s => `Version ${s.secretVersion}` },
  ]
  const logColumns: Column<Delivery>[] = [
    { key: 'time', header: 'Queued', cell: d => <Time at={d.createdAt} date /> },
    { key: 'event', header: 'Event', cell: d => <TwoLine primary={d.eventType} secondary={<span className="mg-mono">{d.eventId}</span>} /> },
    { key: 'sub', header: 'Webhook', cell: d => d.subscriptionName },
    { key: 'status', header: 'Status', cell: d => <StatusChip tone={deliveryTone(d.status)}>{deliveryWord(d)}</StatusChip> },
    { key: 'attempts', header: 'Attempts', cell: d => attemptsText(d.attemptCount) },
    { key: 'result', header: 'Last result', cell: d => <TwoLine primary={resultText(d)} secondary={d.replayOfDeliveryId ? 'A replay' : d.status === 'Pending' && d.nextAttemptAt ? <>Next try <Time at={d.nextAttemptAt} /></> : undefined} /> },
  ]

  if (denied) return <div className="os-page"><PageHeading trail={['Management', 'Webhooks']} title="Webhooks" /><NoPermissionState what="manage webhooks" who={WHO} /></div>

  const deadLetters = subs.reduce((n, s) => n + s.deadLetters, 0)
  const allow = data ? allowListNotice(data.allowList) : null
  const canAdd = data?.allowList.mode !== 'closed'
  const addAction = canAdd
    ? <Button ref={addButton} variant="primary" icon={Plus} onClick={() => { setTab('subscriptions'); open({ kind: 'add' }) }}>Add webhook</Button>
    : <DisabledAction variant="primary" reason="No receiver host is allowed yet. Ask an administrator to set Webhooks:Out:AllowedHosts.">Add webhook</DisabledAction>

  let subsBody
  if (!data) subsBody = null
  else if (subs.length === 0) {
    subsBody = <Panel title="Webhooks"><EmptyState title="No webhooks yet" action={canAdd ? <Button variant="primary" icon={Plus} onClick={() => open({ kind: 'add' })}>Add webhook</Button> : undefined}>Add one to have ReleaseOS post signed events to your systems.</EmptyState></Panel>
  } else {
    subsBody = (
      <Panel title="Webhooks" subtitle="Each one posts the events it asked for, signed with its own secret" meta={`${subs.filter(r => r.status === 'Active').length} active`}>
        <div className="mg-tablewrap">
          <DataTable caption="Webhooks" columns={columns} rows={subs} rowKey={s => s.id} selectedKey={panel.kind === 'row' || panel.kind === 'edit' ? panel.id : null} onSelect={s => open({ kind: 'row', id: s.id })}
            footer={footerText(subs)} empty="No webhooks." />
        </div>
      </Panel>
    )
  }

  return (
    <div className="os-page">
      <PageHeading trail={['Management', 'Webhooks']} title="Webhooks"
        description="ReleaseOS posts signed events (release, gate, approval, deployment and more) to the addresses you add. Each delivery is retried, logged and can be replayed."
        actions={addAction} />
      {error && <ErrorState what="The webhooks" message={error} onRetry={() => void reload()} />}
      {!data && !error && <LoadingState what="the webhooks" />}
      {allow && <Notice tone={allow.tone} title={allow.title}>{allow.text}</Notice>}
      {deadLetters > 0 && <Notice tone="danger" title={`${deadLetters} ${deadLetters === 1 ? 'delivery was' : 'deliveries were'} dead-lettered`} action={<Button onClick={() => { setFilterStatus('DeadLetter'); setTab('deliveries') }}>Show them</Button>}>A receiver stayed unreachable until the attempts ran out. Fix the receiver, then replay them from the delivery log.</Notice>}
      {data && (
        <>
          <Tabs label="Webhooks sections" group="webhooks" value={tab} onChange={t => setTab(t as typeof tab)}
            tabs={[{ id: 'subscriptions', label: 'Webhooks', count: subs.length }, { id: 'deliveries', label: 'Delivery log', count: deadLetters || undefined }]} />
          <TabPanel id="subscriptions" group="webhooks">
            {tab === 'subscriptions' && subsBody && (
              <div className="mg-split">
                {subsBody}
                <div className="mg-side">
                  {panel.kind === 'add' && <FormPanel key="add" mode="add" catalogue={catalogue} busy={pending !== null} problem={problem} onSubmit={add} onClose={() => { close(); addButton.current?.focus() }} />}
                  {panel.kind === 'edit' && sel && <FormPanel key={`edit-${sel.id}-${sel.version}`} mode="edit" row={sel} catalogue={catalogue} busy={pending !== null} problem={problem} conflict={conflict} onUseTheirs={() => { setConflict(null); void reload() }}
                    onSubmit={(n, u, ev) => save(sel, n, u, ev)} onClose={() => open({ kind: 'row', id: sel.id })} />}
                  {panel.kind === 'row' && sel && (
                    <DetailPanel key={sel.id} row={sel} catalogue={catalogue} shown={shown?.id === sel.id ? shown : null} busy={pending !== null} problem={problem} conflict={conflict}
                      onUseTheirs={() => { setConflict(null); void reload() }} onEdit={() => open({ kind: 'edit', id: sel.id })} onToggle={() => void toggle(sel)} onTest={() => void test(sel)}
                      onRotate={() => setConfirm({ kind: 'rotate', row: sel })} onDelete={() => setConfirm({ kind: 'delete', row: sel })} onClose={close} />
                  )}
                  {(panel.kind === 'none' || ((panel.kind === 'row' || panel.kind === 'edit') && !sel)) && subs.length > 0 && (
                    <Panel title="Webhook detail"><EmptyState title="Select a webhook">Pick a row to see its events and queue, send a test event, pause it, rotate its secret or delete it.</EmptyState></Panel>
                  )}
                  {panel.kind === 'none' && <VerifyHelp replayWindow={data.limits.replayWindowSeconds} />}
                </div>
              </div>
            )}
          </TabPanel>
          <TabPanel id="deliveries" group="webhooks">
            {tab === 'deliveries' && (
              <div className="mg-split">
                <Panel title="Delivery log" subtitle="Every event queued for a webhook, with each try" meta={<Button icon={RefreshCw} onClick={refreshAll}>Refresh</Button>}>
                  <div className="wh-filters">
                    <FilterSelect label="Webhook" value={filterSub} options={[{ value: '', label: 'All webhooks' }, ...subs.map(s => ({ value: s.id, label: s.name }))]} onChange={setFilterSub} />
                    <FilterSelect label="Status" value={filterStatus} options={[{ value: '', label: 'Any status' }, { value: 'Pending', label: 'Pending' }, { value: 'Delivered', label: 'Delivered' }, { value: 'DeadLetter', label: 'Dead letter' }]} onChange={setFilterStatus} />
                  </div>
                  {logError && <ErrorState what="The delivery log" message={logError} onRetry={() => void reloadLog()} />}
                  {!log ? (logError ? null : <LoadingState what="the delivery log" />)
                    : log.length === 0 ? <EmptyState title="No deliveries">{filterSub || filterStatus ? 'Nothing matches these filters.' : 'Events appear here once a webhook is added and something happens, or when you send a test event.'}</EmptyState>
                      : <div className="mg-tablewrap"><DataTable caption="Deliveries" columns={logColumns} rows={log} rowKey={d => d.id} selectedKey={openDelivery} onSelect={d => setOpenDelivery(d.id)} footer={deliveryFooter(log, SHOWN_LOG)} empty="No deliveries." /></div>}
                </Panel>
                <div className="mg-side">
                  {openDelivery
                    ? <DeliveryPanel key={openDelivery} id={openDelivery} subs={subs} busy={pending !== null} onClose={() => setOpenDelivery(null)}
                      onReplay={d => run('Replaying…', async () => { try { const r = await replayDelivery(d.id); announce('Replay queued'); setOpenDelivery(r.id); await reloadLog(); void reload() } catch (e) { announce(errMsg(e)); throw e } }).catch(() => undefined)} />
                    : <Panel title="Delivery detail"><EmptyState title="Select a delivery">Pick a row to see every try and the exact body that was signed, and to replay it.</EmptyState></Panel>}
                </div>
              </div>
            )}
          </TabPanel>
        </>
      )}
      <ConfirmDialog open={confirm?.kind === 'delete'} title={`Delete ${confirm?.row.name ?? ''}?`} confirmLabel="Delete webhook" pendingLabel="Deleting…" destructive pending={pending !== null}
        onCancel={() => setConfirm(null)} onConfirm={() => void remove(confirm!.row)}>
        {confirm ? deleteImpact(confirm.row) : ''}
      </ConfirmDialog>
      <ConfirmDialog open={confirm?.kind === 'rotate'} title={`Rotate the secret of ${confirm?.row.name ?? ''}?`} confirmLabel="Rotate secret" pendingLabel="Rotating…" destructive pending={pending !== null}
        onCancel={() => setConfirm(null)} onConfirm={() => void rotate(confirm!.row)}>
        {confirm ? rotateImpact(confirm.row.name) : ''}
      </ConfirmDialog>
    </div>
  )
}

/** How a receiver checks a delivery, in two lines; the full samples are in docs/WEBHOOKS.md. */
function VerifyHelp({ replayWindow }: { replayWindow: number }) {
  return (
    <Panel title="Verifying a delivery" subtitle="What your receiver checks">
      <div className="mg-stack">
        <p className="wh-help">Each request carries <code>X-ReleaseOS-Delivery</code>, <code>X-ReleaseOS-Timestamp</code> and <code>X-ReleaseOS-Signature</code>. The signature is <code>v1=</code> and the hex HMAC-SHA256 of <code>timestamp.delivery.body</code> with the secret.</p>
        <p className="wh-help">Refuse a timestamp more than {replayWindow / 60} minutes from your clock, and drop a delivery id you have already handled: delivery is at least once. C#, JavaScript and Python samples are in docs/WEBHOOKS.md.</p>
      </div>
    </Panel>
  )
}

function FormPanel({ mode, row, catalogue, busy, problem, conflict, onUseTheirs, onSubmit, onClose }: {
  mode: 'add' | 'edit'; row?: Subscription; catalogue: { key: string; name: string; description: string }[]; busy: boolean; problem: string | null; conflict?: ApiError | null
  onUseTheirs?: () => void; onSubmit: (name: string, url: string, events: string[]) => void; onClose: () => void
}) {
  const [name, setName] = useState(row?.name ?? '')
  const [url, setUrl] = useState('')
  const [events, setEvents] = useState<string[]>(row?.eventTypes ?? [])
  const reason = formProblem(name, url, events, mode === 'add')
  const options = useMemo(() => [{ key: ALL_EVENTS, name: 'All events', description: '' }, ...catalogue], [catalogue])
  const label = mode === 'add' ? 'Add webhook' : 'Edit webhook'
  return (
    <SidePanel title={label} subtitle={mode === 'add' ? 'A name, where to post, and which events' : 'Change the name, the events or the address'} closeLabel={`Close the ${label.toLowerCase()} form`} onClose={onClose}>
      <form className="mg-form" aria-label={label} onSubmit={e => { e.preventDefault(); if (!reason && !busy) onSubmit(name, url, normaliseChoice(events)) }}>
        {conflict && <ConflictNotice onUseTheirs={onUseTheirs ?? (() => undefined)} />}
        <Field label="Name" hint="Say what receives it, for example Release dashboard."><TextInput value={name} onChange={e => setName(e.target.value)} maxLength={100} autoComplete="off" /></Field>
        <Field label="Address" hint={mode === 'edit' ? `Posts to ${row?.host}. Leave this empty to keep the address; the address itself is never shown again.` : 'An https address on an allowed host. It is stored encrypted and only its host is shown afterwards.'}>
          <TextInput value={url} onChange={e => setUrl(e.target.value)} inputMode="url" autoComplete="off" spellCheck={false} placeholder="https://hooks.example.com/releaseos" maxLength={2048} />
        </Field>
        <Field label="Events" hint="Hold Ctrl (⌘ on a Mac) to choose several.">
          <select className="mg-input wh-events" multiple size={Math.min(options.length, 8)} value={events} onChange={e => setEvents(Array.from(e.target.selectedOptions, o => o.value))}>
            {options.map(o => <option key={o.key} value={o.key} title={o.description || undefined}>{o.name}</option>)}
          </select>
        </Field>
        <Problem message={problem} />
        <div className="mg-actions">
          {reason ? <DisabledAction variant="primary" reason={reason}>{mode === 'add' ? 'Add webhook' : 'Save'}</DisabledAction>
            : <Button variant="primary" type="submit" disabled={busy}>{busy ? (mode === 'add' ? 'Adding…' : 'Saving…') : mode === 'add' ? 'Add webhook' : 'Save'}</Button>}
          {mode === 'edit' && <Button type="button" onClick={onClose}>Cancel</Button>}
        </div>
      </form>
    </SidePanel>
  )
}

function DetailPanel({ row, catalogue, shown, busy, problem, conflict, onUseTheirs, onEdit, onToggle, onTest, onRotate, onDelete, onClose }: {
  row: Subscription; catalogue: { key: string; name: string; description: string }[]; shown: Shown | null; busy: boolean; problem: string | null; conflict: ApiError | null
  onUseTheirs: () => void; onEdit: () => void; onToggle: () => void; onTest: () => void; onRotate: () => void; onDelete: () => void; onClose: () => void
}) {
  const active = row.status === 'Active'
  return (
    <SidePanel title={row.name} subtitle={<StatusChip tone={statusTone(row.status)}>{row.status}</StatusChip>} closeLabel={`Close ${row.name}`} onClose={onClose}>
      <div className="mg-stack" role="group" aria-label="Webhook detail">
        {shown && <SecretNotice shown={shown} />}
        {problem && <Problem message={problem} />}
        {conflict && <ConflictNotice onUseTheirs={onUseTheirs} />}
        <PropertyList items={[
          { label: 'Id', value: row.id, mono: true },
          { label: 'Posts to', value: row.host, mono: true },
          { label: 'Events', value: eventsText(row, catalogue) },
          { label: 'Queue', value: queueText(row) },
          { label: 'Secret', value: `Version ${row.secretVersion}${row.secretRotatedAt ? ', rotated' : ''}` },
          ...(row.secretRotatedAt ? [{ label: 'Rotated', value: <Time at={row.secretRotatedAt} date /> }] : []),
          { label: 'Added', value: <Time at={row.createdAt} date /> },
          { label: 'Added by', value: row.createdBy ?? 'Unknown' },
          ...(row.lastChangedAt ? [{ label: 'Changed', value: <Time at={row.lastChangedAt} date /> }] : []),
          { label: 'Version', value: String(row.version), mono: true },
        ]} />
        <div className="mg-actions">
          {active ? <Button disabled={busy} onClick={onTest}>Send test event</Button> : <DisabledAction reason="Resume the webhook first.">Send test event</DisabledAction>}
          <Button disabled={busy} onClick={onToggle}>{active ? 'Pause' : 'Resume'}</Button>
          <Button disabled={busy} onClick={onEdit}>Edit</Button>
          <Button disabled={busy} onClick={onRotate}>Rotate secret</Button>
          <Button variant="destructive" disabled={busy} onClick={onDelete}>Delete</Button>
        </div>
      </div>
    </SidePanel>
  )
}

/** One delivery: its tries and the exact body that was signed; Replay sends it again as a new delivery. */
function DeliveryPanel({ id, subs, busy, onReplay, onClose }: { id: string; subs: Subscription[]; busy: boolean; onReplay: (d: Delivery) => void; onClose: () => void }) {
  const [detail, setDetail] = useState<DeliveryDetail | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => { let live = true; getDelivery(id).then(d => { if (live) setDetail(d) }).catch(e => { if (live) setError(errMsg(e, 'Could not load the delivery.')) }); return () => { live = false } }, [id])
  if (error) return <Panel title="Delivery detail"><ErrorState what="The delivery" message={error} /></Panel>
  if (!detail) return <Panel title="Delivery detail"><LoadingState what="the delivery" /></Panel>
  const d = detail.delivery
  const why = replayReason(d, subs.find(s => s.id === d.subscriptionId))
  return (
    <SidePanel title={d.eventType} subtitle={<StatusChip tone={deliveryTone(d.status)}>{deliveryWord(d)}</StatusChip>} closeLabel="Close the delivery" onClose={onClose}>
      <div className="mg-stack" role="group" aria-label="Delivery detail">
        <PropertyList items={[
          { label: 'Delivery id', value: d.id, mono: true },
          { label: 'Event id', value: d.eventId, mono: true },
          { label: 'Webhook', value: d.subscriptionName },
          { label: 'Queued', value: <Time at={d.createdAt} date /> },
          ...(d.finishedAt ? [{ label: d.status === 'Delivered' ? 'Delivered' : 'Gave up', value: <Time at={d.finishedAt} date /> }] : d.nextAttemptAt ? [{ label: 'Next try', value: <Time at={d.nextAttemptAt} date /> }] : []),
          ...(d.replayOfDeliveryId ? [{ label: 'Replay of', value: d.replayOfDeliveryId, mono: true }] : []),
        ]} />
        <h3 className="mg-subhead">Attempts</h3>
        {detail.attempts.length === 0 ? <p className="mg-note">Not tried yet.</p> : (
          <ol className="mg-list" aria-label="Attempts">
            {detail.attempts.map(a => <li key={a.attemptNo}><TwoLine primary={`Attempt ${a.attemptNo}: ${attemptText(a)}`} secondary={<><Time at={a.attemptedAt} date /> · {a.durationMs} ms</>} /></li>)}
          </ol>
        )}
        <h3 className="mg-subhead">Body that was signed</h3>
        <pre className="wh-payload" tabIndex={0} aria-label="Body that was signed">{prettyPayload(detail.payloadJson)}</pre>
        <div className="mg-actions">
          {why ? <DisabledAction reason={why}>Replay</DisabledAction> : <Button variant="primary" disabled={busy} onClick={() => onReplay(d)}>Replay</Button>}
        </div>
      </div>
    </SidePanel>
  )
}

/** The one-time secret: a live Notice, the secret in a selectable mono element, and Copy. Nothing here logs, stores or sends the secret anywhere. */
function SecretNotice({ shown }: { shown: Shown }) {
  const [copied, setCopied] = useState<'yes' | 'no' | null>(null)
  const copy = async () => {
    try {
      if (!navigator.clipboard?.writeText) throw new Error('no clipboard')
      await navigator.clipboard.writeText(shown.secret)
      setCopied('yes'); announce('Copied')
    } catch { setCopied('no'); announce(COPY_FALLBACK) }
  }
  return (
    <Notice tone="success" live title="Copy this secret now">
      {shown.how === 'rotated' ? 'The secret was rotated and the old one has stopped verifying. ' : ''}It is shown once and cannot be recovered. If you lose it, rotate the secret.
      <p className="tk-secret" data-testid="webhook-secret">{shown.secret}</p>
      <div className="tk-secret-actions">
        <Button onClick={() => void copy()}>Copy secret</Button>
        {copied === 'yes' && <span className="tk-copied">Copied</span>}
        {copied === 'no' && <span className="tk-copied">{COPY_FALLBACK}</span>}
      </div>
    </Notice>
  )
}
