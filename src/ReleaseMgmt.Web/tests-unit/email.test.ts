import assert from 'node:assert/strict'
import test from 'node:test'
import { CATEGORY_HELP, MODE_WORDS, credentialWord, digestNote, messageTone, messageWord, modeOptions, prefOf, providerText, retryText, REASON_WORDS, STATUS_FILTER, type EmailMessage, type Preference } from '../src/email/emailLogic.ts'

// REOS-167: the pure parts of the email screens.
const pref = (o: Partial<Preference> = {}): Preference => ({ category: 'ApprovalRequested', label: 'Approval requested', channel: 'Email', mode: 'Immediate', defaultMode: 'Immediate', isDefault: true, version: 0, ...o })
const msg = (o: Partial<EmailMessage> = {}): EmailMessage => ({ id: 'm1', to: 'a@example.com', category: 'ApprovalRequested', categoryLabel: 'Approval requested', subject: 's', status: 'Pending', attemptCount: 0,
  nextAttemptAt: null, lastAttemptAt: null, lastError: null, provider: null, sentAt: null, bouncedAt: null, createdAt: '2026-11-27T12:00:00Z', version: 1, ...o })

test('email can be off, immediate or in the daily digest; the team webhook cannot be summarised daily (the same rule as the table CHECK)', () => {
  assert.deepEqual(modeOptions('Email').map(o => o.value), ['Off', 'Immediate', 'Daily'])
  assert.deepEqual(modeOptions('Webhook').map(o => o.value), ['Off', 'Immediate'])
  assert.deepEqual(modeOptions('Email').map(o => o.label), ['Off', 'Immediately', 'In the daily digest'])
  assert.equal(MODE_WORDS.Daily, 'In the daily digest')
})

test('every kind has a one-line help text and a preference is found by kind and channel', () => {
  for (const k of ['ApprovalRequested', 'DecisionMade', 'EscalationLevel1', 'EscalationLevel2']) assert.ok(CATEGORY_HELP[k].length > 10, k)
  const prefs = [pref(), pref({ channel: 'Webhook', mode: 'Off' }), pref({ category: 'DecisionMade' })]
  assert.equal(prefOf(prefs, 'ApprovalRequested', 'Webhook')?.mode, 'Off')
  assert.equal(prefOf(prefs, 'DecisionMade', 'Email')?.category, 'DecisionMade')
  assert.equal(prefOf(prefs, 'EscalationLevel1', 'Email'), undefined)
})

test('the digest note names the hour in UTC with two digits', () => {
  assert.match(digestNote(7), /after 07:00 UTC/)
  assert.match(digestNote(18), /after 18:00 UTC/)
})

test('a message state is a word first: a pending message that has failed is Retrying, a dead letter is Gave up; the tone only repeats it', () => {
  assert.equal(messageWord(msg()), 'Pending')
  assert.equal(messageWord(msg({ attemptCount: 2 })), 'Retrying')
  assert.equal(messageWord(msg({ status: 'DeadLetter', attemptCount: 6 })), 'Gave up')
  assert.equal(messageWord(msg({ status: 'Sent' })), 'Sent')
  assert.equal(messageTone('Sent'), 'success')
  assert.equal(messageTone('Bounced'), 'danger')
  assert.equal(messageTone('DeadLetter'), 'danger')
  assert.equal(messageTone('Suppressed'), 'neutral')
  assert.equal(messageTone('Pending'), 'warning')
  assert.deepEqual(STATUS_FILTER.map(f => f.value), ['', 'Pending', 'Sent', 'Bounced', 'DeadLetter', 'Suppressed'])
})

test('the attempt text says how far a message got', () => {
  assert.equal(retryText(msg(), 6), 'Not tried yet')
  assert.equal(retryText(msg({ attemptCount: 2 }), 6), 'Attempt 2 of 6 failed; trying again')
  assert.equal(retryText(msg({ status: 'Sent', attemptCount: 1 }), 6), '1 attempt')
  assert.equal(retryText(msg({ status: 'Suppressed' }), 6), 'Not tried')
  assert.equal(retryText(msg({ status: 'DeadLetter', attemptCount: 6 }), 6), '6 attempts')
})

test('the credential is Not needed when the configuration has no user name, else Stored or Not stored in words', () => {
  assert.deepEqual(credentialWord({ needsCredential: false, hasCredential: false }), { word: 'Not needed', tone: 'neutral' })
  assert.deepEqual(credentialWord({ needsCredential: true, hasCredential: true }), { word: 'Stored', tone: 'success' })
  assert.deepEqual(credentialWord({ needsCredential: true, hasCredential: false }), { word: 'Not stored', tone: 'danger' })
})

test('the provider is described without a password, and every suppression reason has words', () => {
  assert.equal(providerText({ provider: 'Smtp', smtpHost: 'smtp.example.com', smtpPort: 587, smtpSecurity: 'StartTls', smtpUserName: 'mailer' }), 'SMTP, smtp.example.com:587, STARTTLS, user mailer')
  assert.equal(providerText({ provider: 'Smtp', smtpHost: 'relay', smtpPort: 25, smtpSecurity: 'None', smtpUserName: null }), 'SMTP, relay:25, no encryption')
  assert.equal(providerText({ provider: 'SendGrid', smtpHost: null, smtpPort: null, smtpSecurity: null, smtpUserName: null }), 'SendGrid')
  assert.deepEqual(Object.keys(REASON_WORDS).sort(), ['Bounce', 'Complaint', 'Manual'])
})
