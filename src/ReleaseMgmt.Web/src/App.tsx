import { lazy, Suspense, useEffect, useRef, useState } from 'react'
import { getMe, type Me } from './api'
import { NotFound, SignIn, Starting, Stopped, Unreachable } from './startup/Startup'
import Admin from './Admin'
import { MyWork } from './MyWork'
import { Inbox } from './Inbox'
import { AuditViewer } from './AuditViewer'
import { Templates } from './Templates'
import { Analytics } from './Analytics'
import { Calendar } from './Calendar'
import { ImportExport } from './exchange/ImportExport'
import { SyncHealth } from './SyncHealth'
import { SyncBanner, notifySyncChanged, useSyncState } from './SyncBanner'
import { Connectors } from './Connectors'
import { CommLibrary } from './CommLibrary'
import { TrainHeader, useStream } from './Trains'
import { Releases } from './releases/Releases'
import { Inspector } from './Planning'
import { Orchestration } from './orchestration/Orchestration'
import { BulkDrawer, bulkKey, type BulkDraft } from './Bulk'
import { CommsDrawer, commsKey, type CommsDraft } from './CommsDrawer'
import { NewTrainDrawer, emptyNewTrain, newTrainKey, type NewTrainDraft } from './NewTrain'
import { getTrain, getUnreadCount, type TrainDetail } from './api'
import { useDraft } from './session'
import { isKnownPath, parsePath, ROOT, useRoute, type Route } from './route'
import { SessionProvider, useSession } from './session'
import { useLive } from './live'
import { setDisplayZone } from './time'
import { getConfig } from './api'
import { Announcer } from './announce'
import { Sidebar } from './shell/Sidebar'
import { WorkspaceGate } from './shell/WorkspaceGate'
import { UtilityBar } from './shell/UtilityBar'
import { Placeholder } from './shell/Placeholder'
import { Deployments } from './deploy/Deployments'
import { Environments } from './environments/Environments'
import { Overview } from './shell/Overview.tsx'
import { Approvals } from './shell/Approvals'
import { useApprovalCount, useHostHealth, useWorkspace } from './shell/useShell'
import { canAdmin as adminRole, canAudit, jumpTargets, pageName, placeholderOf } from './shell/nav.ts'
import { auditPath } from './auditLogic'
import { controlPlane } from './shell/health.ts'
import './shell/shell.css'

// The component gallery is a review page (REOS-93): loaded only when opened, so it adds nothing to the app's first load.
const Gallery = lazy(() => import('./Gallery').then(m => ({ default: m.Gallery })))

export default function App() {
  const [me, setMe] = useState<Me | null | undefined>(undefined)
  const [failed, setFailed] = useState(false)
  const [stopped, setStopped] = useState(false)
  const refresh = () => getMe().then(setMe).catch(() => setFailed(true))
  useEffect(() => { refresh() }, [])

  if (stopped) return <Stopped />
  if (failed) return <Unreachable />
  if (me === undefined) return <Starting />
  if (me === null) return <SignIn onDone={refresh} />
  if (!isKnownPath(window.location.pathname)) return <NotFound path={window.location.pathname} />
  return <WorkspaceGate me={me} onMe={setMe}><SessionProvider enabled><Signed me={me} onSignedOut={() => setMe(null)} onStopped={() => setStopped(true)} /></SessionProvider></WorkspaceGate>
}

function Signed({ me, onSignedOut, onStopped }: { me: Me; onSignedOut: () => void; onStopped: () => void }) {
  const { route, go } = useRoute()
  const session = useSession()
  const [zoneReady, setZoneReady] = useState(false)
  useEffect(() => { getConfig().then(c => setDisplayZone(c.displayTimeZone)).catch(() => { /* keep the D24 default */ }).finally(() => setZoneReady(true)) }, [])
  const { rows, error: rowsError, loadedAt, reload } = useStream(true)
  const [rev, setRev] = useState(0)
  const refetch = () => { setRev(n => n + 1); reload() }
  const [unread, setUnread] = useState(0)
  const [notifRev, setNotifRev] = useState(0)
  const refreshUnread = () => { getUnreadCount().then(c => setUnread(c.unread)).catch(() => { /* the nav count is a hint; the Inbox shows real errors */ }) }
  useEffect(refreshUnread, [])
  const sync = useSyncState(true)   // REOS-42: connector-wide banner state; refetches on notifySyncChanged, every minute, and after a reconnect
  // REOS-94: the shell's sources. A train change or a notification may add or settle a decision (the Approvals badge); a reconnect re-reads everything.
  const [shellRev, setShellRev] = useState(0)
  const bumpShell = () => setShellRev(n => n + 1)
  const workspace = useWorkspace()
  const approvals = useApprovalCount(shellRev)
  const host = useHostHealth(shellRev)
  const live = useLive(true, { onTrainChanged: () => { refetch(); bumpShell() }, onResync: () => { refetch(); refreshUnread(); setNotifRev(n => n + 1); notifySyncChanged(); bumpShell() }, onForecastChanged: refetch, onNotification: () => { refreshUnread(); setNotifRev(n => n + 1); bumpShell() }, onSyncAlert: notifySyncChanged })
  const { view, trainId: selected, selection } = route
  const mode = route.mode ?? 'plan'
  const [bulk, setBulk] = useDraft<BulkDraft>(bulkKey(selected ?? ''))
  const [comms, setComms] = useDraft<CommsDraft>(commsKey(selected ?? ''))   // REOS-45
  const [newTrain, setNewTrain] = useDraft<NewTrainDraft>(newTrainKey)   // REOS-80: the New train drawer wins the right-hand column while open
  const [gates, setGates] = useState<TrainDetail['gates']>([])
  // The train last opened in the Stream: the default for one-train pickers on other pages, so it is never picked twice (UX review #19).
  const [lastTrain, setLastTrain] = useState<string | null>(selected)
  useEffect(() => { if (selected) setLastTrain(selected) }, [selected])
  useEffect(() => { if (selected && bulk?.open) getTrain(selected).then(t => setGates(t.gates)).catch(() => setGates([])) }, [selected, bulk?.open, rev])

  // A URL that names nothing (opening the app fresh) restores where this or the most recent tab was; a deep link always wins.
  const restored = useRef(false)
  useEffect(() => {
    if (!session.ready || restored.current) return
    restored.current = true
    if (parsePath(window.location.pathname) === ROOT && session.restoredRoute && (session.restoredRoute.trainId || session.restoredRoute.view === 'admin')) go({ ...ROOT, ...session.restoredRoute }, true)
    // REOS-143: a deep link to a gate, task, product or step opens that Inspector. A saved drawer would take the column, so it starts closed; its draft stays saved.
    if (route.view === 'trains' && route.selection) {
      if (comms?.open) setComms({ ...comms, open: false })
      if (bulk?.open) setBulk({ ...bulk, open: false })
      if (newTrain?.open) setNewTrain({ ...newTrain, open: false })
    }
  }, [session.ready, session.restoredRoute, go])   // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (session.ready && restored.current) session.saveRoute(route) }, [route, session.ready])   // eslint-disable-line react-hooks/exhaustive-deps

  // WCAG 2.4.2 / 2.4.3: every view has its own document title, and a navigation the user made moves focus to the new page.
  const trainTitle = rows?.find(r => r.id === selected)?.title
  useEffect(() => {
    const page = view === 'trains' && trainTitle ? trainTitle : view === 'orchestration' && trainTitle ? `Orchestration · ${trainTitle}${mode !== 'plan' ? ` · ${mode === 'live' ? 'Live run' : 'Rehearsal'}` : ''}` : pageName(view)
    document.title = `${page} · ReleaseOS`
  }, [view, trainTitle, mode])
  // docs/UI.md: Esc closes the Inspector or step drawer. Keys typed into a field, or already handled (the Comms drawer, confirms), are left alone.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const t = e.target as HTMLElement | null
      if (e.key !== 'Escape' || e.defaultPrevented || !t || t.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(t.tagName)) return
      if (view !== 'trains' || !selection || comms?.open || bulk?.open) return
      e.preventDefault(); go({ ...route, selection: null })
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [view, selection, comms?.open, bulk?.open, route, go])
  const mainRef = useRef<HTMLElement>(null)
  const lastPage = useRef<string | null>(null)
  // Only a navigation the user made moves focus: restoring the last route when the app opens must not.
  const interacted = useRef(false)
  useEffect(() => {
    const mark = () => { interacted.current = true }
    window.addEventListener('pointerdown', mark, true); window.addEventListener('keydown', mark, true)
    return () => { window.removeEventListener('pointerdown', mark, true); window.removeEventListener('keydown', mark, true) }
  }, [])
  useEffect(() => {
    const key = `${view}|${selected ?? ''}|${mode}`
    if (lastPage.current !== null && lastPage.current !== key && interacted.current) {
      const h1 = mainRef.current?.querySelector('h1') as HTMLElement | null
      const target = h1 ?? mainRef.current
      if (target) { if (!target.hasAttribute('tabindex')) target.setAttribute('tabindex', '-1'); target.focus({ preventScroll: true }) }
    }
    lastPage.current = key
  }, [view, selected, mode])

  if (!session.ready || !zoneReady) return <Starting />

  const canAdmin = adminRole(me.roles)
  const to = (r: Partial<Route>) => go({ ...route, ...r })
  const open = (r: Partial<Route>) => go({ ...ROOT, ...r })   // from My work / Inbox / Approvals: open a train (view resets to trains)
  const toView = (v: Route['view']) => go({ ...ROOT, view: v })   // D50: the sidebar's Releases is the list (the Stream that kept a release open beside every page is gone)
  const releases = view === 'trains'   // the Inspector belongs to the Releases destination; every other page uses the full width
  // D50 (REOS-125): Releases is the full-width table until a release is open (or the New train drawer is); then the release page has the Inspector beside it. The Stream is gone.
  const listPage = releases && !selected && !(newTrain?.open && canAdmin)
  const redesigned = view === 'approvals' || view === 'work' || view === 'inbox' || view === 'admin' || view === 'templates' || view === 'library' || view === 'connectors' || view === 'audit' || view === 'overview' || view === 'calendar' || view === 'analytics' || view === 'deployments' || view === 'orchestration' || view === 'sync' || placeholderOf(view) !== undefined   // drawn with the redesign's page layout (shell.css .os-page), which has its own gutters
  return (
    <div className="os-app">
      <a className="skip-link" href="#workspace" onClick={e => { e.preventDefault(); const m = document.getElementById('workspace'); m?.focus() }}>Skip to content</a>
      <Announcer />
      <Sidebar me={me} view={view} workspace={workspace} approvals={approvals} plane={controlPlane(host, sync.state, live.state)} onGo={toView}
        onSignedOut={onSignedOut} onStopped={onStopped} />
      <div className="os-main">
        <UtilityBar workspaceName={workspace?.name ?? 'Workspace'} page={pageName(view)} rows={rows} targets={jumpTargets(me.roles)} unread={unread} live={live.state}
          onTrain={id => go({ view: 'trains', trainId: id, selection: null, mode: 'plan' })} onGo={toView} />
        <SyncBanner sync={sync} onOpen={() => toView('sync')} />
        <div className={listPage || !releases ? 'shell full' : 'shell with-inspector'}>
          <main id="workspace" ref={mainRef} tabIndex={-1} className={releases && !selected ? 'workspace wide redesigned' : releases ? (selected && mode === 'plan' ? 'workspace redesigned' : 'workspace') : redesigned || view === 'environments' ? 'workspace wide redesigned' : 'workspace wide'}>
            {session.saveError && <p className="warn" role="alert">▲ {session.saveError}</p>}
            {view === 'orchestration' ? <Orchestration trains={rows} trainsError={rowsError} onReloadTrains={reload} trainId={selected} mode={mode} selection={selection} live={live.state} refreshKey={shellRev + rev} canPlan={canAdmin} canAudit={canAudit(me.roles)} onRoute={(r, replace) => go({ ...route, ...r }, replace)} onChanged={refetch} onOpenTrain={trainId => open({ trainId })} onOpenDeployment={id => { toView('deployments'); window.history.replaceState(null, '', `/deployments/${encodeURIComponent(id)}`) }} onOpenLogs={() => toView('audit')} /> : view === 'deployments' ? <Deployments live={live.state} refreshKey={shellRev + rev} canControl={canAdmin} onOpenTrain={trainId => open({ trainId })} /> : view === 'overview' ? <Overview refreshKey={shellRev + rev} canCreate={canAdmin} onCreate={() => { setNewTrain({ ...(newTrain ?? emptyNewTrain()), open: true }); toView('trains') }} onOpen={(trainId, gateId) => open({ trainId, selection: gateId ? { kind: 'gate', id: gateId } : null })} /> : view === 'approvals' ? <Approvals refreshKey={shellRev + rev} canAudit={canAudit(me.roles)} onViewAudit={id => { toView('audit'); window.history.replaceState(null, '', auditPath(id)) }} onOpen={(trainId, gateId) => open({ trainId, selection: gateId ? { kind: 'gate', id: gateId } : null })} /> : view === 'environments' ? <Environments me={me} /> : placeholderOf(view) ? <Placeholder view={view} onGo={toView} /> : view === 'gallery' ? <Suspense fallback={<p className="muted">Loading…</p>}><Gallery /></Suspense> : view === 'admin' ? <Admin canEdit={canAdmin} me={me} /> : view === 'work' ? <MyWork me={me} onOpen={open} onOpenApprovals={() => toView('approvals')} refreshKey={rev + notifRev} /> : view === 'inbox' ? <Inbox me={me} onOpen={open} onChanged={refreshUnread} refreshKey={notifRev} /> : view === 'audit' ? <AuditViewer me={me} onOpenTrain={trainId => open({ trainId })} /> : view === 'templates' ? <Templates me={me} /> : view === 'sync' ? <SyncHealth me={me} /> : view === 'analytics' ? <Analytics me={me} /> : view === 'calendar' ? <Calendar me={me} trainId={lastTrain} onOpen={open} onPlan={canAdmin ? () => { setNewTrain({ ...(newTrain ?? emptyNewTrain()), open: true }); toView('trains') } : undefined} /> : view === 'importexport' ? <ImportExport me={me} /> : view === 'connectors' ? <Connectors me={me} /> : view === 'library' ? <CommLibrary me={me} trainId={lastTrain} /> : (selected ? <TrainHeader id={selected} refreshKey={rev} onChanged={refetch} selection={selection} onSelect={s => to({ selection: s })} canPlan={canAdmin} canDecide={me.roles.includes('ReleaseManager')} onMode={m => to({ mode: m, selection: null })} /> : <Releases rows={rows} error={rowsError} live={live.state} loadedAt={loadedAt} canCreate={canAdmin} onReload={reload}
                onOpen={id => go({ view: 'trains', trainId: id, selection: null, mode: rows?.find(r => r.id === id)?.status === 'Executing' ? mode : 'plan' })}
                onNew={() => setNewTrain({ ...(newTrain ?? emptyNewTrain()), open: true })} onImport={() => to({ view: 'importexport' })} />)}
          </main>
          {releases && !listPage && (
            <aside className="inspector" aria-label="Inspector">
              {newTrain?.open && canAdmin
                ? <NewTrainDrawer trains={rows ?? []}
                    onCreated={t => { reload(); go({ view: 'trains', trainId: t.id, selection: null, mode: 'plan' }) }} />
                : selected && comms?.open
                ? <CommsDrawer trainId={selected} canDispatch={canAdmin} refreshKey={rev} onClose={() => setComms(undefined)} onChanged={refetch} />
                : selected && bulk?.open && canAdmin
                ? <BulkDrawer trainId={selected} gates={gates} onClose={() => setBulk(bulk && bulk.text ? { ...bulk, open: false } : undefined)} onChanged={refetch} />
                : <Inspector selection={selection} trainId={selected} canPlan={canAdmin} refreshKey={rev} onClose={() => to({ selection: null })} onChanged={refetch} />}
            </aside>
          )}
        </div>
      </div>
    </div>
  )
}
