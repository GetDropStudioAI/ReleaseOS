import { useEffect, useId, useState } from 'react'
import { getDecisions, type UserRow } from '../api.ts'
import { Button, DisabledAction, InputZoneNote, Panel, useZoneNoteId } from '../ui'
import { Field, Problem, SelectInput, TextInput } from '../mgmt/forms.tsx'
import { TYPES, TYPE_HINT, NONE, formProblem, type ChangeForm } from './cabLogic.ts'
import './cab.css'

export interface Choice { id: string; title: string }

/**
 * The change request form, for raising a change and for editing a Draft. Every field is a text box, a select or a date and time (no checkboxes, no switches). Times are typed in the person's
 * display zone, and the note beside each says what that comes to in UTC (D44). The risk level is not a field: it is worked out from the rules and shown after saving.
 */
export function ChangeEditor({ title, subtitle, form, onChange, releases, people, canPickRequester, saveLabel, pendingLabel, pending, problem, onSave, onCancel }: {
  title: string; subtitle?: string; form: ChangeForm; onChange: (f: ChangeForm) => void; releases: Choice[]; people: UserRow[]; canPickRequester: boolean
  saveLabel: string; pendingLabel: string; pending: boolean; problem: string | null; onSave: () => void; onCancel: () => void
}) {
  const [loadedOptions, setGoOptions] = useState<{ value: string; label: string }[]>([])
  const startNote = useZoneNoteId(), endNote = useZoneNoteId(), titleId = useId()
  useEffect(() => {
    if (!form.releaseId) return
    let live = true
    getDecisions(form.releaseId).then(rows => { if (live) setGoOptions(rows.map(r => ({ value: r.decision.id, label: `${r.decision.decision === 'NoGo' ? 'No-Go' : r.decision.decision === 'Go' ? 'Go' : 'Go with conditions'}, recorded ${r.decision.decidedAt.slice(0, 10)} UTC` }))) }).catch(() => { if (live) setGoOptions([]) })
    return () => { live = false }
  }, [form.releaseId])
  const goOptions = form.releaseId ? loadedOptions : []
  const set = (patch: Partial<ChangeForm>) => onChange({ ...form, ...patch })
  const why = formProblem(form)
  return (
    <Panel title={title} subtitle={subtitle}>
      <form className="mg-form" aria-label={title} onSubmit={e => { e.preventDefault(); if (!why && !pending) onSave() }}>
        <Problem message={problem} />
        <Field label="Title"><TextInput id={titleId} value={form.title} onChange={e => set({ title: e.target.value })} maxLength={200} autoComplete="off" aria-invalid={form.title.length > 0 && !form.title.trim()} /></Field>
        <Field label="Type" hint={TYPE_HINT[form.changeType]}><SelectInput value={form.changeType} onChange={e => set({ changeType: e.target.value })} options={TYPES.map(t => ({ value: t, label: t }))} /></Field>
        <Field label="Description"><textarea className="mg-input" rows={3} value={form.description} onChange={e => set({ description: e.target.value })} maxLength={4000} /></Field>
        {canPickRequester && (
          <Field label="Requested by" hint="Whoever asked for the change. The requester cannot approve or reject it.">
            <SelectInput value={form.requesterId} onChange={e => set({ requesterId: e.target.value })} options={people.filter(p => p.isActive).map(p => ({ value: p.id, label: p.displayName }))} />
          </Field>
        )}
        <Field label="Linked release" hint="Optional. Its products decide which freeze windows apply and how large the scope is.">
          <SelectInput value={form.releaseId} onChange={e => set({ releaseId: e.target.value, goNoGoId: NONE })} options={[{ value: NONE, label: 'No release' }, ...releases.map(r => ({ value: r.id, label: r.title }))]} />
        </Field>
        <Field label="Go/No-Go decision" hint={form.releaseId ? (goOptions.length ? 'A decision recorded on the linked release.' : 'The linked release has no Go/No-Go decision yet.') : 'Choose a release first.'}>
          <SelectInput value={form.goNoGoId} disabled={!form.releaseId || goOptions.length === 0} onChange={e => set({ goNoGoId: e.target.value })} options={[{ value: NONE, label: 'None' }, ...goOptions]} />
        </Field>
        <Field label="Planned start"><TextInput type="datetime-local" value={form.start} onChange={e => set({ start: e.target.value })} aria-describedby={startNote} /></Field>
        <InputZoneNote id={startNote} value={form.start} />
        <Field label="Planned end"><TextInput type="datetime-local" value={form.end} onChange={e => set({ end: e.target.value })} aria-describedby={endNote} /></Field>
        <InputZoneNote id={endNote} value={form.end} />
        <Field label="Rollback note" hint="How the change is undone if it goes wrong. Needed to submit; without one the risk goes up.">
          <textarea className="mg-input" rows={3} value={form.rollbackNote} onChange={e => set({ rollbackNote: e.target.value })} maxLength={4000} />
        </Field>
        <div className="mg-actions">
          {why
            ? <DisabledAction variant="primary" reason={why} type="submit">{saveLabel}</DisabledAction>
            : <Button variant="primary" type="submit" disabled={pending}>{pending ? pendingLabel : saveLabel}</Button>}
          <Button onClick={onCancel} disabled={pending}>Cancel</Button>
        </div>
      </form>
    </Panel>
  )
}
