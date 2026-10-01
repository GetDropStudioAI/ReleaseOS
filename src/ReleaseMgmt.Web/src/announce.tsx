import { useEffect, useState } from 'react'

/**
 * One always-mounted polite live region for the whole app (WCAG 4.1.3). Screen readers only announce a change to a region
 * that already exists, so screens call `announce(text)` instead of mounting their own role="status" together with its text.
 */
type Listener = (text: string) => void
const listeners = new Set<Listener>()
let seq = 0

export function announce(text: string) {
  // The same sentence twice in a row must still be spoken: clear first, then set on the next frame.
  const n = ++seq
  listeners.forEach(l => l(''))
  requestAnimationFrame(() => { if (n === seq) listeners.forEach(l => l(text)) })
}

export function Announcer() {
  const [text, setText] = useState('')
  useEffect(() => { listeners.add(setText); return () => { listeners.delete(setText) } }, [])
  return <div className="sr-only" role="status" aria-live="polite" aria-atomic="true" data-testid="announcer">{text}</div>
}
