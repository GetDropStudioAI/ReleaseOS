import { get, post } from '../api.ts'
import type { AiActionSummary, AiActionView, AiInventory } from './aiInventoryLogic.ts'

/** REOS-184 (D58): the AI inventory and audit trail. The inventory and the list are for an RTE, Release Manager or Governance Officer; one action and its decision are for any signed-in member (the decision itself: the requester or an administrator). */
export const getInventory = () => get<AiInventory>('/api/v1/ai/inventory')
export const listActions = (q: { decision?: string; feature?: string; limit?: number } = {}) => {
  const p = new URLSearchParams()
  if (q.decision) p.set('decision', q.decision)
  if (q.feature) p.set('feature', q.feature)
  if (q.limit) p.set('limit', String(q.limit))
  return get<AiActionSummary[]>(`/api/v1/ai/actions${p.toString() ? `?${p}` : ''}`)
}
export const getAction = (id: string) => get<AiActionView>(`/api/v1/ai/actions/${id}`)
export const decideAction = (id: string, body: { decision: 'Accepted' | 'Edited' | 'Rejected'; finalText?: string; note?: string }, version: number) =>
  post<AiActionView>(`/api/v1/ai/actions/${id}:decide`, body, version)
