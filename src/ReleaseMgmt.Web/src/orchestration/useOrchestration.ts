import { useCallback, useEffect, useState } from 'react'
import { getForecast, getRun, getRuns, type RunDetail, type RunForecast, type RunSummary } from '../api.ts'
import { errMsg } from '../format.ts'
import { onDeploymentChanged } from '../live.ts'
import { getStages, type StagesResponse } from './orchApi.ts'

export type RunMode = 'rehearsal' | 'live'
export interface Loaded { runs: RunSummary[]; run: RunDetail | null; forecast: RunForecast | null; stages: StagesResponse | null; at: string }

/**
 * Everything the pipeline shows about one run, from the existing run endpoints (REOS-98, REOS-128): the train's runs, the chosen run with its steps, its forecast,
 * and its stage summaries with the deployments linked to it. The chosen run is `runId` when the person picked one from the history, else the open run of the
 * mode, else the latest of that mode. Refetches when asked (`refreshKey`: a step event, a train change, a reconnect), when a linked deployment changes, and
 * every 30 s (a window or a canary can end without a push).
 */
export function useOrchestration(trainId: string | null, mode: RunMode, runId: string | null, refreshKey: number) {
  const [data, setData] = useState<Loaded | undefined>(undefined)
  const [error, setError] = useState<string | null>(null)
  const [tick, setTick] = useState(0)
  const reload = useCallback(() => setTick(n => n + 1), [])

  useEffect(() => { setData(undefined); setError(null) }, [trainId, mode, runId])
  useEffect(() => {
    if (!trainId) return
    let current = true
    ;(async () => {
      try {
        const want = mode === 'live' ? 'Live' : 'Rehearsal'
        const all = await getRuns(trainId)
        const ofMode = all.filter(r => r.mode === want)
        const pick = (runId ? all.find(r => r.id === runId) : undefined) ?? ofMode.find(r => r.endedAt === null) ?? ofMode[0]
        if (!pick) { if (current) { setData({ runs: all, run: null, forecast: null, stages: null, at: new Date().toISOString() }); setError(null) } return }
        const [run, forecast, stages] = await Promise.all([getRun(pick.id), getForecast(pick.id), getStages(pick.id)])
        if (current) { setData({ runs: all, run, forecast, stages, at: new Date().toISOString() }); setError(null) }
      } catch (e) { if (current) setError(errMsg(e)) }
    })()
    return () => { current = false }
  }, [trainId, mode, runId, refreshKey, tick])

  useEffect(() => {
    let timer: ReturnType<typeof setTimeout> | undefined
    const linked = new Set((data?.stages?.deployments ?? []).map(d => d.id))
    const off = onDeploymentChanged(id => { if (linked.has(id)) { clearTimeout(timer); timer = setTimeout(reload, 350) } })
    const poll = window.setInterval(reload, 30000)
    return () => { off(); clearTimeout(timer); window.clearInterval(poll) }
  }, [data?.stages?.deployments, reload])

  return { data, error, reload }
}

/** Re-renders every second, so elapsed times and countdowns move without new data (they run on the server's clock, `serverNow`). */
export function useTick() {
  const [, set] = useState(0)
  useEffect(() => { const t = window.setInterval(() => set(n => n + 1), 1000); return () => window.clearInterval(t) }, [])
}
