import { del, get, post, put } from '../api.ts'
import type { Delivery, DeliveryDetail, Subscription, SubscriptionWithSecret, WebhookOverview } from './webhooksLogic.ts'

/** Outbound webhooks (REOS-164, Policies.Admin: RTE and Release Manager). Create and rotate answer with the secret once; nothing here keeps it. Changes take the row's version as If-Match. */
export const getOverview = () => get<WebhookOverview>('/api/v1/webhooks')
export const createSubscription = (b: { name: string; url: string; eventTypes: string[] }) => post<SubscriptionWithSecret>('/api/v1/webhooks', b)
export const updateSubscription = (id: string, b: { name: string; url?: string; eventTypes: string[] }, version: number) => put<Subscription>(`/api/v1/webhooks/${id}`, b, version)
export const pauseSubscription = (id: string, version: number) => post<Subscription>(`/api/v1/webhooks/${id}:pause`, {}, version)
export const resumeSubscription = (id: string, version: number) => post<Subscription>(`/api/v1/webhooks/${id}:resume`, {}, version)
export const rotateSecret = (id: string, version: number) => post<SubscriptionWithSecret>(`/api/v1/webhooks/${id}:rotate-secret`, {}, version)
export const deleteSubscription = (id: string, version: number) => del<Subscription>(`/api/v1/webhooks/${id}`, version)
export const sendTest = (id: string) => post<Delivery>(`/api/v1/webhooks/${id}:test`, {})
export const listDeliveries = (q: { subscriptionId?: string; status?: string; take?: number }) => {
  const p = new URLSearchParams()
  if (q.subscriptionId) p.set('subscriptionId', q.subscriptionId)
  if (q.status) p.set('status', q.status)
  if (q.take) p.set('take', String(q.take))
  return get<{ deliveries: Delivery[] }>(`/api/v1/webhooks/deliveries${p.toString() ? `?${p}` : ''}`)
}
export const getDelivery = (id: string) => get<DeliveryDetail>(`/api/v1/webhooks/deliveries/${id}`)
export const replayDelivery = (id: string) => post<Delivery>(`/api/v1/webhooks/deliveries/${id}:replay`, {})
