import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ApiError, del, get, patch, post, type HolidayRow, type Me, type TeamRow, type UserRow } from './api'
import { useAction } from './useAction'
import { announce } from './announce'
import { AddUser } from './AddUser'
import { Button, ConfirmDialog, DataTable, DisabledAction, EmptyState, ErrorState, LoadingState, Notice, PageHeading, Panel, PropertyList, StatusChip, TabPanel, Tabs, TwoLine, type Column } from './ui'
import { Field, Problem, SelectInput, TextInput, When } from './mgmt/forms'
import { slugFromName, slugProblem } from './mgmt/membersLogic'
import { addMember, createWorkspace, removeMember, roleLabel, ROLE_OPTIONS, setMemberRole, type MemberRow } from './workspaceApi'
import { switchTo } from './workspace'
import { useWorkspaces } from './shell/WorkspaceGate'
import { errMsg } from './format'
import { ReleaseTypesTab } from './mgmt/ReleaseTypes'
import { VersionsTab } from './mgmt/Versions'

/**
 * Teams & settings (REOS-129, G7): people, teams, holidays and the workspace itself, each a table with a panel beside it (D41). The Workspace tab (REOS-106,
 * D45) manages who belongs to the current workspace and in which role, and creates new workspaces; it is for RTEs and Release Managers (the Admin policy).
 */
type Tab = 'users' | 'teams' | 'holidays' | 'types' | 'versions' | 'workspace'

/** The tab lives in the query string (/admin?tab=teams) so a refresh or a shared link reopens it; route.ts only owns the path. */
const tabFromUrl = (allowed: Tab[]): Tab => { const t = new URLSearchParams(window.location.search).get('tab') as Tab | null; return t && allowed.includes(t) ? t : 'users' }
function tabToUrl(tab: Tab) {
  const url = new URL(window.location.href)
  if (tab === 'users') url.searchParams.delete('tab'); else url.searchParams.set('tab', tab)
  window.history.replaceState(window.history.state, '', url.pathname + url.search + url.hash)
}

function useList<T>(url: string, enabled = true) {
  const [rows, setRows] = useState<T[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const reload = useCallback(() => { get<T[]>(url).then(r => { setRows(r); setError(null) }).catch(e => setError(e.message)) }, [url])
  useEffect(() => { if (enabled) reload() }, [enabled, reload])
  return { rows, error, reload }
}

/** A success line that stays visible; the same words go to the app's live region (WCAG 4.1.3). */
function useDone() {
  const [done, setDone] = useState<string | null>(null)
  const say = useCallback((text: string | null) => { setDone(text); if (text) announce(text) }, [])
  const line = done ? <p className="mg-ok" role="status"><span aria-hidden="true">✓ </span>{done}</p> : null
  return { say, line }
}

const activeChip = (on: boolean) => <StatusChip tone={on ? 'success' : 'neutral'}>{on ? 'Active' : 'Inactive'}</StatusChip>

function UsersTab({ canEdit }: { canEdit: boolean }) {
  const { rows, error, reload } = useList<UserRow>('/api/v1/users')
  const [selected, setSelected] = useState<string | null>(null)
  const [handle, setHandle] = useState('')
  const [problem, setProblem] = useState<string | null>(null)
  const [confirm, setConfirm] = useState<UserRow | null>(null)
  const { run, pending } = useAction()
  const { say, line } = useDone()
  const sel = rows?.find(r => r.id === selected)
  const save = (label: string, u: UserRow, body: { handle?: string; isActive?: boolean }, done: string) => run(label, async () => {
    setProblem(null); say(null)
    try { await patch(`/api/v1/users/${u.id}`, body, u.version); say(done); reload() }
    catch (e) { setProblem(e instanceof ApiError && e.status === 409 ? 'Someone else changed this user. Reloaded; try again.' : (e as Error).message); reload() }
  })
  const pick = (u: UserRow) => { setSelected(u.id); setHandle(u.handle ?? ''); setProblem(null); say(null) }
  const columns: Column<UserRow>[] = [
    { key: 'name', header: 'Name', cell: u => <TwoLine primary={u.displayName} secondary={u.handle ? `@${u.handle}` : undefined} /> },
    { key: 'email', header: 'Email', cell: u => u.email, mono: true },
    { key: 'role', header: 'Sign-in role', cell: u => roleLabel(u.role) },
    { key: 'state', header: 'State', cell: u => activeChip(u.isActive) },
  ]
  if (error && !rows) return <ErrorState what="Users" message={error} onRetry={reload} />
  if (!rows) return <LoadingState what="users" />
  return (
    <div className="mg-split">
      <Panel title="Users" subtitle="People who can be named as owners in this workspace" meta={`${rows.length} ${rows.length === 1 ? 'person' : 'people'}`}>
        <div className="mg-tablewrap">
          {canEdit && <AddUser onAdded={reload} />}
          <DataTable caption="Users" columns={columns} rows={rows} rowKey={u => u.id} selectedKey={selected} onSelect={pick}
            empty="No users yet. Users appear when they first sign in." />
        </div>
      </Panel>
      <Panel title={sel ? sel.displayName : 'User detail'} subtitle={sel ? sel.email : undefined}>
        {!sel ? <EmptyState title="Select a user">Pick a row to see the person and change their handle.</EmptyState> : (
          <div className="mg-stack">
            <p className="mg-note">The role comes from the identity provider at sign-in ({roleLabel(sel.role)}). Roles in this workspace are on the Workspace tab.</p>
            <Problem message={error ?? problem} />
            <Field label="Handle"><TextInput value={handle} onChange={e => setHandle(e.target.value)} disabled={!canEdit} placeholder="@rae" /></Field>
            {canEdit ? (
              <div className="mg-actions">
                <Button variant="primary" disabled={!!pending} onClick={() => save('Saving…', sel, { handle }, 'Handle saved')}>{pending === 'Saving…' ? pending : 'Save handle'}</Button>
                {sel.isActive
                  ? <Button variant="destructive" disabled={!!pending} onClick={() => setConfirm(sel)}>Deactivate</Button>
                  : <Button disabled={!!pending} onClick={() => save('Reactivating…', sel, { isActive: true }, `${sel.displayName} reactivated`)}>{pending === 'Reactivating…' ? pending : 'Reactivate'}</Button>}
              </div>
            ) : <p className="mg-note">Only an RTE or Release Manager can edit users.</p>}
            {line}
          </div>
        )}
      </Panel>
      <ConfirmDialog open={!!confirm} title={`Deactivate ${confirm?.displayName ?? ''}?`} confirmLabel="Deactivate user" pendingLabel="Deactivating…" destructive pending={!!pending}
        onCancel={() => setConfirm(null)}
        onConfirm={() => { const u = confirm!; save('Deactivating…', u, { isActive: false }, `${u.displayName} deactivated`).then(() => setConfirm(null)) }}>
        They can no longer sign in, in any workspace. Their history stays.
      </ConfirmDialog>
    </div>
  )
}

function TeamsTab({ canEdit }: { canEdit: boolean }) {
  const { rows, error, reload } = useList<TeamRow>('/api/v1/teams')
  const users = useList<UserRow>('/api/v1/users')
  const [selected, setSelected] = useState<string | null>(null)
  const [handle, setHandle] = useState('')
  const [name, setName] = useState('')
  const [problem, setProblem] = useState<string | null>(null)
  const { run, pending } = useAction()
  const { say, line } = useDone()
  const nameOf = (id: string) => users.rows?.find(u => u.id === id)?.displayName ?? id
  const sel = rows?.find(t => t.id === selected)
  const create = () => run('Creating…', async () => {
    setProblem(null)
    try { await post('/api/v1/teams', { handle, name }); say(`Team @${handle.replace(/^@/, '')} created`); setHandle(''); setName(''); reload() }
    catch (e) { setProblem((e as Error).message) }
  })
  const columns: Column<TeamRow>[] = [
    { key: 'handle', header: 'Handle', cell: t => `@${t.handle}`, mono: true },
    { key: 'name', header: 'Name', cell: t => t.name },
    { key: 'members', header: 'Members', cell: t => t.memberIds.length === 0 ? <span className="os-muted">none</span> : t.memberIds.map(nameOf).join(', ') },
  ]
  if (error && !rows) return <ErrorState what="Teams" message={error} onRetry={reload} />
  if (!rows) return <LoadingState what="teams" />
  return (
    <div className="mg-split">
      <Panel title="Teams" subtitle="Owning teams for gates, tasks and steps" meta={`${rows.length} ${rows.length === 1 ? 'team' : 'teams'}`}>
        <DataTable caption="Teams" columns={columns} rows={rows} rowKey={t => t.id} selectedKey={selected} onSelect={t => setSelected(t.id)} empty="No teams yet." />
      </Panel>
      <div className="mg-side">
        <Panel title={sel ? `@${sel.handle}` : 'Team detail'} subtitle={sel?.name}>
          {!sel ? <EmptyState title="Select a team">Pick a row to see its members.</EmptyState> : (
            <div className="mg-stack">
              <PropertyList items={[{ label: 'Handle', value: `@${sel.handle}`, mono: true }, { label: 'Members', value: String(sel.memberIds.length) }, { label: 'Version', value: String(sel.version), mono: true }]} />
              {sel.memberIds.length > 0 && <ul className="mg-list" aria-label="Members">{sel.memberIds.map(id => <li key={id}>{nameOf(id)}</li>)}</ul>}
            </div>
          )}
        </Panel>
        {canEdit && (
          <Panel title="Add team" subtitle="A handle is how the bulk parser and CSV name the team">
            <div className="mg-form" role="group" aria-label="Add team">
              <Field label="Handle"><TextInput value={handle} onChange={e => setHandle(e.target.value)} placeholder="@ops-db" /></Field>
              <Field label="Name"><TextInput value={name} onChange={e => setName(e.target.value)} /></Field>
              <Problem message={problem} />
              <div className="mg-actions"><Button variant="primary" disabled={!!pending || !handle.trim() || !name.trim()} onClick={create}>{pending ?? 'Add team'}</Button></div>
              {line}
            </div>
          </Panel>
        )}
      </div>
    </div>
  )
}

function HolidaysTab({ canEdit }: { canEdit: boolean }) {
  const { rows, error, reload } = useList<HolidayRow>('/api/v1/holidays')
  const [day, setDay] = useState('')
  const [name, setName] = useState('')
  const [problem, setProblem] = useState<string | null>(null)
  const [confirm, setConfirm] = useState<HolidayRow | null>(null)
  const { run, pending } = useAction()
  const { say, line } = useDone()
  const dayInput = useRef<HTMLInputElement>(null)
  const add = () => run('Adding…', async () => {
    setProblem(null); say(null)
    try { await post('/api/v1/holidays', { day, name }); say(`${name} added`); setDay(''); setName(''); reload(); dayInput.current?.focus() }
    catch (e) { setProblem((e as Error).message) }
  })
  const remove = (h: HolidayRow) => run('Removing…', async () => {
    setProblem(null); say(null)
    try { await del(`/api/v1/holidays/${h.day}`); say(`${h.name} removed`); setConfirm(null); reload(); requestAnimationFrame(() => dayInput.current?.focus()) }   // the row is gone: land on the add form
    catch (e) { setProblem((e as Error).message); setConfirm(null) }
  })
  const columns: Column<HolidayRow>[] = [
    { key: 'day', header: 'Date', cell: h => h.day, mono: true },
    { key: 'name', header: 'Name', cell: h => h.name },
    ...(canEdit ? [{ key: 'actions', header: <span className="sr-only">Actions</span>, align: 'end' as const, cell: (h: HolidayRow) => <Button variant="destructive" aria-label={`Remove ${h.name}`} onClick={() => setConfirm(h)}>Remove</Button> }] : []),
  ]
  if (error && !rows) return <ErrorState what="Holidays" message={error} onRetry={reload} />
  if (!rows) return <LoadingState what="holidays" />
  return (
    <div className="mg-split">
      <Panel title="Holidays" subtitle="Gate due dates count business days: weekends and these holidays are skipped" meta={`${rows.length} ${rows.length === 1 ? 'day' : 'days'}`}>
        <DataTable caption="Holidays" columns={columns} rows={rows} rowKey={h => h.day} empty="No holidays yet." />
      </Panel>
      {canEdit && (
        <Panel title="Add holiday" subtitle="Calendar days, not times: no time zone applies">
          <div className="mg-form" role="group" aria-label="Add holiday">
            <Field label="Date"><TextInput ref={dayInput} type="date" value={day} onChange={e => setDay(e.target.value)} /></Field>
            <Field label="Name"><TextInput value={name} onChange={e => setName(e.target.value)} /></Field>
            <Problem message={problem} />
            <div className="mg-actions"><Button variant="primary" disabled={!day || !name || !!pending} onClick={add}>{pending === 'Adding…' ? pending : 'Add holiday'}</Button></div>
            {line}
          </div>
        </Panel>
      )}
      <ConfirmDialog open={!!confirm} title={`Remove ${confirm?.name ?? ''}?`} confirmLabel="Remove holiday" pendingLabel="Removing…" destructive pending={!!pending}
        onCancel={() => setConfirm(null)} onConfirm={() => remove(confirm!)}>
        Gate due dates that fall on {confirm?.day} count that day as a business day again the next time they are worked out.
      </ConfirmDialog>
    </div>
  )
}

/** The current workspace's members, their role here, and the workspace-level actions (REOS-106). */
function WorkspaceTab({ myId }: { myId: string }) {
  const { rows, error, reload } = useList<MemberRow>('/api/v1/workspace/members')
  const { current, reload: reloadWorkspaces } = useWorkspaces()
  const [selected, setSelected] = useState<string | null>(null)
  const [role, setRole] = useState('Viewer')
  const [problem, setProblem] = useState<string | null>(null)
  const [confirm, setConfirm] = useState<MemberRow | null>(null)
  const { run, pending } = useAction()
  const { say, line } = useDone()
  const sel = rows?.find(m => m.userId === selected)
  const pick = (m: MemberRow) => { setSelected(m.userId); setRole(m.role); setProblem(null); say(null) }

  const change = (m: MemberRow) => run('Changing…', async () => {
    setProblem(null); say(null)
    try { const r = await setMemberRole(m, role); say(`${m.displayName} is now ${roleLabel(r.role)} in ${current?.name ?? 'this workspace'}. It applies on their next request.`); reload() }
    catch (e) { setProblem(e instanceof ApiError && e.status === 409 ? `Someone else changed ${m.displayName}. Reloaded; try again.` : errMsg(e)); reload() }
  })
  const remove = (m: MemberRow) => run('Removing…', async () => {
    setProblem(null); say(null)
    try {
      await removeMember(m); setConfirm(null); setSelected(null)
      say(`${m.displayName} removed from ${current?.name ?? 'this workspace'}`)
      if (m.userId === myId) { reloadWorkspaces(); window.location.assign('/') }   // they left the workspace they are in: start again where they still belong
      else reload()
    } catch (e) { setProblem(e instanceof ApiError && e.status === 409 ? `Someone else changed ${m.displayName}. Reloaded; try again.` : errMsg(e)); setConfirm(null); reload() }
  })

  const columns: Column<MemberRow>[] = [
    { key: 'person', header: 'Person', cell: m => <TwoLine primary={m.userId === myId ? `${m.displayName} (you)` : m.displayName} secondary={m.email} /> },
    { key: 'role', header: 'Role here', cell: m => roleLabel(m.role) },
    { key: 'state', header: 'Account', cell: m => activeChip(m.isActive) },
    { key: 'added', header: 'Added', cell: m => <When iso={m.addedAt} /> },
  ]
  if (error && !rows) return <ErrorState what="Workspace members" message={error} onRetry={reload} />
  if (!rows) return <LoadingState what="workspace members" />
  return (
    <div className="mg-split">
      <Panel title="Members" subtitle={`Who belongs to ${current?.name ?? 'this workspace'}, and the role each holds here`} meta={`${rows.length} ${rows.length === 1 ? 'member' : 'members'}`}>
        <DataTable caption="Workspace members" columns={columns} rows={rows} rowKey={m => m.userId} selectedKey={selected} onSelect={pick} empty="No members." />
      </Panel>
      <div className="mg-side">
        <Panel title={sel ? sel.displayName : 'Member detail'} subtitle={sel?.email}>
          {!sel ? <EmptyState title="Select a member">Pick a row to change their role or remove them from this workspace.</EmptyState> : (
            <div className="mg-stack">
              <PropertyList items={[{ label: 'Role here', value: roleLabel(sel.role) }, { label: 'Handle', value: sel.handle ? `@${sel.handle}` : 'None', mono: !!sel.handle }, { label: 'Membership version', value: String(sel.version), mono: true }]} />
              <Problem message={problem} />
              <Field label="Role in this workspace" hint="A change applies on the person's next request.">
                <SelectInput value={role} onChange={e => setRole(e.target.value)} options={ROLE_OPTIONS} />
              </Field>
              <div className="mg-actions">
                {role === sel.role
                  ? <DisabledAction variant="primary" reason="Choose a different role first.">Change role</DisabledAction>
                  : <Button variant="primary" disabled={!!pending} onClick={() => change(sel)}>{pending === 'Changing…' ? pending : 'Change role'}</Button>}
                <Button variant="destructive" disabled={!!pending} onClick={() => setConfirm(sel)}>Remove from workspace</Button>
              </div>
              {line}
            </div>
          )}
        </Panel>
        <AddMemberPanel workspaceName={current?.name ?? 'this workspace'} onAdded={m => { reload(); setSelected(m.userId); setRole(m.role) }} />
        <CreateWorkspacePanel myId={myId} onCreated={reloadWorkspaces} />
      </div>
      <ConfirmDialog open={!!confirm} title={`Remove ${confirm?.displayName ?? ''} from ${current?.name ?? 'this workspace'}?`} confirmLabel="Remove from workspace" pendingLabel="Removing…" destructive pending={!!pending}
        onCancel={() => setConfirm(null)} onConfirm={() => remove(confirm!)}>
        They lose access to this workspace on their next request and leave its teams. Their account and their other workspaces are not affected, and what they did here stays in the audit log.
        {confirm?.userId === myId && <> This is you: you will be taken to the workspace you still belong to.</>}
      </ConfirmDialog>
    </div>
  )
}

function AddMemberPanel({ workspaceName, onAdded }: { workspaceName: string; onAdded: (m: MemberRow) => void }) {
  const [email, setEmail] = useState('')
  const [name, setName] = useState('')
  const [role, setRole] = useState('Viewer')
  const [problem, setProblem] = useState<string | null>(null)
  const { run, pending } = useAction()
  const { say, line } = useDone()
  const add = () => run('Adding…', async () => {
    setProblem(null); say(null)
    try { const m = await addMember({ email, displayName: name, role }); say(`${m.displayName} added to ${workspaceName} as ${roleLabel(m.role)}`); setEmail(''); setName(''); setRole('Viewer'); onAdded(m) }
    catch (e) { setProblem(errMsg(e, 'The person could not be added.')) }
  })
  const incomplete = !email.trim() || !name.trim()
  return (
    <Panel title="Add a member" subtitle={`Someone with an account elsewhere keeps their name and is added here`}>
      <div className="mg-form" role="group" aria-label="Add a member">
        <Field label="Email"><TextInput type="email" value={email} onChange={e => setEmail(e.target.value)} autoComplete="off" spellCheck={false} placeholder="rae@example.com" /></Field>
        <Field label="Name" hint="Used when the person has no account yet."><TextInput value={name} onChange={e => setName(e.target.value)} autoComplete="off" /></Field>
        <Field label="Role in this workspace"><SelectInput value={role} onChange={e => setRole(e.target.value)} options={ROLE_OPTIONS} /></Field>
        <Problem message={problem} />
        <div className="mg-actions">
          {incomplete ? <DisabledAction variant="primary" reason="Enter the email they sign in with and their name.">Add member</DisabledAction>
            : <Button variant="primary" disabled={!!pending} onClick={add}>{pending ?? 'Add member'}</Button>}
        </div>
        {line}
      </div>
    </Panel>
  )
}

function CreateWorkspacePanel({ myId, onCreated }: { myId: string; onCreated: () => void }) {
  const [name, setName] = useState('')
  const [slug, setSlug] = useState('')
  const [slugEdited, setSlugEdited] = useState(false)
  const [region, setRegion] = useState('')
  const [problem, setProblem] = useState<string | null>(null)
  const [made, setMade] = useState<{ name: string; slug: string } | null>(null)
  const { run, pending } = useAction()
  const shownSlug = slugEdited ? slug : slugFromName(name)
  const bad = shownSlug ? slugProblem(shownSlug) : null
  const create = () => run('Creating…', async () => {
    setProblem(null); setMade(null)
    try {
      const w = await createWorkspace({ name, slug: shownSlug, region })
      setMade({ name: w.name, slug: w.slug }); announce(`Workspace ${w.name} created. You are its first member.`)
      setName(''); setSlug(''); setSlugEdited(false); setRegion(''); onCreated()
    } catch (e) { setProblem(errMsg(e, 'The workspace could not be created.')) }
  })
  return (
    <Panel title="Create a workspace" subtitle="A separate set of releases, teams and connectors. You become its first member.">
      <div className="mg-form" role="group" aria-label="Create a workspace">
        <Field label="Workspace name"><TextInput value={name} onChange={e => setName(e.target.value)} maxLength={100} autoComplete="off" placeholder="Payments EMEA" /></Field>
        <Field label="Short name" hint="The address segment, such as /w/payments-emea. Lower case letters, digits and hyphens.">
          <TextInput value={shownSlug} onChange={e => { setSlug(e.target.value.toLowerCase()); setSlugEdited(true) }} maxLength={40} autoComplete="off" spellCheck={false} aria-invalid={bad ? 'true' : undefined} />
        </Field>
        <Field label="Region" hint="A label shown in the sidebar, such as EU or US-East (optional)."><TextInput value={region} onChange={e => setRegion(e.target.value)} maxLength={60} autoComplete="off" /></Field>
        <Problem message={problem ?? bad} />
        <div className="mg-actions">
          {!name.trim() || !shownSlug || bad ? <DisabledAction variant="primary" reason={!name.trim() ? 'Enter a name.' : 'Enter a valid short name.'}>Create workspace</DisabledAction>
            : <Button variant="primary" disabled={!!pending} onClick={create}>{pending ?? 'Create workspace'}</Button>}
        </div>
        {made && (
          <Notice tone="success" title={`${made.name} created`} live action={<Button onClick={() => switchTo(made.slug, myId)}>Switch to it</Button>}>
            You are its first member. It starts empty: add its people, then its connectors and releases.
          </Notice>
        )}
      </div>
    </Panel>
  )
}

export default function Admin({ canEdit, me }: { canEdit: boolean; me: Me }) {
  const allowed = useMemo<Tab[]>(() => (canEdit ? ['users', 'teams', 'holidays', 'types', 'versions', 'workspace'] : ['users', 'teams', 'holidays', 'types', 'versions']), [canEdit])
  const [tab, setTab] = useState<Tab>(() => tabFromUrl(allowed))
  const choose = (t: string) => { setTab(t as Tab); tabToUrl(t as Tab) }
  const tabs = [
    { id: 'users', label: 'Users' }, { id: 'teams', label: 'Teams' }, { id: 'holidays', label: 'Holidays' }, { id: 'types', label: 'Release types' }, { id: 'versions', label: 'Versions' },
    ...(canEdit ? [{ id: 'workspace', label: 'Workspace' }] : []),
  ]
  return (
    <div className="os-page">
        <PageHeading trail={['Management', 'Teams & settings']} title="Teams & settings"
          description="The people, teams, calendars, release types and versions the workspace runs on, and who belongs to it." />
        <div>
          <Tabs tabs={tabs} value={tab} onChange={choose} label="Teams and settings sections" group="adm" />
          <TabPanel id={tab} group="adm">
            {tab === 'users' && <UsersTab canEdit={canEdit} />}
            {tab === 'teams' && <TeamsTab canEdit={canEdit} />}
            {tab === 'holidays' && <HolidaysTab canEdit={canEdit} />}
            {tab === 'types' && <ReleaseTypesTab canEdit={canEdit} />}
            {tab === 'versions' && <VersionsTab canEdit={canEdit} />}
            {tab === 'workspace' && canEdit && <WorkspaceTab myId={me.id} />}
          </TabPanel>
        </div>
    </div>
  )
}
