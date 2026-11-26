import { useCallback, useEffect, useState } from 'react'
import { ApiError } from '../api.ts'
import { announce } from '../announce.tsx'
import { useAction } from '../useAction.ts'
import { errMsg } from '../format.ts'
import { Button, ConfirmDialog, DataTable, DisabledAction, EmptyState, ErrorState, LoadingState, Panel, PropertyList, StatusChip, TwoLine, type Column } from '../ui'
import { Problem, When } from './forms.tsx'
import { AiSettingsPanel } from './AiSettings.tsx'
import { disableModule, enableModule, listModules, notifyModulesChanged } from './modulesApi.ts'
import { DATA_KEPT, disableIntro, disableLabel, enableBlocker, enableIntro, enableLabel, planLabel, sortModules, stateLabel, summary, type ModuleRow } from './modulesLogic.ts'

/**
 * Modules (REOS-174; D56, D57, D63), a tab of Teams & settings for RTEs and Release Managers: the add-on modules, each off until enabled. A table (module, state, plan) with a detail panel:
 * what the module is, who enabled it and when, and one button. "Enable <module>" asks in a confirmation dialog that states what turns on; "Disable <module>" asks again and says the
 * data is kept. There is no toggle switch (CLAUDE.md rule 9). When the plan does not include a module the button is replaced by the reason in words.
 */
export function ModulesTab() {
  const [rows, setRows] = useState<ModuleRow[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [selected, setSelected] = useState<string | null>(null)
  const [confirm, setConfirm] = useState<{ row: ModuleRow; enable: boolean } | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [done, setDone] = useState<string | null>(null)
  const { run, pending } = useAction()

  const reload = useCallback(() => { listModules().then(r => { setRows(sortModules(r)); setError(null) }).catch(e => setError(errMsg(e))) }, [])
  useEffect(() => { reload() }, [reload])

  const say = (text: string | null) => { setDone(text); if (text) announce(text) }
  const sel = rows?.find(r => r.key === selected) ?? rows?.[0]
  const pick = (r: ModuleRow) => { setSelected(r.key); setProblem(null); say(null) }

  const change = (r: ModuleRow, enable: boolean) => run(enable ? 'Enabling…' : 'Disabling…', async () => {
    setProblem(null); say(null)
    try {
      await (enable ? enableModule : disableModule)(r.key, r.version)
      say(enable ? `${r.name} enabled` : `${r.name} disabled`)
      notifyModulesChanged()
    } catch (e) {
      setProblem(e instanceof ApiError && e.status === 409 ? `Someone else changed the ${r.name}. Reloaded; check its state and try again.` : errMsg(e))
    } finally { setConfirm(null); reload() }
  })

  const columns: Column<ModuleRow>[] = [
    { key: 'name', header: 'Module', cell: r => <TwoLine primary={r.name} secondary={r.summary} /> },
    { key: 'state', header: 'State', cell: r => <StatusChip tone={r.enabled ? 'success' : 'neutral'}>{stateLabel(r)}</StatusChip> },
    { key: 'plan', header: 'Plan', cell: r => planLabel(r) },
  ]

  if (error && !rows) return <ErrorState what="Modules" message={error} onRetry={reload} />
  if (!rows) return <LoadingState what="modules" />
  const blocker = sel ? enableBlocker(sel) : null
  return (
    <div className="mg-split">
      <Panel title="Modules" subtitle="Add-ons that are off until you enable them. The core product works without them" meta={summary(rows)}>
        <DataTable caption="Modules" columns={columns} rows={rows} rowKey={r => r.key} selectedKey={sel?.key ?? null} onSelect={pick} empty="No modules are available." />
      </Panel>
      <div className="mg-side">
        <Panel title={sel ? sel.name : 'Module detail'} subtitle={sel ? stateLabel(sel) : undefined}>
          {!sel ? <EmptyState title="Select a module">Pick a row to see what it turns on.</EmptyState> : (
            <div className="mg-stack">
              <p className="mg-note">{sel.summary}</p>
              <PropertyList items={[
                { label: 'State', value: stateLabel(sel) },
                { label: 'Plan', value: planLabel(sel) },
                { label: sel.enabled ? 'Enabled by' : 'Last enabled by', value: sel.enabledBy?.name ?? 'Never enabled' },
                { label: sel.enabled ? 'Enabled' : 'Last enabled', value: sel.enabledAt ? <When iso={sel.enabledAt} /> : 'Never enabled' },
                { label: 'Last changed', value: sel.lastChangedAt ? <When iso={sel.lastChangedAt} /> : 'Never' },
                { label: 'Version', value: String(sel.version), mono: true },
              ]} />
              <Problem message={problem} />
              <div className="mg-actions">
                {sel.enabled
                  ? <Button disabled={!!pending} onClick={() => setConfirm({ row: sel, enable: false })}>{disableLabel(sel)}</Button>
                  : blocker ? <DisabledAction variant="primary" reason={blocker}>{enableLabel(sel)}</DisabledAction>
                    : <Button variant="primary" disabled={!!pending} onClick={() => setConfirm({ row: sel, enable: true })}>{enableLabel(sel)}</Button>}
              </div>
              {done && <p className="mg-ok" role="status"><span aria-hidden="true">✓ </span>{done}</p>}
            </div>
          )}
        </Panel>
        {sel?.key === 'ai' && <AiSettingsPanel reloadKey={sel.version} />}
      </div>
      <ConfirmDialog open={!!confirm} title={confirm ? (confirm.enable ? `${enableLabel(confirm.row)}?` : `${disableLabel(confirm.row)}?`) : ''}
        confirmLabel={confirm ? (confirm.enable ? enableLabel(confirm.row) : disableLabel(confirm.row)) : ''} pendingLabel={confirm?.enable ? 'Enabling…' : 'Disabling…'}
        pending={!!pending} onCancel={() => setConfirm(null)} onConfirm={() => confirm && change(confirm.row, confirm.enable)}>
        {confirm && (
          <>
            <p>{confirm.enable ? enableIntro(confirm.row) : disableIntro(confirm.row)}</p>
            <ul>{confirm.row.turnsOn.map(t => <li key={t}>{t}</li>)}</ul>
            {!confirm.enable && <p>{DATA_KEPT}</p>}
            {confirm.enable && <p>Every change is recorded in the audit history. You can disable the module again at any time.</p>}
          </>
        )}
      </ConfirmDialog>
    </div>
  )
}
