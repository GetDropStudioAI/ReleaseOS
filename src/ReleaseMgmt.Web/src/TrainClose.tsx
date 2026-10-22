import { useId, type Ref } from 'react'
import { CLOSE_CODES, type CloseCode } from './api'
import { RadioChoice } from './Governance'

/** The header's "Complete…" and this form share one draft, so it survives a closed tab like the other inline forms (Q-LC2). */
export interface CompleteDraft { closeCode: CloseCode | ''; notes: string }
export const completeKey = (trainId: string) => `complete:${trainId}`
export const emptyComplete = (): CompleteDraft => ({ closeCode: '', notes: '' })

export const CLOSE_WORD: Record<CloseCode, string> = { Successful: 'Successful', SuccessfulWithIssues: 'Successful with issues', Unsuccessful: 'Unsuccessful' }

/**
 * Executing -> Complete (POST :complete, never :advance, which has no close code). The close code is an explicit choice: nothing is
 * preselected and Confirm stays off, with the reason on the line below, until one is picked. A code other than Successful makes the
 * database create the post-implementation review (trg_Train_CompleteRequiresPir); the form says so before the user confirms.
 */
export function CompleteForm({ id, draft, onDraft, pending, onConfirm, onCancel, groupRef }: {
  id: string; draft: CompleteDraft; onDraft: (d: CompleteDraft) => void; pending: string | null
  onConfirm: (closeCode: CloseCode, notes: string | null) => void; onCancel: () => void; groupRef: Ref<HTMLSpanElement>
}) {
  const whyId = useId()
  const code = draft.closeCode
  return (
    <div className="inline-form stack" id={id} role="group" aria-label="Complete train">
      <span><span className="cap" aria-hidden="true">Close code</span>{' '}
        <RadioChoice label="Close code" groupRef={groupRef} value={code} options={CLOSE_CODES.map(k => ({ key: k, word: CLOSE_WORD[k] }))} onChange={c => onDraft({ ...draft, closeCode: c })} /></span>
      {code && code !== 'Successful' && <span className="warn"><span aria-hidden="true">▲ </span>A post-implementation review will be required: it is created automatically with close code {CLOSE_WORD[code]}.</span>}
      <label>Notes (optional) <input className="line wide" value={draft.notes} onChange={e => onDraft({ ...draft, notes: e.target.value })} /></label>
      <span>
        <button type="button" className="text" disabled={!code || !!pending} aria-describedby={!code ? whyId : undefined}
          onClick={() => code && onConfirm(code, draft.notes.trim() || null)}>{pending === 'Completing…' ? pending : 'Confirm complete'}</button>{' '}
        <button type="button" className="text" disabled={!!pending} onClick={onCancel}>Cancel</button>{' '}
        <span className="muted">A completed train cannot be reopened.</span>
      </span>
      {!code && <span id={whyId} className="muted small">Confirm is off: choose a close code first.</span>}
    </div>
  )
}
