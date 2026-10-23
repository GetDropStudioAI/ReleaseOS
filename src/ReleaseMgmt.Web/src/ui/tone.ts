/** The five status tones of the redesign (D41, REOS-93). Colour only repeats what the label says; a chip always has a label. */
export type Tone = 'success' | 'info' | 'warning' | 'danger' | 'neutral'

/** The words the design gives each tone (REOS-93), so a screen can pass the status word and get the right chip. Unknown words are neutral. */
const TONES: Record<string, Tone> = {
  successful: 'success', passed: 'success', approved: 'success', healthy: 'success', recorded: 'success', certified: 'success', complete: 'success', done: 'success',
  running: 'info', live: 'info', 'canary active': 'info', executing: 'info', 'in progress': 'info', inprogress: 'info',
  pending: 'warning', blocked: 'warning', 'medium risk': 'warning', 'conditional go': 'warning', 'pending decision': 'warning', waived: 'warning',
  failed: 'danger', 'rolled back': 'danger', rolledback: 'danger', aborted: 'danger', 'high risk': 'danger',
  planned: 'neutral', queued: 'neutral', planning: 'neutral', gated: 'neutral', skipped: 'neutral', 'not started': 'neutral',
}

export function chipTone(status: string): Tone {
  return TONES[status.trim().toLowerCase()] ?? 'neutral'
}
