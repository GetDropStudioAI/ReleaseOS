import { useCallback, useEffect, useState } from 'react'
import { ApiError, getTemplates, type TemplateRow } from '../api.ts'
import { announce } from '../announce.tsx'
import { useAction } from '../useAction.ts'
import { errMsg } from '../format.ts'
import { Button, ConfirmDialog, DataTable, DisabledAction, EmptyState, ErrorState, LoadingState, Notice, Panel, PropertyList, StatusChip, TwoLine, type Column } from '../ui'
import { Field, Problem, SelectInput, TextInput } from './forms.tsx'
import { archiveReleaseType, createReleaseType, listReleaseTypes, updateReleaseType } from './releaseTypesApi.ts'
import { betaLockReason, sortTypes, type ReleaseTypeRow } from './releaseTypesLogic.ts'

/**
 * Release types (REOS-116, D46, D48), a tab of Teams & settings: the workspace's list of types (seeded Beta, CERT, PenTest Cert and Production), each with the template a new
 * release of that type starts from. A table with a panel beside it, as the other tabs; archiving asks first. A Beta type makes its releases carry a number, 1 to 3.
 */
const NUMBERING = [{ value: 'no', label: 'No number' }, { value: 'yes', label: 'Each release has a number, 1 to 3' }]
const NO_TEMPLATE = ''

export function ReleaseTypesTab({ canEdit }: { canEdit: boolean }) {
  const [rows, setRows] = useState<ReleaseTypeRow[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [templates, setTemplates] = useState<TemplateRow[]>([])
  const [selected, setSelected] = useState<string | null>(null)
  const [name, setName] = useState('')
  const [template, setTemplate] = useState(NO_TEMPLATE)
  const [order, setOrder] = useState('')
  const [numbered, setNumbered] = useState('no')
  const [addName, setAddName] = useState('')
  const [addTemplate, setAddTemplate] = useState(NO_TEMPLATE)
  const [addNumbered, setAddNumbered] = useState('no')
  const [addProblem, setAddProblem] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [confirm, setConfirm] = useState<ReleaseTypeRow | null>(null)
  const [done, setDone] = useState<string | null>(null)
  const { run, pending } = useAction()

  const reload = useCallback(() => { listReleaseTypes(true).then(r => { setRows(sortTypes(r)); setError(null) }).catch(e => setError(errMsg(e))) }, [])
  useEffect(() => { reload() }, [reload])
  useEffect(() => { getTemplates().then(setTemplates).catch(() => setTemplates([])) }, [])

  const say = (text: string | null) => { setDone(text); if (text) announce(text) }
  const sel = rows?.find(r => r.id === selected)
  const pick = (r: ReleaseTypeRow) => { setSelected(r.id); setName(r.name); setTemplate(r.defaultTemplate?.id ?? NO_TEMPLATE); setOrder(String(r.sortOrder)); setNumbered(r.isBeta ? 'yes' : 'no'); setProblem(null); say(null) }
  const conflict = (e: unknown, what: string) => (e instanceof ApiError && e.status === 409 ? `Someone else changed ${what}. Reloaded; try again.` : errMsg(e))

  // Approved templates can be chosen; the one already chosen stays listed even if it has been retired since, with its status in words.
  const templateOptions = [{ value: NO_TEMPLATE, label: 'None: a new release starts blank' },
    ...templates.filter(t => t.status === 'Approved' || t.id === sel?.defaultTemplate?.id).map(t => ({ value: t.id, label: t.status === 'Approved' ? t.name : `${t.name} (${t.status})` }))]

  const addTemplateOptions = [{ value: NO_TEMPLATE, label: 'None: a new release starts blank' }, ...templates.filter(t => t.status === 'Approved').map(t => ({ value: t.id, label: t.name }))]
  const add = () => run('Adding…', async () => {
    setAddProblem(null); say(null)
    try {
      const r = await createReleaseType({ name: addName, defaultTemplateId: addTemplate || null, isBeta: addNumbered === 'yes' })
      say(`Release type ${r.name} added`); setAddName(''); setAddTemplate(NO_TEMPLATE); setAddNumbered('no'); reload(); pick(r)
    } catch (e) { setAddProblem(errMsg(e)) }
  })
  const save = (r: ReleaseTypeRow) => run('Saving…', async () => {
    setProblem(null); say(null)
    try {
      const body: Parameters<typeof updateReleaseType>[1] = { name, defaultTemplateId: template, isBeta: numbered === 'yes' }
      if (order.trim() !== '' && Number.isInteger(Number(order))) body.sortOrder = Number(order)
      await updateReleaseType(r.id, body, r.version); say(`${name.trim()} saved`); reload()
    } catch (e) { setProblem(conflict(e, r.name)); reload() }
  })
  const archive = (r: ReleaseTypeRow, archived: boolean) => run(archived ? 'Archiving…' : 'Restoring…', async () => {
    setProblem(null); say(null)
    try { await archiveReleaseType(r.id, archived, r.version); say(archived ? `${r.name} archived` : `${r.name} restored`); setConfirm(null); reload() }
    catch (e) { setProblem(conflict(e, r.name)); setConfirm(null); reload() }
  })

  const columns: Column<ReleaseTypeRow>[] = [
    { key: 'name', header: 'Type', cell: r => <TwoLine primary={r.name} secondary={r.isBeta ? 'Numbered 1 to 3' : undefined} /> },
    { key: 'template', header: 'Default template', cell: r => r.defaultTemplate?.name ?? <span className="os-muted">None</span> },
    { key: 'releases', header: 'Releases', align: 'end', cell: r => String(r.releases), mono: true },
    { key: 'state', header: 'State', cell: r => <StatusChip tone={r.archived ? 'neutral' : 'success'}>{r.archived ? 'Archived' : 'Active'}</StatusChip> },
  ]

  if (error && !rows) return <ErrorState what="Release types" message={error} onRetry={reload} />
  if (!rows) return <LoadingState what="release types" />
  const active = rows.filter(r => !r.archived).length
  const dirty = !!sel && (name.trim() !== sel.name || template !== (sel.defaultTemplate?.id ?? NO_TEMPLATE) || order !== String(sel.sortOrder) || (numbered === 'yes') !== sel.isBeta)
  const lock = sel ? betaLockReason(sel) : null
  return (
    <div className="mg-split">
      <Panel title="Release types" subtitle="Every release has one. A new release starts from its type's default template" meta={`${active} active of ${rows.length}`}>
        <DataTable caption="Release types" columns={columns} rows={rows} rowKey={r => r.id} selectedKey={selected} onSelect={pick} empty="No release types yet." />
      </Panel>
      <div className="mg-side">
        <Panel title={sel ? sel.name : 'Type detail'} subtitle={sel ? (sel.archived ? 'Archived: no new release can take it' : 'Active') : undefined}>
          {!sel ? <EmptyState title="Select a type">Pick a row to see its default template and how many releases use it.</EmptyState> : (
            <div className="mg-stack">
              <PropertyList items={[{ label: 'Releases of this type', value: String(sel.releases), mono: true }, { label: 'Default template', value: sel.defaultTemplate?.name ?? 'None' }, { label: 'Version', value: String(sel.version), mono: true }]} />
              <Problem message={problem} />
              {canEdit ? (
                <>
                  <Field label="Name"><TextInput value={name} onChange={e => setName(e.target.value)} maxLength={100} autoComplete="off" /></Field>
                  <Field label="Default template" hint="A new release of this type starts from it unless a template is chosen. Only an approved template can be chosen.">
                    <SelectInput value={template} onChange={e => setTemplate(e.target.value)} options={templateOptions} />
                  </Field>
                  <Field label="Order" hint="Lower comes first in lists and pickers.">
                    <TextInput type="number" inputMode="numeric" value={order} onChange={e => setOrder(e.target.value)} />
                  </Field>
                  <Field label="Numbering" hint={lock ?? 'A Beta type: its releases are Beta 1, 2 or 3, each its own release.'}>
                    <SelectInput value={numbered} onChange={e => setNumbered(e.target.value)} options={NUMBERING} disabled={!!lock} />
                  </Field>
                  <div className="mg-actions">
                    {!dirty || !name.trim() ? <DisabledAction variant="primary" reason={!name.trim() ? 'Enter a name.' : 'Change something first.'}>Save type</DisabledAction>
                      : <Button variant="primary" disabled={!!pending} onClick={() => save(sel)}>{pending === 'Saving…' ? pending : 'Save type'}</Button>}
                    {sel.archived
                      ? <Button disabled={!!pending} onClick={() => archive(sel, false)}>{pending === 'Restoring…' ? pending : 'Restore'}</Button>
                      : <Button variant="destructive" disabled={!!pending} onClick={() => setConfirm(sel)}>Archive</Button>}
                  </div>
                </>
              ) : <p className="mg-note">Only an RTE or Release Manager can change release types.</p>}
              {done && <p className="mg-ok" role="status"><span aria-hidden="true">✓ </span>{done}</p>}
            </div>
          )}
        </Panel>
        {canEdit && (
          <Panel title="Add release type" subtitle="For example Hotfix. It starts with no default template">
            <div className="mg-form" role="group" aria-label="Add release type">
              <Field label="Name"><TextInput value={addName} onChange={e => setAddName(e.target.value)} maxLength={100} autoComplete="off" /></Field>
              <Field label="Default template">
                <SelectInput value={addTemplate} onChange={e => setAddTemplate(e.target.value)} options={addTemplateOptions} />
              </Field>
              <Field label="Numbering"><SelectInput value={addNumbered} onChange={e => setAddNumbered(e.target.value)} options={NUMBERING} /></Field>
              <Problem message={addProblem} />
              <div className="mg-actions">
                {!addName.trim() ? <DisabledAction variant="primary" reason="Enter a name for the new type.">Add release type</DisabledAction>
                  : <Button variant="primary" disabled={!!pending} onClick={add}>{pending === 'Adding…' ? pending : 'Add release type'}</Button>}
              </div>
            </div>
          </Panel>
        )}
        {rows.length > 0 && active === 0 && <Notice tone="warning" title="No active release type" live>Every type is archived, so no new release can be created. Restore one.</Notice>}
      </div>
      <ConfirmDialog open={!!confirm} title={`Archive ${confirm?.name ?? ''}?`} confirmLabel="Archive type" pendingLabel="Archiving…" destructive pending={!!pending}
        onCancel={() => setConfirm(null)} onConfirm={() => archive(confirm!, true)}>
        No new release can take this type. {confirm?.releases ? `${confirm.releases} existing ${confirm.releases === 1 ? 'release keeps' : 'releases keep'} it, and it stays in filters.` : 'No release uses it.'} You can restore it later.
      </ConfirmDialog>
    </div>
  )
}
