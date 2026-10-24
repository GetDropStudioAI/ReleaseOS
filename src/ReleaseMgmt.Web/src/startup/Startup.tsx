import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Compass, Rocket } from 'lucide-react'
import { devLogin, getAuthConfig, logout, type AuthConfig } from '../api'
import { signInMessage, withoutSignInReason } from '../signinReason'
import { ThemeChoices } from '../shell/UserMenu'
import { Button, LoadingState, Notice, Panel } from '../ui'
import './startup.css'

/**
 * The pages that sit outside the signed-in shell (REOS-135, G13): sign-in, a refused sign-in, loading, server unreachable, stopped, no workspace and an
 * unknown path. Each is the product name, the page's one h1, a short line, and a Panel; the words carry the state, never colour alone. Focus lands on
 * the h1 (or, on the sign-in page, the first field) so a keyboard or screen-reader user starts at the page, and the document title says which page it is.
 */
export const PRODUCT = 'ReleaseOS'

function Frame({ title, description, children, field }: { title: string; description?: ReactNode; children: ReactNode; field?: boolean }) {
  const heading = useRef<HTMLHeadingElement>(null)
  useEffect(() => { document.title = `${title} · ${PRODUCT}` }, [title])
  useEffect(() => { if (!field) heading.current?.focus({ preventScroll: true }) }, [field])
  return (
    <main className="os-start">
      <div className="os-start-card">
        <p className="os-start-brand"><Rocket aria-hidden="true" size={20} strokeWidth={1.6} />{PRODUCT}</p>
        <h1 ref={heading} tabIndex={-1}>{title}</h1>
        {description && <p className="os-start-lead">{description}</p>}
        {children}
      </div>
    </main>
  )
}

/** A refusal or failure, announced at once. The wrapper is the alert; the Notice inside gives it an icon, a title and words. */
const Alert = ({ children }: { children: ReactNode }) => <div role="alert">{children}</div>

const ROLES = ['Viewer', 'RTE', 'ReleaseManager', 'GovernanceOfficer']

export function SignIn({ onDone }: { onDone: () => void }) {
  const [email, setEmail] = useState('dev@example.com')
  const [role, setRole] = useState('RTE')
  const [error, setError] = useState<string | null>(null)
  const [cfg, setCfg] = useState<AuthConfig | null>(null)
  const first = useRef<HTMLInputElement>(null)
  // REOS-65: a refused organisation sign-in comes back as /?signin=<reason>; show it in words, then drop it from the address so a reload is clean
  const [refused] = useState(() => signInMessage(window.location.search))
  useEffect(() => {
    if (refused) window.history.replaceState(null, '', window.location.pathname + withoutSignInReason(window.location.search) + window.location.hash)
  }, [refused])
  useEffect(() => { getAuthConfig().then(setCfg) }, [])
  useEffect(() => { first.current?.focus({ preventScroll: true }) }, [])
  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setError(null)
    if (await devLogin(email, email.split('@')[0], role)) onDone()
    else setError('Dev sign-in is only available in the Development environment. Use your organisation sign-in instead.')
  }
  return (
    <Frame field title="Sign in" description="Use your organisation account. In a development environment you can also choose an email address and a role.">
      {refused && <Alert><Notice tone="danger" title="Sign-in was refused">{refused}</Notice></Alert>}
      <Panel title="Organisation sign-in" subtitle="Your identity provider asks for your password and authenticator app or MFA code.">
        <p className="os-start-actions">
          <a className="os-button primary" href="/auth/login">Organisation sign-in</a>
          {cfg?.passwordResetUrl && <a className="os-link" href={cfg.passwordResetUrl} rel="noreferrer">Forgot your password? Reset it with your authenticator app or MFA code</a>}
        </p>
      </Panel>
      <Panel title="Development sign-in" subtitle="Available only in the Development environment.">
        <form className="os-start-form" onSubmit={submit}>
          <label className="os-start-field"><span>Email</span>
            <input ref={first} type="email" autoComplete="email" value={email} onChange={e => setEmail(e.target.value)} required /></label>
          <label className="os-start-field"><span>Role</span>
            <select value={role} onChange={e => setRole(e.target.value)}>{ROLES.map(r => <option key={r}>{r}</option>)}</select></label>
          <div><Button type="submit">Sign in (development)</Button></div>
          {error && <Alert><Notice tone="danger" title="Development sign-in is not available">{error}</Notice></Alert>}
        </form>
      </Panel>
      <div className="os-start-theme"><span id="os-start-appearance">Appearance</span><ThemeChoices /></div>
    </Frame>
  )
}

/** Before /me, before the session restores, before the display zone loads. */
export function Starting() {
  return <Frame title="Starting" description="Checking your session."><Panel title="Please wait"><LoadingState what="ReleaseOS" /></Panel></Frame>
}

/** /me could not be read at all (not a 401, which is the sign-in page). */
export function Unreachable() {
  return (
    <Frame title="Cannot reach the server" description="ReleaseOS could not contact its server, so nothing can be shown.">
      <Alert><Notice tone="danger" title="The server did not answer" action={<Button onClick={() => window.location.reload()}>Try again</Button>}>
        Check that the app is running and that your network is connected, then try again.</Notice></Alert>
    </Frame>
  )
}

/** After Exit under start.py. */
export function Stopped() {
  return (
    <Frame title="ReleaseOS has stopped">
      <div role="status"><Notice tone="info" title="The app has stopped">You can close this tab.</Notice></div>
    </Frame>
  )
}

/** Signed in, but a member of no workspace, or the only workspace is archived (D45). */
export function NoWorkspace({ name, onSignedOut, onRetry }: { name: string; onSignedOut: () => void; onRetry: () => void }) {
  return (
    <Frame title="No workspace" description={`${name}, you are signed in, but you are not a member of any active workspace.`}>
      <Panel title="What to do" subtitle="A workspace holds your releases, teams and settings.">
        <p className="os-start-text">Ask an administrator to add you to a workspace. If your only workspace was archived, ask them to restore it or add you to another. Then check again.</p>
        <p className="os-start-actions">
          <Button variant="primary" onClick={onRetry}>Check again</Button>
          <Button onClick={async () => { await logout(); onSignedOut() }}>Sign out</Button>
        </p>
      </Panel>
    </Frame>
  )
}

/** An address that names no page. A plain link (a full load) so nothing of the unknown address stays. */
export function NotFound({ path }: { path: string }) {
  return (
    <Frame title="Page not found" description="There is no page at this address.">
      <Panel title="Where to go" subtitle="The address may be mistyped, or the page may have moved.">
        <p className="os-start-text"><Compass aria-hidden="true" size={16} strokeWidth={1.6} /> <code>{path}</code></p>
        <p className="os-start-actions"><a className="os-button primary" href="/">Go to Releases</a></p>
      </Panel>
    </Frame>
  )
}

/** Same frame for the workspace gate's loading and error states. */
export function StartFrame({ title, description, children }: { title: string; description?: ReactNode; children: ReactNode }) {
  return <Frame title={title} description={description}>{children}</Frame>
}
