import type { Tone } from '../ui/tone.ts'

/**
 * The pure parts of the email screens (REOS-167): the shapes the API returns, the words of each choice and each message state, and the text the screens show. Tested without a browser.
 * Selects and buttons only (rule 9): a choice is a native select, never a checkbox or a switch. The inbox is always on, so it is shown as fact, not as a choice.
 */
export type Channel = 'Email' | 'Webhook'
export interface Preference { category: string; label: string; channel: Channel; mode: 'Off' | 'Immediate' | 'Daily'; defaultMode: string; isDefault: boolean; version: number }
export interface PreferencesResponse {
  emailEnabled: boolean; digestHourUtc: number; categories: { key: string; label: string }[]; channels: string[]; modes: { key: string; label: string }[]; preferences: Preference[]
}

/** What each kind of notification is, in one line, so a person knows what they are choosing. */
export const CATEGORY_HELP: Record<string, string> = {
  ApprovalRequested: 'An approval is waiting for your decision.',
  DecisionMade: 'A decision was made on an approval you requested.',
  EscalationLevel1: 'Something you own is late and has been escalated once.',
  EscalationLevel2: 'Something you own is still late and has been escalated again.',
}

export const MODE_WORDS: Record<string, string> = { Off: 'Off', Immediate: 'Immediately', Daily: 'In the daily digest' }

/** The options of one channel's select: email can be summarised once a day, the team webhook cannot. */
export const modeOptions = (channel: Channel): { value: string; label: string }[] =>
  (channel === 'Email' ? ['Off', 'Immediate', 'Daily'] : ['Off', 'Immediate']).map(v => ({ value: v, label: MODE_WORDS[v] }))

export const prefOf = (prefs: Preference[], category: string, channel: Channel): Preference | undefined => prefs.find(p => p.category === category && p.channel === channel)

export const digestNote = (hourUtc: number): string => `The daily digest is sent once a day, after ${String(hourUtc).padStart(2, '0')}:00 UTC, and only when there is something in it.`

export const WEBHOOK_NOTE = 'The team webhook is set up per team under Connectors, so this choice is saved but does not yet change what a team webhook posts.'

export const OFF_NOTICE = 'Email delivery is off on this host, so no email is sent whatever you choose here. Your choices are saved and apply when an administrator turns it on.'

// ---- the administrator's view ----

export interface EmailCounts { pending: number; sentLast7Days: number; bouncedLast7Days: number; deadLettersLast7Days: number; suppressed: number }
export interface EmailStatus {
  enabled: boolean; provider: string; fromAddress: string; fromName: string | null; smtpHost: string | null; smtpPort: number | null; smtpSecurity: string | null; smtpUserName: string | null
  needsCredential: boolean; hasCredential: boolean; hasEventsSecret: boolean; maxAttempts: number; retentionDays: number; digestHourUtc: number; publicBaseUrlSet: boolean; counts: EmailCounts
}
export interface EmailMessage {
  id: string; to: string; category: string; categoryLabel: string; subject: string; status: MessageStatus; attemptCount: number; nextAttemptAt: string | null; lastAttemptAt: string | null
  lastError: string | null; provider: string | null; sentAt: string | null; bouncedAt: string | null; createdAt: string; version: number
}
export interface Suppression { id: string; address: string; reason: 'Bounce' | 'Complaint' | 'Manual'; detail: string | null; createdAt: string; createdBy: string | null; version: number }
export type MessageStatus = 'Pending' | 'Sent' | 'Bounced' | 'DeadLetter' | 'Suppressed'
export interface EventsSecretMade { secret: string; signatureHeader: string; timestampHeader: string; idHeader: string; toleranceSeconds: number }

/** The chip: the word says what is true (a pending message that has already failed once is "Retrying"); the tone only repeats it. */
export function messageWord(m: Pick<EmailMessage, 'status' | 'attemptCount'>): string {
  return m.status === 'DeadLetter' ? 'Gave up' : m.status === 'Pending' && m.attemptCount > 0 ? 'Retrying' : m.status
}
export const messageTone = (s: MessageStatus): Tone => (s === 'Sent' ? 'success' : s === 'Bounced' || s === 'DeadLetter' ? 'danger' : s === 'Suppressed' ? 'neutral' : 'warning')

export const STATUS_FILTER = [
  { value: '', label: 'All messages' }, { value: 'Pending', label: 'Pending' }, { value: 'Sent', label: 'Sent' }, { value: 'Bounced', label: 'Bounced' }, { value: 'DeadLetter', label: 'Gave up' }, { value: 'Suppressed', label: 'Suppressed' },
]

export const REASON_WORDS: Record<string, string> = { Bounce: 'Hard bounce', Complaint: 'Spam complaint', Manual: 'Added by hand' }

/** How the credential stands, in words: a configuration that needs none says so rather than showing an empty field. */
export function credentialWord(s: Pick<EmailStatus, 'needsCredential' | 'hasCredential'>): { word: string; tone: Tone } {
  if (!s.needsCredential) return { word: 'Not needed', tone: 'neutral' }
  return s.hasCredential ? { word: 'Stored', tone: 'success' } : { word: 'Not stored', tone: 'danger' }
}

export const providerText = (s: Pick<EmailStatus, 'provider' | 'smtpHost' | 'smtpPort' | 'smtpSecurity' | 'smtpUserName'>): string =>
  s.provider === 'Smtp' ? `SMTP, ${s.smtpHost ?? 'no host'}:${s.smtpPort ?? ''}, ${s.smtpSecurity === 'StartTls' ? 'STARTTLS' : 'no encryption'}${s.smtpUserName ? `, user ${s.smtpUserName}` : ''}` : s.provider

export const OFF_ADMIN = 'Email delivery is off. Set Notifications:Smtp:Enabled to true, name the sender and the provider in Notifications:Email, and restart. Nothing is queued while it is off.'

export const EVENTS_HELP = 'Your email provider can tell ReleaseOS about bounces and spam complaints: POST a JSON event {"type":"bounce"|"complaint","address":"…","messageId":"…","reason":"…"} to /api/v1/email/events, signed like a webhook with this secret.'

export function retryText(m: Pick<EmailMessage, 'status' | 'attemptCount' | 'nextAttemptAt'>, max: number): string {
  if (m.status === 'Pending') return m.attemptCount === 0 ? 'Not tried yet' : `Attempt ${m.attemptCount} of ${max} failed; trying again`
  return m.attemptCount === 0 ? 'Not tried' : `${m.attemptCount} ${m.attemptCount === 1 ? 'attempt' : 'attempts'}`
}

export const deleteImpact = (address: string) => `${address} will be mailed again from the next notification. If it still bounces it is suppressed again. Its "undeliverable address" alert is closed.`
export const rotateImpact = 'The old events secret stops verifying at once: your provider must be given the new one, which is shown once.'
export const removeCredentialImpact = 'Email cannot be sent through this provider until a credential is stored again; messages wait and are retried.'
