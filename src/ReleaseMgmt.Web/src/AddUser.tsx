import { useState } from 'react'
import { createUser } from './api'
import { errMsg } from './format'

const ROLES = [{ key: 'Viewer', label: 'Viewer' }, { key: 'RTE', label: 'RTE' }, { key: 'ReleaseManager', label: 'Release Manager' }, { key: 'GovernanceOfficer', label: 'Governance Officer' }]

/**
 * REOS-83: add a person before their first sign-in so they can be named as an owner straight away (inline row form, like Teams and Holidays).
 * Their first organisation sign-in finds this row by email; the identity provider's role then replaces the one chosen here (Q-003, Q-083a).
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
  const reset = () => { setEmail(''); setName(''); setRole('Viewer'); setHandle('') }

  const create = async () => {
    setBusy(true); setProblem(null); setAdded(null)
    try {
      const u = await createUser({ email, displayName: name, role, ...(handle.trim() ? { handle: handle.trim() } : {}) })
      setAdded(`${u.displayName} (${u.email}) added. They can be named as an owner now; their first sign-in uses this account.`)
      reset(); setOpen(false); onAdded()
    } catch (e) { setProblem(errMsg(e, 'The user could not be added.')) }
    finally { setBusy(false) }
  }

  if (!open) return (
    <>
      {added && <p className="ok" role="status">✓ {added}</p>}
      <p><button type="button" className="text" onClick={() => { setOpen(true); setAdded(null) }}>Add user</button></p>
    </>
  )
  return (
    <div className="inline-form" role="group" aria-label="Add user">
      <label>Email <input className="line" type="email" value={email} onChange={e => setEmail(e.target.value)} autoComplete="off" spellCheck={false} placeholder="rae@example.com" /></label>{' '}
      <label>Name <input className="line" value={name} onChange={e => setName(e.target.value)} autoComplete="off" /></label>{' '}
      <label>Role <select className="line" value={role} onChange={e => setRole(e.target.value)}>
        {ROLES.map(r => <option key={r.key} value={r.key}>{r.label}</option>)}
      </select></label>{' '}
      <label>Handle <input className="line" value={handle} onChange={e => setHandle(e.target.value)} autoComplete="off" spellCheck={false} placeholder="@rae (optional)" /></label>{' '}
      <button type="button" className="text" onClick={() => { setOpen(false); reset(); setProblem(null) }}>Cancel</button>{' '}
      <button type="button" className="text" disabled={busy || !email.trim() || !name.trim()} onClick={create}>Add user</button>
      {(!email.trim() || !name.trim()) && <p className="muted small">Enter the email they sign in with and their name.</p>}
      <p className="muted small">The role is used until their first sign-in; from then on it comes from the identity provider.</p>
      {problem && <p className="bad" role="alert">✗ {problem}</p>}
    </div>
  )
}
