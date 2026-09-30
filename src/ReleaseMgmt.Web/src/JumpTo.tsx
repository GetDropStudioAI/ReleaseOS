import { useEffect, useId, useRef, useState } from 'react'
import type { StreamRow } from './api'
import type { Route } from './route'

type View = Route['view']
const VIEWS: { name: string; view: View }[] = [
  { name: 'My work', view: 'work' }, { name: 'Inbox', view: 'inbox' }, { name: 'Calendar', view: 'calendar' }, { name: 'Analytics', view: 'analytics' },
  { name: 'Imports & exports', view: 'importexport' }, { name: 'Sync health', view: 'sync' }, { name: 'Comm library', view: 'library' }, { name: 'Templates', view: 'templates' },
]

/**
 * "Jump to…" (design direction D, workflow review recommendation 4): one field in the toolbar that opens any train or page by typing,
 * so a frequent destination is one keystroke plus a few letters instead of a scroll through the Stream or the nav.
 * Ctrl+K / Cmd+K focuses it from anywhere (not while typing elsewhere). A native <datalist> keeps it accessible without a custom listbox.
 */
export function JumpTo({ rows, onTrain, onView }: { rows: StreamRow[] | null; onTrain: (id: string) => void; onView: (v: View) => void }) {
  const [q, setQ] = useState('')
  const [miss, setMiss] = useState(false)
  const input = useRef<HTMLInputElement>(null)
  const listId = useId(), hintId = useId()
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if ((e.ctrlKey || e.metaKey) && !e.altKey && e.key.toLowerCase() === 'k') { e.preventDefault(); input.current?.focus(); input.current?.select() } }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])
  const trainLabel = (r: StreamRow) => `${r.title} · ${r.status}`
  const go = (text: string) => {
    const t = text.trim().toLowerCase()
    if (!t) return
    const view = VIEWS.find(v => v.name.toLowerCase() === t) ?? VIEWS.find(v => v.name.toLowerCase().startsWith(t))
    const train = (rows ?? []).find(r => trainLabel(r).toLowerCase() === t) ?? (rows ?? []).find(r => r.title.toLowerCase().includes(t) || r.id.toLowerCase().startsWith(t))
    if (train && (!view || trainLabel(train).toLowerCase() === t)) onTrain(train.id)
    else if (view) onView(view.view)
    else { setMiss(true); return }
    setMiss(false); setQ(''); input.current?.blur()
  }
  return (
    <form className="jump" role="search" onSubmit={e => { e.preventDefault(); go(q) }}>
      <label>Jump to{' '}
        <input ref={input} className="line" list={listId} value={q} placeholder="Train or page" aria-keyshortcuts="Control+K Meta+K"
          aria-invalid={miss || undefined} aria-describedby={miss ? hintId : undefined}
          onChange={e => { setQ(e.target.value); setMiss(false); const v = e.target.value; if ((rows ?? []).some(r => trainLabel(r) === v) || VIEWS.some(x => x.name === v)) go(v) }}
          onKeyDown={e => { if (e.key === 'Escape') { setQ(''); setMiss(false); input.current?.blur() } }} />
      </label>
      <kbd aria-hidden="true">Ctrl K</kbd>
      <datalist id={listId}>
        {(rows ?? []).map(r => <option key={r.id} value={trainLabel(r)} />)}
        {VIEWS.map(v => <option key={v.view} value={v.name} />)}
      </datalist>
      {miss && <span id={hintId} className="bad small" role="alert">✗ Nothing matches “{q}”</span>}
    </form>
  )
}
