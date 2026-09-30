import { useCallback, useRef, useState } from 'react'

/**
 * One in-flight guard for a screen's actions (docs/review/UX_WORKFLOW_REVIEW.md, recommendation 1).
 * `run(label, fn)` ignores re-entry while a request is in flight (no double submits, no self-inflicted 409s),
 * and exposes `pending` (the label of the running action) so the trigger can disable itself and say what is happening.
 * Errors are rethrown: callers keep their own error and conflict handling.
 */
export function useAction() {
  const [pending, setPending] = useState<string | null>(null)
  const busy = useRef(false)
  const run = useCallback(async <T,>(label: string, fn: () => Promise<T>): Promise<T | undefined> => {
    if (busy.current) return undefined
    busy.current = true
    setPending(label)
    try { return await fn() } finally { busy.current = false; setPending(null) }
  }, [])
  return { run, pending, busy: pending !== null }
}
