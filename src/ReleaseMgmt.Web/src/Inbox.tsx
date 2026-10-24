import { useCallback, useEffect, useRef, useState, type MouseEvent } from 'react'
import { Check } from 'lucide-react'
import { getInbox, markAllNotificationsRead, markNotificationRead, type InboxItem, type Me } from './api'
import type { Route } from './route'
import { primaryOf, useRowNav } from './rowNav'
import { announce } from './announce'
import { useAction } from './useAction'
import { dueText } from './shell/approvalsApi'
import { Button, DataTable, EmptyState, ErrorState, LoadingState, Notice, PageHeading, Panel, StatusChip, TwoLine, type Column, type Tone } from './ui'
import './inbox.css'

/**
 * REOS-38, restyled with REOS-131 (G9): my notifications, newest first. Each row has a chip for its kind and one for its escalation level; unread rows are
 * semibold with the word "Unread"; opening a row marks it read and goes to its train. Times are UTC with my own time beside them (D44).
 */
const PAGE = 50
const LEVEL: Record<number, { tone: Tone; text: string }> = { 1: { tone: 'warning', text: 'Escalation level 1' }, 2: { tone: 'danger', text: 'Escalation level 2' } }

/** The words and tone of a notification kind (db/schema.sql Notifications.Kind). A kind not listed shows its own name in words, neutral. */
const KIND: Record<string, { tone: Tone; text: string }> = {
  GateEntered: { tone: 'info', text: 'Gate entered' }, GateReminder: { tone: 'warning', text: 'Gate reminder' }, GateOverdue: { tone: 'danger', text: 'Gate overdue' },
  NextGateDue: { tone: 'info', text: 'Next gate due' }, StepLate: { tone: 'warning', text: 'Step late' }, RollbackDeadline: { tone: 'danger', text: 'Rollback deadline' },
  SyncAlert: { tone: 'danger', text: 'Sync alert' }, ApprovalRequested: { tone: 'warning', text: 'Approval requested' }, ApprovalChangesRequested: { tone: 'warning', text: 'Changes requested' },
  ChangesRequested: { tone: 'warning', text: 'Changes requested' }, FreezeOverrideRequested: { tone: 'warning', text: 'Override requested' }, FreezeOverrideDeclined: { tone: 'neutral', text: 'Override declined' },
  ConditionExpired: { tone: 'danger', text: 'Condition expired' }, PirDue: { tone: 'warning', text: 'Review due' },
}
export const kindOf = (kind: string) => KIND[kind] ?? { tone: 'neutral' as Tone, text: kind.replace(/([a-z])([A-Z])/g, '$1 $2') || 'Notice' }

/** Where a notice points. A SyncAlert (or anything that resolves to no train) has nowhere to go: opening it only marks it read. */
export function targetOf(n: InboxItem): Partial<Route> | null {
  if (!n.trainId) return null
  switch (n.entityType) {
    case 'StageGate': return { trainId: n.trainId, selection: { kind: 'gate', id: n.entityId } }
    case 'StepExecution': case 'RunbookRun': return { trainId: n.trainId, mode: 'live' }
    default: return { trainId: n.trainId }
  }
}

export function Inbox({ onOpen, onChanged, refreshKey = 0 }: { me: Me; onOpen?: (r: Partial<Route>) => void; onChanged?: () => void; refreshKey?: number }) {
  const [unreadOnly, setUnreadOnly] = useState(false)
  const [items, setItems] = useState<InboxItem[] | null>(null)
  const [total, setTotal] = useState(0)
  const [unread, setUnread] = useState(0)
  const [error, setError] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [tick, setTick] = useState(0)
  const { run, pending } = useAction()
  const loaded = useRef(0); loaded.current = items?.length ?? 0

  // Reload keeps as many rows as are already on screen (up to the server's 100), so a live push does not collapse "Load more".
  const reload = useCallback(async () => {
    try {
      const p = await getInbox(unreadOnly, Math.min(100, Math.max(PAGE, loaded.current)), 0)
      setItems(p.items); setTotal(p.total); setUnread(p.unread); setError(null)
    } catch (e) { setError((e as Error).message) }
  }, [unreadOnly])
  useEffect(() => { void reload() }, [reload, refreshKey, tick])
  useEffect(() => { setItems(null) }, [unreadOnly])

  const loadMore = () => run('Loading…', async () => {
    if (!items) return
    try {
      const p = await getInbox(unreadOnly, PAGE, items.length)
      setItems([...items, ...p.items.filter(n => !items.some(x => x.id === n.id))]); setTotal(p.total); setUnread(p.unread)
    } catch (e) { setActionError((e as Error).message) }
  })

  const markRead = async (n: InboxItem) => {
    if (n.readAt) return
    try {
      const r = await markNotificationRead(n.id)
      setItems(cur => cur && (unreadOnly ? cur.filter(x => x.id !== n.id) : cur.map(x => (x.id === n.id ? { ...x, readAt: r.readAt, version: r.version } : x))))
      if (unreadOnly) setTotal(t => Math.max(0, t - 1))
      setUnread(u => Math.max(0, u - 1))
      onChanged?.()
    } catch (e) { setActionError((e as Error).message) }
  }
  /** The "Mark read" button goes away with the unread state: focus moves to the next unread row's button (else this row's, else the last row's), never to <body>. */
  const markReadButton = (n: InboxItem, i: number) => run('Marking read…', async () => {
    await markRead(n)
    requestAnimationFrame(() => {
      const trs = Array.from(nav.ref.current?.querySelectorAll('tbody tr') ?? [])
      const next = trs.slice(i).map(tr => tr.querySelector<HTMLElement>('[data-mark]')).find(Boolean)
      ;(next ?? primaryOf(trs[Math.min(i, trs.length - 1)]))?.focus()
    })
  })
  const markAll = () => run('Marking all read…', async () => {
    try { await markAllNotificationsRead(); await reload(); onChanged?.(); announce('All notifications marked read.') } catch (e) { setActionError((e as Error).message) }
  })
  const open = (n: InboxItem) => { void markRead(n); const t = targetOf(n); if (t) onOpen?.(t) }

  const rows = items ?? []
  const [sel, setSel, nav] = useRowNav<HTMLDivElement>(rows.length, i => { if (rows[i]) open(rows[i]) })

  const columns: Column<InboxItem>[] = [
    { key: 'state', header: 'State', cell: n => <StatusChip tone={n.readAt ? 'neutral' : 'info'}>{n.readAt ? 'Read' : 'Unread'}</StatusChip> },
    { key: 'when', header: 'When (UTC)', cell: n => { const d = dueText({ dueOn: null, dueAt: n.createdAt }); return <TwoLine primary={d.utc} secondary={d.local ?? undefined} /> } },
    { key: 'kind', header: 'Kind', cell: n => { const k = kindOf(n.kind); return <StatusChip tone={k.tone}>{k.text}</StatusChip> } },
    {
      key: 'message', header: 'Notification',
      cell: n => <button type="button" className="os-link os-link-button" style={{ fontWeight: 'inherit' }} data-rownav aria-current={rows[sel ?? -1] === n ? 'true' : undefined}>{n.message}</button>,
    },
    { key: 'train', header: 'Release', cell: n => n.trainTitle ?? <span className="os-muted">None</span> },
    { key: 'level', header: 'Level', cell: n => { const l = LEVEL[n.escalationLevel]; return l ? <StatusChip tone={l.tone}>{l.text}</StatusChip> : <StatusChip tone="neutral">Info</StatusChip> } },
    {
      key: 'actions', header: <span className="sr-only">Actions</span>,
      cell: n => (n.readAt ? null : <Button data-mark aria-label={`Mark read: ${n.message}`} disabled={!!pending} onClick={e => { e.stopPropagation(); void markReadButton(n, rows.indexOf(n)) }}>Mark read</Button>),
    },
  ]
  const openRow = (e: MouseEvent<HTMLDivElement>) => {
    if ((e.target as HTMLElement).closest('[data-mark]')) return
    const tr = (e.target as HTMLElement).closest('tbody tr'); if (!tr) return
    const i = Array.from(nav.ref.current?.querySelectorAll('tbody tr') ?? []).indexOf(tr)
    if (rows[i]) { setSel(i); open(rows[i]) }
  }

  const heading = (
    <PageHeading trail={['Workspace', 'Inbox']} title="Inbox" description="Reminders and escalations for your gates, steps and conditions, newest first." />
  )
  if (error && items === null) return <div className="os-page">{heading}<ErrorState what="Your inbox" message={error} onRetry={() => setTick(n => n + 1)} /></div>

  const counts = `${unread === 0 ? 'No unread notifications' : `${unread} unread`} · ${total} ${unreadOnly ? 'unread ' : ''}${total === 1 ? 'notification' : 'notifications'} listed`
  return (
    <div className="os-page">
      {heading}
      {error && <ErrorState what="Your inbox" message={`${error} The list below is from the last successful read.`} onRetry={() => setTick(n => n + 1)} />}
      {actionError && <Notice tone="danger" title="That did not work" live action={<Button onClick={() => setActionError(null)}>Dismiss</Button>}>{actionError}</Notice>}
      <Panel title="Notifications" subtitle={<span data-testid="inbox-counts" role="status">{counts}</span>}
        meta={
          <span className="os-inbox-tools">
            <Button icon={unreadOnly ? Check : undefined} aria-pressed={unreadOnly} onClick={() => setUnreadOnly(v => !v)}>Unread only</Button>
            <Button disabled={unread === 0 || !!pending} onClick={markAll}>{pending === 'Marking all read…' ? pending : 'Mark all read'}</Button>
          </span>
        }>
        {items === null && <LoadingState what="your notifications" />}
        {items && rows.length === 0 && (unreadOnly
          ? <EmptyState title="No unread notifications.">Choose “Unread only” again to see everything.</EmptyState>
          : <EmptyState title="No notifications yet.">Reminders and escalations for your gates, steps and conditions land here.</EmptyState>)}
        {rows.length > 0 && (
          <div {...nav} onClick={openRow}>
            <DataTable columns={columns} rows={rows} rowKey={n => n.id} caption="Notifications, newest first" selectedKey={sel !== null ? rows[sel]?.id : null}
              rowClass={n => (n.readAt ? undefined : 'unread')}
              footer={<>
                <span>In the table, j / k moves the selection and Enter opens the notification’s train and marks it read.</span>
                {rows.length < total
                  ? <Button disabled={!!pending} onClick={loadMore}>{pending === 'Loading…' ? 'Loading…' : `Load more (${total - rows.length} older)`}</Button>
                  : <span>{rows.length} {rows.length === 1 ? 'notification' : 'notifications'}</span>}
              </>} />
          </div>
        )}
      </Panel>
    </div>
  )
}
