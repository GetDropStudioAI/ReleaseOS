// REOS-65: a refused or failed organisation sign-in comes back to the sign-in page as /?signin=<reason>. The server sends a fixed code, never exception text
// (OidcSignIn.Reasons in src/ReleaseMgmt.Api/Auth/OidcSignIn.cs; keep the two lists in step). Unknown codes get the generic message, so nothing a URL carries
// is ever shown as-is.

export const SIGNIN_QUERY_KEY = 'signin'

const MESSAGES: Record<string, string> = {
  'no-role': 'Your account has no role in this app. Ask an administrator to add you to a Release Management group, then sign in again.',
  'email-unverified': 'Your identity provider says your email address is not verified, so you were not signed in. Verify it there, then sign in again.',
  'no-email': 'Your identity provider did not send an email address, so you were not signed in. Ask an administrator to check the app registration.',
  'no-subject': 'Your identity provider did not say who you are, so you were not signed in. Ask an administrator to check the app registration.',
  'identity-conflict': 'This email address belongs to another account in this app, so you were not signed in. Ask an administrator.',
  'denied': 'Sign-in was cancelled or refused at your identity provider. If you did not cancel, ask an administrator to give you access to this app.',
  'expired': 'That sign-in took too long or was started in another tab. Sign in again.',
  'failed': 'Organisation sign-in did not complete. Try again; if it keeps failing, tell an administrator the time it happened.',
}

/** The words for a sign-in refusal code from the query string, or null when the query carries none. */
export function signInMessage(search: string): string | null {
  const code = new URLSearchParams(search).get(SIGNIN_QUERY_KEY)
  if (code === null) return null
  return Object.hasOwn(MESSAGES, code) ? MESSAGES[code] : MESSAGES.failed
}

/** The same query string without the sign-in code, so a reload does not show the message again. */
export function withoutSignInReason(search: string): string {
  const q = new URLSearchParams(search)
  q.delete(SIGNIN_QUERY_KEY)
  const s = q.toString()
  return s ? `?${s}` : ''
}
