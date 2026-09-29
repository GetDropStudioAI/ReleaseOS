import { useEffect, useState } from 'react'
import { devLogin, getAuthConfig, getMe, logout, type AuthConfig, type Me } from './api'
import { loadTheme, saveTheme, type ThemeChoice } from './theme'
import Admin from './Admin'
import { Stream, TrainHeader, useStream } from './Trains'
import { controlStatus, requestExit, requestReset, waitUntilReady, type ControlStatus } from './control'

const ROLES = ['Viewer', 'RTE', 'ReleaseManager', 'GovernanceOfficer']
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

/** Reset and Exit, shown only when start.py is supervising the app. Confirmation is inline (no modals). */
function SessionControls({ onSignedOut, onStopped }: { onSignedOut: () => void; onStopped: () => void }) {
  const [status, setStatus] = useState<ControlStatus | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [confirmExit, setConfirmExit] = useState(false)

  useEffect(() => { controlStatus().then(setStatus) }, [])
  if (!status) return null

  const reset = async () => {
    setError(null); setBusy('Pulling the latest code…')
    const r = await requestReset()
    if (!r.ok) { setBusy(null); setError(r.message); return }
    const result = await waitUntilReady(s => setBusy(s?.message ?? 'Restarting…'))
    if (result === 'ready') { location.reload(); return }
    setBusy(null)
    setError(result === 'error' ? ((await controlStatus())?.message ?? 'Reset failed') : 'Reset is taking too long; check the terminal running start.py')
  }
  const exit = async () => {
    setError(null); setBusy('Signing out and stopping…')
    await logout()
    onSignedOut()
    const r = await requestExit()
    if (!r.ok) { setBusy(null); setError(r.message); return }
    onStopped() // App shows the stopped page; this component is gone after sign-out, so the notice must live above it
  }

  return (
    <span className="session-controls">
      {busy && <span className="muted" role="status">{busy}</span>}
      {error && <span className="bad" role="alert">✗ {error}</span>}
      {!busy && !confirmExit && (
        <>
          <button type="button" className="text" onClick={reset}>Reset</button>
          <button type="button" className="text" onClick={() => setConfirmExit(true)}>Exit</button>
        </>
      )}
      {!busy && confirmExit && (
        <span>
          Sign out and stop the app?{' '}
          <button type="button" className="text destructive" onClick={exit}>Sign out and exit</button>{' '}
          <button type="button" className="text" onClick={() => setConfirmExit(false)}>Cancel</button>
        </span>
      )}
    </span>
  )
}

function SignIn({ onDone }: { onDone: () => void }) {
  const [email, setEmail] = useState('dev@example.com')
  const [role, setRole] = useState('RTE')
  const [error, setError] = useState<string | null>(null)
  const [cfg, setCfg] = useState<AuthConfig | null>(null)
  useEffect(() => { getAuthConfig().then(setCfg) }, [])
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
        {cfg?.passwordResetUrl && <p><a href={cfg.passwordResetUrl} rel="noreferrer">Forgot your password? Reset it with your authenticator app or MFA code</a></p>}
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
  const [stopped, setStopped] = useState(false)
  const [selected, setSelected] = useState<string | null>(null)
  const { rows, reload } = useStream(!!me)
  const [rev, setRev] = useState(0)
  const refresh = () => getMe().then(setMe).catch(() => setFailed(true))
  useEffect(() => { refresh() }, [])

  if (stopped) return <main className="signin"><h1>Release Management</h1><p role="status">The app has stopped. You can close this tab.</p></main>
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
        <SessionControls onSignedOut={() => setMe(null)} onStopped={() => setStopped(true)} />
      </header>
      <aside className="stream" aria-label="Stream">
        <Stream rows={rows} selected={selected} onSelect={id => { setSelected(id); setView('trains') }} />
      </aside>
      <main className={view === 'admin' ? 'workspace wide' : 'workspace'}>
        {view === 'admin' ? <Admin canEdit={canAdmin} /> : (selected ? <TrainHeader id={selected} refreshKey={rev} onChanged={() => { setRev(n => n + 1); reload() }} /> : <><h1>Trains</h1><p className="muted">{rows && rows.length === 0 ? 'No trains yet. Create one to start planning.' : 'Select a train in the Stream.'}</p></>)}
      </main>
      {view === 'trains' && (
        <aside className="inspector" aria-label="Inspector">
          <p className="muted">Select a train, gate or task to see its detail here.</p>
        </aside>
      )}
    </div>
  )
}
