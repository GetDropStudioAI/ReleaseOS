import { useEffect, useMemo, useState } from 'react'
import { ApiError, type TrainDetail } from '../api.ts'
import { announce } from '../announce.tsx'
import { errMsg } from '../format.ts'
import { useAction } from '../useAction.ts'
import { Button, ConfirmDialog, DisabledAction, EmptyState, ErrorState, LoadingState, Notice, Panel, PropertyList, StatusChip } from '../ui'
import { Field, Problem, SelectInput, TextInput } from '../mgmt/forms.tsx'
import { listReleaseTypes } from '../mgmt/releaseTypesApi.ts'
import { ChainStrip } from './ChainStrip.tsx'
import { openRelease } from './navigate.ts'
import { createNextRelease, createVersion, listVersions, setTrainVersion, type NextOption, type TrainVersionView, type VersionView } from './versionsApi.ts'
import { defaultTitle, endOfChain, firstNext, nextProblem, stepsOf, versionOption, warningsById } from './versionsLogic.ts'

const NONE = ''

/**
 * The release's version (REOS-117, RT2, D48), on the Dependencies tab: the chain this release sits in (Beta 1 to 3, CERT, PenTest Cert, Production) with each step's status and
 * date, the advisories (a release planned too soon after the one before it; nothing is blocked), and what a planner can do: create the next release from this one, put this release
 * in a version, or take it out (asks first). A release in no version says so and offers the choices.
 */
export function VersionPanel({ t, canPlan, load, onChanged }: { t: TrainDetail; canPlan: boolean; load: { data: TrainVersionView | null; error: string | null; reload: () => void }; onChanged: () => void }) {
  const v = load.data
  if (load.error && !v) return <Panel label="Version" title="Version"><ErrorState what="The version" message={load.error} onRetry={load.reload} /></Panel>
  if (!v) return <Panel label="Version" title="Version"><LoadingState what="the version" /></Panel>
  return v.version ? <InVersion t={t} v={v} version={v.version} canPlan={canPlan} reload={load.reload} onChanged={onChanged} /> : <NotInVersion t={t} v={v} canPlan={canPlan} reload={load.reload} onChanged={onChanged} />
}

function InVersion({ t, v, version, canPlan, reload, onChanged }: { t: TrainDetail; v: TrainVersionView; version: VersionView; canPlan: boolean; reload: () => void; onChanged: () => void }) {
  const [creating, setCreating] = useState(false)
  const [confirm, setConfirm] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [made, setMade] = useState<{ id: string; title: string } | null>(null)
  const { run, pending } = useAction()
  const me = version.chain.find(c => c.trainId === t.id)
  const stop = endOfChain(version, v.next)
  const flagged = version.warnings.find(w => w.trainId === t.id)

  const takeOut = () => run('Taking out…', async () => {
    setProblem(null)
    try { await setTrainVersion(t.id, null, v.trainVersion); announce(`Taken out of ${version.name}`); setConfirm(false); onChanged(); reload() }
    catch (e) { setProblem(e instanceof ApiError && e.status === 409 ? 'Someone else changed this release. Reloaded; try again.' : errMsg(e)); setConfirm(false); reload() }
  })

  return (
    <Panel label="Version" title="Version" subtitle={`${version.name}: the releases of one product version, in order. Each keeps its own gates, dates and runbook.`}
      meta={<StatusChip tone={version.warnings.length ? 'warning' : 'success'}>{version.warnings.length ? `${version.warnings.length} ${version.warnings.length === 1 ? 'advisory' : 'advisories'}` : 'Dates in order'}</StatusChip>}>
      <div className="vc-panel">
        <ChainStrip steps={stepsOf(version)} name={version.name} currentId={t.id} warnings={warningsById(version)} onOpen={openRelease} />
        {version.warnings.length > 0 && (
          <ul className="vc-warnings" aria-label="Chain advisories">
            {version.warnings.map(w => (
              <li key={w.trainId}><Notice tone="warning" title={`${w.label} comes too soon after ${w.afterLabel}`}>{w.message}. Advisory only: nothing is blocked. Move {w.label} to {w.earliestOn} or later, or move {w.afterLabel} back.</Notice></li>
            ))}
          </ul>
        )}
        <PropertyList items={[
          { label: 'Version', value: version.name },
          { label: 'This release', value: me ? `${me.label}, ${me.position} of ${version.chain.length}` : 'Not in the chain' },
          ...(flagged ? [{ label: 'Earliest date after the release before it', value: flagged.earliestOn, mono: true }] : []),
        ]} />
        <Problem message={problem} />
        {made && <p className="mg-ok" role="status"><span aria-hidden="true">✓ </span>Created {made.title}. <button type="button" className="os-link os-link-button" onClick={() => openRelease(made.id)}>Open it →</button></p>}
        {canPlan && (
          <div className="vc-actions">
            {stop ? <DisabledAction reason={stop}>Create next release…</DisabledAction>
              : <Button variant="primary" aria-expanded={creating} onClick={() => { setCreating(c => !c); setMade(null) }}>{creating ? 'Cancel' : 'Create next release…'}</Button>}
            <Button variant="destructive" disabled={!!pending} onClick={() => setConfirm(true)}>Take out of version…</Button>
          </div>
        )}
        {canPlan && creating && !stop && (
          <CreateNext t={t} v={v} version={version} onDone={(id, title) => { setCreating(false); setMade({ id, title }); announce(`Created ${title}`); onChanged(); reload() }} />
        )}
      </div>
      <ConfirmDialog open={confirm} title={`Take ${t.title} out of ${version.name}?`} confirmLabel="Take out of version" pendingLabel="Taking out…" destructive pending={!!pending}
        onCancel={() => setConfirm(false)} onConfirm={takeOut}>
        The release keeps its gates, dates and runbook. It leaves the chain, so the other releases no longer follow or precede it, and its place becomes free. You can put it back.
      </ConfirmDialog>
    </Panel>
  )
}

/** The form behind "Create next release": the place in the chain, the title, the date. It says which template the new release starts from. */
function CreateNext({ t, v, version, onDone }: { t: TrainDetail; v: TrainVersionView; version: VersionView; onDone: (id: string, title: string) => void }) {
  const first = firstNext(v.next)
  const [pick, setPick] = useState(first ? optionKey(first) : '')
  const option = v.next.find(o => optionKey(o) === pick)
  const [title, setTitle] = useState(defaultTitle(version.name, first))
  const [titleEdited, setTitleEdited] = useState(false)
  const [date, setDate] = useState(v.defaultTargetDate)
  const [problem, setProblem] = useState<string | null>(null)
  const [types, setTypes] = useState<Awaited<ReturnType<typeof listReleaseTypes>> | null>(null)
  const { run, pending } = useAction()
  useEffect(() => { listReleaseTypes().then(setTypes).catch(() => setTypes([])) }, [])
  const why = nextProblem(option, title, date)
  const template = useMemo(() => option ? types?.find(x => x.id === option.typeId)?.defaultTemplate?.name ?? null : null, [types, option])
  const choose = (key: string) => { setPick(key); if (!titleEdited) setTitle(defaultTitle(version.name, v.next.find(o => optionKey(o) === key))) }

  const create = () => run('Creating…', async () => {
    if (!option) return
    setProblem(null)
    try {
      const made = await createNextRelease(t.id, { releaseTypeId: option.typeId, betaNumber: option.betaNumber, title: title.trim(), targetReleaseDate: date })
      onDone(made.id, made.title)
    } catch (e) { setProblem(errMsg(e)) }
  })

  return (
    <div className="vc-fields" role="group" aria-label="Create next release">
      <Field label="Next release" hint="The places after this release that are still free in the chain.">
        <SelectInput value={pick} onChange={e => choose(e.target.value)} options={v.next.map(o => ({ value: optionKey(o), label: o.label }))} />
      </Field>
      <Field label="Title"><TextInput value={title} maxLength={200} autoComplete="off" onChange={e => { setTitle(e.target.value); setTitleEdited(true) }} /></Field>
      <Field label="Target date (UTC calendar date)" hint={`Offered: ${v.defaultTargetDate}, a week after this release. The date can be changed afterwards.`}>
        <TextInput type="date" value={date} onChange={e => setDate(e.target.value)} />
      </Field>
      <p className="mg-note">
        {types === null ? 'Looking up the template…' : template ? `Starts from the ${option?.typeName} template “${template}”: its gates and plan are added.` : `The ${option?.typeName ?? 'release'} type has no default template, so the release starts blank.`}
        {' '}Owner, team, branch, on-call, environments and products are carried over from this release.
      </p>
      <Problem message={problem} />
      <div className="mg-actions">
        {why ? <DisabledAction variant="primary" reason={why}>Create release</DisabledAction>
          : <Button variant="primary" disabled={!!pending} onClick={create}>{pending === 'Creating…' ? pending : 'Create release'}</Button>}
      </div>
    </div>
  )
}

const optionKey = (o: NextOption) => `${o.typeId}|${o.betaNumber ?? ''}`

function NotInVersion({ t, v, canPlan, reload, onChanged }: { t: TrainDetail; v: TrainVersionView; canPlan: boolean; reload: () => void; onChanged: () => void }) {
  const [versions, setVersions] = useState<VersionView[] | null>(null)
  const [pick, setPick] = useState(NONE)
  const [name, setName] = useState('')
  const [problem, setProblem] = useState<string | null>(null)
  const { run, pending } = useAction()
  useEffect(() => { if (canPlan) listVersions().then(setVersions).catch(e => { setVersions([]); setProblem(errMsg(e)) }) }, [canPlan])

  const join = (versionId: string, label: string) => run('Adding…', async () => {
    setProblem(null)
    try { await setTrainVersion(t.id, versionId, v.trainVersion); announce(`Added to ${label}`); onChanged(); reload() }
    catch (e) { setProblem(e instanceof ApiError && e.status === 409 ? 'Someone else changed this release. Reloaded; try again.' : errMsg(e)); reload() }
  })
  const createAndJoin = () => run('Creating…', async () => {
    setProblem(null)
    let made: VersionView
    try { made = await createVersion(name.trim()) } catch (e) { setProblem(errMsg(e)); return }
    try { await setTrainVersion(t.id, made.id, v.trainVersion); announce(`Created ${made.name} and added this release`); onChanged(); reload() }
    catch (e) { setProblem(`The version ${made.name} was created, but this release could not join it: ${errMsg(e)}`); setVersions(null); listVersions().then(setVersions).catch(() => setVersions([])) }
  })
  const choices = [{ value: NONE, label: 'Choose a version' }, ...(versions ?? []).map(versionOption)]
  const joinWhy = !pick ? 'Choose a version.' : null
  const createWhy = name.trim() === '' ? 'Enter the new version’s name.' : null

  return (
    <Panel label="Version" title="Version" subtitle="A version links the releases of one product version in order: Beta 1 to 3, CERT, PenTest Cert, Production.">
      <div className="vc-panel">
        <EmptyState title="This release is not in a version">It stands alone. Put it in a version to see the releases before and after it, and to create the next one from it.</EmptyState>
        {canPlan ? (
          <>
            <div className="vc-fields" role="group" aria-label="Add to a version">
              <Field label="Add to an existing version"><SelectInput value={pick} onChange={e => setPick(e.target.value)} options={choices} disabled={versions === null} /></Field>
              <div className="mg-actions">
                {joinWhy ? <DisabledAction reason={joinWhy}>Add to version</DisabledAction>
                  : <Button variant="primary" disabled={!!pending} onClick={() => join(pick, versions?.find(x => x.id === pick)?.name ?? 'the version')}>{pending === 'Adding…' ? pending : 'Add to version'}</Button>}
              </div>
            </div>
            <div className="vc-fields" role="group" aria-label="Start a version">
              <Field label="Or start a new version with this release" hint="For example Orion 5.2. The name is the product version."><TextInput value={name} maxLength={100} autoComplete="off" onChange={e => setName(e.target.value)} /></Field>
              <div className="mg-actions">
                {createWhy ? <DisabledAction reason={createWhy}>Create version and add this release</DisabledAction>
                  : <Button disabled={!!pending} onClick={createAndJoin}>{pending === 'Creating…' ? pending : 'Create version and add this release'}</Button>}
              </div>
            </div>
            <Problem message={problem} />
          </>
        ) : <p className="mg-note">Only an RTE or Release Manager can put a release in a version.</p>}
      </div>
    </Panel>
  )
}
