import { useCallback, useEffect, useRef, useState } from 'react'
import { announce } from './announce'
import { ApiError, getCommSchedule, getTemplates, markCommSent, seedCommSchedule, type Me, type ScheduleItem, type TemplateRow } from './api'
import { isConflict } from './Conflict'
import { serverNow } from './time'
import { Button, ConfirmDialog, ConflictNotice, DataTable, EmptyState, LoadingState, StatusChip, type Column, type Tone } from './ui'
import { Field, Problem, SelectInput, When } from './mgmt/forms'

// REOS-43 / REOS-129: a train's T-minus schedule as a table. A status is a chip (dot and words); recording a send asks for confirmation in a dialog.
// Times are UTC with the person's own time beside them (D44).

/** 43 min, 1 h 25 min, 2 h, 3 days 4 h. */
export function span(totalMin: number): string {
  const m = Math.max(1, Math.round(totalMin))
  if (m < 60) return `${m} min`
  const h = Math.floor(m / 60), r = m % 60
  if (h < 48) return r === 0 ? `${h} h` : `${h} h ${r} min`
  const d = Math.floor(h / 24), hh = h % 24
  return hh === 0 ? `${d} days` : `${d} days ${hh} h`
}

const utcHM = (iso: string) => `${iso.slice(11, 16)} UTC`
const utcDay = (iso: string) => iso.slice(0, 10)
/** "sent 08:51 UTC", or "sent 2026-10-27 08:51 UTC" when it went out on another day than it was due. */
const sentText = (i: ScheduleItem) => {
  const s = i.sentAt!
  return utcDay(s) === utcDay(i.dueAt) ? `sent ${utcHM(s)}` : `sent ${utcDay(s)} ${utcHM(s)}`
}

export function StatusText({ item, now }: { item: ScheduleItem; now: number }) {
  const [tone, text]: [Tone, string] =
    item.state === 'Sent' ? ['success', `Sent ${sentText(item).slice(5)}, on time`]
    : item.state === 'SentLate' ? ['warning', `Sent ${sentText(item).slice(5)}, ${span(item.lateMinutes)} late`]
    : item.state === 'Overdue' ? ['danger', `Overdue by ${span((now - Date.parse(item.dueAt)) / 60000)}`]
    : item.state === 'Ready' ? ['info', 'Ready to send']
    : ['neutral', 'Scheduled']
  return <StatusChip tone={tone}>{text}</StatusChip>
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
  const [confirm, setConfirm] = useState<ScheduleItem | 'all' | null>(null)

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
    try { await f(); await load(); onChanged?.(); if (done) announce(done) } catch (e) { focusId.current = null; fail(e) } finally { setBusy(false); setConfirm(null) }
  }
  const markSent = (i: ScheduleItem) => run(async () => { focusId.current = i.id; await markCommSent(i.id, i.version) }, `${i.label} ${i.name} recorded as sent.`)
  const overdue = items?.filter(i => !i.sentAt && i.state === 'Overdue') ?? []
  const markAllOverdue = () => run(async () => {
    // one at a time, each with its own version: a conflict stops the run and says which item moved
    focusId.current = overdue[0]?.id ?? null
    try { for (const i of overdue) await markCommSent(i.id, i.version) } catch (e) { focusId.current = null; await load(); throw e }   // show what did get recorded
  }, `${overdue.length} overdue ${overdue.length === 1 ? 'message' : 'messages'} recorded as sent.`)

  const columns: Column<ScheduleItem>[] = [
    { key: 'when', header: 'When', mono: true, cell: i => i.label },
    { key: 'msg', header: 'Message', cell: i => i.name },
    { key: 'aud', header: 'Audience', cell: i => i.audience },
    { key: 'due', header: 'Due (UTC)', cell: i => <When iso={i.dueAt} /> },
    { key: 'status', header: 'Status', cell: i => <span id={`cs-status-${i.id}`} tabIndex={-1}><StatusText item={i} now={now} /></span> },
    { key: 'act', header: <span className="sr-only">Actions</span>, align: 'end', cell: i => (canEdit && !i.sentAt
      ? <Button aria-label={`Mark sent: ${i.label} ${i.name}`} disabled={busy} onClick={() => setConfirm(i)}>Mark sent</Button> : null) },
  ]

  return (
    <div className="mg-stack">
      <Problem message={problem} />
      {conflict && <ConflictNotice onUseTheirs={() => { setConflict(null); void load() }} />}
      {items === null && !problem && <LoadingState what="the schedule" />}
      {items?.length === 0 && (
        <EmptyState title="This train has no T-minus schedule yet">
          {canEdit && templates.length === 0 && <>No train template has a T-minus plan to seed from. Add messages to a template&apos;s T-minus plan first.</>}
        </EmptyState>
      )}
      {items?.length === 0 && canEdit && templates.length > 0 && (
        <div className="mg-form">
          <Field label="Seed from template">
            <SelectInput value={pick} onChange={e => setPick(e.target.value)} options={templates.map(t => ({ value: t.id, label: `${t.name} (${t.scheduleCount} messages)` }))} />
          </Field>
          <div className="mg-actions"><Button variant="primary" disabled={busy || !pick} onClick={() => run(() => seedCommSchedule(trainId, pick), 'Schedule created.')}>{busy ? 'Creating…' : 'Create schedule'}</Button></div>
        </div>
      )}
      {items && items.length > 0 && (
        <>
          <DataTable caption="T-minus schedule" columns={columns} rows={items} rowKey={i => i.id} />
          {canEdit && overdue.length > 1 && <div className="mg-actions"><Button disabled={busy} onClick={() => setConfirm('all')}>{`Mark all ${overdue.length} overdue sent`}</Button></div>}
          <p className="mg-note">Due times are business days before the target date. They are shown in UTC with your own time beside them. A message counts as on time when it is sent at or before its due time.</p>
        </>
      )}
      <ConfirmDialog open={confirm !== null} title={confirm === 'all' ? `Record ${overdue.length} overdue messages as sent now?` : 'Record as sent now?'} confirmLabel="Record as sent" pendingLabel="Recording…" pending={busy}
        onCancel={() => setConfirm(null)} onConfirm={() => void (confirm === 'all' ? markAllOverdue() : confirm ? markSent(confirm) : undefined)}>
        {confirm === 'all' ? 'Each counts as late.' : confirm ? `${confirm.label} ${confirm.name}. The time of sending is recorded against its due time and cannot be changed.` : null}
      </ConfirmDialog>
    </div>
  )
}
