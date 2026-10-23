import { useEffect, useState } from 'react'
import { errMsg } from './format'
import type { Selection } from './Planning'
import { ReleaseDetail } from './release/ReleaseDetail'
import { ErrorState, LoadingState } from './ui'
import { getAdvisories, getReadiness, getStream, getTrain, type Advisories, type Readiness, type StreamRow, type TrainDetail } from './api'

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
 * The release list's rows (GET /trains), shared by the Releases table, Jump to, the document title and the Calendar. A failed load is an error the table shows with Retry,
 * never an empty list (UX review 7); `loadedAt` is when the rows last arrived.
 */
export function useStream(enabled: boolean) {
  const [rows, setRows] = useState<StreamRow[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loadedAt, setLoadedAt] = useState<string | undefined>()
  const [tick, setTick] = useState(0)
  useEffect(() => {
    if (enabled) getStream().then(d => { setRows(d); setError(null); setLoadedAt(new Date().toISOString()) }).catch(e => setError(errMsg(e)))
  }, [tick, enabled])
  return { rows, error, loadedAt, reload: () => setTick(n => n + 1) }
}
