import { get, patch, post } from '../api.ts'
import type { EnvironmentRow, EnvironmentSlo, SigningKeyResult, WebhookOption } from './envLogic.ts'

/** The Environments destination's calls (REOS-156; server: EnvironmentEndpoints and DeploymentOpsEndpoints). Edits and archive take the row's version as If-Match. */
export const listEnvironments = () => get<EnvironmentRow[]>('/api/v1/environments?includeArchived=true')
export const createEnvironment = (b: { name: string; kind: string; region?: string }) => post<EnvironmentRow>('/api/v1/environments', b)
export const archiveEnvironment = (id: string, archived: boolean, version: number) =>
  post<EnvironmentRow>(`/api/v1/environments/${encodeURIComponent(id)}:${archived ? 'archive' : 'unarchive'}`, {}, version)
/** `webhookId` null removes the control webhook (the server's `clear` list), a string sets it. */
export const setControlWebhook = (id: string, webhookId: string | null, version: number) =>
  patch<EnvironmentRow>(`/api/v1/environments/${encodeURIComponent(id)}`, webhookId === null ? { clear: ['controlWebhookDestinationId'] } : { controlWebhookDestinationId: webhookId }, version)
export const getSlo = (id: string, days: number) => get<EnvironmentSlo>(`/api/v1/environments/${encodeURIComponent(id)}/slo?days=${days}`)
/** The destinations a control webhook can be chosen from: the workspace's webhook allowlist (managed on Sync health). */
export const listWebhooks = () => get<WebhookOption[]>('/api/v1/sync/webhook-allowlist')
/** Creates or replaces the signing key. The response carries the secret once; callers keep it in memory only. */
export const rotateSigningKey = (id: string) => post<SigningKeyResult>(`/api/v1/environments/${encodeURIComponent(id)}/control-webhook:signing-key`, {})
