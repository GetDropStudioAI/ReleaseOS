// REOS-185 (D57, D58): the AI allowance panel's words. Money arrives as micro-dollars and is shown as credits (a cent each); state is a word, never colour alone.
import assert from 'node:assert/strict'
import { test } from 'node:test'
import { capsText, chatLine, creditsText, creditsUnit, featureKind, ledgerTone, ledgerWho, ledgerWord, levelTone, levelWord, percentText, periodText, refusalWord, tokensText, usageNotice, usdText, type AiLedgerEntry, type AiUsage } from '../src/mgmt/aiUsageLogic.ts'
import { outcomeWord } from '../src/ai/aiInventoryLogic.ts'

const usage = (over: Partial<AiUsage> = {}): AiUsage => ({
  periodStart: '2026-11-01', periodEnd: '2026-12-01', resetsOn: '1 December 2026 (UTC)', includedMicroUsd: 3_000_000, packMicroUsd: 0, totalMicroUsd: 3_000_000, usedMicroUsd: 0, reservedMicroUsd: 0,
  remainingMicroUsd: 3_000_000, percentUsed: 0, softCapPercent: 80, chatSharePercent: 50, chatUsedMicroUsd: 0, chatLimitMicroUsd: 1_500_000, chatAvailable: true, userPerMinute: 10, userPerHour: 100,
  level: 'Ok', message: null, microUsdPerCredit: 10_000, features: [], users: [], packs: [], opened: true, ...over,
})
const entry = (over: Partial<AiLedgerEntry> = {}): AiLedgerEntry => ({
  id: 'l1', feature: 'chat', featureName: 'Question about release data', class: 'Chat', model: 'm', batch: false, state: 'Settled', refusalReason: null, reservedMicroUsd: 1902, costMicroUsd: 2000,
  inputTokens: 100, outputTokens: 180, cacheReadTokens: 0, cacheWriteTokens: 0, requestedBy: { id: 'u', name: 'Dev' }, reservedAt: '2026-11-26T10:00:00Z', settledAt: '2026-11-26T10:00:01Z', aiActionId: 'a1', ...over,
})

test('credits are cents: whole numbers stay whole, fractions keep two decimals, and one is singular', () => {
  assert.equal(creditsText(3_000_000, 10_000), '300')
  assert.equal(creditsText(53_200, 10_000), '5.32')
  assert.equal(creditsText(2_000, 10_000), '0.2')
  assert.equal(creditsText(0, 10_000), '0')
  assert.equal(creditsText(12_345_000_000, 10_000), '1,234,500')
  assert.equal(creditsUnit(10_000, 10_000), 'credit')
  assert.equal(creditsUnit(20_000, 10_000), 'credits')
})

test('dollars are shown for the person who pays the provider: four decimals under a dollar, two above', () => {
  assert.equal(usdText(3_400), '$0.0034')
  assert.equal(usdText(3_000_000), '$3.00')
  assert.equal(usdText(0), '$0.0000')
})

test('the level is a word and a tone, and a notice says what is stopped, until when, and what to do', () => {
  assert.deepEqual([levelWord('Ok'), levelWord('Soft'), levelWord('Hard')], ['Within allowance', 'Chat paused', 'Allowance used up'])
  assert.deepEqual([levelTone('Ok'), levelTone('Soft'), levelTone('Hard')], ['success', 'warning', 'danger'])
  assert.equal(usageNotice(usage()), null)
  assert.deepEqual(usageNotice(usage({ level: 'Soft', message: 'AI chat is paused: ...' })), { tone: 'warning', title: 'AI chat is paused', text: 'AI chat is paused: ...' })
  assert.deepEqual(usageNotice(usage({ level: 'Hard', message: 'The AI allowance ... used up' })), { tone: 'danger', title: 'The AI allowance is used up', text: 'The AI allowance ... used up' })
})

test('the caps are explained in words so nothing surprises anyone', () => {
  const t = capsText(usage())
  assert.match(t, /Chat is capped first: it stops at 50% of the allowance, or at the 80% soft cap/)
  assert.match(t, /Drafts and summaries go on until the allowance is used up \(100%\)/)
  assert.match(t, /10 times a minute and 100 times an hour/)
  assert.doesNotMatch(capsText(usage({ userPerMinute: 0, userPerHour: 0 })), /times a/)
  assert.match(capsText(usage({ chatSharePercent: 0 })), /Chat is not included/)
  assert.equal(percentText(usage({ percentUsed: 62 })), '62% used')
})

test('chat and feature lines say where chat stands', () => {
  assert.match(chatLine(usage({ chatUsedMicroUsd: 500_000 })), /^Available: 50 of 150 used/)
  assert.equal(chatLine(usage({ chatAvailable: false })), 'Paused until the allowance renews')
  assert.equal(chatLine(usage({ chatSharePercent: 0 })), 'Not included')
  assert.equal(featureKind({ feature: 'chat', name: 'c', cappedFirst: true, calls: 0, refused: 0, costMicroUsd: 0, inputTokens: 0, outputTokens: 0 }), 'Chat: capped first')
  assert.equal(featureKind({ feature: 'risk-score', name: 'r', cappedFirst: false, calls: 0, refused: 0, costMicroUsd: 0, inputTokens: 0, outputTokens: 0 }), 'Drafts and summaries')
})

test('every ledger entry has a word for what happened and who asked', () => {
  assert.equal(ledgerWord(entry()), 'Charged')
  assert.equal(ledgerWord(entry({ state: 'Reserved' })), 'In progress')
  assert.equal(ledgerWord(entry({ state: 'Released' })), 'Not charged (call failed)')
  assert.equal(ledgerWord(entry({ state: 'Refused', refusalReason: 'HardCap' })), 'Refused: allowance used up')
  assert.equal(ledgerWord(entry({ state: 'Refused', refusalReason: 'ChatCap' })), 'Refused: chat is capped first')
  assert.equal(ledgerWord(entry({ state: 'Refused', refusalReason: 'RateLimit' })), 'Refused: asked too often')
  assert.equal(refusalWord(null), 'Refused')
  assert.equal(ledgerTone(entry()), 'success')
  assert.equal(ledgerTone(entry({ state: 'Refused', refusalReason: 'HardCap' })), 'warning')
  assert.equal(ledgerWho(entry({ requestedBy: null })), 'The system')
  assert.equal(tokensText(1200, 30), '1,200 in, 30 out')
  assert.equal(periodText(usage()), 'November 2026')
})

test('the audit trail names the allowance when it was the allowance that refused, not a data rule', () => {
  assert.equal(outcomeWord('Blocked'), 'Refused: data rule')
  assert.equal(outcomeWord('Blocked', 'HardCap'), 'Refused: allowance used up')
  assert.equal(outcomeWord('Blocked', 'RateLimit'), 'Refused: asked too often')
  assert.equal(outcomeWord('Completed', null), 'Draft produced')
})
