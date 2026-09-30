import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError, del, get, patch, post, type HolidayRow, type TeamRow, type UserRow } from './api'
import { useAction } from './useAction'
import { ConfirmInline } from './ConfirmInline'
import { announce } from './announce'

type Tab = 'users' | 'teams' | 'holidays'
const TABS: { key: Tab; label: string }[] = [{ key: 'users', label: 'Users' }, { key: 'teams', label: 'Teams' }, { key: 'holidays', label: 'Holidays' }]

/** The tab lives in the query string (/admin?tab=teams) so a refresh or a shared link reopens it; route.ts only owns the path. */
const tabFromUrl = (): Tab => { const t = new URLSearchParams(window.location.search).get('tab'); return TABS.some(x => x.key === t) ? t as Tab : 'users' }
function tabToUrl(tab: Tab) {
  const url = new URL(window.location.href)
  if (tab === 'users') url.searchParams.delete('tab'); else url.searchParams.set('tab', tab)
  window.history.replaceState(window.history.state, '', url.pathname + url.search + url.hash)
}

function useList<T>(url: string) {
  const [rows, setRows] = useState<T[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const reload = useCallback(() => { get<T[]>(url).then(setRows).catch(e => setError(e.message)) }, [url])
  useEffect(() => { reload() }, [reload])
  return { rows, error, reload }
}

function Problem({ error }: { error: string | null }) {
  return error ? <p className="bad" role="alert">✗ {error}</p> : null
}

/** A success line that stays visible; the same words go to the app's live region (WCAG 4.1.3). */
function useDone() {
  const [done, setDone] = useState<string | null>(null)
  const say = useCallback((text: string | null) => { setDone(text); if (text) announce(text) }, [])
  const line = done ? <p className="ok"><span aria-hidden="true">✓ </span>{done}</p> : null
  return { say, line }
}

function UsersTable({ canEdit }: { canEdit: boolean }) {
  const { rows, error, reload } = useList<UserRow>('/api/v1/users')
  const [selected, setSelected] = useState<string | null>(null)
  const [handle, setHandle] = useState('')
  const [problem, setProblem] = useState<string | null>(null)
  const { run, pending } = useAction()
  const { say, line } = useDone()
  const sel = rows?.find(r => r.id === selected)
  const save = (label: string, u: UserRow, body: { handle?: string; isActive?: boolean }, done: string) => run(label, async () => {
    setProblem(null); say(null)
    try { await patch(`/api/v1/users/${u.id}`, body, u.version); say(done); reload() }
    catch (e) { setProblem(e instanceof ApiError && e.status === 409 ? 'Someone else changed this user. Reloaded; try again.' : (e as Error).message); reload() }
  })
  const pick = (u: UserRow) => { setSelected(u.id); setHandle(u.handle ?? ''); setProblem(null); say(null) }
  return (
    <div className="split">
      <div>
        <Problem error={error ?? problem} />
        <table className="grid">
          <thead><tr><th>Name</th><th>Email</th><th>Role</th><th>Handle</th><th>State</th></tr></thead>
          <tbody>
            {rows?.map(u => (
              <tr key={u.id} className={u.id === selected ? 'selected' : undefined} onClick={() => pick(u)}>
                <td><button type="button" className="text plainlink" aria-current={u.id === selected ? 'true' : undefined} onClick={e => { e.stopPropagation(); pick(u) }}>{u.displayName}</button></td>
                <td className="mono">{u.email}</td><td>{u.role}</td>
                <td className="mono">{u.handle ? `@${u.handle}` : <span className="muted">none</span>}</td>
                <td className={u.isActive ? 'ok' : 'muted'}>{u.isActive ? '● active' : '○ inactive'}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {rows?.length === 0 && <p className="muted">No users yet. Users appear when they first sign in.</p>}
      </div>
      <aside className="detail" aria-label="User detail">
        {!sel ? <p className="muted">Select a user.</p> : (
          <>
            <h2>{sel.displayName}</h2>
            <p className="muted">Role comes from the identity provider at sign-in ({sel.role}).</p>
            <p><label>Handle <input className="line" value={handle} onChange={e => setHandle(e.target.value)} disabled={!canEdit} placeholder="@rae" /></label></p>
            {canEdit ? (
              <p>
                <button type="button" className="text" disabled={!!pending} onClick={() => save('Saving…', sel, { handle }, 'Handle saved')}>{pending === 'Saving…' ? pending : 'Save handle'}</button>{' '}
                {sel.isActive
                  ? <ConfirmInline label="Deactivate" disabled={!!pending} question={`Deactivate ${sel.displayName}? They can no longer sign in.`} confirmLabel="Deactivate user" pendingLabel="Deactivating…"
                      onConfirm={() => save('Deactivating…', sel, { isActive: false }, `${sel.displayName} deactivated`)} />
                  : <button type="button" className="text" disabled={!!pending} onClick={() => save('Reactivating…', sel, { isActive: true }, `${sel.displayName} reactivated`)}>{pending === 'Reactivating…' ? pending : 'Reactivate'}</button>}
              </p>
            ) : <p className="muted">Only an RTE or Release Manager can edit users.</p>}
            {line}
          </>
        )}
      </aside>
    </div>
  )
}

function TeamsTable({ canEdit }: { canEdit: boolean }) {
  const { rows, error, reload } = useList<TeamRow>('/api/v1/teams')
  const users = useList<UserRow>('/api/v1/users')
  const [adding, setAdding] = useState(false)
  const [handle, setHandle] = useState('')
  const [name, setName] = useState('')
  const [problem, setProblem] = useState<string | null>(null)
  const { run, pending } = useAction()
  const { say, line } = useDone()
  const addBtn = useRef<HTMLButtonElement>(null)
  const first = useRef<HTMLInputElement>(null)
  const nameOf = (id: string) => users.rows?.find(u => u.id === id)?.displayName ?? id
  const open = () => { setAdding(true); say(null); requestAnimationFrame(() => first.current?.focus()) }
  const close = () => { setAdding(false); requestAnimationFrame(() => addBtn.current?.focus()) }
  const create = () => run('Creating…', async () => {
    setProblem(null)
    try { await post('/api/v1/teams', { handle, name }); say(`Team @${handle.replace(/^@/, '')} created`); close(); setHandle(''); setName(''); reload() }
    catch (e) { setProblem((e as Error).message) }
  })
  return (
    <div>
      <Problem error={error ?? problem} />
      {canEdit && !adding && <p><button ref={addBtn} type="button" className="text" onClick={open}>Add team</button></p>}
      {adding && (
        <p className="inline-form" role="group" aria-label="Add team">
          <label>Handle <input ref={first} className="line" value={handle} onChange={e => setHandle(e.target.value)} placeholder="@ops-db" /></label>{' '}
          <label>Name <input className="line" value={name} onChange={e => setName(e.target.value)} /></label>{' '}
          <button type="button" className="text" disabled={!!pending} onClick={create}>{pending ?? 'Create'}</button>{' '}
          <button type="button" className="text" disabled={!!pending} onClick={close}>Cancel</button>
        </p>
      )}
      {line}
      <table className="grid">
        <thead><tr><th>Handle</th><th>Name</th><th>Members</th></tr></thead>
        <tbody>
          {rows?.map(t => (
            <tr key={t.id}><td className="mono">@{t.handle}</td><td>{t.name}</td><td>{t.memberIds.map(nameOf).join(', ') || <span className="muted">none</span>}</td></tr>
          ))}
        </tbody>
      </table>
      {rows?.length === 0 && <p className="muted">No teams yet.</p>}
    </div>
  )
}

function HolidaysTable({ canEdit }: { canEdit: boolean }) {
  const { rows, error, reload } = useList<HolidayRow>('/api/v1/holidays')
  const [day, setDay] = useState('')
  const [name, setName] = useState('')
  const [problem, setProblem] = useState<string | null>(null)
  const { run, pending } = useAction()
  const { say, line } = useDone()
  const dayInput = useRef<HTMLInputElement>(null)
  const add = () => run('Adding…', async () => {
    setProblem(null); say(null)
    try { await post('/api/v1/holidays', { day, name }); say(`${name} added`); setDay(''); setName(''); reload(); dayInput.current?.focus() }
    catch (e) { setProblem((e as Error).message) }
  })
  const remove = async (h: HolidayRow) => {
    setProblem(null); say(null)
    try { await del(`/api/v1/holidays/${h.day}`); say(`${h.name} removed`); reload(); requestAnimationFrame(() => dayInput.current?.focus()) }   // the row is gone: land on the add form
    catch (e) { setProblem((e as Error).message) }
  }
  return (
    <div>
      <p className="muted">Gate due dates count business days: weekends and these holidays are skipped.</p>
      <Problem error={error ?? problem} />
      {canEdit && (
        <p className="inline-form">
          <label>Date <input ref={dayInput} className="line" type="date" value={day} onChange={e => setDay(e.target.value)} /></label>{' '}
          <label>Name <input className="line" value={name} onChange={e => setName(e.target.value)} /></label>{' '}
          <button type="button" className="text" disabled={!day || !name || !!pending} onClick={add}>{pending ?? 'Add holiday'}</button>
        </p>
      )}
      {line}
      <table className="grid">
        <thead><tr><th>Date</th><th>Name</th>{canEdit && <th><span className="sr-only">Actions</span></th>}</tr></thead>
        <tbody>
          {rows?.map(h => (
            <tr key={h.day}>
              <td className="mono">{h.day}</td><td>{h.name}</td>
              {canEdit && <td><ConfirmInline label="Remove" triggerLabel={`Remove ${h.name}`} question={`Remove ${h.name}?`} confirmLabel="Remove holiday" pendingLabel="Removing…" onConfirm={() => remove(h)} /></td>}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

export default function Admin({ canEdit }: { canEdit: boolean }) {
  const [tab, setTab] = useState<Tab>(tabFromUrl)
  const choose = (t: Tab) => { setTab(t); tabToUrl(t) }
  return (
    <section>
      <h1>Admin</h1>
      <div className="tabs" role="group" aria-label="Admin sections">
        {TABS.map(t => <button key={t.key} type="button" className="choice" aria-pressed={tab === t.key} onClick={() => choose(t.key)}>{t.label}</button>)}
      </div>
      <div className="tab-body">
        <h2 className="sr-only">{TABS.find(t => t.key === tab)!.label}</h2>
        {tab === 'users' && <UsersTable canEdit={canEdit} />}
        {tab === 'teams' && <TeamsTable canEdit={canEdit} />}
        {tab === 'holidays' && <HolidaysTable canEdit={canEdit} />}
      </div>
    </section>
  )
}
