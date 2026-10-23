import { useEffect, useId, useRef, useState } from 'react'
import { Search } from 'lucide-react'
import type { StreamRow } from './api'
import type { View } from './route'
import { shortcutHint } from './ui'

/**
 * Search in the utility bar (REOS-94; "Jump to", design direction D, workflow review recommendation 4): opens any release or page by typing, so a
 * frequent destination is one keystroke plus a few letters. Ctrl+K / Cmd+K focuses it from anywhere. A native <datalist> keeps it accessible without a
 * custom listbox. The pages offered are the ones the sidebar offers this person (shell/nav.ts).
 */
export function JumpTo({ rows, targets, onTrain, onView }: {
  rows: StreamRow[] | null; targets: { name: string; view: View }[]; onTrain: (id: string) => void; onView: (v: View) => void
}) {
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
    const view = targets.find(v => v.name.toLowerCase() === t) ?? targets.find(v => v.name.toLowerCase().startsWith(t))
    const train = (rows ?? []).find(r => trainLabel(r).toLowerCase() === t) ?? (rows ?? []).find(r => r.title.toLowerCase().includes(t) || r.id.toLowerCase().startsWith(t))
    if (train && (!view || trainLabel(train).toLowerCase() === t)) onTrain(train.id)
    else if (view) onView(view.view)
    else { setMiss(true); return }
    setMiss(false); setQ(''); input.current?.blur()
  }
  return (
    <form className="os-search os-jump" role="search" aria-label="Jump to" onSubmit={e => { e.preventDefault(); go(q) }}>
      <Search className="os-search-icon" size={14} strokeWidth={1.6} aria-hidden="true" />
      <input ref={input} list={listId} value={q} placeholder="Search releases and pages…" aria-label="Search releases and pages" aria-keyshortcuts="Control+K Meta+K"
        aria-invalid={miss || undefined} aria-describedby={miss ? hintId : undefined}
        onChange={e => { setQ(e.target.value); setMiss(false); const v = e.target.value; if ((rows ?? []).some(r => trainLabel(r) === v) || targets.some(x => x.name === v)) go(v) }}
        onKeyDown={e => { if (e.key === 'Escape') { setQ(''); setMiss(false); input.current?.blur() } }} />
      <kbd className="os-kbd" aria-hidden="true">{shortcutHint()}</kbd>
      <datalist id={listId}>
        {(rows ?? []).map(r => <option key={r.id} value={trainLabel(r)} />)}
        {targets.map(v => <option key={v.view} value={v.name} />)}
      </datalist>
      {miss && <span id={hintId} className="os-jump-miss" role="alert">✗ Nothing matches “{q}”</span>}
    </form>
  )
}
