import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Plus } from 'lucide-react'
import { ApiError, type Me } from '../api.ts'
import { announce } from '../announce.tsx'
import { errMsg } from '../format.ts'
import { useAction } from '../useAction.ts'
import { isConflict } from '../Conflict.tsx'
import { Button, ConfirmDialog, ConflictNotice, DataTable, DisabledAction, EmptyState, ErrorState, FilterSelect, LoadingState, PageHeading, Panel, PropertyList, ReadOnlyNotice, SearchField, StatusChip, TwoLine, type Column } from '../ui'
import { Field, Problem, SelectInput, TextInput } from '../mgmt/forms.tsx'
import { archiveService, createService, listServices, listTeams, updateService } from './catalogApi.ts'
import {
  STATE_OPTIONS, archiveImpact, createBody, draftOf, emptyDraft, filterServices, isDirty, patchBody, serviceProblem, sortServices, summary, teamOptions,
  type ServiceDraft, type ServiceRow, type StateFilter, type TeamChoice,
} from './catalogLogic.ts'

/**
 * Service catalog (REOS-155, B8, D42): the services a workspace's releases ship. A searchable table with a panel beside it, as Connectors and Clients. Everyone reads; an RTE or
 * Release Manager adds, edits and archives (the API's Plan policy). Edits and archiving send the row's Version as If-Match; a 409 offers Use theirs / Overwrite. Archiving asks first.
 */
type Panelled = { kind: 'none' } | { kind: 'add' } | { kind: 'row'; id: string }

export function ServiceCatalog({ me }: { me: Me }) {
  const canEdit = me.roles.includes('RTE') || me.roles.includes('ReleaseManager')
  const [rows, setRows] = useState<ServiceRow[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [teams, setTeams] = useState<TeamChoice[]>([])
  const [teamsError, setTeamsError] = useState(false)
  const [query, setQuery] = useState('')
  const [state, setState] = useState<StateFilter>('active')
  const [panel, setPanel] = useState<Panelled>({ kind: 'none' })
  const [confirm, setConfirm] = useState<ServiceRow | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [done, setDone] = useState<string | null>(null)
  const { run, pending } = useAction()
  const addButton = useRef<HTMLButtonElement>(null)

  const reload = useCallback(() => listServices().then(r => { setRows(sortServices(r)); setError(null); return r }).catch(e => { setError(errMsg(e, 'Could not load the service catalog.')); return null }), [])
  useEffect(() => { void reload() }, [reload])
  useEffect(() => { listTeams().then(t => { setTeams(t); setTeamsError(false) }).catch(() => { setTeams([]); setTeamsError(true) }) }, [])

  const say = (text: string | null) => { setDone(text); if (text) announce(text) }
  const put = (r: ServiceRow) => setRows(rs => rs && sortServices(rs.some(x => x.id === r.id) ? rs.map(x => (x.id === r.id ? r : x)) : [...rs, r]))
  const sel = panel.kind === 'row' ? rows?.find(r => r.id === panel.id) ?? null : null
  const shown = useMemo(() => (rows ? filterServices(rows, query, state) : []), [rows, query, state])

  const archive = (r: ServiceRow, archived: boolean) => run(archived ? 'Archiving…' : 'Restoring…', async () => {
    setProblem(null); say(null)
    try { const saved = await archiveService(r.id, archived, r.version); put(saved); say(archived ? `${r.name} archived` : `${r.name} restored`); setConfirm(null) }
    catch (e) { setProblem(isConflict(e) ? `Someone else changed ${r.name}. Reloaded; try again.` : errMsg(e)); setConfirm(null); void reload() }
  })

  const columns: Column<ServiceRow>[] = [
    { key: 'name', header: 'Service', cell: r => <TwoLine primary={r.name} secondary={<span className="mg-mono">{r.serviceKey}</span>} /> },
    { key: 'team', header: 'Owner team', cell: r => (r.ownerTeam ? <TwoLine primary={r.ownerTeam.name} secondary={`@${r.ownerTeam.handle}`} /> : <span className="os-muted">No owner team</span>) },
    { key: 'repo', header: 'Repository', cell: r => (r.repository || r.projectCode ? <TwoLine primary={r.repository ?? 'No repository'} secondary={r.projectCode ? `Project ${r.projectCode}` : undefined} /> : <span className="os-muted">None</span>) },
    { key: 'products', header: 'Products', align: 'end', mono: true, cell: r => r.products },
    { key: 'deployments', header: 'Deployments', align: 'end', mono: true, cell: r => r.deployments },
    { key: 'state', header: 'State', cell: r => <StatusChip tone={r.archived ? 'neutral' : 'success'}>{r.archived ? 'Archived' : 'Active'}</StatusChip> },
  ]

  const counts = rows ? summary(rows) : null
  const open = (p: Panelled) => { setPanel(p); setProblem(null); say(null) }
  const anyActive = !!rows && rows.length > 0
  let body
  if (!rows) body = error ? null : <LoadingState what="the service catalog" />
  else if (!anyActive) {
    body = <Panel title="Services"><EmptyState title="No services yet" action={canEdit ? <Button variant="primary" icon={Plus} onClick={() => open({ kind: 'add' })}>Add service</Button> : undefined}>
      {canEdit ? 'Add the services your releases ship, or import them from a CSV.' : 'An RTE or Release Manager adds the services your releases ship.'}</EmptyState></Panel>
  } else {
    body = (
      <Panel title="Services" subtitle="Each service has a key that imports and the ingest API use, an owning team, and where its code lives"
        meta={counts ? `${counts.active} active${counts.archived ? `, ${counts.archived} archived` : ''}` : undefined}>
        <div className="mg-tablewrap">
          <div className="mg-toolbar">
            <SearchField label="Search services" placeholder="Search by name, key, team or repository" value={query} onChange={setQuery} />
            <FilterSelect label="State" value={state} options={STATE_OPTIONS} onChange={v => setState(v as StateFilter)} />
            <span className="mg-note" role="status">{shown.length === 1 ? '1 service' : `${shown.length} services`}{query.trim() || state !== 'active' ? ' match' : ''}</span>
          </div>
          <DataTable caption="Services" columns={columns} rows={shown} rowKey={r => r.id} selectedKey={panel.kind === 'row' ? panel.id : null} onSelect={r => open({ kind: 'row', id: r.id })}
            empty={query.trim() ? `No services match “${query.trim()}”.` : state === 'archived' ? 'No archived services.' : 'No active services.'} />
          {canEdit && <p className="mg-note">Many at once? <a href="/importexport">Import services from a CSV</a>.</p>}
        </div>
      </Panel>
    )
  }

  return (
    <div className="os-page">
      <PageHeading trail={['Management', 'Service catalog']} title="Service catalog"
        description="Every service your releases ship, with its owning team, repository and the products and deployments that use it."
        actions={canEdit ? <Button ref={addButton} variant="primary" icon={Plus} onClick={() => open({ kind: 'add' })}>Add service</Button> : undefined} />
      {error && <ErrorState what="The service catalog" message={error} onRetry={() => void reload()} />}
      {!canEdit && rows && <ReadOnlyNotice>Only an RTE or Release Manager can add, edit or archive services.</ReadOnlyNotice>}
      {body && (
        <div className="mg-split">
          {body}
          <div className="mg-side">
            {panel.kind === 'add' && canEdit && (
              <ServicePanel key="add" row={null} teams={teams} teamsError={teamsError} busy={pending !== null}
                onSaved={r => { put(r); say(`Service ${r.name} added`); setPanel({ kind: 'row', id: r.id }); setState(s => (s === 'archived' ? 'all' : s)) }}
                onReload={reload} onArchive={() => undefined} onClose={() => { setPanel({ kind: 'none' }); addButton.current?.focus() }} />
            )}
            {panel.kind === 'row' && sel && (
              <ServicePanel key={sel.id} row={sel} teams={teams} teamsError={teamsError} canEdit={canEdit} busy={pending !== null} problem={problem} done={done}
                onSaved={r => { put(r); say(`${r.name} saved`) }} onReload={reload}
                onArchive={archived => (archived ? setConfirm(sel) : void archive(sel, false))} onClose={() => setPanel({ kind: 'none' })} />
            )}
            {(panel.kind === 'none' || (panel.kind === 'row' && !sel) || (panel.kind === 'add' && !canEdit)) && rows && anyActive && (
              <Panel title="Service detail"><EmptyState title="Select a service">Pick a row to see its owner, repository and what uses it{canEdit ? ', or add a new service' : ''}.</EmptyState></Panel>
            )}
          </div>
        </div>
      )}
      <ConfirmDialog open={!!confirm} title={`Archive ${confirm?.name ?? ''}?`} confirmLabel="Archive service" pendingLabel="Archiving…" destructive pending={pending !== null}
        onCancel={() => setConfirm(null)} onConfirm={() => void archive(confirm!, true)}>
        The service leaves the pickers that offer services, and its record stays. {confirm ? archiveImpact(confirm) : ''} You can restore it later.
      </ConfirmDialog>
    </div>
  )
}

/** The add form (`row` null) or the detail and edit form of one service. Its draft is its own state, so a conflict keeps what the person typed. */
function ServicePanel({ row, teams, teamsError, canEdit = true, busy, problem: outer, done, onSaved, onReload, onArchive, onClose }: {
  row: ServiceRow | null; teams: TeamChoice[]; teamsError: boolean; canEdit?: boolean; busy: boolean; problem?: string | null; done?: string | null
  onSaved: (r: ServiceRow) => void; onReload: () => Promise<ServiceRow[] | null>; onArchive: (archived: boolean) => void; onClose: () => void
}) {
  const isNew = row === null
  const [draft, setDraft] = useState<ServiceDraft>(row ? draftOf(row) : emptyDraft())
  const [problem, setProblem] = useState<string | null>(null)
  const [conflict, setConflict] = useState<ApiError | null>(null)
  const [saving, setSaving] = useState(false)
  const set = (k: keyof ServiceDraft) => (v: string) => setDraft(d => ({ ...d, [k]: v }))
  const formProblem = serviceProblem(draft, isNew)
  const dirty = isNew || (!!row && isDirty(draft, row))

  const fail = (e: unknown) => { if (isConflict(e)) { setConflict(e); setProblem(null) } else { setConflict(null); setProblem(errMsg(e)) } }
  const save = async (version?: number) => {
    if (saving) return
    setSaving(true); setProblem(null); setConflict(null)
    try { onSaved(isNew ? await createService(createBody(draft)) : await updateService(row.id, patchBody(draft), version ?? row.version)) }
    catch (e) { fail(e) }
    finally { setSaving(false) }
  }
  const takeTheirs = async () => { const r = await onReload(); const mine = r?.find(x => x.id === row?.id); if (mine) setDraft(draftOf(mine)); setConflict(null) }
  const overwrite = () => { const cur = conflict?.body?.current as { version?: number } | undefined; if (cur?.version !== undefined) void save(cur.version) }

  const teamField = (
    <Field label="Owner team" hint={teamsError ? 'The teams could not be loaded; reload the page to choose one.' : undefined}>
      <SelectInput value={draft.ownerTeamId} onChange={e => set('ownerTeamId')(e.target.value)} options={teamOptions(teams, row?.ownerTeam ?? null)} />
    </Field>
  )

  if (row && !canEdit) {
    return (
      <Panel title={row.name} subtitle={row.archived ? 'Archived' : 'Active'} meta={<Button onClick={onClose}>Close</Button>}>
        <PropertyList items={detailItems(row)} />
      </Panel>
    )
  }
  return (
    <Panel title={row ? row.name : 'Add service'} subtitle={row ? (row.archived ? 'Archived' : 'Active') : 'A key, a name, and optionally the owning team and where the code lives'}
      meta={<Button onClick={onClose}>Close</Button>}>
      <div className="mg-stack" role="group" aria-label={row ? 'Edit service' : 'Add service'}>
        {row && <PropertyList items={detailItems(row)} />}
        {(problem ?? outer) && <Problem message={(problem ?? outer)!} />}
        {conflict && <ConflictNotice onUseTheirs={() => void takeTheirs()} onOverwrite={overwrite} />}
        <div className="mg-form">
          {isNew && <Field label="Key" hint='Letters, digits, ".", "_" or "-". Imports and the ingest API name a service by it; it cannot be changed later.'>
            <TextInput value={draft.key} onChange={e => set('key')(e.target.value)} maxLength={60} autoComplete="off" spellCheck={false} /></Field>}
          <Field label="Name"><TextInput value={draft.name} onChange={e => set('name')(e.target.value)} maxLength={200} autoComplete="off" /></Field>
          {teamField}
          <Field label="Repository"><TextInput value={draft.repository} onChange={e => set('repository')(e.target.value)} maxLength={300} autoComplete="off" spellCheck={false} /></Field>
          <Field label="Project code"><TextInput value={draft.projectCode} onChange={e => set('projectCode')(e.target.value)} maxLength={100} autoComplete="off" spellCheck={false} /></Field>
        </div>
        <div className="mg-actions">
          {formProblem || !dirty
            ? <DisabledAction variant="primary" reason={formProblem ?? 'Change something first.'}>{isNew ? 'Add service' : 'Save service'}</DisabledAction>
            : <Button variant="primary" disabled={busy || saving} onClick={() => void save()}>{saving ? (isNew ? 'Adding…' : 'Saving…') : isNew ? 'Add service' : 'Save service'}</Button>}
          {row && (row.archived
            ? <Button disabled={busy || saving} onClick={() => onArchive(false)}>Restore</Button>
            : <Button variant="destructive" disabled={busy || saving} onClick={() => onArchive(true)}>Archive</Button>)}
        </div>
        {done && <p className="mg-ok" role="status"><span aria-hidden="true">✓ </span>{done}</p>}
      </div>
    </Panel>
  )
}

const detailItems = (r: ServiceRow) => [
  { label: 'Key', value: r.serviceKey, mono: true },
  { label: 'Used by', value: `${r.products} ${r.products === 1 ? 'product' : 'products'}, ${r.deployments} ${r.deployments === 1 ? 'deployment' : 'deployments'}` },
  { label: 'Version', value: String(r.version), mono: true },
]
