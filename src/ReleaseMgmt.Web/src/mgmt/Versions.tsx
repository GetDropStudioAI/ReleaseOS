import { useCallback, useEffect, useState } from 'react'
import { ApiError } from '../api.ts'
import { announce } from '../announce.tsx'
import { useAction } from '../useAction.ts'
import { errMsg } from '../format.ts'
import { Button, ConfirmDialog, DataTable, DisabledAction, EmptyState, ErrorState, LoadingState, Panel, PropertyList, StatusChip, TwoLine, type Column } from '../ui'
import { Field, Problem, TextInput } from './forms.tsx'
import { ChainStrip } from '../versions/ChainStrip.tsx'
import { openRelease } from '../versions/navigate.ts'
import { archiveVersion, createVersion, listVersions, renameVersion, type VersionView } from '../versions/versionsApi.ts'
import { stepsOf, warningsById } from '../versions/versionsLogic.ts'

/**
 * Versions (REOS-117, RT2, D48), a tab of Teams & settings: the product versions of the workspace, each with the chain of its releases (Beta 1 to 3, CERT, PenTest Cert,
 * Production). A table with a panel beside it, as the other tabs; archiving asks first and keeps the releases linked. Releases join a version from their own page or when they
 * are created.
 */
export function VersionsTab({ canEdit }: { canEdit: boolean }) {
  const [rows, setRows] = useState<VersionView[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [selected, setSelected] = useState<string | null>(null)
  const [name, setName] = useState('')
  const [addName, setAddName] = useState('')
  const [addProblem, setAddProblem] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [confirm, setConfirm] = useState<VersionView | null>(null)
  const [done, setDone] = useState<string | null>(null)
  const { run, pending } = useAction()

  const reload = useCallback(() => { listVersions(true).then(r => { setRows(r); setError(null) }).catch(e => setError(errMsg(e))) }, [])
  useEffect(() => { reload() }, [reload])

  const say = (text: string | null) => { setDone(text); if (text) announce(text) }
  const sel = rows?.find(r => r.id === selected)
  const pick = (r: VersionView) => { setSelected(r.id); setName(r.name); setProblem(null); say(null) }
  const conflict = (e: unknown, what: string) => (e instanceof ApiError && e.status === 409 ? `Someone else changed ${what}. Reloaded; try again.` : errMsg(e))

  const add = () => run('Adding…', async () => {
    setAddProblem(null); say(null)
    try { const r = await createVersion(addName); say(`Version ${r.name} added`); setAddName(''); reload(); pick(r) }
    catch (e) { setAddProblem(errMsg(e)) }
  })
  const save = (r: VersionView) => run('Saving…', async () => {
    setProblem(null); say(null)
    try { await renameVersion(r.id, name, r.version); say(`${name.trim()} saved`); reload() }
    catch (e) { setProblem(conflict(e, r.name)); reload() }
  })
  const archive = (r: VersionView, archived: boolean) => run(archived ? 'Archiving…' : 'Restoring…', async () => {
    setProblem(null); say(null)
    try { await archiveVersion(r.id, archived, r.version); say(archived ? `${r.name} archived` : `${r.name} restored`); setConfirm(null); reload() }
    catch (e) { setProblem(conflict(e, r.name)); setConfirm(null); reload() }
  })

  const columns: Column<VersionView>[] = [
    { key: 'name', header: 'Version', cell: r => <TwoLine primary={r.name} secondary={r.chain.length ? r.chain.map(c => c.label).join(' → ') : 'No releases yet'} /> },
    { key: 'releases', header: 'Releases', align: 'end', cell: r => String(r.chain.length), mono: true },
    { key: 'advisories', header: 'Advisories', cell: r => r.warnings.length ? <StatusChip tone="warning">{`${r.warnings.length} too soon`}</StatusChip> : <span className="os-muted">None</span> },
    { key: 'state', header: 'State', cell: r => <StatusChip tone={r.archived ? 'neutral' : 'success'}>{r.archived ? 'Archived' : 'Active'}</StatusChip> },
  ]

  if (error && !rows) return <ErrorState what="Versions" message={error} onRetry={reload} />
  if (!rows) return <LoadingState what="versions" />
  const active = rows.filter(r => !r.archived).length
  const dirty = !!sel && name.trim() !== sel.name
  return (
    <div className="mg-split">
      <Panel title="Versions" subtitle="A version links the releases of one product version, in order" meta={`${active} active of ${rows.length}`}>
        <DataTable caption="Versions" columns={columns} rows={rows} rowKey={r => r.id} selectedKey={selected} onSelect={pick}
          empty="No versions yet. Add one, then put its releases in it from their pages." />
      </Panel>
      <div className="mg-side">
        <Panel title={sel ? sel.name : 'Version detail'} subtitle={sel ? (sel.archived ? 'Archived: it takes no new release' : 'Active') : undefined}>
          {!sel ? <EmptyState title="Select a version">Pick a row to see its chain of releases.</EmptyState> : (
            <div className="mg-stack">
              {sel.chain.length > 0
                ? <ChainStrip steps={stepsOf(sel)} name={sel.name} warnings={warningsById(sel)} onOpen={openRelease} />
                : <p className="mg-note">No release is in this version yet.</p>}
              {sel.warnings.length > 0 && <ul className="vc-warnings" aria-label="Chain advisories">{sel.warnings.map(w => <li key={w.trainId}><span className="warn" aria-hidden="true">▲ </span><span className="sr-only">Advisory: </span>{w.message}</li>)}</ul>}
              <PropertyList items={[{ label: 'Releases', value: String(sel.chain.length), mono: true }, { label: 'Version', value: String(sel.version), mono: true }]} />
              <Problem message={problem} />
              {canEdit ? (
                <>
                  <Field label="Name"><TextInput value={name} onChange={e => setName(e.target.value)} maxLength={100} autoComplete="off" /></Field>
                  <div className="mg-actions">
                    {!dirty || !name.trim() ? <DisabledAction variant="primary" reason={!name.trim() ? 'Enter a name.' : 'Change something first.'}>Save version</DisabledAction>
                      : <Button variant="primary" disabled={!!pending} onClick={() => save(sel)}>{pending === 'Saving…' ? pending : 'Save version'}</Button>}
                    {sel.archived
                      ? <Button disabled={!!pending} onClick={() => archive(sel, false)}>{pending === 'Restoring…' ? pending : 'Restore'}</Button>
                      : <Button variant="destructive" disabled={!!pending} onClick={() => setConfirm(sel)}>Archive</Button>}
                  </div>
                </>
              ) : <p className="mg-note">Only an RTE or Release Manager can change versions.</p>}
              {done && <p className="mg-ok" role="status"><span aria-hidden="true">✓ </span>{done}</p>}
            </div>
          )}
        </Panel>
        {canEdit && (
          <Panel title="Add version" subtitle="For example Orion 5.2. It starts with no releases">
            <div className="mg-form" role="group" aria-label="Add version">
              <Field label="Name"><TextInput value={addName} onChange={e => setAddName(e.target.value)} maxLength={100} autoComplete="off" /></Field>
              <Problem message={addProblem} />
              <div className="mg-actions">
                {!addName.trim() ? <DisabledAction variant="primary" reason="Enter a name for the new version.">Add version</DisabledAction>
                  : <Button variant="primary" disabled={!!pending} onClick={add}>{pending === 'Adding…' ? pending : 'Add version'}</Button>}
              </div>
            </div>
          </Panel>
        )}
      </div>
      <ConfirmDialog open={!!confirm} title={`Archive ${confirm?.name ?? ''}?`} confirmLabel="Archive version" pendingLabel="Archiving…" destructive pending={!!pending}
        onCancel={() => setConfirm(null)} onConfirm={() => archive(confirm!, true)}>
        No new release can join this version. {confirm?.chain.length ? `Its ${confirm.chain.length} ${confirm.chain.length === 1 ? 'release stays' : 'releases stay'} linked and keep ${confirm.chain.length === 1 ? 'its' : 'their'} order.` : 'No release is in it.'} You can restore it later.
      </ConfirmDialog>
    </div>
  )
}
