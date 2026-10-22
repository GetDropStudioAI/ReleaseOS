import { useEffect, useRef, useState } from 'react'
import { RadioChoice } from './Governance'
import { abortTrain, completeTrain } from './api'
import { errMsg } from './format'
import { useFocusWhen } from './focus'
import { announce } from './announce'

const CLOSE_CODES = [
  { key: 'Successful', word: 'Successful' }, { key: 'SuccessfulWithIssues', word: 'Successful with issues' }, { key: 'Unsuccessful', word: 'Unsuccessful' },
] as const
type CloseCode = typeof CLOSE_CODES[number]['key']
export type Closing = 'complete' | 'abort'

/**
 * REOS-86: ending a train from its header. "Complete…" asks for the close code (POST :complete; :advance has no close code, so it was always
 * refused with CloseCodeRequired) and "Abort…" asks for a reason, kept in the audit row. Both are inline forms under the header line, never a
 * modal (docs/UI.md); the trigger buttons live in the header's action row and toggle `closing`.
 */
export function TrainCloseForm({ kind, trainId, version, status, onCancel, onDone }: { kind: Closing; trainId: string; version: number; status: string; onCancel: () => void; onDone: () => void }) {
  const [code, setCode] = useState<CloseCode>('Successful')
  const [text, setText] = useState('')
  const [busy, setBusy] = useState(false)
  const [err, setErr] = useState<string | null>(null)
  const group = useRef<HTMLSpanElement>(null)
  const reasonRef = useFocusWhen<HTMLInputElement>(kind === 'abort')
  // Opening Complete puts focus on the chosen close code (the radio group renders in this pass, so focus it on the next frame).
  useEffect(() => { if (kind === 'complete') requestAnimationFrame(() => group.current?.querySelector<HTMLElement>('[aria-checked="true"]')?.focus()) }, [kind])
  const short = kind === 'abort' && text.trim().length === 0
  const submit = async () => {
    if (busy || short) return
    setBusy(true); setErr(null)
    try {
      if (kind === 'complete') { await completeTrain(trainId, code, text.trim() || null, version); announce('Train completed.') }
      else { await abortTrain(trainId, text.trim(), version); announce('Train aborted.') }
      onDone()
    } catch (e) { setErr(errMsg(e, kind === 'complete' ? 'Could not complete the train.' : 'Could not abort the train.')) }
    finally { setBusy(false) }
  }
  const keys = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && (e.target as HTMLElement).tagName === 'INPUT') { e.preventDefault(); void submit() }
    if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); onCancel() }
  }
  if (kind === 'complete') {
    return (
      <div className="inline-form train-close" role="group" aria-label="Complete the train" onKeyDown={keys}>
        <span>Close code <RadioChoice label="Close code" options={[...CLOSE_CODES]} value={code} onChange={setCode} groupRef={group} /></span>
        <label>Notes <input className="line wide" value={text} onChange={e => setText(e.target.value)} /></label>
        <span><button type="button" className="text" disabled={busy} onClick={submit}>{busy ? 'Completing…' : 'Complete train'}</button>{' '}
          <button type="button" className="text" disabled={busy} onClick={onCancel}>Cancel</button></span>
        {code !== 'Successful' && <span className="muted small">A post-implementation review will be required.</span>}
        {err && <span className="bad" role="alert">✗ {err}</span>}
      </div>)
  }
  return (
    <div className="inline-form train-close" role="group" aria-label="Abort the train" onKeyDown={keys}>
      <span>Abort the train? It cannot be restarted.</span>
      <label>Reason (required) <input ref={reasonRef} className="line wide" value={text} aria-required="true" onChange={e => setText(e.target.value)} /></label>
      <span><button type="button" className="text destructive" disabled={busy || short} onClick={submit}>{busy ? 'Aborting…' : 'Abort train'}</button>{' '}
        <button type="button" className="text" disabled={busy} onClick={onCancel}>Cancel</button></span>
      {status === 'Executing' && <span className="muted small">An Executing train that is aborted requires a post-implementation review.</span>}
      {err && <span className="bad" role="alert">✗ {err}</span>}
    </div>)
}
