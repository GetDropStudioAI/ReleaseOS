import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { getMe, type Me } from '../api'
import { ErrorState, LoadingState } from '../ui'
import { getWorkspaces } from '../workspaceApi'
import { choose, pick, remembered, type WorkspaceChoice } from '../workspace'

interface Ctx { choices: WorkspaceChoice[]; current: WorkspaceChoice | null; reload: () => void }
const WorkspaceCtx = createContext<Ctx>({ choices: [], current: null, reload: () => {} })

/** The person's workspaces and the one this page is in (REOS-106). The switcher and the workspace pages read it. */
export const useWorkspaces = () => useContext(WorkspaceCtx)

/**
 * Names the workspace before anything else is read (REOS-106): the person's memberships, then the one they chose last time on this browser (else the one
 * the server picked for the first request). `me` was read before a workspace was named, so it is read again when the choice differs. Everything below
 * this component starts in the chosen workspace; switching reloads the page (workspace.ts), so nothing of the old one survives on screen.
 */
export function WorkspaceGate({ me, onMe, children }: { me: Me; onMe: (me: Me | null) => void; children: ReactNode }) {
  const [choices, setChoices] = useState<WorkspaceChoice[] | null>(null)
  const [current, setCurrent] = useState<WorkspaceChoice | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [attempt, setAttempt] = useState(0)

  useEffect(() => {
    let live = true
    setError(null)
    getWorkspaces().then(async list => {
      const chosen = pick(list, remembered(me.id), me.workspaceId)
      if (chosen) choose(chosen.slug, me.id)
      if (chosen && me.workspaceId !== chosen.id) { const fresh = await getMe(); if (!live) return; if (!fresh) { onMe(null); return }; onMe(fresh) }
      if (!live) return
      setChoices(list); setCurrent(chosen)
    }).catch((e: Error) => { if (live) setError(e.message) })
    return () => { live = false }
  }, [me.id, attempt])   // eslint-disable-line react-hooks/exhaustive-deps -- me.workspaceId and onMe change when this very effect re-reads me

  const reload = useCallback(() => { getWorkspaces().then(list => { setChoices(list); setCurrent(c => list.find(w => w.id === c?.id) ?? c) }).catch((e: Error) => console.error('The workspaces could not be read', e)) }, [])
  const value = useMemo(() => ({ choices: choices ?? [], current, reload }), [choices, current, reload])

  if (error) return <main className="signin"><ErrorState what="Your workspaces" message={error} onRetry={() => setAttempt(n => n + 1)} /></main>
  if (choices === null) return <main className="signin"><LoadingState what="your workspaces" /></main>
  return <WorkspaceCtx.Provider value={value}>{children}</WorkspaceCtx.Provider>
}
