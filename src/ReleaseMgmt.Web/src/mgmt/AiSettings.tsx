import { useCallback, useEffect, useState } from 'react'
import { announce } from '../announce.tsx'
import { useAction } from '../useAction.ts'
import { errMsg } from '../format.ts'
import { Button, ConfirmDialog, ErrorState, LoadingState, Panel, PropertyList, StatusChip } from '../ui'
import { Problem } from './forms.tsx'
import { aiAction, getAiStatus } from './aiApi.ts'
import { ACTION_NOTES, actionDone, actionIntro, actionIsRestrictive, actionLabel, callLabel, draftingLabel, globalLabel, keyLabel, moduleLabel, optOutAction, optOutLabel, pauseAction, pausedLabel, smallLabel, type AiAction, type AiStatus } from './aiLogic.ts'

/**
 * AI settings (REOS-183; D58), shown under the AI module on the Modules tab. Read-only facts (module, provider, whether a key is stored, never the key, the configured models, the global kill switch)
 * and two workspace controls, each a button with a confirmation dialog: pause AI (the workspace kill switch) and opt the workspace out. No toggle switches (CLAUDE.md rule 9).
 */
export function AiSettingsPanel({ reloadKey }: { reloadKey?: number }) {
  const [status, setStatus] = useState<AiStatus | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [confirm, setConfirm] = useState<AiAction | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [done, setDone] = useState<string | null>(null)
  const { run, pending } = useAction()

  const reload = useCallback(() => { getAiStatus().then(s => { setStatus(s); setError(null) }).catch(e => setError(errMsg(e))) }, [])
  useEffect(() => { reload() }, [reload, reloadKey])

  const change = (a: AiAction) => run('Saving…', async () => {
    setProblem(null); setDone(null)
    try {
      setStatus(await aiAction(a))
      setDone(actionDone[a]); announce(actionDone[a])
    } catch (e) { setProblem(errMsg(e)) } finally { setConfirm(null) }
  })

  if (error && !status) return <ErrorState what="AI settings" message={error} onRetry={reload} />
  if (!status) return <LoadingState what="AI settings" />
  const pa = pauseAction(status), oa = optOutAction(status)
  return (
    <>
      <Panel title="AI settings" subtitle="Advisory only. Every call goes through one governed place" meta={<StatusChip tone={status.canCall ? 'success' : 'neutral'}>{status.canCall ? 'Calls allowed' : 'Calls off'}</StatusChip>}>
        <div className="mg-stack">
          <p className="mg-note" role="status">{callLabel(status)}</p>
          <PropertyList items={[
            { label: 'Module', value: moduleLabel(status) },
            { label: 'Provider', value: status.provider },
            { label: 'Provider key', value: keyLabel(status) },
            { label: 'Small model', value: smallLabel(status), mono: true },
            { label: 'Drafting model', value: draftingLabel(status), mono: true },
            { label: 'Global kill switch', value: globalLabel(status) },
            { label: 'This workspace', value: pausedLabel(status) },
            { label: 'Data use', value: optOutLabel(status) },
            { label: 'Timeout and retries', value: `${status.timeoutSeconds} seconds, up to ${status.maxRetries} retries` },
          ]} />
          <Problem message={problem} />
          <div className="mg-actions">
            <Button disabled={!!pending} onClick={() => setConfirm(pa)}>{actionLabel[pa]}</Button>
            <Button disabled={!!pending} onClick={() => setConfirm(oa)}>{actionLabel[oa]}</Button>
          </div>
          {done && <p className="mg-ok" role="status"><span aria-hidden="true">✓ </span>{done}</p>}
        </div>
      </Panel>
      <ConfirmDialog open={!!confirm} title={confirm ? `${actionLabel[confirm]}?` : ''} confirmLabel={confirm ? actionLabel[confirm] : ''} pendingLabel="Saving…"
        destructive={confirm ? actionIsRestrictive(confirm) : false} pending={!!pending} onCancel={() => setConfirm(null)} onConfirm={() => confirm && change(confirm)}>
        {confirm && (
          <>
            <p>{actionIntro[confirm]}</p>
            <ul>{ACTION_NOTES.map(n => <li key={n}>{n}</li>)}</ul>
          </>
        )}
      </ConfirmDialog>
    </>
  )
}
