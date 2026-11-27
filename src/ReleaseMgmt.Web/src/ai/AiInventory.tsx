import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError, type Me } from '../api.ts'
import { announce } from '../announce.tsx'
import { errMsg } from '../format.ts'
import { useAction } from '../useAction.ts'
import { isConflict } from '../Conflict.tsx'
import { canAudit } from '../shell/nav.ts'
import { Button, ConfirmDialog, ConflictNotice, DataTable, EmptyState, ErrorState, LoadingState, MetricCard, NoPermissionState, Notice, PageHeading, Panel, PropertyList, ScrollRegion, SidePanel, StatusChip, Time, type Column } from '../ui'
import { Field, Problem } from '../mgmt/forms.tsx'
import { callLabel, draftingLabel, globalLabel, keyLabel, moduleLabel, optOutLabel, pausedLabel, smallLabel } from '../mgmt/aiLogic.ts'
import '../mgmt/mgmt.css'
import { decideAction, getAction, getInventory, listActions } from './aiInventoryApi.ts'
import {
  canDecide, decisionTone, decisionWord, featureReason, featureState, killSwitchSummary, outcomeTone, outcomeWord, shortHash, subjectText, tokenText,
  type AiActionSummary, type AiActionView, type AiInventory, type AiInventoryFeature, type AiInventoryModel,
} from './aiInventoryLogic.ts'

/**
 * AI inventory (REOS-184, D58, Management group): what AI the workspace has and what it did, for RTEs, Release Managers and Governance Officers (the API's AuditRead policy). Four read-only facts
 * (the kill switches, the features and whether each can run now, the models used, the data that is sent) and the audit trail of AI-assisted actions. Opening an action shows the draft, the human edit,
 * the hashes and whether the stored texts still match the audit rows the chain covers. A pending draft can be decided here by the person who asked for it or an administrator: Accept, Edit and accept,
 * or Reject (behind a confirmation). The decision is final and carries the row's Version as If-Match. No checkboxes, no toggle switches (rule 9); status is always a word.
 */
const WHO = 'Release Train Engineers, Release Managers and Governance Officers'
const SHOWN = 100

export function AiInventoryPage({ me }: { me: Me }) {
  if (!canAudit(me.roles)) return <div className="os-page"><PageHeading trail={['Management', 'AI inventory']} title="AI inventory" /><NoPermissionState what="see the AI inventory" who={WHO} /></div>
  return <Screen me={me} />
}

function Screen({ me }: { me: Me }) {
  const [inv, setInv] = useState<AiInventory | null>(null)
  const [actions, setActions] = useState<AiActionSummary[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [openId, setOpenId] = useState<string | null>(null)
  const [filter, setFilter] = useState<'all' | 'Pending'>('all')

  const reload = useCallback(() => {
    getInventory().then(i => { setInv(i); setError(null) }).catch(e => setError(errMsg(e, 'Could not load the AI inventory.')))
    listActions({ limit: SHOWN, decision: filter === 'Pending' ? 'Pending' : undefined }).then(setActions).catch(e => setError(errMsg(e, 'Could not load the AI actions.')))
  }, [filter])
  useEffect(() => { reload() }, [reload])

  if (error && !inv) return <div className="os-page"><PageHeading trail={['Management', 'AI inventory']} title="AI inventory" /><ErrorState what="the AI inventory" message={error} onRetry={reload} /></div>
  if (!inv) return <div className="os-page"><PageHeading trail={['Management', 'AI inventory']} title="AI inventory" /><LoadingState what="the AI inventory" /></div>
  const c = inv.controls
  return (
    <div className="os-page">
      <PageHeading trail={['Management', 'AI inventory']} title="AI inventory" description="Advisory only (D58): AI suggests, a person decides, and nothing AI produced can change a status. Every AI-assisted action is on the audit trail below." />
      <div className="mg-stack">
        <Notice tone={c.canCall ? 'success' : 'neutral'} title={c.canCall ? 'AI calls are allowed' : 'AI calls are not made'} live>{killSwitchSummary(c)}</Notice>
        <div className="os-metrics" role="group" aria-label="AI use">
          <MetricCard label="Actions on record" value={inv.decisions.total} detail={`newest ${inv.actionsConsidered}`} />
          <MetricCard label="Awaiting decision" value={inv.decisions.pending} />
          <MetricCard label="Accepted" value={inv.decisions.accepted} />
          <MetricCard label="Edited" value={inv.decisions.edited} />
          <MetricCard label="Rejected" value={inv.decisions.rejected} />
          <MetricCard label="Refused" value={inv.decisions.refused} />
        </div>
        <Panel title="Kill switches" subtitle="Any one of these stops every AI call">
          <PropertyList items={[
            { label: 'Module', value: moduleLabel(c) }, { label: 'Global kill switch', value: globalLabel(c) }, { label: 'This workspace', value: pausedLabel(c) }, { label: 'Data use', value: optOutLabel(c) },
            { label: 'Provider', value: c.provider }, { label: 'Provider key', value: keyLabel(c) }, { label: 'Calls', value: callLabel(c) },
          ]} />
        </Panel>
        <Panel title="Features" subtitle="What is switched on, which model it uses, and what happened">
          <ScrollRegion label="Features">
            <DataTable<AiInventoryFeature> caption="AI features" rows={inv.features} rowKey={f => f.key} columns={featureColumns} />
          </ScrollRegion>
        </Panel>
        <Panel title="Models used" subtitle={`Small model ${smallLabel(c)}; drafting model ${draftingLabel(c)}`}>
          <ScrollRegion label="Models used">
            <DataTable<AiInventoryModel> caption="Models used" rows={inv.models} rowKey={m => `${m.provider}/${m.model}`} columns={modelColumns} empty={<EmptyState title="No model has been called yet">Once a feature calls a model, it is listed here with the tokens it used.</EmptyState>} />
          </ScrollRegion>
        </Panel>
        <Panel title="Data sent to a model" subtitle="The same rules for every feature">
          <div className="mg-stack">
            <PropertyList items={[
              { label: 'Sent', value: <List items={inv.data.sent} /> },
              { label: 'Never sent', value: <List items={inv.data.neverSent} /> },
              { label: 'Removed before sending', value: <List items={inv.data.redacted} /> },
              { label: 'Largest prompt', value: `${inv.data.maxInputChars.toLocaleString('en-US')} characters` },
              { label: 'What the trail keeps', value: 'Hashes and sizes of what was sent, never the text' },
            ]} />
            {inv.features.some(f => f.inputLabels.length > 0) && (
              <ul className="mg-note">{inv.features.filter(f => f.inputLabels.length > 0).map(f => <li key={f.key}>{f.name} has sent: {f.inputLabels.join(', ')}</li>)}</ul>
            )}
          </div>
        </Panel>
        <div className="mg-stack">
          <Panel title="Audit trail" subtitle="Every AI-assisted action, newest first"
            meta={<Button disabled={filter === 'all'} onClick={() => setFilter('all')}>All actions</Button>}
            footer={<Button disabled={filter === 'Pending'} onClick={() => setFilter('Pending')}>Only awaiting decision</Button>}>
            <ScrollRegion label="Audit trail">
              <DataTable<AiActionSummary> caption="AI actions" rows={actions ?? []} rowKey={a => a.id} selectedKey={openId} onSelect={a => setOpenId(a.id)} columns={actionColumns}
                empty={<EmptyState title={filter === 'Pending' ? 'No draft is waiting for a decision' : 'No AI action has been recorded yet'}>Each call to the AI platform, and each refusal, is recorded here.</EmptyState>} />
            </ScrollRegion>
          </Panel>
          {openId && <ActionPanel id={openId} me={me} onClose={() => setOpenId(null)} onChanged={reload} />}
        </div>
      </div>
    </div>
  )
}

const List = ({ items }: { items: string[] }) => <ul className="mg-list">{items.map(i => <li key={i}>{i}</li>)}</ul>

const featureColumns: Column<AiInventoryFeature>[] = [
  { key: 'name', header: 'Feature', cell: f => f.name },
  { key: 'model', header: 'Model', cell: f => f.model ?? 'Not configured', mono: true },
  { key: 'state', header: 'Status', cell: f => <><StatusChip tone={f.enabled ? 'success' : 'neutral'}>{featureState(f)}</StatusChip> <span className="mg-note">{featureReason(f)}</span></> },
  { key: 'calls', header: 'Calls', cell: f => f.calls, align: 'end' },
  { key: 'accepted', header: 'Accepted', cell: f => f.accepted, align: 'end' },
  { key: 'edited', header: 'Edited', cell: f => f.edited, align: 'end' },
  { key: 'rejected', header: 'Rejected', cell: f => f.rejected, align: 'end' },
  { key: 'pending', header: 'Awaiting', cell: f => f.pending, align: 'end' },
  { key: 'prompt', header: 'Prompt versions', cell: f => (f.promptTemplateVersions.length ? f.promptTemplateVersions.join(', ') : 'None yet'), mono: true },
  { key: 'last', header: 'Last used', cell: f => (f.lastUsedAt ? <Time at={f.lastUsedAt} date /> : 'Never') },
]
const modelColumns: Column<AiInventoryModel>[] = [
  { key: 'model', header: 'Model', cell: m => m.model, mono: true },
  { key: 'provider', header: 'Provider', cell: m => m.provider },
  { key: 'served', header: 'Versions reported', cell: m => (m.servedVersions.length ? m.servedVersions.join(', ') : 'Same as the name'), mono: true },
  { key: 'calls', header: 'Calls', cell: m => m.calls, align: 'end' },
  { key: 'tokens', header: 'Tokens', cell: m => tokenText(m.inputTokens, m.outputTokens), align: 'end' },
  { key: 'features', header: 'Used by', cell: m => m.features.join(', ') },
  { key: 'last', header: 'Last used', cell: m => (m.lastUsedAt ? <Time at={m.lastUsedAt} date /> : 'Never') },
]
const actionColumns: Column<AiActionSummary>[] = [
  { key: 'at', header: 'When', cell: a => <Time at={a.requestedAt} date /> },
  { key: 'feature', header: 'Feature', cell: a => a.featureName },
  { key: 'outcome', header: 'Result', cell: a => <StatusChip tone={outcomeTone(a.outcome)}>{outcomeWord(a.outcome, a.capReason)}</StatusChip> },
  { key: 'decision', header: 'Human decision', cell: a => <StatusChip tone={decisionTone(a.decision)}>{decisionWord(a.decision)}</StatusChip> },
  { key: 'who', header: 'Asked by', cell: a => a.requestedBy?.name ?? 'The system' },
  { key: 'model', header: 'Model', cell: a => a.model ?? 'None', mono: true },
]

function ActionPanel({ id, me, onClose, onChanged }: { id: string; me: Me; onClose: () => void; onChanged: () => void }) {
  const [a, setA] = useState<AiActionView | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [conflict, setConflict] = useState<ApiError | null>(null)
  const [editing, setEditing] = useState(false)
  const [text, setText] = useState('')
  const [rejecting, setRejecting] = useState(false)
  const [note, setNote] = useState('')
  const { run, pending } = useAction()
  const title = useRef<HTMLHeadingElement>(null)

  const load = useCallback(() => { setA(null); getAction(id).then(x => { setA(x); setError(null); setText(x.outputText ?? '') }).catch(e => setError(errMsg(e, 'Could not load the action.'))) }, [id])
  useEffect(() => { load(); setEditing(false); setRejecting(false); setProblem(null); setConflict(null) }, [load])
  useEffect(() => { if (a) title.current?.focus() }, [a?.id])   // eslint-disable-line react-hooks/exhaustive-deps

  const decide = (decision: 'Accepted' | 'Edited' | 'Rejected') => run('Saving…', async () => {
    if (!a) return
    setProblem(null); setConflict(null)
    try {
      const next = await decideAction(a.id, { decision, finalText: decision === 'Edited' ? text : undefined, note: note.trim() || undefined }, a.version)
      setA(next); setEditing(false); setRejecting(false); announce(`The draft was ${decisionWord(next.decision).toLowerCase()}`); onChanged()
    } catch (e) {
      if (isConflict(e)) setConflict(e as ApiError); else setProblem(errMsg(e))
    }
  })

  if (error) return <SidePanel title="AI action" closeLabel="Close the AI action" onClose={onClose}><ErrorState what="the AI action" message={error} onRetry={load} /></SidePanel>
  if (!a) return <SidePanel title="AI action" closeLabel="Close the AI action" onClose={onClose}><LoadingState what="the AI action" /></SidePanel>
  const decidable = canDecide(a, me)
  return (
    <>
      <SidePanel title={a.featureName} titleRef={title} subtitle={<>Asked <Time at={a.requestedAt} date /> by {a.requestedBy?.name ?? 'the system'}</>} closeLabel="Close the AI action" onClose={onClose}
        meta={<StatusChip tone={decisionTone(a.decision)}>{decisionWord(a.decision)}</StatusChip>}>
        <div className="mg-stack">
          {!a.integrity.ok && <Notice tone="danger" title="This record does not match its audit rows" live>The stored text or its hash differs from what the audit chain holds. Treat the draft as untrusted and tell the audit owner.</Notice>}
          {a.outputText === null
            ? <Notice tone="neutral" title={outcomeWord(a.outcome, a.capReason)}>{a.detail ?? 'No draft was produced.'}</Notice>
            : <Field label="The draft, as the model returned it"><textarea className="mg-input" readOnly rows={7} value={a.outputText} aria-label="Draft" /></Field>}
          {a.finalText !== null && <Field label="The human edit, as it was used"><textarea className="mg-input" readOnly rows={7} value={a.finalText} aria-label="Human edit" /></Field>}
          {a.decisionNote && <p className="mg-note">Note: {a.decisionNote}</p>}
          {a.decidedBy && a.decidedAt && <p className="mg-note">{decisionWord(a.decision)} by {a.decidedBy.name} at <Time at={a.decidedAt} date />. The decision is final.</p>}
          {conflict && <ConflictNotice onUseTheirs={load} />}
          <Problem message={problem} />
          {decidable && !editing && (
            <div className="mg-actions">
              <Button variant="primary" disabled={!!pending} onClick={() => decide('Accepted')}>Accept draft</Button>
              <Button disabled={!!pending} onClick={() => setEditing(true)}>Edit and accept</Button>
              <Button disabled={!!pending} onClick={() => setRejecting(true)}>Reject draft</Button>
            </div>
          )}
          {decidable && editing && (
            <div className="mg-stack">
              <Field label="Your edit" hint="This replaces the draft where it is used. Both are kept on the audit trail."><textarea className="mg-input" rows={7} value={text} onChange={e => setText(e.target.value)} /></Field>
              <div className="mg-actions">
                <Button variant="primary" disabled={!!pending || !text.trim() || text === a.outputText} onClick={() => decide('Edited')}>Save edit and accept</Button>
                <Button disabled={!!pending} onClick={() => { setEditing(false); setText(a.outputText ?? '') }}>Cancel edit</Button>
              </div>
            </div>
          )}
          {!decidable && a.decision === 'Pending' && <p className="mg-note">Only the person who asked for this draft, or an RTE or Release Manager, can record the decision.</p>}
          <PropertyList items={[
            { label: 'Result', value: outcomeWord(a.outcome, a.capReason) },
            { label: 'Model', value: a.model ?? 'None', mono: true },
            { label: 'Version reported', value: a.servedModel ?? 'Same as the model', mono: true },
            { label: 'Provider', value: a.provider },
            { label: 'Prompt version', value: a.promptTemplateVersion, mono: true },
            { label: 'About', value: subjectText(a) },
            { label: 'Tokens', value: tokenText(a.inputTokens, a.outputTokens) },
            { label: 'Redactions', value: String(a.redactions) },
            { label: 'Prompt hash', value: shortHash(a.promptHash), mono: true },
            { label: 'Input hash', value: shortHash(a.inputHash), mono: true },
            { label: 'Draft hash', value: shortHash(a.outputHash), mono: true },
            { label: 'Edit hash', value: shortHash(a.finalHash), mono: true },
            { label: 'Matches the audit chain', value: a.integrity.ok ? 'Yes' : 'No' },
          ]} />
          {a.inputs.length > 0 && (
            <ScrollRegion label="Inputs">
              <DataTable<AiActionView['inputs'][number]> caption="Inputs (hashes only)" rows={a.inputs} rowKey={i => `${i.label}-${i.sha256}`}
                columns={[{ key: 'label', header: 'Input', cell: i => i.label }, { key: 'class', header: 'Class', cell: i => i.class }, { key: 'chars', header: 'Characters', cell: i => i.chars, align: 'end' }, { key: 'hash', header: 'Hash', cell: i => shortHash(i.sha256), mono: true }]} />
            </ScrollRegion>
          )}
        </div>
      </SidePanel>
      <ConfirmDialog open={rejecting} title="Reject this draft?" confirmLabel="Reject draft" pendingLabel="Saving…" destructive pending={!!pending} onCancel={() => setRejecting(false)} onConfirm={() => decide('Rejected')}>
        <p>The draft is thrown away and the decision is final. It stays on the audit trail.</p>
        <Field label="Why (optional)"><textarea className="mg-input" rows={3} value={note} onChange={e => setNote(e.target.value)} maxLength={1000} /></Field>
      </ConfirmDialog>
    </>
  )
}
