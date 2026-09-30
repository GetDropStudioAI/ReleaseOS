import { useCallback, useEffect, useState } from 'react'
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
  const [confirming, setConfirming] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [conflict, setConflict] = useState<ApiError | null>(null)
  const [now, setNow] = useState(serverNow())

  const load = useCallback(async () => {
    try { setItems(await getCommSchedule(trainId)); setProblem(null) } catch (e) { setProblem((e as Error).message) }
  }, [trainId])
  useEffect(() => { setItems(null); setConfirming(null); setConflict(null); void load() }, [load, refreshKey])
  useEffect(() => {
    if (!canEdit) return
    getTemplates().then(t => { const usable = t.filter(x => x.status !== 'Retired' && x.scheduleCount > 0); setTemplates(usable); setPick(p => p || usable[0]?.id || '') }).catch(() => setTemplates([]))
  }, [canEdit])
  useEffect(() => { const t = window.setInterval(() => setNow(serverNow()), 30000); return () => window.clearInterval(t) }, [])

  const fail = (e: unknown) => {
    if (isConflict(e)) { setConflict(e); setProblem(null) }
    else { setConflict(null); setProblem(e instanceof ApiError ? (e.body?.message ?? e.message) : (e as Error).message) }
  }
  const run = async (f: () => Promise<unknown>) => {
    setBusy(true); setProblem(null); setConflict(null)
    try { await f(); setConfirming(null); await load(); onChanged?.() } catch (e) { fail(e) } finally { setBusy(false) }
  }

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
                <button type="button" className="text strong" disabled={busy || !pick} onClick={() => run(() => seedCommSchedule(trainId, pick))}>Create schedule</button>
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
                  <td><StatusText item={i} now={now} /></td>
                  <td>
                    {canEdit && !i.sentAt && (confirming === i.id
                      ? (
                        <span>
                          Record as sent now?{' '}
                          <button type="button" className="text strong" disabled={busy} onClick={() => run(() => markCommSent(i.id, i.version))}>Confirm</button>{' '}
                          <button type="button" className="text" onClick={() => setConfirming(null)}>Cancel</button>
                        </span>
                      )
                      : <button type="button" className="text" aria-label={`Mark ${i.label} ${i.name} as sent`} onClick={() => setConfirming(i.id)}>Mark sent</button>)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <p className="muted small">Due times are business days before the target date, in {zoneAbbr()}. A message counts as on time when it is sent at or before its due time.</p>
        </>
      )}
    </div>
  )
}
