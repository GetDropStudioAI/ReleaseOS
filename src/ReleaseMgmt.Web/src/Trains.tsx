import { useEffect, useState } from 'react'
import { useDraft, useSession } from './session'
import { ChangeRecordPanel, GoNoGo, emptyGoNoGo, goNoGoKey, type GoNoGoDraft } from './Governance'
import { FreezesPanel } from './Freezes'
import { fmtDayTime } from './time'
import { day, errMsg, plural, splitId, tMinus } from './format'
import { Runbook } from './Runbook'
import { Closeout } from './Closeout'
import { ExportsPanel } from './ExportsPanel'
import { Checklist, Products, Timeline, WindowLine, type Selection } from './Planning'
import { advanceTrain, getFreezesAhead, getReadiness, getStream, getTrain, type FreezeAhead, type Readiness, type StreamRow, type TrainDetail } from './api'

const GROUPS: { label: string; status: string }[] = [
  { label: 'Executing', status: 'Executing' }, { label: 'Gated', status: 'Gated' },
  { label: 'Planning', status: 'Planning' }, { label: 'Complete · last 30 days', status: 'Complete' },
]
const ORDER = ['Planning', 'Gated', 'Executing', 'Complete']

// Status is words + glyphs + coloured text, never pills or badges (CLAUDE.md rule 9).
const GLYPH: Record<string, { g: string; cls: string; word: string }> = {
  Certified: { g: '●', cls: 'ok', word: 'Certified' }, Waived: { g: '●', cls: 'warn', word: 'Waived' },
  Failed: { g: '✗', cls: 'bad', word: 'Failed' }, InProgress: { g: '◐', cls: 'accent', word: 'In progress' },
  Pending: { g: '○', cls: 'muted', word: 'Pending' },
}
export function Glyph({ status }: { status: string }) {
  const x = GLYPH[status] ?? GLYPH.Pending
  return <span className={x.cls} role="img" aria-label={x.word} title={x.word}>{x.g}</span>
}

export function Stream({ rows, selected, onSelect }: { rows: StreamRow[] | null; selected: string | null; onSelect: (id: string) => void }) {
  const { filter: q, setFilter: setQ } = useSession()   // the filter is saved with the tab's UI state
  const shown = (rows ?? []).filter(r => r.title.toLowerCase().includes(q.trim().toLowerCase()))
  return (
    <>
      <p><label className="cap" htmlFor="flt">Filter trains</label>
        <input id="flt" className="line block" placeholder="Title or release id" value={q} onChange={e => setQ(e.target.value)} /></p>
      {GROUPS.map(g => {
        const list = shown.filter(r => r.status === g.status)
        return (
          <section key={g.label}>
            <h2 className="group-label">{g.label}</h2>
            {rows === null ? <p className="muted empty">Loading…</p> : list.length === 0 ? <p className="muted empty">No trains</p> :
              list.map(r => {
                const { id, name } = splitId(r.title)
                const done = r.status === 'Complete'
                return (
                  <button key={r.id} type="button" className="stream-row" aria-current={selected === r.id ? 'true' : undefined} onClick={() => onSelect(r.id)}>
                    <span className="stream-top"><span className="mono muted">{id}</span>
                      {!done && (r.blockers > 0 ? <span className="mono warn">▲ {plural(r.blockers, 'blocker')}</span> : <span className="mono muted">0 blockers</span>)}</span>
                    <span className="stream-title">{name}</span>
                    {done
                      ? <span className={r.closeCode === 'Successful' ? 'ok stream-meta' : 'warn stream-meta'}>{r.closeCode === 'Successful' ? '✓ ' : ''}{r.closeCode ?? 'Complete'}{r.endedOn ? ` · ${day(r.endedOn)}` : ''}</span>
                      : <span className="stream-bottom"><span className="muted">{day(r.targetReleaseDate)} · {tMinus(r.daysToTarget)}</span>
                          <span className="mono stream-gates" aria-label="Gates">{r.gates.map((s, i) => <Glyph key={i} status={s} />)}</span></span>}
                  </button>
                )
              })}
          </section>
        )
      })}
    </>
  )
}

export function TrainHeader({ id, refreshKey, onChanged, selection, onSelect, canPlan, canDecide, onMode }: { id: string; refreshKey: number; onChanged: () => void; selection: Selection; onSelect: (s: Selection) => void; canPlan: boolean; canDecide: boolean; onMode: (m: import('./route').Mode) => void }) {
  const [t, setT] = useState<TrainDetail | null>(null)
  const [r, setR] = useState<Readiness | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [chosenGate, setChosenGate] = useState<string | null>(null)
  const [goDraft, setGoDraft] = useDraft<GoNoGoDraft>(goNoGoKey(id))   // the header button and the Go/No-Go section share one draft
  useEffect(() => {
    let live = true
    setErr(null)
    getTrain(id).then(async d => {
      if (!live) return
      setT(d)
      setR(d.nextStatus ? await getReadiness(id, d.nextStatus).catch(() => null) : null)
    }).catch(e => live && setErr(e.message))
    return () => { live = false }
  }, [id, refreshKey])

  if (err) return <p className="bad" role="alert">✗ {err}</p>
  if (!t) return <p className="muted">Loading…</p>

  const advance = async () => {
    if (!t.nextStatus) return
    setBusy(true); setErr(null)
    try { await advanceTrain(t.id, t.nextStatus, t.version); onChanged() }
    catch (e) { setErr(errMsg(e, 'Could not advance the train.')); onChanged() }
    finally { setBusy(false) }
  }
  const { id: relId, name } = splitId(t.title)
  const blockers = r?.blockers.length ?? 0
  const gatesBefore = t.nextStatus ? t.gates.filter(g => ORDER.indexOf(g.requiredBeforeStatus) <= ORDER.indexOf(t.nextStatus!)) : []
  const others = (r?.blockers ?? []).filter(b => b.failure.guard !== 'GateLockout')
  const soon = 'Available in a later milestone'
  // Checklist shows the gate the user picked, else the first gate still open, else the last.
  const checklistGate = t.gates.find(g => g.id === chosenGate) ?? t.gates.find(g => g.status !== 'Certified' && g.status !== 'Waived') ?? t.gates[t.gates.length - 1]
  const pickGate = (gid: string) => { setChosenGate(gid); onSelect({ kind: 'gate', id: gid }) }

  return (
    <>
      <div className="header-line">
        <span className="cap">Release train{t.changeTicketNumber ? <> · <span className="mono">{t.changeTicketNumber}</span></> : ''} · Risk {t.riskTier} · <span className="lc">v{t.version}</span></span>
        <span className="actions">
          <button type="button" className="text" onClick={() => onMode('rehearsal')}>Rehearsal</button>
          <button type="button" className="text" onClick={() => onMode('live')}>Live runbook</button>
          {canDecide && <button type="button" className="text" onClick={() => setGoDraft(goDraft ? undefined : emptyGoNoGo())}>{goDraft ? 'Cancel Go/No-Go' : 'Record Go/No-Go'}</button>}
          <button type="button" className="text" disabled title={soon}>Communicate</button>
          <button type="button" className="text" onClick={() => document.getElementById('exports')?.scrollIntoView({ behavior: 'smooth', block: 'start' })}>Export</button>
          {t.nextStatus && <button type="button" className="text" disabled={busy || blockers > 0} title={blockers > 0 ? `${plural(blockers, 'guard')} not met` : undefined} onClick={advance}>Advance to {t.nextStatus}</button>}
        </span>
      </div>
      <h1>{relId ? `${relId} ${name}` : name}</h1>
      <p className="muted">{t.status} · Target <strong className="label">{day(t.targetReleaseDate)} {t.targetReleaseDate.slice(0, 4)}</strong> · <span className="mono">{tMinus(t.daysToTarget)}</span> business days · <WindowLine trainId={t.id} canEdit={canPlan} refreshKey={refreshKey} onChanged={onChanged} /></p>
      {err && <p className="bad" role="alert">✗ {err}</p>}
      {t.nextStatus && r && (
        <section className="readiness" aria-label="Readiness">
          <span className="cap">To reach {t.nextStatus}</span>
          {gatesBefore.map(g => {
            const ok = g.status === 'Certified' || g.status === 'Waived'
            return <span key={g.id}><span className={ok ? 'ok' : 'bad'}>{ok ? '✓' : '✗'}</span> {g.name}{!ok && <span className="muted"> due {day(g.dueOn)}</span>}</span>
          })}
          {others.map((b, i) => <span key={i}><span className="bad">✗</span> {b.failure.message}</span>)}
          {r.ready && <span className="ok">✓ Ready for {t.nextStatus}</span>}
        </section>
      )}
      <Products trainId={t.id} refreshKey={refreshKey} selection={selection} onSelect={onSelect} />
      <Timeline gates={t.gates} todayT={t.daysToTarget} targetDate={t.targetReleaseDate} selectedId={checklistGate?.id ?? null} onSelect={pickGate} />
      {checklistGate && <Checklist gateId={checklistGate.id} trainId={t.id} refreshKey={refreshKey} selection={selection} onSelect={onSelect} onChanged={onChanged} />}
      <GoNoGo trainId={t.id} trainVersion={t.version} canDecide={canDecide} refreshKey={refreshKey} onChanged={onChanged} />
      <ChangeRecordPanel trainId={t.id} canEdit={canPlan} refreshKey={refreshKey} onChanged={onChanged} />
      <FreezesPanel trainId={t.id} refreshKey={refreshKey} onChanged={onChanged} />
      <Runbook trainId={t.id} canPlan={canPlan} refreshKey={refreshKey} selection={selection} onSelect={onSelect} onChanged={onChanged} />
      <Closeout train={t} canPlan={canPlan} refreshKey={refreshKey} onChanged={onChanged} />
      <ExportsPanel trainId={t.id} refreshKey={refreshKey} />
    </>
  )
}

export function useStream(enabled: boolean) {
  const [rows, setRows] = useState<StreamRow[] | null>(null)
  const [tick, setTick] = useState(0)
  useEffect(() => { if (enabled) getStream().then(setRows).catch(() => setRows([])) }, [tick, enabled])
  return { rows, reload: () => setTick(n => n + 1) }
}

const stamp = fmtDayTime

/** Pinned to the bottom of the Stream (mockups/Main.html): freezes and chills that are running or coming, with the overrides already granted. */
export function FreezeFooter({ refreshKey }: { refreshKey: number }) {
  const [rows, setRows] = useState<FreezeAhead[]>([])
  useEffect(() => { getFreezesAhead().then(setRows).catch(() => setRows([])) }, [refreshKey])
  if (rows.length === 0) return null
  return (
    <div className="freeze-footer" aria-label="Freeze ahead">
      <div className="cap">{rows.some(r => r.active) ? 'Freeze in effect' : 'Freeze ahead'}</div>
      {rows.map(r => (
        <div key={r.id}>
          <div><span className={r.kind === 'Freeze' ? 'bad' : 'warn'}>{r.name}</span> · {stamp(r.startsAt)} – {stamp(r.endsAt)}{r.active ? <span className="bad"> · now</span> : null}</div>
          <div className="muted">Scope: {r.scope.replace('Products matching ', '')}{r.overridesGranted > 0 ? ` · ${plural(r.overridesGranted, 'override')} granted` : ''}</div>
        </div>))}
    </div>
  )
}
