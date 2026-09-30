import { useId, useRef, useState } from 'react'
import { useFocusWhen } from './focus'

/**
 * The one inline confirm for destructive or irreversible actions (docs/UI.md: "Mark failed" → inline reason field + "Confirm"; no modals).
 * The trigger opens a line with an optional reason field and Confirm / Cancel. Focus moves to the reason (or Confirm) on open and
 * back to the trigger on Cancel. While `onConfirm` runs, Confirm is disabled and shows `pendingLabel`, so a double click is ignored.
 */
export function ConfirmInline({ label, question, confirmLabel, pendingLabel, onConfirm, destructive = true, reason, disabled, triggerLabel }: {
  label: string
  question?: string
  confirmLabel?: string
  pendingLabel?: string
  onConfirm: (reason: string) => Promise<unknown> | unknown
  destructive?: boolean
  reason?: { label: string; minLength?: number; required?: boolean }
  disabled?: boolean
  triggerLabel?: string
}) {
  const [open, setOpen] = useState(false)
  const [text, setText] = useState('')
  const [pending, setPending] = useState(false)
  const trigger = useRef<HTMLButtonElement>(null)
  const reasonRef = useFocusWhen<HTMLInputElement>(open && !!reason)
  const confirmRef = useFocusWhen<HTMLButtonElement>(open && !reason)
  const hintId = useId()
  const min = reason?.minLength ?? (reason?.required ? 1 : 0)
  const short = !!reason && text.trim().length < min
  const close = () => { setOpen(false); setText(''); requestAnimationFrame(() => trigger.current?.focus()) }
  const confirm = async () => {
    if (pending || short) return
    setPending(true)
    try { await onConfirm(text.trim()); setOpen(false); setText('') } finally { setPending(false) }
  }
  if (!open) return <button ref={trigger} type="button" className={destructive ? 'text destructive' : 'text'} disabled={disabled} aria-label={triggerLabel} onClick={() => setOpen(true)}>{label}</button>
  return (
    <span className="confirm-inline" role="group" aria-label={question ?? label}>
      {question && <span>{question} </span>}
      {reason && (
        <label>{reason.label}{reason.required || min ? ' (required)' : ''}{' '}
          <input ref={reasonRef} className="line" value={text} onChange={e => setText(e.target.value)} aria-required={reason.required || min > 0}
            aria-describedby={short ? hintId : undefined} onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); void confirm() } if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); close() } }} />
        </label>
      )}
      {' '}<button ref={confirmRef} type="button" className={destructive ? 'text destructive' : 'text'} disabled={pending || short} aria-describedby={short ? hintId : undefined} onClick={confirm}>{pending ? (pendingLabel ?? 'Working…') : (confirmLabel ?? 'Confirm')}</button>
      {' '}<button type="button" className="text" disabled={pending} onClick={close}>Cancel</button>
      {short && min > 1 && <span id={hintId} className="muted small"> At least {min} characters.</span>}
      {short && min <= 1 && <span id={hintId} className="sr-only"> Enter a reason first.</span>}
    </span>
  )
}
