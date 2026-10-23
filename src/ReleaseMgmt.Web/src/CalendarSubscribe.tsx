import { useCallback, useEffect, useRef, useState } from 'react'
import { Copy, KeyRound, Link2 } from 'lucide-react'
import { ApiError, get, getStream, post, type StreamRow } from './api'
import { announce } from './announce'
import { stamp } from './calendarModel'
import { browserZone } from './shell/clock'
import { Button, ConfirmDialog, DataTable, EmptyState, ErrorState, LoadingState, Notice, Panel, StatusChip, TwoLine, type Column } from './ui'
import { useAction } from './useAction'

/**
 * The Subscribe panel (REOS-51, REOS-134): the ICS feed link is created, copied, rotated and revoked here. The secret is shown once, straight after it is issued, and is
 * not recoverable (only its hash is stored). Rotate and Revoke are irreversible, so each asks in a confirmation dialog (D41, D53). Times are UTC with the person's own beside them.
 */
interface IcsTokenRow { id: string; createdAt: string; revokedAt: string | null; active: boolean; lastUsedAt: string | null }
interface IcsIssued { id: string; token: string; path: string; scope: string; createdAt: string }

const errText = (e: unknown) => (e instanceof ApiError ? (e.body?.message ?? e.message) : (e as Error).message)

function When({ t }: { t: string }) {
  const s = stamp(t, browserZone())
  return <TwoLine primary={s.utc} secondary={s.local ?? undefined} />
}

export function CalendarSubscribe({ current }: { current?: string | null }) {
  const [rows, setRows] = useState<IcsTokenRow[] | null>(null)
  const [issued, setIssued] = useState<IcsIssued | null>(null)
  const [confirm, setConfirm] = useState<{ id: string; action: 'rotate' | 'revoke' } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const { run: guard, pending } = useAction()
  const busy = pending !== null
  const [copied, setCopied] = useState<string | null>(null)
  const region = useRef<HTMLDivElement>(null)
  const firstLink = useRef<HTMLInputElement>(null)
  const createRef = useRef<HTMLButtonElement>(null)
  const [trains, setTrains] = useState<StreamRow[]>([])
  const [trainId, setTrainId] = useState('')
  const load = useCallback(() => get<IcsTokenRow[]>('/api/v1/me/ics-tokens').then(r => { setRows(r); setError(null) }).catch(e => setError(errText(e))), [])
  useEffect(() => { load() }, [load])
  // One-train subscriptions default to the train last opened, else the first.
  useEffect(() => { getStream().then(t => { setTrains(t); setTrainId(id => id || (current && t.some(x => x.id === current) ? current : t[0]?.id) || '') }).catch(() => setTrains([])) }, [])   // eslint-disable-line react-hooks/exhaustive-deps

  const active = rows?.find(r => r.active) ?? null
  // One request at a time: a double click on Create or Confirm is ignored. After each, focus goes where the next step is, never to <body>.
  const run = (label: string, fn: () => Promise<void>, then: () => void) => guard(label, async () => {
    setActionError(null)
    try { await fn(); await load(); requestAnimationFrame(then) } catch (e) { setActionError(errText(e)); setConfirm(null) }
  })
  const ready = () => { announce('Link ready. Copy it now: it is shown only once.'); firstLink.current?.focus() }
  const create = () => run('Creating…', async () => { setIssued(await post<IcsIssued>('/api/v1/me/ics-tokens', { scope: 'all' })); setCopied(null) }, ready)
  const rotate = (id: string) => run('Rotating…', async () => { setIssued(await post<IcsIssued>(`/api/v1/me/ics-tokens/${id}:rotate`, { scope: 'all' })); setConfirm(null); setCopied(null) }, ready)
  const revoke = (id: string) => run('Revoking…', async () => { await post(`/api/v1/me/ics-tokens/${id}:revoke`, {}); setIssued(null); setConfirm(null) },
    () => { announce('Link revoked.'); createRef.current?.focus() })
  const hide = () => { setIssued(null); requestAnimationFrame(() => region.current?.focus()) }

  const origin = window.location.origin
  const urls = (t: string) => [
    { key: 'all', label: 'All trains', url: `${origin}/api/v1/ics/${t}.ics` },
    { key: 'mine', label: 'My work (my gates, steps and milestones)', url: `${origin}/api/v1/ics/${t}/mine.ics` },
    { key: 'freezes', label: 'Freeze and chill windows', url: `${origin}/api/v1/ics/${t}/freezes.ics` },
    ...(trainId ? [{ key: 'train', label: 'One train', url: `${origin}/api/v1/ics/${t}/trains/${encodeURIComponent(trainId)}.ics` }] : []),
  ]
  const copy = async (key: string, url: string) => {
    try { await navigator.clipboard.writeText(url); setCopied(key); announce('Link copied.') }
    catch { setCopied(null); setActionError('The browser did not allow copying. Select the address and copy it by hand.') }
  }

  type Feed = ReturnType<typeof urls>[number]
  const feedCols: Column<Feed>[] = [
    { key: 'feed', header: 'Feed', cell: u => u.key === 'train'
      ? <label>One train <select className="os-cal-select" value={trainId} onChange={e => setTrainId(e.target.value)} aria-label="Train for the one-train feed">{trains.map(t => <option key={t.id} value={t.id}>{t.title}</option>)}</select></label>
      : u.label },
    { key: 'link', header: 'Link', cell: u => <input ref={u.key === 'all' ? firstLink : undefined} className="os-cal-link" readOnly value={u.url} aria-label={`${u.label} link`} onFocus={e => e.currentTarget.select()} /> },
    { key: 'act', header: 'Action', cell: u => <Button icon={Copy} onClick={() => copy(u.key, u.url)}>{copied === u.key ? '✓ Copied' : 'Copy'}</Button> },
  ]
  const rowCols: Column<IcsTokenRow>[] = [
    { key: 'created', header: 'Created', cell: r => <When t={r.createdAt} /> },
    { key: 'used', header: 'Last used', cell: r => r.lastUsedAt ? <When t={r.lastUsedAt} /> : <span className="os-muted">not since the server started</span> },
    { key: 'state', header: 'State', cell: r => r.active ? <StatusChip tone="success">Active</StatusChip>
      : <TwoLine primary={<StatusChip tone="neutral">Revoked</StatusChip>} secondary={r.revokedAt ? stamp(r.revokedAt, browserZone()).utc : undefined} /> },
    { key: 'actions', header: 'Actions', cell: r => r.active && (
      <span className="os-state-buttons">
        <Button icon={KeyRound} disabled={busy} onClick={() => setConfirm({ id: r.id, action: 'rotate' })}>Rotate</Button>
        <Button variant="destructive" disabled={busy} onClick={() => setConfirm({ id: r.id, action: 'revoke' })}>Revoke</Button>
      </span>) },
  ]

  return (
    <div ref={region} id="subscribe" tabIndex={-1} className="os-cal-subscribe">
      <Panel title="Subscribe" subtitle="Add the calendar to Outlook, Apple Calendar or Google Calendar with a link. Anyone who has the link can see gate, milestone, window and freeze titles and times, so keep it private. You have one link; it works until you rotate or revoke it.">
        <div className="os-cal-stack">
          {error && <ErrorState what="Your calendar links" message={error} onRetry={() => void load()} />}
          {actionError && <Notice tone="danger" title="That did not work" live>{actionError}</Notice>}
          {rows === null && !error && <LoadingState what="your calendar links" />}

          {issued && (
            <div role="region" aria-label="Your new calendar link" className="os-cal-stack">
              <Notice tone="success" title="Link ready" live>Copy it now: it is shown only once and cannot be shown again. If you lose it, rotate to get a new one.</Notice>
              <DataTable caption="Calendar feed links" columns={feedCols} rows={urls(issued.token)} rowKey={u => u.key} />
              <div><Button onClick={hide}>I have copied it, hide the link</Button></div>
            </div>
          )}

          {rows && !active && (
            <EmptyState title="You have no calendar link." action={<Button ref={createRef} variant="primary" icon={Link2} disabled={busy} onClick={() => void create()}>{pending === 'Creating…' ? pending : 'Create link'}</Button>}>
              A link adds every release window, gate, milestone and freeze to your calendar app.
            </EmptyState>
          )}

          {rows && rows.length > 0 && <DataTable caption="Calendar links" columns={rowCols} rows={rows} rowKey={r => r.id} />}
        </div>
      </Panel>
      <ConfirmDialog open={confirm !== null} destructive={confirm?.action === 'revoke'} pending={busy}
        title={confirm?.action === 'rotate' ? 'Rotate your calendar link?' : 'Revoke your calendar link?'}
        confirmLabel={confirm?.action === 'rotate' ? 'Confirm rotate' : 'Confirm revoke'} pendingLabel={pending ?? undefined}
        onConfirm={() => { if (confirm) void (confirm.action === 'rotate' ? rotate(confirm.id) : revoke(confirm.id)) }}
        onCancel={() => setConfirm(null)}>
        {confirm?.action === 'rotate'
          ? 'The current link stops working at once. Calendars using it will stop updating until you paste the new link.'
          : 'The link stops working at once and you will have no calendar link until you create one.'}
      </ConfirmDialog>
    </div>
  )
}
