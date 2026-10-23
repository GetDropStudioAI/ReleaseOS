import { useEffect, useRef, useState } from 'react'
import { createUser } from './api'
import { errMsg } from './format'
import { Button } from './ui'
import { Field, Problem, SelectInput, TextInput } from './mgmt/forms'
import { ROLE_OPTIONS } from './workspaceApi'

/**
 * REOS-83: add a person before their first sign-in so they can be named as an owner straight away. Their first organisation sign-in finds this row by
 * email; the identity provider's role then replaces the one chosen here (Q-003, Q-083a). Restyled with the management screens (REOS-129).
 */
export function AddUser({ onAdded }: { onAdded: () => void }) {
  const [open, setOpen] = useState(false)
  const [email, setEmail] = useState('')
  const [name, setName] = useState('')
  const [role, setRole] = useState('Viewer')
  const [handle, setHandle] = useState('')
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [added, setAdded] = useState<string | null>(null)
  const first = useRef<HTMLInputElement>(null)
  const trigger = useRef<HTMLButtonElement>(null)
  const reset = () => { setEmail(''); setName(''); setRole('Viewer'); setHandle('') }
  useEffect(() => { if (open) first.current?.focus() }, [open])

  const create = async () => {
    setBusy(true); setProblem(null); setAdded(null)
    try {
      const u = await createUser({ email, displayName: name, role, ...(handle.trim() ? { handle: handle.trim() } : {}) })
      setAdded(`${u.displayName} (${u.email}) added. They can be named as an owner now; their first sign-in uses this account.`)
      reset(); setOpen(false); onAdded()
      requestAnimationFrame(() => trigger.current?.focus())
    } catch (e) { setProblem(errMsg(e, 'The user could not be added.')) }
    finally { setBusy(false) }
  }

  if (!open) return (
    <div className="mg-stack">
      {added && <p className="mg-ok" role="status">✓ {added}</p>}
      <div className="mg-actions"><Button ref={trigger} variant="primary" onClick={() => { setOpen(true); setAdded(null) }}>Add user</Button></div>
    </div>
  )
  const incomplete = !email.trim() || !name.trim()
  return (
    <div className="mg-form" role="group" aria-label="Add user">
      <Field label="Email"><TextInput ref={first} type="email" value={email} onChange={e => setEmail(e.target.value)} autoComplete="off" spellCheck={false} placeholder="rae@example.com" /></Field>
      <Field label="Name"><TextInput value={name} onChange={e => setName(e.target.value)} autoComplete="off" /></Field>
      <Field label="Role"><SelectInput value={role} onChange={e => setRole(e.target.value)} options={ROLE_OPTIONS} /></Field>
      <Field label="Handle"><TextInput value={handle} onChange={e => setHandle(e.target.value)} autoComplete="off" spellCheck={false} placeholder="@rae (optional)" /></Field>
      {incomplete && <p className="mg-note">Enter the email they sign in with and their name.</p>}
      <p className="mg-note">The role is used until their first sign-in; from then on it comes from the identity provider.</p>
      <Problem message={problem} />
      <div className="mg-actions">
        <Button onClick={() => { setOpen(false); reset(); setProblem(null); requestAnimationFrame(() => trigger.current?.focus()) }}>Cancel</Button>
        <Button variant="primary" disabled={busy || incomplete} onClick={create}>Add user</Button>
      </div>
    </div>
  )
}
