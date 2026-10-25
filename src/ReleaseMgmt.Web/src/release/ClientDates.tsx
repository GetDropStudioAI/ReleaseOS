import { useState } from 'react'
import { ApiError } from '../api.ts'
import { announce } from '../announce.tsx'
import { useAction } from '../useAction.ts'
import { errMsg } from '../format.ts'
import { Button, ConfirmDialog, DataTable, DisabledAction, EmptyState, ErrorState, LoadingState, Panel, StatusChip, Time, TwoLine, type Column } from '../ui'
import { Field, Problem, TextInput } from '../mgmt/forms.tsx'
import { useLoad } from './useLoad.ts'
import { getClientSchedule, scheduleClients, setClientDates } from './clientDatesApi.ts'
import { advisories, againstStandard, dateProblem, datePatch, dateSource, dateText, kindLabel, segmentWord, summaryText, type ClientDateRow, type ClientSchedule } from './clientDatesLogic.ts'

/** The ▲ + "Advisory:" + words of the Advisories style (Advisories.tsx): warning colour for the glyph, the word for everyone else. */
function Advisory({ children }: { children: string }) {
  return <span className="cd-advisory"><span className="warn" aria-hidden="true">▲</span><span className="sr-only">Advisory:</span> {children} <span className="os-muted">(advisory)</span></span>
}

/**
 * Client dates (REOS-119, RT4, D47), a section of the release's Overview for CERT and Production releases: every client with its date and segment in one table.
 * Standard clients take the release's Standard date; each On-Demand client has an offered date (Standard plus the configured offset) and may have a chosen one, recorded
 * with who and when. A date inside a freeze or chill window is an advisory beside the row, never a refusal. Recording or clearing a date is for planners; clearing asks first.
 */
export function ClientDates({ trainId, refreshKey, canPlan, onChanged }: { trainId: string; refreshKey: number; canPlan: boolean; onChanged?: () => void }) {
  const load = useLoad(() => getClientSchedule(trainId), [trainId, refreshKey])
  const [selected, setSelected] = useState<string | null>(null)
  const [chosen, setChosen] = useState('')
  const [offered, setOffered] = useState('')
  const [problem, setProblem] = useState<string | null>(null)
  const [done, setDone] = useState<string | null>(null)
  const [confirm, setConfirm] = useState<ClientDateRow | null>(null)
  const { run, pending } = useAction()
  const view = load.data
  const sel = view?.rows.find(r => r.clientId === selected) ?? null

  const say = (text: string | null) => { setDone(text); if (text) announce(text) }
  const pick = (r: ClientDateRow) => { setSelected(r.clientId); setChosen(r.chosenDate ?? ''); setOffered(r.offeredDate ?? ''); setProblem(null); say(null) }
  const conflict = (e: unknown, what: string) => (e instanceof ApiError && e.status === 409 ? `Someone else changed ${what}. Reloaded; try again.` : errMsg(e))
  const show = (next: ClientSchedule, row?: string) => { load.reload(); onChanged?.(); const r = next.rows.find(x => x.clientId === row); if (r) { setChosen(r.chosenDate ?? ''); setOffered(r.offeredDate ?? '') } }

  const save = (r: ClientDateRow, body: { chosenDate?: string; offeredDate?: string }, message: string) => run('Saving…', async () => {
    setProblem(null); say(null)
    try { const next = await setClientDates(trainId, r.clientId, body, r.version); say(message); setConfirm(null); show(next, r.clientId) }
    catch (e) { setProblem(conflict(e, r.name)); setConfirm(null); load.reload() }
  })
  const scheduleAll = () => run('Scheduling…', async () => {
    setProblem(null); say(null)
    try { const next = await scheduleClients(trainId); say('On-Demand clients scheduled'); show(next) }
    catch (e) { setProblem(errMsg(e)); load.reload() }
  })

  if (load.error && !view) return <ErrorState what="Client dates" message={load.error} onRetry={load.reload} />
  if (!view) return <LoadingState what="client dates" />
  if (!view.kind) return null   // this release's type carries no client dates (Beta, PenTest Cert...): nothing to show or ask
  const open = view.open
  const columns: Column<ClientDateRow>[] = [
    { key: 'client', header: 'Client', cell: r => <TwoLine primary={r.name} secondary={r.archived ? 'Archived' : undefined} /> },
    { key: 'segment', header: 'Segment', cell: r => <StatusChip tone={r.segment === 'OnDemand' ? 'info' : 'neutral'}>{segmentWord(r.segment)}</StatusChip> },
    { key: 'date', header: kindLabel(view.kind) + ' date', cell: r => <TwoLine primary={<span className="mono">{dateText(r.date)}</span>} secondary={`${dateSource(r)} · ${againstStandard(r.date, view.standardDate)} · ${r.wave} wave`} /> },
    { key: 'offered', header: 'Offered', cell: r => r.offeredDate ? <span className="mono">{dateText(r.offeredDate)}</span> : <span className="os-muted">Not applicable</span> },
    { key: 'chosen', header: 'Chosen by the client', cell: r => r.chosenDate
        ? <TwoLine primary={<span className="mono">{dateText(r.chosenDate)}</span>} secondary={r.chosenAt ? <>Recorded by {r.chosenBy?.name ?? 'unknown'} · <Time at={r.chosenAt} date /></> : undefined} />
        : <span className="os-muted">{r.segment === 'Standard' ? 'Not applicable' : 'None'}</span> },
    { key: 'advisory', header: 'Advisories', cell: r => { const a = advisories(r, view.standardDate); return a.length ? <ul className="cd-list">{a.map(t => <li key={t}><Advisory>{t}</Advisory></li>)}</ul> : <span className="os-muted">None</span> } },
  ]
  const dirty = !!sel && datePatch(sel, chosen, offered) !== null
  const formProblem = sel ? dateProblem(chosen, offered, view.standardDate) : null
  return (
    <Panel label="Client dates" title="Client dates" subtitle={<>{kindLabel(view.kind)} release · Standard date <span className="mono">{dateText(view.standardDate)}</span> · On-Demand clients are offered Standard + {view.offsetDays} {view.offsetDays === 1 ? 'day' : 'days'}</>}
      meta={<>
        <span className="cd-summary">{summaryText(view)}</span>
        {view.warnings > 0 && <StatusChip tone="warning">{`${view.warnings} ${view.warnings === 1 ? 'advisory' : 'advisories'}`}</StatusChip>}
        {view.unscheduled > 0 && <StatusChip tone="neutral">{`${view.unscheduled} not scheduled yet`}</StatusChip>}
      </>}>
      <div className="cd-stack">
        {load.error && <ErrorState what="Client dates" message={load.error} onRetry={load.reload} />}
        {!open && <p className="mg-note">This release is complete, aborted or archived: its client dates no longer change.</p>}
        <DataTable caption="Client dates" columns={columns} rows={view.rows} rowKey={r => r.clientId} selectedKey={selected} onSelect={pick}
          empty={<EmptyState title="No clients yet">Add clients under Teams &amp; settings, Clients. A {kindLabel(view.kind)} release lists {view.kind === 'Cert' ? 'the On-Demand clients (they have a CERT environment)' : 'every client'}.</EmptyState>} />
        {canPlan && open && view.unscheduled > 0 && (
          <div className="mg-actions">
            <Button disabled={!!pending} onClick={scheduleAll}>{pending === 'Scheduling…' ? pending : `Schedule ${view.unscheduled} On-Demand ${view.unscheduled === 1 ? 'client' : 'clients'}`}</Button>
            <span className="mg-note">Records the offered date for each, so a later change of the Standard date moves it with the release.</span>
          </div>
        )}
        {sel && (
          <div className="cd-edit" role="group" aria-label={`Dates for ${sel.name}`}>
            <p className="mg-subhead">{sel.name}</p>
            {sel.segment === 'Standard'
              ? <p className="mg-note">A Standard client takes the Standard date ({dateText(view.standardDate)}). Move the release's target date to change it.</p>
              : !canPlan || !open ? <p className="mg-note">{open ? 'Only an RTE or Release Manager can record client dates.' : 'This release is closed.'}</p>
              : (
                <>
                  <div className="mg-two">
                    <Field label="Chosen date" hint="The date the client picked. Leave empty if the client takes the offered date."><TextInput type="date" value={chosen} min={view.standardDate} onChange={e => setChosen(e.target.value)} /></Field>
                    <Field label="Offered date" hint={`Ours. Default: Standard + ${view.offsetDays} days.`}><TextInput type="date" value={offered} min={view.standardDate} onChange={e => setOffered(e.target.value)} /></Field>
                  </div>
                  <Problem message={problem} />
                  <div className="mg-actions">
                    {formProblem || !dirty ? <DisabledAction variant="primary" reason={formProblem ?? 'Change a date first.'}>Save dates</DisabledAction>
                      : <Button variant="primary" disabled={!!pending} onClick={() => save(sel, datePatch(sel, chosen, offered)!, `Dates for ${sel.name} saved`)}>{pending === 'Saving…' ? pending : 'Save dates'}</Button>}
                    {sel.chosenDate && <Button variant="destructive" disabled={!!pending} onClick={() => setConfirm(sel)}>Clear chosen date</Button>}
                  </div>
                  {sel.chosenAt && <p className="mg-note">Chosen date recorded by {sel.chosenBy?.name ?? 'unknown'}, <Time at={sel.chosenAt} date />.</p>}
                </>)}
            {done && <p className="mg-ok" role="status"><span aria-hidden="true">✓ </span>{done}</p>}
          </div>
        )}
      </div>
      <ConfirmDialog open={!!confirm} title={`Clear the chosen date for ${confirm?.name ?? ''}?`} confirmLabel="Clear chosen date" pendingLabel="Clearing…" destructive pending={!!pending}
        onCancel={() => setConfirm(null)} onConfirm={() => save(confirm!, { chosenDate: '' }, `${confirm!.name} takes the offered date again`)}>
        The client goes back to the offered date{confirm?.offeredDate ? ` (${dateText(confirm.offeredDate)})` : ''}. Who recorded the choice and when stays in the audit trail.
      </ConfirmDialog>
    </Panel>
  )
}
