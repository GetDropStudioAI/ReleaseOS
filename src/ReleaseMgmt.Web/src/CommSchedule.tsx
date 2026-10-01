import { useCallback, useEffect, useRef, useState } from 'react'
import { announce } from './announce'
import { ConfirmInline } from './ConfirmInline'
import { ApiError, getCommSchedule, getTemplates, markCommSent, seedCommSchedule, type Me, type ScheduleItem, type TemplateRow } from './api'
import { Conflict, isConflict } from './Conflict'
import { fmtDay, fmtDayTime, fmtHM, serverNow, zoneAbbr } from './time'

// REOS-43: a train's T-minus schedule. Status is words, glyphs and coloured text (rule 9); the only actions are text buttons.
// Exported for the Comms drawer (REOS-45); this file does not mount itself anywhere.

/** 43 min, 1 h 25 min, 2 h, 3 days 4 h. */
export function span(totalMin: number): string {
  const m = Math.max(1, Math.round(totalMin))
  if (m < 60) return `${m} min`
  const h = Math.floor(m / 60), r = m % 60
  if (h < 48) return r === 0 ? `${h} h` : `${h} h ${r} min`
  const d = Math.floor(h / 24), hh = h % 24
  return hh === 0 ? `${d} days` : `${d} days ${hh} h`
}

/** "sent 08:51", or "sent Tue 27 Oct 08:51" when it went out on another day than it was due. */
const sentText = (i: ScheduleItem) => {
  const s = i.sentAt!
  return fmtDay(s) === fmtDay(i.dueAt) ? `sent ${fmtHM(s)}` : `sent ${fmtDay(s)} ${fmtHM(s)}`
}

export function StatusText({ item, now }: { item: ScheduleItem; now: number }) {
  switch (item.state) {
    case 'Sent': return <span className="ok">✓ {sentText(item)}, on time</span>
    case 'SentLate': return <span className="warn">▲ {sentText(item)}, {span(item.lateMinutes)} late</span>
    case 'Overdue': return <span className="warn">▲ overdue by {span((now - Date.parse(item.dueAt)) / 60000)}</span>
    case 'Ready': return <span className="accent">● ready to send</span>
    default: return <span className="muted">○ scheduled</span>
  }
}

export function CommSchedule({ trainId, me, refreshKey = 0, onChanged }: { trainId: string; me: Me; refreshKey?: number; onChanged?: () => void }) {
  const canEdit = me.roles.includes('RTE') || me.roles.includes('ReleaseManager')
  const [items, setItems] = useState<ScheduleItem[] | null>(null)
  const [templates, setTemplates] = useState<TemplateRow[]>([])
  const [pick, setPick] = useState('')
  const [busy, setBusy] = useState(false)
  const focusId = useRef<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [conflict, setConflict] = useState<ApiError | null>(null)
  const [now, setNow] = useState(serverNow())

  const load = useCallback(async () => {
    try { setItems(await getCommSchedule(trainId)); setProblem(null) } catch (e) { setProblem((e as Error).message) }
  }, [trainId])
  useEffect(() => { setItems(null); setConflict(null); void load() }, [load, refreshKey])
  // "Mark sent" is replaced by the sent time once recorded: focus that status cell so focus is not lost to the page (WCAG 2.4.3)
  useEffect(() => { const id = focusId.current; if (id && items?.some(i => i.id === id)) { focusId.current = null; document.getElementById(`cs-status-${id}`)?.focus() } }, [items])
  useEffect(() => {
    if (!canEdit) return
    getTemplates().then(t => { const usable = t.filter(x => x.status !== 'Retired' && x.scheduleCount > 0); setTemplates(usable); setPick(p => p || usable[0]?.id || '') }).catch(() => setTemplates([]))
  }, [canEdit])
  useEffect(() => { const t = window.setInterval(() => setNow(serverNow()), 30000); return () => window.clearInterval(t) }, [])

  const fail = (e: unknown) => {
    if (isConflict(e)) { setConflict(e); setProblem(null) }
    else { setConflict(null); setProblem(e instanceof ApiError ? (e.body?.message ?? e.message) : (e as Error).message) }
  }
  const run = async (f: () => Promise<unknown>, done?: string) => {
    if (busy) return
    setBusy(true); setProblem(null); setConflict(null)
    try { await f(); await load(); onChanged?.(); if (done) announce(done) } catch (e) { focusId.current = null; fail(e) } finally { setBusy(false) }
  }
  const markSent = (i: ScheduleItem) => run(async () => { focusId.current = i.id; await markCommSent(i.id, i.version) }, `${i.label} ${i.name} recorded as sent.`)
  const overdue = items?.filter(i => !i.sentAt && i.state === 'Overdue') ?? []
  const markAllOverdue = () => run(async () => {
    // one at a time, each with its own version: a conflict stops the run and says which item moved
    focusId.current = overdue[0]?.id ?? null
    try { for (const i of overdue) await markCommSent(i.id, i.version) } catch (e) { focusId.current = null; await load(); throw e }   // show what did get recorded
  }, `${overdue.length} overdue ${overdue.length === 1 ? 'message' : 'messages'} recorded as sent.`)

  return (
    <div>
      {problem && <p className="bad" role="alert">✗ {problem}</p>}
      {conflict && <Conflict error={conflict} what="schedule item"><button type="button" className="text" onClick={() => { setConflict(null); void load() }}>Reload</button></Conflict>}
      {items === null && !problem && <p className="muted">Loading the schedule.</p>}
      {items?.length === 0 && (
        <div>
          <p className="muted">This train has no T-minus schedule yet.</p>
          {canEdit && (templates.length === 0
            ? <p className="muted">No train template has a T-minus plan to seed from. Add messages to a template&apos;s T-minus plan first.</p>
            : (
              <p className="inline-form">
                <label>Seed from template{' '}
                  <select className="line" value={pick} onChange={e => setPick(e.target.value)}>
                    {templates.map(t => <option key={t.id} value={t.id}>{t.name} ({t.scheduleCount} messages)</option>)}
                  </select>
                </label>
                <button type="button" className="text strong" disabled={busy || !pick} onClick={() => run(() => seedCommSchedule(trainId, pick), 'Schedule created.')}>{busy ? 'Creating…' : 'Create schedule'}</button>
              </p>
            ))}
        </div>
      )}
      {items && items.length > 0 && (
        <>
          <table className="grid">
            <thead><tr><th>When</th><th>Message</th><th>Audience</th><th>Due ({zoneAbbr()})</th><th>Status</th><th><span className="sr-only">Actions</span></th></tr></thead>
            <tbody>
              {items.map(i => (
                <tr key={i.id}>
                  <td className="mono">{i.label}</td>
                  <td>{i.name}</td>
                  <td>{i.audience}</td>
                  <td className="mono">{fmtDayTime(i.dueAt)}</td>
                  <td id={`cs-status-${i.id}`} tabIndex={-1}><StatusText item={i} now={now} /></td>
                  <td>
                    {canEdit && !i.sentAt && (
                      <ConfirmInline label="Mark sent" triggerLabel={`Mark sent: ${i.label} ${i.name}`} question="Record as sent now?" confirmLabel="Confirm" pendingLabel="Recording…"
                        destructive={false} disabled={busy} onConfirm={() => markSent(i)} />
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {canEdit && overdue.length > 1 && (
            <p><ConfirmInline label={`Mark all ${overdue.length} overdue sent`} question={`Record ${overdue.length} overdue messages as sent now? Each counts as late.`} confirmLabel="Confirm" pendingLabel="Recording…"
              destructive={false} disabled={busy} onConfirm={markAllOverdue} /></p>
          )}
          <p className="muted small">Due times are business days before the target date, in {zoneAbbr()}. A message counts as on time when it is sent at or before its due time.</p>
        </>
      )}
    </div>
  )
}
