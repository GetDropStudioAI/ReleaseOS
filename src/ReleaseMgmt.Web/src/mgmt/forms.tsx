import type { ComponentProps, ReactNode } from 'react'
import { TwoLine } from '../ui'
import { dueText } from '../shell/approvalsApi.ts'
import './mgmt.css'

/** A labelled field for the management panels: the label wraps the control, so the accessible name and the click target are the same. */
export function Field({ label, hint, children }: { label: string; hint?: ReactNode; children: ReactNode }) {
  return <label className="mg-field"><span>{label}</span>{children}{hint && <span className="mg-hint">{hint}</span>}</label>
}

export function TextInput(props: ComponentProps<'input'>) {
  return <input {...props} className={['mg-input', props.className].filter(Boolean).join(' ')} />
}

export function SelectInput({ options, ...rest }: Omit<ComponentProps<'select'>, 'children'> & { options: { value: string; label: string }[] }) {
  return <select {...rest} className={['mg-input', rest.className].filter(Boolean).join(' ')}>{options.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}</select>
}

/** An instant, written as the app writes times: UTC first and labelled, the person's own time beside it (D44). */
export function When({ iso }: { iso: string | null | undefined }) {
  if (!iso) return <span className="os-muted">Not set</span>
  const t = dueText({ dueAt: iso, dueOn: null })
  return <TwoLine primary={t.utc} secondary={t.local ?? undefined} />
}

/** A problem under a form: always words, announced when it appears. */
export function Problem({ message }: { message: string | null }) {
  return message ? <p className="mg-problem" role="alert">▲ {message}</p> : null
}
