import { useCallback, useEffect, useId, useMemo, useRef, useState } from 'react'
import { ApiError, type Me } from '../api.ts'
import { announce } from '../announce.tsx'
import { useAction } from '../useAction.ts'
import { errMsg } from '../format.ts'
import { canAdmin } from '../shell/nav.ts'
import { browserZone } from '../shell/clock.ts'
import { timeText } from '../utcTime.ts'
import {
  Button, ConfirmDialog, DataTable, DisabledAction, EmptyState, ErrorState, FilterSelect, LoadingState, Notice, PageHeading, Panel, PropertyList, ReadOnlyNotice,
  SearchField, StatusChip, Time, TwoLine, type Column,
} from '../ui'
import { Field, Problem, SelectInput, TextInput } from '../mgmt/forms.tsx'
import { archiveEnvironment, createEnvironment, getSlo, listEnvironments, listWebhooks, rotateSigningKey, setControlWebhook } from './envApi.ts'
import {
  addProblem, archiveBlock, budgetLine, conflictText, KINDS, keyFacts, ms, pct, signingState, SLO_DAYS, sloLine, sloWord, sortEnvironments, stateTone, stateWord,
  type EnvironmentRow, type EnvironmentSlo, type SigningKeyResult, type WebhookOption,
} from './envLogic.ts'
import '../mgmt/mgmt.css'
import './env.css'

/**
 * Environments (REOS-156, B6, D42), a Management destination: the workspace's staging and production environments, each with the SLO it is held to and the control webhook
 * that pause, promote and rollback call. A table with a panel beside it, as Connectors and Release types. RTE and Release Manager add, archive and restore, choose the control
 * webhook and create or rotate its signing key; everyone else reads. The signing key is shown once, held in this component's state only, and gone when the person
 * dismisses it, picks another row or leaves the page.
 */
const ALL = 'all'
const STATES = [{ value: ALL, label: 'All environments' }, { value: 'active', label: 'Active' }, { value: 'archived', label: 'Archived' }]
const NO_WEBHOOK = ''

export function Environments({ me }: { me: Me }) {
  const canEdit = canAdmin(me.roles)
  const [rows, setRows] = useState<EnvironmentRow[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loadedAt, setLoadedAt] = useState<string | null>(null)
  const [selected, setSelected] = useState<string | null>(null)
  const [query, setQuery] = useState('')
  const [state, setState] = useState(ALL)
  const [webhooks, setWebhooks] = useState<WebhookOption[]>([])
  const [addName, setAddName] = useState('')
  const [addKind, setAddKind] = useState(KINDS[0])
  const [addRegion, setAddRegion] = useState('')
  const [addFailed, setAddFailed] = useState<string | null>(null)
  const { run, pending } = useAction()

  const load = useCallback(() => {
    return listEnvironments().then(r => { setRows(sortEnvironments(r)); setError(null); setLoadedAt(new Date().toISOString()) }).catch(e => setError(errMsg(e, 'Could not load the environments.')))
  }, [])
  useEffect(() => { void load() }, [load])
  useEffect(() => { listWebhooks().then(setWebhooks).catch(() => setWebhooks([])) }, [])   // the choice list only; the detail says when a chosen webhook is not in it

  const shown = useMemo(() => {
    const q = query.trim().toLowerCase()
    return (rows ?? []).filter(r => (state === ALL || (state === 'archived') === r.archived)
      && (q === '' || [r.name, r.region ?? '', r.kind, r.controlWebhook?.name ?? ''].some(t => t.toLowerCase().includes(q))))
  }, [rows, query, state])
  const sel = rows?.find(r => r.id === selected) ?? null
  const problem = rows ? addProblem(addName, addKind, addRegion, rows) : null

  const add = () => run('Adding…', async () => {
    setAddFailed(null)
    try {
      const r = await createEnvironment({ name: addName.trim(), kind: addKind, region: addRegion.trim() || undefined })
      announce(`Environment ${r.name} added`)
      setAddName(''); setAddRegion(''); setQuery(''); setState(ALL)
      await load(); setSelected(r.id)
    } catch (e) { setAddFailed(errMsg(e)) }
  })

  const columns: Column<EnvironmentRow>[] = [
    { key: 'name', header: 'Environment', cell: r => <TwoLine primary={r.name} secondary={r.region ?? 'No region'} /> },
    { key: 'kind', header: 'Kind', cell: r => r.kind },
    { key: 'state', header: 'State', cell: r => <StatusChip tone={stateTone(r)}>{stateWord(r)}</StatusChip> },
    { key: 'active', header: 'Active deployments', align: 'end', mono: true, cell: r => String(r.activeDeployments) },
    { key: 'webhook', header: 'Control webhook', cell: r => (r.controlWebhook ? <TwoLine primary={r.controlWebhook.name} secondary={r.controlWebhook.host} /> : <span className="os-muted">None</span>) },
    { key: 'key', header: 'Signing key', cell: r => {
      const s = signingState(r, true)
      return r.controlWebhook ? <StatusChip tone={s.tone}>{s.word}</StatusChip> : <span className="os-muted">Not applicable</span>
    } },
  ]

  return (
    <div className="os-page">
      <PageHeading trail={['Management', 'Environments']} title="Environments"
        description="Staging and production, each with the SLO it is held to and the control webhook that pause, promote and rollback call." />
      {error && <ErrorState what="The environments" message={error} onRetry={() => void load()} />}
      {rows === null && !error && <LoadingState what="the environments" />}
      {!canEdit && rows && <ReadOnlyNotice>Only an RTE or Release Manager can add, archive or change environments. You can read them and their SLO.</ReadOnlyNotice>}
      {rows && (
        <div className="mg-split">
          <Panel title="Environments" subtitle="Archived environments cannot be chosen for a release or deployed to; they stay on what already uses them"
            meta={`${rows.filter(r => !r.archived).length} active of ${rows.length}`}
            footer={`${shown.length} of ${rows.length} ${rows.length === 1 ? 'environment' : 'environments'}${loadedAt ? ` · loaded ${timeText(loadedAt, { date: true, zone: browserZone() })}` : ''}`}>
            {rows.length === 0
              ? <EmptyState title="No environments yet">{canEdit ? 'Add the first one with the form. Deployments are recorded against an environment.' : 'An RTE or Release Manager adds them. Deployments are recorded against an environment.'}</EmptyState>
              : (
                <div className="mg-tablewrap">
                  <div className="env-filters" role="search" aria-label="Filter environments">
                    <SearchField label="Search environments" placeholder="Search name, region, webhook…" value={query} onChange={setQuery} />
                    <FilterSelect label="State" value={state} onChange={setState} options={STATES} />
                  </div>
                  <DataTable caption="Environments" columns={columns} rows={shown} rowKey={r => r.id} selectedKey={selected} onSelect={r => setSelected(r.id)}
                    empty={query !== '' || state !== ALL ? 'No environment matches these filters.' : 'No environments.'} />
                </div>
              )}
          </Panel>
          <div className="mg-side">
            {sel
              ? <Detail key={sel.id} row={sel} canEdit={canEdit} webhooks={webhooks} onReload={load} />
              : <Panel title="Environment detail"><EmptyState title="Select an environment">Pick a row to see its SLO, its control webhook and its signing key.</EmptyState></Panel>}
            {canEdit && (
              <Panel title="Add environment" subtitle="Limits, SLO targets and the control webhook can be set after it exists">
                <form className="mg-form" aria-label="Add environment" onSubmit={e => { e.preventDefault(); if (!problem) void add() }}>
                  <Field label="Name"><TextInput value={addName} onChange={e => setAddName(e.target.value)} maxLength={100} autoComplete="off" /></Field>
                  <Field label="Kind"><SelectInput value={addKind} onChange={e => setAddKind(e.target.value)} options={KINDS.map(k => ({ value: k, label: k }))} /></Field>
                  <Field label="Region" hint="Optional, for example eu-west-1."><TextInput value={addRegion} onChange={e => setAddRegion(e.target.value)} maxLength={60} autoComplete="off" /></Field>
                  <Problem message={addFailed} />
                  <div className="mg-actions">
                    {problem ? <DisabledAction variant="primary" reason={problem}>Add environment</DisabledAction>
                      : <Button variant="primary" type="submit" disabled={!!pending}>{pending === 'Adding…' ? pending : 'Add environment'}</Button>}
                  </div>
                </form>
              </Panel>
            )}
          </div>
        </div>
      )}
    </div>
  )
}

function Detail({ row, canEdit, webhooks, onReload }: { row: EnvironmentRow; canEdit: boolean; webhooks: WebhookOption[]; onReload: () => Promise<void> }) {
  const { run, pending } = useAction()
  const [problem, setProblem] = useState<string | null>(null)
  const [confirmArchive, setConfirmArchive] = useState(false)
  const [confirmRotate, setConfirmRotate] = useState(false)
  const [picked, setPicked] = useState<string | null>(null)   // null: follow the row (a reload after a save or a 409 puts the server's choice back)
  const webhook = picked ?? row.controlWebhook?.id ?? NO_WEBHOOK
  const [key, setKey] = useState<SigningKeyResult | null>(null)   // the secret: this state only, never a draft, storage, address or announcement
  const keyRef = useRef<HTMLHeadingElement>(null)
  const ids = useId()

  // A write: one at a time, a 409 reloads the row and says so, any other failure says why in words.
  const write = (label: string, fn: () => Promise<void>) => run(label, async () => {
    setProblem(null)
    try { await fn() }
    catch (e) {
      if (e instanceof ApiError && e.status === 409) { setProblem(conflictText(row.name)); await onReload(); setPicked(null) }
      else setProblem(errMsg(e))
    } finally { setConfirmArchive(false); setConfirmRotate(false) }
  })
  const archive = (archived: boolean) => write(archived ? 'Archiving…' : 'Restoring…', async () => {
    await archiveEnvironment(row.id, archived, row.version)
    announce(archived ? `${row.name} archived` : `${row.name} restored`)
    await onReload()
  })
  const saveWebhook = () => write('Saving…', async () => {
    await setControlWebhook(row.id, webhook === NO_WEBHOOK ? null : webhook, row.version)
    announce(`Control webhook for ${row.name} saved`)
    await onReload(); setPicked(null)
  })
  const rotate = () => write('Creating key…', async () => {
    const had = row.controlWebhookSigned
    const result = await rotateSigningKey(row.id)
    setKey(result)
    announce(`Signing key ${had ? 'rotated' : 'created'}. It is shown once; copy it now.`)   // never the key itself
    await onReload()
    requestAnimationFrame(() => keyRef.current?.focus())
  })
  const copy = () => {
    if (!key) return
    navigator.clipboard.writeText(key.secret).then(() => announce('Signing key copied'), () => setProblem('Could not copy. Select the key and copy it by hand.'))
  }

  const block = archiveBlock(row)
  const sign = signingState(row, canEdit)
  const webhookDirty = webhook !== (row.controlWebhook?.id ?? NO_WEBHOOK)
  const options = [{ value: NO_WEBHOOK, label: 'None: pause, promote and rollback are not available' },
    ...webhooks.map(w => ({ value: w.id, label: `${w.name} (${w.host})` })),
    ...(row.controlWebhook && !webhooks.some(w => w.id === row.controlWebhook!.id) ? [{ value: row.controlWebhook.id, label: `${row.controlWebhook.name} (${row.controlWebhook.host})` }] : [])]

  return (
    <div role="region" aria-label={`${row.name} environment`} className="mg-side">
      <Panel title={row.name} subtitle={`${row.kind}${row.region ? ` · ${row.region}` : ''}`} meta={<StatusChip tone={stateTone(row)}>{stateWord(row)}</StatusChip>}>
        <div className="mg-stack">
          <PropertyList items={[
            { label: 'Active deployments', value: String(row.activeDeployments), mono: true },
            { label: 'Release order', value: String(row.sortOrder), mono: true },
            { label: 'Version', value: String(row.version), mono: true },
          ]} />
          <Problem message={problem} />
          {canEdit && (
            <div className="mg-actions">
              {row.archived ? <Button disabled={!!pending} onClick={() => void archive(false)}>{pending === 'Restoring…' ? pending : 'Restore'}</Button>
                : block ? <DisabledAction variant="destructive" reason={block}>Archive</DisabledAction>
                : <Button variant="destructive" disabled={!!pending} onClick={() => setConfirmArchive(true)}>Archive</Button>}
            </div>
          )}
        </div>
      </Panel>

      <SloPanel envId={row.id} />

      <Panel title="Control webhook" subtitle="Where pause, promote and rollback are sent. Webhooks are added on Sync health"
        meta={row.controlWebhook ? <StatusChip tone={sign.tone}>{sign.word}</StatusChip> : undefined}>
        <div className="mg-stack">
          {canEdit ? (
            <div className="mg-form">
              <Field label="Control webhook"><SelectInput value={webhook} onChange={e => setPicked(e.target.value)} options={options} disabled={row.archived} /></Field>
              <div className="mg-actions">
                {row.archived ? <DisabledAction variant="primary" reason="Restore the environment to change its webhook.">Save webhook</DisabledAction>
                  : !webhookDirty ? <DisabledAction variant="primary" reason="Choose a different webhook first.">Save webhook</DisabledAction>
                  : <Button variant="primary" disabled={!!pending} onClick={() => void saveWebhook()}>{pending === 'Saving…' ? pending : 'Save webhook'}</Button>}
              </div>
            </div>
          ) : (
            <PropertyList items={[{ label: 'Control webhook', value: row.controlWebhook ? `${row.controlWebhook.name} (${row.controlWebhook.host})` : 'None' }]} />
          )}
          <PropertyList items={[{ label: 'Signing key', value: <StatusChip tone={sign.tone}>{sign.word}</StatusChip> }]} />
          <p className="mg-note">The receiver checks each call with this key. It is stored encrypted on the server and cannot be read again; creating another replaces it.</p>
          {canEdit && (
            <div className="mg-actions">
              {sign.off ? <DisabledAction reason={sign.off}>{sign.action}</DisabledAction>
                : <Button disabled={!!pending} onClick={() => (row.controlWebhookSigned ? setConfirmRotate(true) : void rotate())}>{pending === 'Creating key…' ? pending : sign.action}</Button>}
            </div>
          )}
          {key && (
            <Notice tone="warning" title="Copy this signing key now" live action={<Button onClick={() => setKey(null)}>Done</Button>}>
              <div className="mg-stack" role="group" aria-labelledby={`${ids}-key`}>
                <h3 id={`${ids}-key`} ref={keyRef} tabIndex={-1} className="mg-subhead">Signing key for {row.name}</h3>
                <p className="mg-note">{key.note} It will not be shown again.</p>
                <code className="env-secret" data-testid="signing-secret">{key.secret}</code>
                <div className="mg-actions"><Button variant="primary" onClick={copy}>Copy key</Button></div>
                <PropertyList items={keyFacts(key).map(f => ({ label: f.label, value: f.value, mono: true }))} />
              </div>
            </Notice>
          )}
        </div>
      </Panel>

      <ConfirmDialog open={confirmArchive} title={`Archive ${row.name}?`} confirmLabel="Archive environment" pendingLabel="Archiving…" destructive pending={pending === 'Archiving…'}
        onCancel={() => setConfirmArchive(false)} onConfirm={() => void archive(true)}>
        It can no longer be chosen for a release or deployed to. Releases and deployments that already use it keep it. You can restore it later.
      </ConfirmDialog>
      <ConfirmDialog open={confirmRotate} title={`Rotate the signing key for ${row.name}?`} confirmLabel="Rotate key" pendingLabel="Rotating…" destructive pending={pending === 'Creating key…'}
        onCancel={() => setConfirmRotate(false)} onConfirm={() => void rotate()}>
        The current key stops working at once. Update the deployment tool with the new key, or its calls will be refused until you do.
      </ConfirmDialog>
    </div>
  )
}

function SloPanel({ envId }: { envId: string }) {
  const [days, setDays] = useState(30)
  const [result, setResult] = useState<{ key: string; slo?: EnvironmentSlo; error?: string } | null>(null)
  const [retry, setRetry] = useState(0)
  const key = `${envId}|${days}|${retry}`
  useEffect(() => {
    let current = true
    getSlo(envId, days).then(slo => { if (current) setResult({ key, slo }) }).catch(e => { if (current) setResult({ key, error: errMsg(e, 'Could not load the SLO.') }) })
    return () => { current = false }
  }, [envId, days, key])
  const answered = result?.key === key ? result : null   // an answer for another range or environment is not shown
  const slo = answered?.slo ?? null, error = answered?.error ?? null
  const load = () => setRetry(n => n + 1)

  const metrics = slo && [
    { name: 'Availability', m: slo.availability, line: sloLine(slo.availability, v => pct(v), 'higher') },
    { name: 'Latency p95', m: slo.latencyP95, line: sloLine(slo.latencyP95, ms, 'lower') },
    { name: 'Error rate', m: slo.errorRate, line: sloLine(slo.errorRate, v => pct(v), 'lower') },
  ]
  return (
    <Panel title="Service level objectives" subtitle="Measured from deployment samples"
      meta={<FilterSelect label="Range" value={String(days)} onChange={v => setDays(Number(v))} options={SLO_DAYS.map(d => ({ value: String(d), label: `Last ${d} days` }))} />}>
      {error && <ErrorState what="The SLO" message={error} onRetry={load} />}
      {!slo && !error && <LoadingState what="the SLO" />}
      {slo && metrics && (
        <div className="mg-stack">
          <ul className="env-slo" aria-label="SLO by metric">
            {metrics.map(x => { const w = sloWord(x.m); return (
              <li key={x.name}><span className="env-slo-head">{x.name}<StatusChip tone={w.tone}>{w.word}</StatusChip></span><span className="mg-note">{x.line}</span></li>
            ) })}
          </ul>
          <PropertyList items={[
            { label: 'Error budget', value: budgetLine(slo.errorBudget) },
            { label: 'Samples', value: String(slo.samples + slo.rolledUpSamples), mono: true },
            { label: 'From', value: <Time at={slo.from} date /> },
            { label: 'To', value: <Time at={slo.to} date /> },
          ]} />
          {slo.samples + slo.rolledUpSamples === 0 && <p className="mg-note">No deployment has reported samples for this environment in this range.</p>}
        </div>
      )}
    </Panel>
  )
}
