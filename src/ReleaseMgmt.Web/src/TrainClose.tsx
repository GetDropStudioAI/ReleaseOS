import { useEffect, useId, useRef, useState } from 'react'
import { abortTrain, completeTrain } from './api'
import { errMsg } from './format'
import { announce } from './announce'
import { ConfirmDialog } from './ui'

export const CLOSE_CODES = [
  { key: 'Successful', word: 'Successful' }, { key: 'SuccessfulWithIssues', word: 'Successful with issues' }, { key: 'Unsuccessful', word: 'Unsuccessful' },
] as const
export type CloseCode = typeof CLOSE_CODES[number]['key']
export type Closing = 'complete' | 'abort'

/** What the dialog says it will do, so the wording is one place the tests can check. */
export const closeCopy = (kind: Closing, code: CloseCode, status: string) => kind === 'complete'
  ? { title: 'Complete this release?', confirm: 'Complete release', pending: 'Completing…', note: code !== 'Successful' ? 'A post-implementation review will be required.' : null }
  : { title: 'Abort this release?', confirm: 'Abort release', pending: 'Aborting…', note: status === 'Executing' ? 'An Executing release that is aborted requires a post-implementation review.' : null }

/**
 * REOS-86 / REOS-127 (D53): ending a release from its header. "Complete…" asks for the close code (POST :complete; :advance has no close code, so it was
 * always refused with CloseCodeRequired) and "Abort…" asks for a reason, kept in the audit row. Both are confirmation dialogs (ui ConfirmDialog, D41 allows a
 * dialog for an irreversible action): they start on Cancel, Escape cancels, focus returns to the button that opened them, and a failure from the server
 * is shown inside the dialog, which stays open. `kind` is null while closed; the dialog stays mounted so focus can return.
 */
export function CloseDialog({ kind, trainId, version, status, onCancel, onDone }: { kind: Closing | null; trainId: string; version: number; status: string; onCancel: () => void; onDone: () => void }) {
  const [code, setCode] = useState<CloseCode>('Successful')
  const [text, setText] = useState('')
  const [busy, setBusy] = useState(false)
  const [err, setErr] = useState<string | null>(null)
  const [bad, setBad] = useState(false)
  const last = useRef<Closing>('complete')
  if (kind) last.current = kind
  const shown = kind ?? last.current
  const codeId = useId(), textId = useId(), errId = useId()
  useEffect(() => { if (kind) { setCode('Successful'); setText(''); setErr(null); setBad(false); setBusy(false) } }, [kind])
  const copy = closeCopy(shown, code, status)
  const submit = async () => {
    if (busy) return
    if (shown === 'abort' && text.trim().length === 0) { setBad(true); setErr('Reason: say why this release is being aborted.'); return }
    setBusy(true); setErr(null); setBad(false)
    try {
      if (shown === 'complete') { await completeTrain(trainId, code, text.trim() || null, version); announce('Release completed.') }
      else { await abortTrain(trainId, text.trim(), version); announce('Release aborted.') }
      onDone()
    } catch (e) { setErr(errMsg(e, shown === 'complete' ? 'Could not complete the release.' : 'Could not abort the release.')) }
    finally { setBusy(false) }
  }
  // Escape closes the dialog (the platform's cancel); it must not also reach the page's Escape that closes the Inspector.
  return (
    <div onKeyDown={e => { if (e.key === 'Escape') e.stopPropagation() }}>
      <ConfirmDialog open={kind !== null} title={copy.title} confirmLabel={copy.confirm} pendingLabel={copy.pending} destructive={shown === 'abort'} pending={busy} onConfirm={() => void submit()} onCancel={onCancel}>
        <div className="rd-dialog-fields" onKeyDown={e => { if (e.key === 'Enter' && (e.target as HTMLElement).tagName === 'INPUT') { e.preventDefault(); void submit() } }}>
          {shown === 'complete'
            ? <>
                <p>Completing ends the release and records how it went. It cannot be undone.</p>
                <label className="rd-field" htmlFor={codeId}>Close code
                  <select id={codeId} value={code} onChange={e => setCode(e.target.value as CloseCode)}>{CLOSE_CODES.map(c => <option key={c.key} value={c.key}>{c.word}</option>)}</select></label>
                <label className="rd-field" htmlFor={textId}>Notes (optional)<input id={textId} value={text} onChange={e => setText(e.target.value)} /></label>
              </>
            : <>
                <p>Aborting ends the release. It cannot be restarted.</p>
                <label className="rd-field" htmlFor={textId}>Reason (required)
                  <input id={textId} value={text} aria-required="true" aria-invalid={bad || undefined} aria-describedby={bad ? errId : undefined} onChange={e => setText(e.target.value)} /></label>
              </>}
          {copy.note && <p>{copy.note}</p>}
          {err && <p id={errId} className="rd-error" role="alert">{err}</p>}
        </div>
      </ConfirmDialog>
    </div>
  )
}
