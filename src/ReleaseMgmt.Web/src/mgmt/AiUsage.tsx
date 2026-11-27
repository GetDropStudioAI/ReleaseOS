import { useCallback, useEffect, useState } from 'react'
import { errMsg } from '../format.ts'
import { Button, DataTable, EmptyState, ErrorState, LoadingState, MetricCard, Notice, Panel, ProgressBar, ScrollRegion, StatusChip, Time, type Column } from '../ui'
import { getAiLedger, getAiUsage } from './aiUsageApi.ts'
import {
  capsText, chatLine, creditsText, creditsUnit, featureKind, ledgerTone, ledgerWho, ledgerWord, levelTone, levelWord, percentText, periodText, tokensText, usageNotice, usdText,
  type AiFeatureUsage, type AiLedgerEntry, type AiUsage, type AiUserUsage,
} from './aiUsageLogic.ts'
import './mgmt.css'

/**
 * AI allowance and usage (REOS-185; D57, D58), shown under the AI module settings for administrators: what the workspace's month includes, what has been used and what is left, where it went (by feature
 * and by person), the caps in words, and the newest ledger entries. The state is a word and a chip as well as the meter (never colour alone, rule 9); when something is stopped a notice says what, until
 * when, and what an administrator can do. Read-only: nothing here adds credits (credit packs come from billing). Times are UTC with the person's local time alongside (D44).
 */
export function AiUsagePanel({ reloadKey }: { reloadKey?: number }) {
  const [usage, setUsage] = useState<AiUsage | null>(null)
  const [ledger, setLedger] = useState<AiLedgerEntry[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  const reload = useCallback(() => {
    getAiUsage().then(u => { setUsage(u); setError(null) }).catch(e => setError(errMsg(e, 'Could not load the AI allowance.')))
    getAiLedger().then(setLedger).catch(e => setError(errMsg(e, 'Could not load the AI usage ledger.')))
  }, [])
  useEffect(() => { reload() }, [reload, reloadKey])

  if (error && !usage) return <ErrorState what="the AI allowance" message={error} onRetry={reload} />
  if (!usage) return <LoadingState what="the AI allowance" />
  const per = usage.microUsdPerCredit
  const cr = (n: number) => creditsText(n, per)
  const notice = usageNotice(usage)
  return (
    <Panel title="AI allowance and usage" subtitle={`${periodText(usage)} (UTC month). Renews on ${usage.resetsOn}`} meta={<StatusChip tone={levelTone(usage.level)}>{levelWord(usage.level)}</StatusChip>}
      footer={<Button onClick={reload}>Refresh usage</Button>}>
      <div className="mg-stack">
        {notice && <Notice tone={notice.tone} title={notice.title} live>{notice.text}</Notice>}
        <div className="os-metrics" role="group" aria-label="AI allowance this month">
          <MetricCard label="Included" value={cr(usage.includedMicroUsd)} unit={creditsUnit(usage.includedMicroUsd, per)} detail={usage.packMicroUsd > 0 ? `plus ${cr(usage.packMicroUsd)} from credit packs` : 'no credit packs this month'} />
          <MetricCard label="Used" value={cr(usage.usedMicroUsd)} unit={creditsUnit(usage.usedMicroUsd, per)} detail={`${usdText(usage.usedMicroUsd)} at provider prices`} />
          <MetricCard label="Remaining" value={cr(usage.remainingMicroUsd)} unit={creditsUnit(usage.remainingMicroUsd, per)} detail={usage.reservedMicroUsd > 0 ? `${cr(usage.reservedMicroUsd)} held for calls in progress` : percentText(usage)} />
          <MetricCard label="Chat" value={usage.chatSharePercent === 0 ? 'Off' : `${usage.chatSharePercent}%`} detail={chatLine(usage)} />
        </div>
        <ProgressBar value={usage.percentUsed / 100} tone={levelTone(usage.level)} label={`AI allowance used: ${usage.percentUsed} percent`} />
        <p className="mg-note">{percentText(usage)} of {cr(usage.totalMicroUsd)} {creditsUnit(usage.totalMicroUsd, per)}. {capsText(usage)}</p>

        <ScrollRegion label="Usage by feature">
          <DataTable<AiFeatureUsage> caption="Usage by feature this month" rows={usage.features} rowKey={f => f.feature} columns={featureColumns(per)} />
        </ScrollRegion>
        <ScrollRegion label="Usage by person">
          <DataTable<AiUserUsage> caption="Usage by person this month" rows={usage.users} rowKey={u => u.userId} columns={userColumns(per)}
            empty={<EmptyState title="No one has used AI this month">Calls asked for by a person appear here, most expensive first. Scheduled runs are in the ledger as the system.</EmptyState>} />
        </ScrollRegion>
        <ScrollRegion label="Recent usage">
          <DataTable<AiLedgerEntry> caption="Recent AI usage, newest first" rows={ledger ?? []} rowKey={e => e.id} columns={ledgerColumns(per)}
            empty={<EmptyState title="No AI call has asked for allowance yet">Every call, and every call a cap or the rate limit stopped, is recorded here with its cost.</EmptyState>} />
        </ScrollRegion>
        <p className="mg-note">Credit packs add to the month they are bought in and are not editable here; they arrive with billing. The ledger and the audit trail record every call, and a refusal always says why.</p>
      </div>
    </Panel>
  )
}

const featureColumns = (per: number): Column<AiFeatureUsage>[] => [
  { key: 'name', header: 'Feature', cell: f => f.name },
  { key: 'kind', header: 'Capped', cell: f => featureKind(f) },
  { key: 'calls', header: 'Calls', cell: f => f.calls, align: 'end' },
  { key: 'refused', header: 'Refused by a cap', cell: f => f.refused, align: 'end' },
  { key: 'tokens', header: 'Tokens', cell: f => tokensText(f.inputTokens, f.outputTokens), align: 'end' },
  { key: 'credits', header: 'Credits used', cell: f => creditsText(f.costMicroUsd, per), align: 'end' },
]
const userColumns = (per: number): Column<AiUserUsage>[] => [
  { key: 'name', header: 'Person', cell: u => u.name },
  { key: 'calls', header: 'Calls', cell: u => u.calls, align: 'end' },
  { key: 'credits', header: 'Credits used', cell: u => creditsText(u.costMicroUsd, per), align: 'end' },
]
const ledgerColumns = (per: number): Column<AiLedgerEntry>[] => [
  { key: 'at', header: 'When', cell: e => <Time at={e.reservedAt} date /> },
  { key: 'feature', header: 'Feature', cell: e => e.featureName },
  { key: 'who', header: 'Asked by', cell: e => ledgerWho(e) },
  { key: 'status', header: 'Result', cell: e => <StatusChip tone={ledgerTone(e)}>{ledgerWord(e)}</StatusChip> },
  { key: 'model', header: 'Model', cell: e => e.model ?? 'None', mono: true },
  { key: 'credits', header: 'Credits', cell: e => (e.state === 'Settled' ? creditsText(e.costMicroUsd, per) : '0'), align: 'end' },
]
