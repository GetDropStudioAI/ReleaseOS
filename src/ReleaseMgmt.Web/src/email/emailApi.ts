import { del, get, post, put } from '../api.ts'
import type { EmailMessage, EmailStatus, EventsSecretMade, Preference, PreferencesResponse, Suppression } from './emailLogic.ts'

/** Preferences are the signed-in person's own (any role). Everything under /email is Policies.Admin (RTE and Release Manager); credentials are write-only. */
export const getPreferences = () => get<PreferencesResponse>('/api/v1/me/notification-preferences')
export const setPreference = (b: { category: string; channel: string; mode: string; version: number }) => put<Preference>('/api/v1/me/notification-preferences', b)

export const getStatus = () => get<EmailStatus>('/api/v1/email/status')
export const listMessages = (status?: string, take = 100) => get<{ messages: EmailMessage[] }>(`/api/v1/email/messages?take=${take}${status ? `&status=${encodeURIComponent(status)}` : ''}`)
export const sendTest = () => post<EmailMessage>('/api/v1/email/test', {})
export const setCredential = (value: string) => put<boolean>('/api/v1/email/credential', { value })
export const clearCredential = () => del<boolean>('/api/v1/email/credential')
export const rotateEventsSecret = () => post<EventsSecretMade>('/api/v1/email/events-secret:rotate', {})
export const listSuppressions = () => get<{ suppressions: Suppression[] }>('/api/v1/email/suppressions')
export const addSuppression = (address: string, detail: string) => post<Suppression>('/api/v1/email/suppressions', { address, detail })
export const removeSuppression = (id: string, version: number) => del<Suppression>(`/api/v1/email/suppressions/${id}`, version)
