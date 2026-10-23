import { get, post } from '../api.ts'
import type { Deployment } from '../deploy/deployApi.ts'

/** The Orchestration screen's reads and writes (REOS-98, REOS-128). Runs, steps and the forecast are the existing run endpoints; stages, pause and resume are REOS-112's. */
export interface StageSummary {
  section: string; order: number; steps: number; scheduled: number; running: number; done: number; failed: number; skipped: number; status: 'Pending' | 'Running' | 'Done' | 'Failed'
  plannedStartAt: string | null; plannedEndAt: string | null; actualStartAt: string | null; actualEndAt: string | null; startDelayMin: number | null
}
export interface RunStages {
  runId: string; trainId: string; mode: 'Rehearsal' | 'Live'; version: number; startedAt: string; endedAt: string | null; outcome: string | null
  paused: boolean; pausedAt: string | null; pausedBy: string | null; pauseReason: string | null; steps: number; stepsDone: number; stages: StageSummary[]
}
export interface StagesResponse { run: RunStages; deployments: Deployment[] }

export const getStages = (runId: string) => get<StagesResponse>(`/api/v1/runs/${encodeURIComponent(runId)}/stages`)
export const getReleaseKey = (trainId: string) => get<{ key?: string | null } | null>(`/api/v1/trains/${encodeURIComponent(trainId)}/release`)

/** Pause needs a reason (1 to 500 characters); the row's version is the If-Match. */
export const pauseRun = (runId: string, reason: string, version: number) => post<unknown>(`/api/v1/runs/${encodeURIComponent(runId)}:pause`, { reason }, version)
export const resumeRun = (runId: string, version: number) => post<unknown>(`/api/v1/runs/${encodeURIComponent(runId)}:resume`, {}, version)
