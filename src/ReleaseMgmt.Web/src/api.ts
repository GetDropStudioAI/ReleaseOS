export interface Me { email: string; name: string; roles: string[] }

export async function getMe(): Promise<Me | null> {
  const r = await fetch('/api/v1/me')
  if (r.status === 401) return null
  if (!r.ok) throw new Error(`GET /api/v1/me failed: ${r.status}`)
  return r.json()
}

export async function devLogin(email: string, name: string, role: string): Promise<boolean> {
  const r = await fetch('/auth/dev-login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, name, role }),
  })
  return r.ok
}

export async function logout() {
  await fetch('/auth/logout', { method: 'POST' })
}
