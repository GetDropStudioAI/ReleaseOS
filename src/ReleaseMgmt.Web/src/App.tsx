import { useEffect, useState } from 'react'
import { devLogin, getMe, logout, type Me } from './api'
import { loadTheme, saveTheme, type ThemeChoice } from './theme'
import Admin from './Admin'

const ROLES = ['Viewer', 'RTE', 'ReleaseManager', 'GovernanceOfficer']
const GROUPS = ['Executing', 'Gated', 'Planning', 'Complete (30 days)']
const NAV = ['Trains', 'Calendar', 'Analytics', 'Sync health', 'Imports & exports']
type View = 'trains' | 'admin'

function ThemeChoices() {
  const [choice, setChoice] = useState<ThemeChoice>(loadTheme())
  const pick = (c: ThemeChoice) => { setChoice(c); saveTheme(c) }
  return (
    <span className="tabs" role="group" aria-label="Appearance">
      {(['auto', 'light', 'dark'] as const).map(c => (
        <button key={c} type="button" className="choice" aria-pressed={choice === c} onClick={() => pick(c)}>
          {c[0].toUpperCase() + c.slice(1)}
        </button>
      ))}
    </span>
  )
}

function SignIn({ onDone }: { onDone: () => void }) {
  const [email, setEmail] = useState('dev@example.com')
  const [role, setRole] = useState('RTE')
  const [error, setError] = useState<string | null>(null)
  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setError(null)
    if (await devLogin(email, email.split('@')[0], role)) onDone()
    else setError('Dev sign-in is only available in the Development environment. Use your organisation sign-in instead.')
  }
  return (
    <main className="signin">
      <h1>Release Management</h1>
      <form onSubmit={submit}>
        <p><label>Email <input className="line" type="email" value={email} onChange={e => setEmail(e.target.value)} required /></label></p>
        <p><label>Role <select className="line" value={role} onChange={e => setRole(e.target.value)}>
          {ROLES.map(r => <option key={r}>{r}</option>)}</select></label></p>
        <p><button className="text" type="submit">Sign in (development)</button> <a href="/auth/login">Organisation sign-in</a></p>
        {error && <p className="bad" role="alert">{error}</p>}
      </form>
      <p><ThemeChoices /></p>
    </main>
  )
}

export default function App() {
  const [me, setMe] = useState<Me | null | undefined>(undefined)
  const [failed, setFailed] = useState(false)
  const [view, setView] = useState<View>('trains')
  const refresh = () => getMe().then(setMe).catch(() => setFailed(true))
  useEffect(() => { refresh() }, [])

  if (failed) return <main className="signin"><p className="bad" role="alert">✗ Cannot reach the server.</p></main>
  if (me === undefined) return <main className="signin"><p className="muted">Loading…</p></main>
  if (me === null) return <SignIn onDone={refresh} />

  const canAdmin = me.roles.includes('RTE') || me.roles.includes('ReleaseManager')
  return (
    <div className="shell">
      <header className="toolbar">
        <strong>Release Management</strong>
        <nav aria-label="Primary" className="tabs">
          {NAV.map((n, i) => <a key={n} href="#" onClick={e => { e.preventDefault(); setView('trains') }} aria-current={view === 'trains' && i === 0 ? 'page' : undefined}>{n}</a>)}
          {canAdmin && <a href="#admin" onClick={e => { e.preventDefault(); setView('admin') }} aria-current={view === 'admin' ? 'page' : undefined}>Admin</a>}
        </nav>
        <span className="spacer" />
        <ThemeChoices />
        <span className="muted">{me.name} · {me.roles.join(', ')}</span>
        <button type="button" className="text" onClick={async () => { await logout(); setMe(null) }}>Sign out</button>
      </header>
      <aside className="stream" aria-label="Stream">
        {GROUPS.map(g => (
          <section key={g}>
            <h2 className="group-label">{g}</h2>
            <p className="muted empty">No trains</p>
          </section>
        ))}
      </aside>
      <main className={view === 'admin' ? 'workspace wide' : 'workspace'}>
        {view === 'admin' ? <Admin canEdit={canAdmin} /> : (<><h1>Trains</h1><p className="muted">No trains yet. Create one to start planning.</p></>)}
      </main>
      {view === 'trains' && (
        <aside className="inspector" aria-label="Inspector">
          <p className="muted">Select a train, gate or task to see its detail here.</p>
        </aside>
      )}
    </div>
  )
}
