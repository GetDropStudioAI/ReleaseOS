import { useCallback, useEffect, useState } from 'react'
import { ApiError, del, get, patch, post, type HolidayRow, type TeamRow, type UserRow } from './api'

type Tab = 'users' | 'teams' | 'holidays'
const TABS: { key: Tab; label: string }[] = [{ key: 'users', label: 'Users' }, { key: 'teams', label: 'Teams' }, { key: 'holidays', label: 'Holidays' }]

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

function UsersTable({ canEdit }: { canEdit: boolean }) {
  const { rows, error, reload } = useList<UserRow>('/api/v1/users')
  const [selected, setSelected] = useState<string | null>(null)
  const [handle, setHandle] = useState('')
  const [problem, setProblem] = useState<string | null>(null)
  const sel = rows?.find(r => r.id === selected)
  const save = async (u: UserRow, body: { handle?: string; isActive?: boolean }) => {
    setProblem(null)
    try { await patch(`/api/v1/users/${u.id}`, body, u.version); reload() }
    catch (e) { setProblem(e instanceof ApiError && e.status === 409 ? 'Someone else changed this user. Reloaded; try again.' : (e as Error).message); reload() }
  }
  return (
    <div className="split">
      <div>
        <Problem error={error ?? problem} />
        <table className="grid">
          <thead><tr><th>Name</th><th>Email</th><th>Role</th><th>Handle</th><th>State</th></tr></thead>
          <tbody>
            {rows?.map(u => (
              <tr key={u.id} aria-selected={u.id === selected} onClick={() => { setSelected(u.id); setHandle(u.handle ?? '') }}>
                <td>{u.displayName}</td><td className="mono">{u.email}</td><td>{u.role}</td>
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
                <button type="button" className="text" onClick={() => save(sel, { handle })}>Save handle</button>{' '}
                <button type="button" className={sel.isActive ? 'text destructive' : 'text'} onClick={() => save(sel, { isActive: !sel.isActive })}>
                  {sel.isActive ? 'Deactivate' : 'Reactivate'}
                </button>
              </p>
            ) : <p className="muted">Only an RTE or Release Manager can edit users.</p>}
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
  const nameOf = (id: string) => users.rows?.find(u => u.id === id)?.displayName ?? id
  const create = async () => {
    setProblem(null)
    try { await post('/api/v1/teams', { handle, name }); setAdding(false); setHandle(''); setName(''); reload() }
    catch (e) { setProblem((e as Error).message) }
  }
  return (
    <div>
      <Problem error={error ?? problem} />
      {canEdit && !adding && <p><button type="button" className="text" onClick={() => setAdding(true)}>Add team</button></p>}
      {adding && (
        <p className="inline-form">
          <label>Handle <input className="line" value={handle} onChange={e => setHandle(e.target.value)} placeholder="@ops-db" /></label>{' '}
          <label>Name <input className="line" value={name} onChange={e => setName(e.target.value)} /></label>{' '}
          <button type="button" className="text" onClick={create}>Create</button>{' '}
          <button type="button" className="text" onClick={() => setAdding(false)}>Cancel</button>
        </p>
      )}
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
  const add = async () => {
    setProblem(null)
    try { await post('/api/v1/holidays', { day, name }); setDay(''); setName(''); reload() }
    catch (e) { setProblem((e as Error).message) }
  }
  const remove = async (d: string) => { setProblem(null); try { await del(`/api/v1/holidays/${d}`); reload() } catch (e) { setProblem((e as Error).message) } }
  return (
    <div>
      <p className="muted">Gate due dates count business days: weekends and these holidays are skipped.</p>
      <Problem error={error ?? problem} />
      {canEdit && (
        <p className="inline-form">
          <label>Date <input className="line" type="date" value={day} onChange={e => setDay(e.target.value)} /></label>{' '}
          <label>Name <input className="line" value={name} onChange={e => setName(e.target.value)} /></label>{' '}
          <button type="button" className="text" disabled={!day || !name} onClick={add}>Add holiday</button>
        </p>
      )}
      <table className="grid">
        <thead><tr><th>Date</th><th>Name</th>{canEdit && <th><span className="sr-only">Actions</span></th>}</tr></thead>
        <tbody>
          {rows?.map(h => (
            <tr key={h.day}>
              <td className="mono">{h.day}</td><td>{h.name}</td>
              {canEdit && <td><button type="button" className="text destructive" aria-label={`Remove ${h.name}`} onClick={() => remove(h.day)}>Remove</button></td>}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

export default function Admin({ canEdit }: { canEdit: boolean }) {
  const [tab, setTab] = useState<Tab>('users')
  return (
    <section>
      <h1>Admin</h1>
      <nav className="tabs" aria-label="Admin sections">
        {TABS.map(t => <button key={t.key} type="button" className="choice" aria-pressed={tab === t.key} onClick={() => setTab(t.key)}>{t.label}</button>)}
      </nav>
      <div className="tab-body">
        {tab === 'users' && <UsersTable canEdit={canEdit} />}
        {tab === 'teams' && <TeamsTable canEdit={canEdit} />}
        {tab === 'holidays' && <HolidaysTable canEdit={canEdit} />}
      </div>
    </section>
  )
}
