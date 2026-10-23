import { useLayoutEffect, useRef, useState } from 'react'
import { Panel } from '../ui'
import type { LiveState } from '../live.ts'
import type { DeploymentEvent } from './deployApi.ts'
import { LEVEL_WORD, utcHMS } from './deployLogic.ts'
import { localClock, browserZone } from '../shell/clock.ts'

/**
 * The deployment events panel (REOS-100, the design's dark "Deployment events"): what the deployment tool reported, oldest at the top and newest at the
 * bottom, in UTC. It follows the newest event while the person is at the bottom. Scrolling up, or choosing "Pause auto-scroll", stops the following: new events are
 * appended below without moving what is on screen, and the footer counts them until the person returns to the bottom or resumes. The header says in words whether
 * the live channel is connected; when it is not, the panel says the log may be behind (the page shows the shared stale banner as well).
 */
const NEAR_BOTTOM = 24

export function EventStream({ events, live, capped, onLoadMore }: {
  events: DeploymentEvent[]; live: LiveState; capped: boolean; onLoadMore: () => void
}) {
  const box = useRef<HTMLDivElement>(null)
  const [follow, setFollow] = useState(true)
  const [seen, setSeen] = useState(events.length)   // how many events were on screen when following stopped
  const connected = live === 'live'

  // Follow the newest event; when not following, leave the scroll position alone (appended rows are below the viewport).
  useLayoutEffect(() => {
    const el = box.current
    if (!el) return
    if (follow) el.scrollTop = el.scrollHeight
  }, [events, follow])
  const missed = follow ? 0 : Math.max(0, events.length - seen)

  const onScroll = () => {
    const el = box.current
    if (!el) return
    const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight <= NEAR_BOTTOM
    if (atBottom && !follow) setFollow(true)
    if (!atBottom && follow) { setSeen(events.length); setFollow(false) }
  }
  const pause = () => { setSeen(events.length); setFollow(false) }
  const zone = browserZone()

  return (
    <Panel title="Deployment events" inverse level={2} subtitle="Times in UTC, oldest first"
      meta={<span className={`dp-stream-state ${connected ? 'on' : 'off'}`} role="status">{connected ? 'STREAM CONNECTED' : live === 'reconnecting' ? 'STREAM RECONNECTING' : live === 'connecting' ? 'STREAM CONNECTING' : 'STREAM DISCONNECTED'}</span>}
      footer={
        <div className="dp-events-foot">
          <span className="dp-foot-actions">
            <button type="button" className="dp-link" onClick={follow ? pause : () => setFollow(true)}>{follow ? 'Pause auto-scroll' : missed > 0 ? `Resume auto-scroll · ${missed} new` : 'Resume auto-scroll'}</button>
            {capped && <button type="button" className="dp-link" onClick={onLoadMore}>Load earlier events</button>}
          </span>
          <span className="dp-count">{events.length} {events.length === 1 ? 'EVENT' : 'EVENTS'}{capped ? ' SHOWN' : ''}</span>
        </div>
      }>
      {!connected && <p className="dp-stream-note">Live updates are {live === 'reconnecting' || live === 'connecting' ? 'reconnecting' : 'offline'}: this log may be behind.</p>}
      <div ref={box} className="dp-events" tabIndex={0} role="region" aria-label="Deployment events (scrollable)" onScroll={onScroll}>
        {events.length === 0
          ? <p className="dp-event-empty">No events reported yet. The deployment tool pushes them to the ingest API.</p>
          : <ol className="dp-event-list">
              {events.map(e => {
                const ms = Date.parse(e.occurredAt), local = localClock(ms, zone)
                return (
                  <li key={e.id} className="dp-event">
                    <span className="dp-event-meta">
                      <time dateTime={e.occurredAt}>{utcHMS(ms)}<span className="sr-only"> UTC{local ? `, ${local} for you` : ''}</span></time>
                      <span className={`dp-level ${e.level.toLowerCase()}`}>{LEVEL_WORD[e.level] ?? e.level}</span>
                    </span>
                    <span className="dp-event-text">{e.message}</span>
                  </li>
                )
              })}
            </ol>}
      </div>
    </Panel>
  )
}
