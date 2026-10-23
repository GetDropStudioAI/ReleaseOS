import { useEffect, useId, useRef, useState } from 'react'
import { logout, type Me } from '../api'
import { controlStatus, requestExit, requestReset, waitUntilReady, type ControlStatus } from '../control'
import { loadTheme, saveTheme, type ThemeChoice } from '../theme'
import { Button } from '../ui'
import { initials, roleName } from './health.ts'

/** Appearance: Auto (the OS decides), Light or Dark. A group of pressed buttons, never a toggle switch (rule 9). */
export function ThemeChoices() {
  const [choice, setChoice] = useState<ThemeChoice>(loadTheme())
  const pick = (c: ThemeChoice) => { setChoice(c); saveTheme(c) }
  return (
    <span className="os-segmented" role="group" aria-label="Appearance">
      {(['auto', 'light', 'dark'] as const).map(c => (
        <button key={c} type="button" aria-pressed={choice === c} onClick={() => pick(c)}>{c[0].toUpperCase() + c.slice(1)}</button>
      ))}
    </span>
  )
}

/** Reset and Exit, shown only when start.py is supervising the app. The confirmation is inline. */
function SessionControls({ onSignedOut, onStopped }: { onSignedOut: () => void; onStopped: () => void }) {
  const [status, setStatus] = useState<ControlStatus | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [confirmExit, setConfirmExit] = useState(false)

  useEffect(() => { controlStatus().then(setStatus) }, [])
  if (!status) return null

  const reset = async () => {
    setError(null); setBusy('Pulling the latest code…')
    const r = await requestReset()
    if (!r.ok) { setBusy(null); setError(r.message); return }
    const result = await waitUntilReady(s => setBusy(s?.message ?? 'Restarting…'))
    if (result === 'ready') { location.reload(); return }
    setBusy(null)
    setError(result === 'error' ? ((await controlStatus())?.message ?? 'Reset failed') : 'Reset is taking too long; check the terminal running start.py')
  }
  const exit = async () => {
    setError(null); setBusy('Signing out and stopping…')
    await logout()
    onSignedOut()
    const r = await requestExit()
    if (!r.ok) { setBusy(null); setError(r.message); return }
    onStopped()   // App shows the stopped page; this component is gone after sign-out, so the notice must live above it
  }

  return (
    <div className="os-user-session">
      {busy && <p className="os-user-note" role="status">{busy}</p>}
      {error && <p className="os-user-note bad" role="alert">✗ {error}</p>}
      {!busy && !confirmExit && <div className="os-user-row"><Button onClick={reset}>Reset</Button><Button onClick={() => setConfirmExit(true)}>Exit</Button></div>}
      {!busy && confirmExit && (
        <div className="os-user-row">
          <span className="os-user-note">Sign out and stop the app?</span>
          <Button variant="destructive" onClick={exit}>Sign out and exit</Button>
          <Button onClick={() => setConfirmExit(false)}>Cancel</Button>
        </div>
      )}
    </div>
  )
}

/**
 * The user card in the sidebar footer (REOS-94, Figma node 6:4759): initials, name and role. It opens the account panel (a disclosure, not a modal):
 * Appearance, Sign out, and Reset/Exit under start.py. Escape or a click elsewhere closes it and focus returns to the card.
 */
export function UserMenu({ me, onSignedOut, onStopped }: { me: Me; onSignedOut: () => void; onStopped: () => void }) {
  const [open, setOpen] = useState(false)
  const id = useId()
  const box = useRef<HTMLDivElement>(null)
  const card = useRef<HTMLButtonElement>(null)
  useEffect(() => {
    if (!open) return
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape' && !e.defaultPrevented) { e.preventDefault(); setOpen(false); card.current?.focus() } }
    const onDown = (e: PointerEvent) => { if (!box.current?.contains(e.target as Node)) setOpen(false) }
    window.addEventListener('keydown', onKey); window.addEventListener('pointerdown', onDown)
    return () => { window.removeEventListener('keydown', onKey); window.removeEventListener('pointerdown', onDown) }
  }, [open])
  return (
    <div className="os-user" ref={box}>
      {open && (
        <div id={id} className="os-user-menu" role="group" aria-label="Your account">
          <p className="os-user-menu-label">Appearance</p>
          <ThemeChoices />
          <SessionControls onSignedOut={onSignedOut} onStopped={onStopped} />
          <Button onClick={async () => { await logout(); onSignedOut() }}>Sign out</Button>
        </div>
      )}
      <button ref={card} type="button" className="os-user-card" aria-expanded={open} aria-controls={open ? id : undefined} onClick={() => setOpen(o => !o)}>
        <span className="os-avatar" aria-hidden="true">{initials(me.name || me.email)}</span>
        <span className="os-user-text"><span className="os-user-name">{me.name}</span><span className="os-user-role">{roleName(me.roles[0])}</span></span>
        <span className="sr-only">, account and appearance</span>
      </button>
    </div>
  )
}
