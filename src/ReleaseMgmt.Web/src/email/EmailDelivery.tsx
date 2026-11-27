import { useCallback, useEffect, useState } from 'react'
import { Send } from 'lucide-react'
import { ApiError } from '../api.ts'
import { announce } from '../announce.tsx'
import { errMsg } from '../format.ts'
import { isConflict } from '../Conflict.tsx'
import { useAction } from '../useAction.ts'
import { Button, ConfirmDialog, DataTable, DisabledAction, EmptyState, ErrorState, FilterSelect, LoadingState, MetricCard, NoPermissionState, Notice, Panel, PropertyList, StatusChip, TwoLine, type Column } from '../ui'
import { Field, Problem, TextInput, When } from '../mgmt/forms.tsx'
import '../mgmt/mgmt.css'
import '../tokens/tokens.css'
import './email.css'
import { addSuppression, clearCredential, getStatus, listMessages, listSuppressions, removeSuppression, rotateEventsSecret, sendTest, setCredential } from './emailApi.ts'
import {
  EVENTS_HELP, OFF_ADMIN, REASON_WORDS, STATUS_FILTER, credentialWord, deleteImpact, messageTone, messageWord, providerText, removeCredentialImpact, retryText, rotateImpact,
  type EmailMessage, type EmailStatus, type EventsSecretMade, type Suppression,
} from './emailLogic.ts'

/**
 * Email delivery (REOS-167, D61), a tab of Integrations. RTE and Release Manager only (the API's Admin policy). It shows whether email is on and how it is configured, the counts, a test message to the
 * signed-in person's own address, the provider credential (write-only: the screen never shows or reads it back), the secret that signs bounce events from the provider (shown once, with Copy), the send log and
 * the suppression list. Removing a credential, rotating the events secret and lifting a suppression ask first. Every state is words beside the dot; no checkbox or switch.
 */
const WHO = 'Release Train Engineers and Release Managers'

export function EmailDelivery() {
  const [status, setStatus] = useState<EmailStatus | null>(null)
  const [log, setLog] = useState<EmailMessage[] | null>(null)
  const [sups, setSups] = useState<Suppression[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [denied, setDenied] = useState(false)
  const [filter, setFilter] = useState('')
  const [problem, setProblem] = useState<string | null>(null)
  const [secret, setSecret] = useState('')
  const [shown, setShown] = useState<EventsSecretMade | null>(null)
  const [copied, setCopied] = useState<'yes' | 'no' | null>(null)
  const [address, setAddress] = useState('')
  const [note, setNote] = useState('')
  const [confirm, setConfirm] = useState<{ kind: 'credential' } | { kind: 'rotate' } | { kind: 'lift'; row: Suppression } | null>(null)
  const { run, pending } = useAction()

  const load = useCallback(() => Promise.all([getStatus(), listMessages(filter || undefined), listSuppressions()]).then(([s, m, u]) => { setStatus(s); setLog(m.messages); setSups(u.suppressions); setError(null) })
    .catch(e => { if (e instanceof ApiError && e.status === 403) setDenied(true); else setError(errMsg(e, 'Could not load email delivery.')) }), [filter])
  useEffect(() => { void load() }, [load])

  const fail = (e: unknown) => { setProblem(isConflict(e) ? 'Someone else changed this. It has been reloaded; try again.' : errMsg(e)); if (isConflict(e)) void load() }
  const test = () => run('Sending…', async () => {
    setProblem(null)
    try { const m = await sendTest(); announce(`Test email queued to ${m.to}.`); await load() } catch (e) { fail(e) }
  })
  const saveCredential = () => run('Saving…', async () => {
    setProblem(null)
    try { await setCredential(secret); setSecret(''); announce('Credential stored.'); await load() } catch (e) { fail(e) }
  })
  const removeCredential = () => run('Removing…', async () => {
    setProblem(null)
    try { await clearCredential(); setConfirm(null); announce('Credential removed.'); await load() } catch (e) { setConfirm(null); fail(e) }
  })
  const rotate = () => run('Rotating…', async () => {
    setProblem(null); setCopied(null)
    try { setShown(await rotateEventsSecret()); setConfirm(null); announce('Events secret made. Copy it now: it is shown once.'); await load() } catch (e) { setConfirm(null); setShown(null); fail(e) }
  })
  const add = () => run('Adding…', async () => {
    setProblem(null)
    try { await addSuppression(address.trim(), note.trim()); setAddress(''); setNote(''); announce('Address suppressed.'); await load() } catch (e) { fail(e) }
  })
  const lift = (row: Suppression) => run('Removing…', async () => {
    setProblem(null)
    try { await removeSuppression(row.id, row.version); setConfirm(null); announce(`${row.address} removed from the suppression list.`); await load() } catch (e) { setConfirm(null); fail(e) }
  })
  const copy = async () => {
    try {
      if (!shown || !navigator.clipboard?.writeText) throw new Error('no clipboard')
      await navigator.clipboard.writeText(shown.secret); setCopied('yes'); announce('Copied')
    } catch { setCopied('no'); announce('Copying did not work here. Select the secret above and copy it yourself.') }
  }

  if (denied) return <NoPermissionState what="manage email delivery" who={WHO} />
  if (error) return <ErrorState what="Email delivery" message={error} onRetry={() => void load()} />
  if (!status || !log || !sups) return <LoadingState what="email delivery" />

  const cred = credentialWord(status)
  const busy = pending !== null
  const logColumns: Column<EmailMessage>[] = [
    { key: 'time', header: 'Queued', cell: m => <When iso={m.createdAt} /> },
    { key: 'to', header: 'To', cell: m => <TwoLine primary={m.to} secondary={m.categoryLabel} /> },
    { key: 'status', header: 'Status', cell: m => <StatusChip tone={messageTone(m.status)}>{messageWord(m)}</StatusChip> },
    { key: 'tries', header: 'Attempts', cell: m => retryText(m, status.maxAttempts) },
    { key: 'result', header: 'Last result', cell: m => <TwoLine primary={m.lastError ?? (m.status === 'Sent' ? 'Accepted by the provider' : 'None yet')} secondary={m.sentAt ? <>Sent <When iso={m.sentAt} /></> : m.status === 'Pending' && m.nextAttemptAt ? <>Next try <When iso={m.nextAttemptAt} /></> : undefined} /> },
  ]
  const supColumns: Column<Suppression>[] = [
    { key: 'address', header: 'Address', cell: s => <span className="mg-mono">{s.address}</span> },
    { key: 'reason', header: 'Why', cell: s => <TwoLine primary={REASON_WORDS[s.reason]} secondary={s.detail ?? undefined} /> },
    { key: 'when', header: 'Since', cell: s => <When iso={s.createdAt} /> },
    { key: 'by', header: 'By', cell: s => s.createdBy ?? 'The system' },
    { key: 'act', header: '', cell: s => <Button disabled={busy} onClick={() => setConfirm({ kind: 'lift', row: s })}>{`Remove ${s.address}`}</Button> },
  ]

  return (
    <div className="em-page">
      {!status.enabled && <Notice tone="warning" title="Email delivery is off">{OFF_ADMIN}</Notice>}
      {status.counts.deadLettersLast7Days > 0 && <Notice tone="danger" title={`${status.counts.deadLettersLast7Days} ${status.counts.deadLettersLast7Days === 1 ? 'message was' : 'messages were'} given up on in the last 7 days`}>Check the send log below and the provider's state. The undeliverable addresses are also raised on Sync health.</Notice>}
      {problem && <Problem message={problem} />}
      <div className="em-metrics">
        <MetricCard label="Waiting to send" value={status.counts.pending} />
        <MetricCard label="Sent in 7 days" value={status.counts.sentLast7Days} />
        <MetricCard label="Bounced in 7 days" value={status.counts.bouncedLast7Days} tone={status.counts.bouncedLast7Days ? 'danger' : undefined} />
        <MetricCard label="Given up in 7 days" value={status.counts.deadLettersLast7Days} tone={status.counts.deadLettersLast7Days ? 'danger' : undefined} />
        <MetricCard label="Suppressed addresses" value={status.counts.suppressed} />
      </div>
      <Panel title="Delivery" subtitle="How email is set up on this host (the settings live in configuration, not here)"
        meta={status.enabled ? <Button icon={Send} disabled={busy} onClick={() => void test()}>Send test email to me</Button> : <DisabledAction icon={Send} reason="Email delivery is off, so a test cannot be sent.">Send test email to me</DisabledAction>}>
        <PropertyList items={[
          { label: 'State', value: <StatusChip tone={status.enabled ? 'success' : 'neutral'}>{status.enabled ? 'On' : 'Off'}</StatusChip> },
          { label: 'Provider', value: providerText(status) },
          { label: 'Sender', value: `${status.fromName ?? ''} <${status.fromAddress || 'not set'}>` },
          { label: 'Credential', value: <StatusChip tone={cred.tone}>{cred.word}</StatusChip> },
          { label: 'Bounce events secret', value: <StatusChip tone={status.hasEventsSecret ? 'success' : 'neutral'}>{status.hasEventsSecret ? 'Set' : 'Not set'}</StatusChip> },
          { label: 'Links in mail', value: status.publicBaseUrlSet ? 'On (App:PublicBaseUrl is set)' : 'Off: App:PublicBaseUrl is not set, so mail has no link' },
          { label: 'Retries', value: `Up to ${status.maxAttempts} attempts, doubling the wait each time` },
          { label: 'Digest', value: `Once a day after ${String(status.digestHourUtc).padStart(2, '0')}:00 UTC` },
          { label: 'Kept', value: `Finished messages are removed after ${status.retentionDays} days` },
        ]} />
      </Panel>
      {status.needsCredential && (
        <Panel title="Provider credential" subtitle="The SMTP password or the API key. Stored encrypted on this host (Data Protection), never shown again">
          <div className="em-inline">
            <Field label="New credential" hint="Write-only. Saving replaces the stored one.">
              <TextInput type="password" autoComplete="off" value={secret} onChange={e => setSecret(e.target.value)} />
            </Field>
            <Button variant="primary" disabled={busy || secret.trim() === ''} onClick={() => void saveCredential()}>Save credential</Button>
            {status.hasCredential && <Button variant="destructive" disabled={busy} onClick={() => setConfirm({ kind: 'credential' })}>Remove credential</Button>}
          </div>
        </Panel>
      )}
      <Panel title="Bounce and complaint events" subtitle="Optional: lets your provider tell ReleaseOS about mail that bounced after it was accepted">
        {shown && (
          <Notice tone="success" live title="Copy this secret now">
            It is shown once and cannot be recovered. If you lose it, make a new one.
            <p className="tk-secret" data-testid="email-events-secret">{shown.secret}</p>
            <div className="tk-secret-actions">
              <Button onClick={() => void copy()}>Copy secret</Button>
              {copied === 'yes' && <span className="tk-copied">Copied</span>}
              {copied === 'no' && <span className="mg-problem" role="status">Copying did not work here. Select the secret above and copy it yourself.</span>}
            </div>
            <p className="mg-note">Send the signature in <span className="mg-mono">{shown.signatureHeader}</span>, the Unix time in <span className="mg-mono">{shown.timestampHeader}</span> and a unique id in <span className="mg-mono">{shown.idHeader}</span>; an event older than {shown.toleranceSeconds} seconds is refused.</p>
          </Notice>
        )}
        <p className="mg-note">{EVENTS_HELP}</p>
        <div className="mg-actions"><Button disabled={busy} onClick={() => (status.hasEventsSecret ? setConfirm({ kind: 'rotate' }) : void rotate())}>{status.hasEventsSecret ? 'Rotate events secret' : 'Make events secret'}</Button></div>
      </Panel>
      <Panel title="Send log" subtitle="Every message, with what happened to it" meta={<FilterSelect label="Message state" value={filter} options={STATUS_FILTER} onChange={setFilter} />}>
        {log.length === 0 ? <EmptyState title={status.enabled ? 'No messages yet' : 'Nothing was queued'}>{status.enabled ? 'Mail appears here when a notification is emailed.' : 'While email is off nothing is queued.'}</EmptyState> : (
          <div className="mg-tablewrap"><DataTable caption="Send log" columns={logColumns} rows={log} rowKey={m => m.id} empty="No messages." /></div>
        )}
      </Panel>
      <Panel title="Suppressed addresses" subtitle="Never mailed again from this workspace until you remove them">
        <div className="em-inline">
          <Field label="Address"><TextInput type="email" autoComplete="off" value={address} onChange={e => setAddress(e.target.value)} /></Field>
          <Field label="Note (optional)"><TextInput value={note} maxLength={500} onChange={e => setNote(e.target.value)} /></Field>
          <Button disabled={busy || address.trim() === ''} onClick={() => void add()}>Suppress address</Button>
        </div>
        {sups.length === 0 ? <EmptyState title="No suppressed addresses">An address is added when it bounces or someone complains, or by hand here.</EmptyState> : (
          <div className="mg-tablewrap"><DataTable caption="Suppressed addresses" columns={supColumns} rows={sups} rowKey={s => s.id} empty="None." /></div>
        )}
      </Panel>
      <ConfirmDialog open={confirm?.kind === 'credential'} title="Remove the stored credential?" confirmLabel="Remove credential" pendingLabel="Removing…" destructive pending={busy} onConfirm={() => void removeCredential()} onCancel={() => setConfirm(null)}>{removeCredentialImpact}</ConfirmDialog>
      <ConfirmDialog open={confirm?.kind === 'rotate'} title="Rotate the events secret?" confirmLabel="Rotate secret" pendingLabel="Rotating…" destructive pending={busy} onConfirm={() => void rotate()} onCancel={() => setConfirm(null)}>{rotateImpact}</ConfirmDialog>
      <ConfirmDialog open={confirm?.kind === 'lift'} title={`Remove ${confirm?.kind === 'lift' ? confirm.row.address : ''} from the suppression list?`} confirmLabel="Remove from list" pendingLabel="Removing…" destructive pending={busy}
        onConfirm={() => confirm?.kind === 'lift' && void lift(confirm.row)} onCancel={() => setConfirm(null)}>{confirm?.kind === 'lift' ? deleteImpact(confirm.row.address) : ''}</ConfirmDialog>
    </div>
  )
}
