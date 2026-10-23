import { useEffect, useState, type MouseEvent } from 'react'
import { Bell } from 'lucide-react'
import type { StreamRow } from '../api'
import type { LiveState } from '../live'
import { serverNow } from '../time'
import { JumpTo } from '../JumpTo'
import { browserZone, clockLabel, localClock, utcClock } from './clock.ts'
import type { View } from './nav.ts'

const LIVE_WORDS: Record<LiveState, string | null> = { connecting: 'Connecting…', live: null, reconnecting: 'Reconnecting…', offline: 'Offline: reload to reconnect' }

/**
 * The workspace clock (D44): UTC as the design writes it, the person's own time and zone beside it, both from the server's clock as last pushed over
 * SignalR (time.ts), so every tab shows the same minute. It redraws when the minute changes. The connection state follows, in words, only while
 * the live connection is not up; it is the live region (the clock itself is not read out every minute, WCAG 4.1.3).
 */
function Clock({ live }: { live: LiveState }) {
  const minuteOf = () => Math.floor(serverNow() / 60_000) * 60_000
  const [minute, setMinute] = useState(minuteOf)
  useEffect(() => {
    const t = window.setInterval(() => { const m = minuteOf(); setMinute(prev => (prev === m ? prev : m)) }, 1000)
    return () => window.clearInterval(t)
  }, [])
  const zone = browserZone()
  const local = localClock(minute, zone)
  const word = LIVE_WORDS[live]
  return (
    <span className="os-clock">
      <time dateTime={new Date(minute).toISOString()} aria-hidden="true">{utcClock(minute)}{local && <span className="os-clock-local"> · {local}</span>}</time>
      <span className="sr-only">{clockLabel(minute, zone)}</span>
      <span role="status" className={`os-live ${live}`}>{word && <><span aria-hidden="true">{live === 'offline' ? '○' : '◐'}</span> {word}</>}</span>
    </span>
  )
}

/**
 * The utility bar (REOS-94, Figma node 6:4764): where you are (workspace / page), search (Jump to, ⌘K / Ctrl+K), the clock and the Inbox bell with its
 * unread count. 64 px, above every page.
 */
export function UtilityBar({ workspaceName, page, rows, targets, unread, live, onTrain, onGo }: {
  workspaceName: string; page: string; rows: StreamRow[] | null; targets: { name: string; view: View }[]; unread: number; live: LiveState
  onTrain: (id: string) => void; onGo: (v: View) => void
}) {
  const inbox = (e: MouseEvent<HTMLAnchorElement>) => {
    if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return
    e.preventDefault(); onGo('inbox')
  }
  return (
    <header className="os-utility">
      <nav className="os-utility-crumbs" aria-label="Location"><ol><li>{workspaceName}</li><li aria-current="page">{page}</li></ol></nav>
      <JumpTo rows={rows} targets={targets} onTrain={onTrain} onView={onGo} />
      <Clock live={live} />
      <a href="/inbox" className="os-bell" onClick={inbox} aria-label={unread > 0 ? `Inbox, ${unread} unread` : 'Inbox'}>
        <Bell size={17} strokeWidth={1.6} aria-hidden="true" />
        {unread > 0 && <span className="os-bell-count" aria-hidden="true">{unread > 99 ? '99+' : unread}</span>}
      </a>
    </header>
  )
}
