import { useCallback, useEffect, useRef, useState } from 'react'
import { getInbox, markAllNotificationsRead, markNotificationRead, type InboxItem, type Me } from './api'
import type { Route } from './route'
import { primaryOf, useRowNav } from './rowNav'
import { announce } from './announce'
import { useAction } from './useAction'
import { fmtDayTime, zoneAbbr } from './time'

/** REOS-38: my notifications, newest first. Unread rows are semibold with "● unread"; opening a row marks it read and goes to its train. */
const PAGE = 50
const LEVEL: Record<number, { cls: string; text: string }> = { 1: { cls: 'warn', text: '▲ level 1' }, 2: { cls: 'bad', text: '✗ level 2' } }

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
  const { run, pending } = useAction()
  const loaded = useRef(0); loaded.current = items?.length ?? 0

  // Reload keeps as many rows as are already on screen (up to the server's 100), so a live push does not collapse "Load more".
  const reload = useCallback(async () => {
    try {
      const p = await getInbox(unreadOnly, Math.min(100, Math.max(PAGE, loaded.current)), 0)
      setItems(p.items); setTotal(p.total); setUnread(p.unread); setError(null)
    } catch (e) { setError((e as Error).message) }
  }, [unreadOnly])
  useEffect(() => { void reload() }, [reload, refreshKey])
  useEffect(() => { setItems(null) }, [unreadOnly])

  const loadMore = () => run('Loading…', async () => {
    if (!items) return
    try {
      const p = await getInbox(unreadOnly, PAGE, items.length)
      setItems([...items, ...p.items.filter(n => !items.some(x => x.id === n.id))]); setTotal(p.total); setUnread(p.unread)
    } catch (e) { setError((e as Error).message) }
  })

  const markRead = async (n: InboxItem) => {
    if (n.readAt) return
    try {
      const r = await markNotificationRead(n.id)
      setItems(cur => cur && (unreadOnly ? cur.filter(x => x.id !== n.id) : cur.map(x => (x.id === n.id ? { ...x, readAt: r.readAt, version: r.version } : x))))
      if (unreadOnly) setTotal(t => Math.max(0, t - 1))
      setUnread(u => Math.max(0, u - 1))
      onChanged?.()
    } catch (e) { setError((e as Error).message) }
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
    try { await markAllNotificationsRead(); await reload(); onChanged?.(); announce('All notifications marked read.') } catch (e) { setError((e as Error).message) }
  })
  const open = (n: InboxItem) => { void markRead(n); const t = targetOf(n); if (t) onOpen?.(t) }

  const rows = items ?? []
  const [sel, setSel, nav] = useRowNav(rows.length, i => { if (rows[i]) open(rows[i]) })

  return (
    <section>
      <h1>Inbox</h1>
      {error && <p className="bad" role="alert">✗ {error}</p>}
      <p className="actions">
        <span className="muted" data-testid="inbox-counts" role="status">{unread === 0 ? 'No unread notifications' : `${unread} unread`} · {total} {unreadOnly ? 'unread ' : ''}{total === 1 ? 'notification' : 'notifications'} listed</span>
        <span className="tabs"><button type="button" className="choice" aria-pressed={unreadOnly} onClick={() => setUnreadOnly(v => !v)}>Unread only</button></span>
        <button type="button" className="text" disabled={unread === 0 || !!pending} onClick={markAll}>{pending === 'Marking all read…' ? pending : 'Mark all read'}</button>
      </p>
      {items === null && !error && <p className="muted">Loading…</p>}
      {items && rows.length === 0 && <p className="muted">{unreadOnly ? 'No unread notifications. Choose “Unread only” again to see everything.' : 'No notifications yet. Reminders and escalations for your gates, steps and conditions land here.'}</p>}
      {rows.length > 0 && (
        <table className="grid" aria-label="Notifications, newest first" {...nav}>
          <thead><tr><th>State</th><th>When</th><th>Notification</th><th>Train</th><th>Level</th><th><span className="sr-only">Actions</span></th></tr></thead>
          <tbody>
            {rows.map((n, i) => (
              <tr key={n.id} className={sel === i ? 'selected' : undefined} style={n.readAt ? undefined : { fontWeight: 600 }} onClick={() => { setSel(i); open(n) }}>
                <td>{n.readAt ? <span className="muted">○ read</span> : <span className="accent">● unread</span>}</td>
                <td className="nowrap">{fmtDayTime(n.createdAt)} {zoneAbbr(n.createdAt)}</td>
                <td><button type="button" className="text plainlink" style={{ fontWeight: 'inherit' }} data-rownav aria-current={sel === i ? 'true' : undefined}>{n.message}</button></td>
                <td>{n.trainTitle ?? <span className="muted">none</span>}</td>
                <td>{LEVEL[n.escalationLevel] ? <span className={LEVEL[n.escalationLevel].cls}>{LEVEL[n.escalationLevel].text}</span> : <span className="muted">info</span>}</td>
                <td>{!n.readAt && <button type="button" className="text" data-mark aria-label={`Mark read: ${n.message}`} disabled={!!pending} onClick={e => { e.stopPropagation(); void markReadButton(n, i) }}>Mark read</button>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {rows.length > 0 && rows.length < total && <p><button type="button" className="text" disabled={!!pending} onClick={loadMore}>{pending === 'Loading…' ? 'Loading…' : `Load more (${total - rows.length} older)`}</button></p>}
      {rows.length > 0 && <p className="muted">In the table, j / k moves the selection and Enter opens the notification’s train and marks it read.</p>}
    </section>
  )
}
