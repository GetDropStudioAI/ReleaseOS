import { useEffect, useRef, useState } from 'react'
import { devLogin, getAuthConfig, getMe, logout, type AuthConfig, type Me } from './api'
import { loadTheme, saveTheme, type ThemeChoice } from './theme'
import Admin from './Admin'
import { MyWork } from './MyWork'
import { Inbox } from './Inbox'
import { AuditViewer } from './AuditViewer'
import { Templates } from './Templates'
import { Analytics } from './Analytics'
import { Calendar } from './Calendar'
import { ImportExport } from './ImportExport'
import { SyncHealth } from './SyncHealth'
import { SyncBanner, notifySyncChanged, useSyncState } from './SyncBanner'
import { Connectors } from './Connectors'
import { CommLibrary } from './CommLibrary'
import { FreezeFooter, Stream, TrainHeader, useStream } from './Trains'
import { Inspector } from './Planning'
import { LiveRun, RunStepDrawer, useRun } from './LiveRun'
import { BulkDrawer, bulkKey, type BulkDraft } from './Bulk'
import { CommsDrawer, commsKey, type CommsDraft } from './CommsDrawer'
import { NewTrainDrawer, emptyNewTrain, newTrainKey, type NewTrainDraft } from './NewTrain'
import { getTrain, getUnreadCount, type TrainDetail } from './api'
import { useDraft } from './session'
import { parsePath, ROOT, useRoute, type Route } from './route'
import { SessionProvider, useSession } from './session'
import { useLive, type LiveState } from './live'
import { fmtDayTime, setDisplayZone, zoneAbbr } from './time'
import { getConfig } from './api'
import { controlStatus, requestExit, requestReset, waitUntilReady, type ControlStatus } from './control'

const ROLES = ['Viewer', 'RTE', 'ReleaseManager', 'GovernanceOfficer']
const NAV = ['Trains']

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

const LIVE: Record<LiveState, { g: string; cls: string; word: string }> = {
  connecting: { g: '◐', cls: 'muted', word: 'Connecting…' }, live: { g: '●', cls: 'ok', word: 'Live' },
  reconnecting: { g: '◐', cls: 'warn', word: 'Reconnecting…' }, offline: { g: '○', cls: 'bad', word: 'Offline: reload to reconnect' },
}
// Server clock pushed every 10 s, so every tab shows the same time; the state is words + glyph, never a badge.
function LiveStatus({ state, time }: { state: LiveState; time: string | null }) {
  const x = LIVE[state]
  const when = time ? `${fmtDayTime(time)} ${zoneAbbr(time)}` : null
  return <span className="muted live" role="status">{when && <>{when} · </>}<span className={x.cls}>{x.g} {x.word}</span></span>
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
  const [stopped, setStopped] = useState(false)
  const refresh = () => getMe().then(setMe).catch(() => setFailed(true))
  useEffect(() => { refresh() }, [])

  if (stopped) return <main className="signin"><h1>Release Management</h1><p role="status">The app has stopped. You can close this tab.</p></main>
  if (failed) return <main className="signin"><p className="bad" role="alert">✗ Cannot reach the server.</p></main>
  if (me === undefined) return <main className="signin"><p className="muted">Loading…</p></main>
  if (me === null) return <SignIn onDone={refresh} />
  return <SessionProvider enabled><Signed me={me} onSignedOut={() => setMe(null)} onStopped={() => setStopped(true)} /></SessionProvider>
}

function Signed({ me, onSignedOut, onStopped }: { me: Me; onSignedOut: () => void; onStopped: () => void }) {
  const { route, go } = useRoute()
  const session = useSession()
  const [zoneReady, setZoneReady] = useState(false)
  useEffect(() => { getConfig().then(c => setDisplayZone(c.displayTimeZone)).catch(() => { /* keep the D24 default */ }).finally(() => setZoneReady(true)) }, [])
  const { rows, reload } = useStream(true)
  const [rev, setRev] = useState(0)
  const refetch = () => { setRev(n => n + 1); reload() }
  const [unread, setUnread] = useState(0)
  const [notifRev, setNotifRev] = useState(0)
  const refreshUnread = () => { getUnreadCount().then(c => setUnread(c.unread)).catch(() => { /* the nav count is a hint; the Inbox shows real errors */ }) }
  useEffect(refreshUnread, [])
  const sync = useSyncState(true)   // REOS-42: connector-wide banner state; refetches on notifySyncChanged, every minute, and after a reconnect
  const live = useLive(true, { onTrainChanged: refetch, onResync: () => { refetch(); refreshUnread(); setNotifRev(n => n + 1); notifySyncChanged() }, onForecastChanged: refetch, onNotification: () => { refreshUnread(); setNotifRev(n => n + 1) }, onSyncAlert: notifySyncChanged })
  const { view, trainId: selected, selection } = route
  const mode = route.mode ?? 'plan'
  const runData = useRun(selected, mode, rev)
  const [bulk, setBulk] = useDraft<BulkDraft>(bulkKey(selected ?? ''))
  const [comms, setComms] = useDraft<CommsDraft>(commsKey(selected ?? ''))   // REOS-45
  const [newTrain, setNewTrain] = useDraft<NewTrainDraft>(newTrainKey)   // REOS-80: the New train drawer wins the right-hand column while open
  const [gates, setGates] = useState<TrainDetail['gates']>([])
  useEffect(() => { if (selected && bulk?.open) getTrain(selected).then(t => setGates(t.gates)).catch(() => setGates([])) }, [selected, bulk?.open, rev])

  // A URL that names nothing (opening the app fresh) restores where this or the most recent tab was; a deep link always wins.
  const restored = useRef(false)
  useEffect(() => {
    if (!session.ready || restored.current) return
    restored.current = true
    if (parsePath(window.location.pathname) === ROOT && session.restoredRoute && (session.restoredRoute.trainId || session.restoredRoute.view === 'admin')) go({ ...ROOT, ...session.restoredRoute }, true)
  }, [session.ready, session.restoredRoute, go])
  useEffect(() => { if (session.ready && restored.current) session.saveRoute(route) }, [route, session.ready])   // eslint-disable-line react-hooks/exhaustive-deps

  if (!session.ready || !zoneReady) return <main className="signin"><p className="muted">Loading…</p></main>

  const canAdmin = me.roles.includes('RTE') || me.roles.includes('ReleaseManager')
  const to = (r: Partial<Route>) => go({ ...route, ...r })
  const open = (r: Partial<Route>) => go({ ...ROOT, ...r })   // from My work / Inbox: open a train (view resets to trains)
  return (
    <div className="shell">
      <header className="toolbar">
        <strong>Release Management</strong>
        <nav aria-label="Primary" className="tabs">
          {NAV.map((n, i) => <a key={n} href="/" onClick={e => { e.preventDefault(); to({ view: 'trains' }) }} aria-current={view === 'trains' && i === 0 ? 'page' : undefined}>{n}</a>)}
          <a href="/work" onClick={e => { e.preventDefault(); to({ view: 'work' }) }} aria-current={view === 'work' ? 'page' : undefined}>My work</a>
          <a href="/inbox" onClick={e => { e.preventDefault(); to({ view: 'inbox' }) }} aria-current={view === 'inbox' ? 'page' : undefined}>{unread > 0 ? `Inbox (${unread})` : 'Inbox'}</a>
          <a href="/calendar" onClick={e => { e.preventDefault(); to({ view: 'calendar' }) }} aria-current={view === 'calendar' ? 'page' : undefined}>Calendar</a>
          <a href="/analytics" onClick={e => { e.preventDefault(); to({ view: 'analytics' }) }} aria-current={view === 'analytics' ? 'page' : undefined}>Analytics</a>
          <a href="/importexport" onClick={e => { e.preventDefault(); to({ view: 'importexport' }) }} aria-current={view === 'importexport' ? 'page' : undefined}>Imports &amp; exports</a>
          <a href="/sync" onClick={e => { e.preventDefault(); to({ view: 'sync' }) }} aria-current={view === 'sync' ? 'page' : undefined}>Sync health</a>
          <a href="/library" onClick={e => { e.preventDefault(); to({ view: 'library' }) }} aria-current={view === 'library' ? 'page' : undefined}>Comm library</a>
          {canAdmin && <a href="/connectors" onClick={e => { e.preventDefault(); to({ view: 'connectors' }) }} aria-current={view === 'connectors' ? 'page' : undefined}>Connectors</a>}
          <a href="/templates" onClick={e => { e.preventDefault(); to({ view: 'templates' }) }} aria-current={view === 'templates' ? 'page' : undefined}>Templates</a>
          {me.roles.some(r => r === 'RTE' || r === 'ReleaseManager' || r === 'GovernanceOfficer') && <a href="/audit" onClick={e => { e.preventDefault(); to({ view: 'audit' }) }} aria-current={view === 'audit' ? 'page' : undefined}>Audit</a>}
          {canAdmin && <a href="/admin" onClick={e => { e.preventDefault(); to({ view: 'admin' }) }} aria-current={view === 'admin' ? 'page' : undefined}>Admin</a>}
        </nav>
        <span className="spacer" />
        <LiveStatus state={live.state} time={live.serverTime} />
        <ThemeChoices />
        <span className="muted">{me.name} · {me.roles.join(', ')}</span>
        <button type="button" className="text" onClick={async () => { await logout(); onSignedOut() }}>Sign out</button>
        <SessionControls onSignedOut={onSignedOut} onStopped={onStopped} />
      </header>
      <SyncBanner sync={sync} onOpen={() => to({ view: 'sync' })} />
      <aside className="stream" aria-label="Stream">
        <Stream rows={rows} selected={selected} onSelect={id => go({ view: 'trains', trainId: id, selection: null, mode: 'plan' })}
          onNew={canAdmin ? () => { setNewTrain({ ...(newTrain ?? emptyNewTrain()), open: true }); if (view !== 'trains') go({ ...route, view: 'trains' }) } : undefined} />
        <FreezeFooter refreshKey={rev} />
      </aside>
      <main className={view !== 'trains' ? 'workspace wide' : 'workspace'}>
        {session.saveError && <p className="warn" role="alert">▲ {session.saveError}</p>}
        {view === 'admin' ? <Admin canEdit={canAdmin} /> : view === 'work' ? <MyWork me={me} onOpen={open} refreshKey={rev + notifRev} /> : view === 'inbox' ? <Inbox me={me} onOpen={open} onChanged={refreshUnread} refreshKey={notifRev} /> : view === 'audit' ? <AuditViewer me={me} /> : view === 'templates' ? <Templates me={me} /> : view === 'sync' ? <SyncHealth me={me} /> : view === 'analytics' ? <Analytics me={me} /> : view === 'calendar' ? <Calendar me={me} /> : view === 'importexport' ? <ImportExport me={me} /> : view === 'connectors' ? <Connectors me={me} /> : view === 'library' ? <CommLibrary me={me} /> : (selected && mode !== 'plan' ? <LiveRun trainId={selected} mode={mode} canPlan={canAdmin} data={runData} selection={selection} onSelect={s => to({ selection: s })} onMode={m => to({ mode: m, selection: null })} onChanged={refetch} /> : selected ? <TrainHeader id={selected} refreshKey={rev} onChanged={refetch} selection={selection} onSelect={s => to({ selection: s })} canPlan={canAdmin} canDecide={me.roles.includes('ReleaseManager')} onMode={m => to({ mode: m, selection: null })} /> : <><h1>Trains</h1><p className="muted">{rows && rows.length === 0 ? 'No trains yet. Create one to start planning.' : 'Select a train in the Stream.'}</p></>)}
      </main>
      {view === 'trains' && (
        <aside className="inspector" aria-label="Inspector">
          {newTrain?.open && canAdmin
            ? <NewTrainDrawer trains={rows ?? []}
                onCreated={t => { reload(); go({ view: 'trains', trainId: t.id, selection: null, mode: 'plan' }) }} />
            : selected && comms?.open
            ? <CommsDrawer trainId={selected} canDispatch={canAdmin} refreshKey={rev} onClose={() => setComms(undefined)} onChanged={refetch} />
            : selected && bulk?.open && canAdmin
            ? <BulkDrawer trainId={selected} gates={gates} onClose={() => setBulk(bulk && bulk.text ? { ...bulk, open: false } : undefined)} onChanged={refetch} />
            : selected && mode !== 'plan' && selection?.kind === 'step'
              ? <RunStepDrawer stepId={selection.id} data={runData} onClose={() => to({ selection: null })} onChanged={refetch} />
              : <Inspector selection={selection} trainId={selected} canPlan={canAdmin} refreshKey={rev} onClose={() => to({ selection: null })} onChanged={refetch} />}
        </aside>
      )}
    </div>
  )
}
