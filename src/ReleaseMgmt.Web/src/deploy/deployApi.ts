import { get, post } from '../api.ts'

/** The Deployments destination's reads (REOS-110, REOS-111, REOS-112). Shapes mirror DeploymentView, DeploymentMonitoring and DeploymentMetrics on the server. */
export interface Deployment {
  id: string; externalId: string; serviceId: string; serviceKey: string; serviceName: string; environmentId: string; environmentName: string; environmentKind: string
  trainId: string | null; releaseKey: string | null; trainTitle: string | null; runId: string | null; releaseVersion: string; strategy: string; canarySteps: number[] | null
  state: string; trafficPercent: number; readyPods: number | null; desiredPods: number | null; startedAt: string | null; finishedAt: string | null; lastEventAt: string | null
  createdAt: string; updatedAt: string; version: number; stepStartedAt: string | null; autoRollbackArmed: boolean
}
export interface DeploymentEvent { id: number; occurredAt: string; receivedAt: string; level: 'Info' | 'Warning' | 'Error'; message: string }
export interface DeploymentDetail { deployment: Deployment; events: DeploymentEvent[] }
export interface Thresholds { errorRateMaxPct: number | null; latencyP95MaxMs: number | null; availabilityMinPct: number | null; forSeconds: number }
export interface SloTargets { availabilityPct: number | null; latencyP95Ms: number | null; errorRatePct: number | null }
export interface Breach {
  id: string; series: string; metric: string; thresholdValue: number; forSeconds: number; startedAt: string; confirmedAt: string | null; resolvedAt: string | null
  peakValue: number; lastValue: number; status: 'Watching' | 'Breached' | 'Resolved'
}
export interface CanaryCheck { metric: string; value: number | null; threshold: number | null; passed: boolean | null; breachId: string | null }
export interface Canary {
  strategy: string; isCanary: boolean; state: string; steps: number[]; currentStep: number; stepCount: number; trafficPercent: number; nextTrafficPercent: number | null
  canaryStartedAt: string | null; observationWindowSeconds: number; observedSeconds: number; windowComplete: boolean; nextPromotionAt: string | null
  result: 'Pass' | 'Fail' | 'Unknown'; atRisk: boolean; readyToPromote: boolean; lastSampleAt: string | null; checks: CanaryCheck[]; autoRollbackArmed: boolean
}
export interface RollbackStatus { armed: boolean; armedAt: string | null; webhookConfigured: boolean; signingSecretSet: boolean; canArm: boolean; whyNot: string | null }
export interface ControlAction {
  id: string; action: 'Pause' | 'Resume' | 'Promote' | 'Rollback'; source: string; requestedByName: string | null; requestedAt: string; reason: string | null
  status: 'Pending' | 'Succeeded' | 'Failed'; httpStatus: number | null; error: string | null; completedAt: string | null
}
export interface LatestSample { series: string; at: string; errorRatePct: number | null; latencyP95Ms: number | null; availabilityPct: number | null; requestCount: number | null }
export interface SloMetric { objective: number | null; measured: number | null; meets: boolean | null }
export interface SloStatus { days: number; samples: number; availability: SloMetric; latencyP95: SloMetric; errorRate: SloMetric }
export interface Monitoring {
  deployment: Deployment; thresholds: Thresholds; slo: SloTargets; canary: Canary; breaches: Breach[]; rollback: RollbackStatus
  actions: ControlAction[]; latest: LatestSample[]; sloStatus: SloStatus | null
}
export interface MetricPoint { at: string; errorRatePct: number | null; latencyP95Ms: number | null; availabilityPct: number | null; requestCount: number | null }
export interface Metrics {
  canaryStartedAt: string | null; thresholds: Thresholds; from: string; to: string; lastSampleAt: string | null; truncated: boolean
  series: { series: 'Canary' | 'Stable'; points: MetricPoint[] }[]
}

export interface DeploymentQuery { environmentId?: string; serviceId?: string; state?: string; take?: number }
export const getDeployments = (q: DeploymentQuery = {}) => {
  const p = new URLSearchParams()
  for (const [k, v] of Object.entries(q)) if (v !== undefined && v !== '') p.set(k, String(v))
  return get<Deployment[]>(`/api/v1/deployments?${p}`)
}
export const getTrainDeployments = (trainId: string) => get<Deployment[]>(`/api/v1/trains/${encodeURIComponent(trainId)}/deployments`)
export const getDeployment = (id: string, events = 200) => get<DeploymentDetail>(`/api/v1/deployments/${encodeURIComponent(id)}?events=${events}`)
export const getMonitoring = (id: string) => get<Monitoring>(`/api/v1/deployments/${encodeURIComponent(id)}/monitoring`)
export const getMetrics = (id: string, from: string) => get<Metrics>(`/api/v1/deployments/${encodeURIComponent(id)}/metrics?from=${encodeURIComponent(from)}&maxPoints=240`)

/** Pause, Resume, Promote and Rollback: one confirmed, audited call to the environment's control webhook (REOS-112). The row's version is the If-Match. */
export const control = (id: string, action: 'pause' | 'resume' | 'rollback', version: number, reason?: string) =>
  post<ControlAction>(`/api/v1/deployments/${encodeURIComponent(id)}:${action}`, { confirm: true, reason: reason?.trim() || undefined }, version)
