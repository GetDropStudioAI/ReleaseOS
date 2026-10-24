import { useCallback, useEffect, useState } from 'react'
import { ApiError } from '../api.ts'
import { announce } from '../announce.tsx'
import { useAction } from '../useAction.ts'
import { errMsg } from '../format.ts'
import { Button, ConfirmDialog, DataTable, DisabledAction, EmptyState, ErrorState, LoadingState, Panel, PropertyList, StatusChip, TwoLine, type Column } from '../ui'
import { Field, Problem, SelectInput, TextInput, When } from './forms.tsx'
import { archiveClient, createClient, listClients, listEnvironmentChoices, updateClient } from './clientsApi.ts'
import { NO_ENVIRONMENT, SEGMENTS, clientProblem, environmentOptions, saveBody, segmentLabel, sortClients, summary, type ClientRow, type EnvironmentChoice, type Segment } from './clientsLogic.ts'

/**
 * Clients (REOS-118, RT3, D47), a tab of Teams & settings: the workspace's clients, each Standard or On-Demand. An On-Demand client names its CERT environment, one of the
 * workspace's; a Standard client has none. A table with a panel beside it, as the other tabs; archiving asks first. Clients can also be imported from a CSV (Imports & exports).
 */
export function ClientsTab({ canEdit }: { canEdit: boolean }) {
  const [rows, setRows] = useState<ClientRow[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [envs, setEnvs] = useState<EnvironmentChoice[]>([])
  const [selected, setSelected] = useState<string | null>(null)
  const [name, setName] = useState('')
  const [segment, setSegment] = useState<Segment>('Standard')
  const [env, setEnv] = useState(NO_ENVIRONMENT)
  const [addName, setAddName] = useState('')
  const [addSegment, setAddSegment] = useState<Segment>('Standard')
  const [addEnv, setAddEnv] = useState(NO_ENVIRONMENT)
  const [addProblem, setAddProblem] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [confirm, setConfirm] = useState<ClientRow | null>(null)
  const [done, setDone] = useState<string | null>(null)
  const { run, pending } = useAction()

  const reload = useCallback(() => { listClients(true).then(r => { setRows(sortClients(r)); setError(null) }).catch(e => setError(errMsg(e))) }, [])
  useEffect(() => { reload() }, [reload])
  useEffect(() => { listEnvironmentChoices().then(setEnvs).catch(() => setEnvs([])) }, [])

  const say = (text: string | null) => { setDone(text); if (text) announce(text) }
  const sel = rows?.find(r => r.id === selected)
  const pick = (r: ClientRow) => { setSelected(r.id); setName(r.name); setSegment(r.segment); setEnv(r.certEnvironment?.id ?? NO_ENVIRONMENT); setProblem(null); say(null) }
  const conflict = (e: unknown, what: string) => (e instanceof ApiError && e.status === 409 ? `Someone else changed ${what}. Reloaded; try again.` : errMsg(e))

  const add = () => run('Adding…', async () => {
    setAddProblem(null); say(null)
    try {
      const r = await createClient({ name: addName.trim(), segment: addSegment, certEnvironmentId: addSegment === 'OnDemand' ? addEnv : null })
      say(`Client ${r.name} added`); setAddName(''); setAddSegment('Standard'); setAddEnv(NO_ENVIRONMENT); reload(); pick(r)
    } catch (e) { setAddProblem(errMsg(e)) }
  })
  const save = (r: ClientRow) => run('Saving…', async () => {
    setProblem(null); say(null)
    try {
      const saved = await updateClient(r.id, saveBody(name, segment, env), r.version)
      setName(saved.name); setSegment(saved.segment); setEnv(saved.certEnvironment?.id ?? NO_ENVIRONMENT)   // the form shows what was saved: a Standard client has no environment left
      say(`${saved.name} saved`); reload()
    }
    catch (e) { setProblem(conflict(e, r.name)); reload() }
  })
  const archive = (r: ClientRow, archived: boolean) => run(archived ? 'Archiving…' : 'Restoring…', async () => {
    setProblem(null); say(null)
    try { await archiveClient(r.id, archived, r.version); say(archived ? `${r.name} archived` : `${r.name} restored`); setConfirm(null); reload() }
    catch (e) { setProblem(conflict(e, r.name)); setConfirm(null); reload() }
  })

  const columns: Column<ClientRow>[] = [
    { key: 'name', header: 'Client', cell: r => <TwoLine primary={r.name} secondary={r.archived ? 'Archived' : undefined} /> },
    { key: 'segment', header: 'Segment', cell: r => <StatusChip tone={r.segment === 'OnDemand' ? 'info' : 'neutral'}>{segmentLabel(r.segment)}</StatusChip> },
    { key: 'cert', header: 'CERT environment', cell: r => r.certEnvironment?.name ?? <span className="os-muted">None</span> },
    { key: 'state', header: 'State', cell: r => <StatusChip tone={r.archived ? 'neutral' : 'success'}>{r.archived ? 'Archived' : 'Active'}</StatusChip> },
  ]

  if (error && !rows) return <ErrorState what="Clients" message={error} onRetry={reload} />
  if (!rows) return <LoadingState what="clients" />
  const counts = summary(rows)
  const dirty = !!sel && (name.trim() !== sel.name || segment !== sel.segment || (segment === 'OnDemand' && env !== (sel.certEnvironment?.id ?? NO_ENVIRONMENT)))
  const formProblem = sel ? clientProblem(name, segment, env) : null
  const addFormProblem = clientProblem(addName, addSegment, addEnv)
  const currentEnv = sel?.certEnvironment?.id
  return (
    <div className="mg-split">
      <Panel title="Clients" subtitle="Standard clients take the standard date. On-Demand clients have a CERT environment and may choose another date"
        meta={`${counts.active} active: ${counts.standard} Standard, ${counts.onDemand} On-Demand`}>
        <DataTable caption="Clients" columns={columns} rows={rows} rowKey={r => r.id} selectedKey={selected} onSelect={pick}
          empty={canEdit ? 'No clients yet. Add one, or import a CSV.' : 'No clients yet.'} />
        {canEdit && <p className="mg-note">Many at once? <a href="/importexport">Import clients from a CSV</a> (Name, Segment, CertEnvironment).</p>}
      </Panel>
      <div className="mg-side">
        <Panel title={sel ? sel.name : 'Client detail'} subtitle={sel ? (sel.archived ? 'Archived' : segmentLabel(sel.segment)) : undefined}>
          {!sel ? <EmptyState title="Select a client">Pick a row to see its segment and CERT environment.</EmptyState> : (
            <div className="mg-stack">
              <PropertyList items={[
                { label: 'Segment', value: segmentLabel(sel.segment) },
                { label: 'CERT environment', value: sel.certEnvironment?.name ?? 'None' },
                { label: 'Last changed', value: <When iso={sel.lastChangedAt} /> },
                { label: 'Version', value: String(sel.version), mono: true },
              ]} />
              <Problem message={problem} />
              {canEdit ? (
                <>
                  <Field label="Name"><TextInput value={name} onChange={e => setName(e.target.value)} maxLength={200} autoComplete="off" /></Field>
                  <Field label="Segment"><SelectInput value={segment} onChange={e => setSegment(e.target.value as Segment)} options={SEGMENTS} /></Field>
                  {segment === 'OnDemand'
                    ? <Field label="CERT environment" hint="One of this workspace's environments."><SelectInput value={env} onChange={e => setEnv(e.target.value)} options={environmentOptions(envs, currentEnv)} /></Field>
                    : <p className="mg-note">A Standard client has no CERT environment.{sel.certEnvironment ? ' Saving removes the one it has.' : ''}</p>}
                  <div className="mg-actions">
                    {formProblem || !dirty ? <DisabledAction variant="primary" reason={formProblem ?? 'Change something first.'}>Save client</DisabledAction>
                      : <Button variant="primary" disabled={!!pending} onClick={() => save(sel)}>{pending === 'Saving…' ? pending : 'Save client'}</Button>}
                    {sel.archived
                      ? <Button disabled={!!pending} onClick={() => archive(sel, false)}>{pending === 'Restoring…' ? pending : 'Restore'}</Button>
                      : <Button variant="destructive" disabled={!!pending} onClick={() => setConfirm(sel)}>Archive</Button>}
                  </div>
                </>
              ) : <p className="mg-note">Only an RTE or Release Manager can change clients.</p>}
              {done && <p className="mg-ok" role="status"><span aria-hidden="true">✓ </span>{done}</p>}
            </div>
          )}
        </Panel>
        {canEdit && (
          <Panel title="Add client" subtitle="A Standard client, or an On-Demand client with its CERT environment">
            <div className="mg-form" role="group" aria-label="Add client">
              <Field label="Name"><TextInput value={addName} onChange={e => setAddName(e.target.value)} maxLength={200} autoComplete="off" /></Field>
              <Field label="Segment"><SelectInput value={addSegment} onChange={e => setAddSegment(e.target.value as Segment)} options={SEGMENTS} /></Field>
              {addSegment === 'OnDemand' && (
                <Field label="CERT environment" hint="One of this workspace's environments."><SelectInput value={addEnv} onChange={e => setAddEnv(e.target.value)} options={environmentOptions(envs, null)} /></Field>
              )}
              <Problem message={addProblem} />
              <div className="mg-actions">
                {addFormProblem ? <DisabledAction variant="primary" reason={addFormProblem}>Add client</DisabledAction>
                  : <Button variant="primary" disabled={!!pending} onClick={add}>{pending === 'Adding…' ? pending : 'Add client'}</Button>}
              </div>
            </div>
          </Panel>
        )}
      </div>
      <ConfirmDialog open={!!confirm} title={`Archive ${confirm?.name ?? ''}?`} confirmLabel="Archive client" pendingLabel="Archiving…" destructive pending={!!pending}
        onCancel={() => setConfirm(null)} onConfirm={() => archive(confirm!, true)}>
        The client leaves the active list and the pickers that offer clients. Its record stays, and you can restore it later.
      </ConfirmDialog>
    </div>
  )
}
