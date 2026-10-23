import { useEffect, useId, useState, useSyncExternalStore } from 'react'
import { useSession } from './session'
import { fmtDayTime } from './time'
import { day, errMsg, plural, splitId, tMinus } from './format'
import type { Selection } from './Planning'
import { ReleaseDetail } from './release/ReleaseDetail'
import { ErrorState, LoadingState } from './ui'
import { getAdvisories, getFreezesAhead, getReadiness, getStream, getTrain, type Advisories, type FreezeAhead, type Readiness, type StreamRow, type TrainDetail } from './api'

// Aborted trains keep a group of their own (shown only when the list has any), so a train never silently vanishes from the Stream (UX review 26).
const GROUPS: { label: string; status: string; hideEmpty?: boolean }[] = [
  { label: 'Executing', status: 'Executing' }, { label: 'Gated', status: 'Gated' },
  { label: 'Planning', status: 'Planning' }, { label: 'Complete · last 30 days', status: 'Complete' }, { label: 'Aborted', status: 'Aborted', hideEmpty: true },
]
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

export function Stream({ rows, selected, onSelect, onNew }: { rows: StreamRow[] | null; selected: string | null; onSelect: (id: string) => void; onNew?: () => void }) {
  const { filter: q, setFilter: setQ } = useSession()   // the filter is saved with the tab's UI state
  const failed = useSyncExternalStore(streamLoad.subscribe, () => streamLoad.error)
  const shown = (rows ?? []).filter(r => r.title.toLowerCase().includes(q.trim().toLowerCase()))
  return (
    <>
      {onNew && <div className="section-head stream-head"><span className="cap">Trains</span><button type="button" className="text" onClick={onNew}>New train</button></div>}
      <p><label className="cap" htmlFor="flt">Filter trains</label>
        <input id="flt" className="line block" placeholder="Title or release id" value={q} onChange={e => setQ(e.target.value)} /></p>
      {failed && <p className="bad" role="alert">✗ The trains could not be loaded: {failed} <button type="button" className="text" onClick={() => streamLoad.retry()}>Retry</button></p>}
      {(!failed || rows) && GROUPS.map(g => {
        const list = shown.filter(r => r.status === g.status)
        if (g.hideEmpty && list.length === 0) return null
        return (
          <section key={g.label}>
            <h2 className="group-label">{g.label}</h2>
            {rows === null ? <p className="muted empty">Loading…</p> : list.length === 0 ? <p className="muted empty">No trains</p> :
              list.map(r => {
                const { id, name } = splitId(r.title)
                const done = r.status === 'Complete' || r.status === 'Aborted'
                return (
                  <button key={r.id} type="button" className="stream-row" aria-current={selected === r.id ? 'true' : undefined} onClick={() => onSelect(r.id)}>
                    <span className="stream-top"><span className="mono muted">{id}</span>
                      {!done && (r.blockers > 0 ? <span className="mono warn">▲ {plural(r.blockers, 'blocker')}</span> : <span className="mono muted">0 blockers</span>)}</span>
                    <span className="stream-title">{name}</span>
                    {done
                      ? <span className={r.closeCode === 'Successful' ? 'ok stream-meta' : 'warn stream-meta'}>{r.closeCode === 'Successful' ? '✓ ' : r.status === 'Aborted' ? '✗ ' : ''}{r.closeCode ?? r.status}{r.endedOn ? ` · ${day(r.endedOn)}` : ''}</span>
                      : <span className="stream-bottom"><span className="muted">{day(r.targetReleaseDate)} · {tMinus(r.daysToTarget)}</span>
                          <span className="mono stream-gates"><span className="sr-only">Gates: </span>{r.gates.map((s, i) => <Glyph key={i} status={s} />)}</span></span>}
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
  const [rErr, setRErr] = useState<string | null>(null)
  const [adv, setAdv] = useState<Advisories | null>(null)   // REOS-85/88: contingency and window collisions, shown beside readiness, never as guards
  const [tick, setTick] = useState(0)
  const [err, setErr] = useState<string | null>(null)
  const [chosenGate, setChosenGate] = useState<string | null>(null)
  useEffect(() => {
    let live = true
    setErr(null); setRErr(null)
    getAdvisories(id).then(a => { if (live) setAdv(a) }).catch(() => { if (live) setAdv(null) })   // advisory: a failed load hides the warnings, it blocks nothing
    getTrain(id).then(async d => {
      if (!live) return
      setT(d)
      setR(d.nextStatus ? await getReadiness(id, d.nextStatus).catch(e => { if (live) setRErr(errMsg(e)); return null }) : null)
    }).catch(e => live && setErr(errMsg(e)))
    return () => { live = false }
  }, [id, refreshKey, tick])
  const retry = () => setTick(n => n + 1)

  // REOS-97: the release workspace is a redesigned page (App gives it the canvas); its states are the shared ones.
  if (err && !t) return <div className="os-page"><ErrorState what="The release" message={err} onRetry={retry} /></div>
  if (!t) return <div className="os-page"><LoadingState what="the release" /></div>
  // A train that is not the one asked for (the id changed and the answer has not come yet) is not shown as if it were.
  if (t.id !== id) return <div className="os-page"><LoadingState what="the release" /></div>
  return (
    <ReleaseDetail key={t.id} t={t} r={r} rErr={rErr} adv={adv} err={err} refreshKey={refreshKey} onChanged={onChanged} retry={retry} selection={selection} onSelect={onSelect}
      canPlan={canPlan} canDecide={canDecide} onMode={onMode} chosenGate={chosenGate} onPickGate={gid => { setChosenGate(gid); onSelect({ kind: 'gate', id: gid }) }} />
  )
}

/**
 * App owns the Stream's rows (useStream) and passes only `rows` to <Stream>, so a failed load and its retry reach the Stream through this
 * small store. A failure is an inline error with Retry, never an empty "No trains" list (UX review 7).
 */
const streamLoad = {
  error: null as string | null, retry: () => {}, listeners: new Set<() => void>(),
  set(e: string | null) { if (e !== this.error) { this.error = e; this.listeners.forEach(l => l()) } },
  subscribe: (l: () => void) => { streamLoad.listeners.add(l); return () => { streamLoad.listeners.delete(l) } },
}

export function useStream(enabled: boolean) {
  const [rows, setRows] = useState<StreamRow[] | null>(null)
  const [tick, setTick] = useState(0)
  useEffect(() => { streamLoad.retry = () => setTick(n => n + 1) }, [])
  useEffect(() => { if (enabled) getStream().then(d => { setRows(d); streamLoad.set(null) }).catch(e => streamLoad.set(errMsg(e))) }, [tick, enabled])
  return { rows, reload: () => setTick(n => n + 1) }
}

const stamp = fmtDayTime

/** Pinned to the bottom of the Stream (mockups/Main.html): freezes and chills that are running or coming, with the overrides already granted. */
export function FreezeFooter({ refreshKey }: { refreshKey: number }) {
  const [rows, setRows] = useState<FreezeAhead[]>([])
  const [err, setErr] = useState<string | null>(null)
  const [tick, setTick] = useState(0)
  const headId = useId()
  useEffect(() => { getFreezesAhead().then(d => { setRows(d); setErr(null) }).catch(e => setErr(errMsg(e))) }, [refreshKey, tick])
  if (err) return <div className="freeze-footer"><p className="bad" role="alert">✗ Freezes ahead could not be loaded: {err} <button type="button" className="text" onClick={() => setTick(n => n + 1)}>Retry</button></p></div>
  if (rows.length === 0) return null
  return (
    <section className="freeze-footer" aria-labelledby={headId}>
      <div id={headId} className="cap">{rows.some(r => r.active) ? 'Freeze in effect' : 'Freeze ahead'}</div>
      {rows.map(r => (
        <div key={r.id}>
          {/* Freeze and Chill differ in words and glyph, not only red vs orange (WCAG 1.4.1) */}
          <div><span className={r.kind === 'Freeze' ? 'bad' : 'warn'}>{r.kind === 'Freeze' ? '▲ Freeze' : '◐ Chill'} · {r.name}</span> · {stamp(r.startsAt)} – {stamp(r.endsAt)}{r.active ? <span className="bad"> · now</span> : null}</div>
          <div className="muted">Scope: {r.scope.replace('Products matching ', '')}{r.overridesGranted > 0 ? ` · ${plural(r.overridesGranted, 'override')} granted` : ''}</div>
        </div>))}
    </section>
  )
}
