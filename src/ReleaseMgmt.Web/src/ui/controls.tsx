import { useEffect, useId, useRef, type ComponentProps, type ReactNode, type Ref } from 'react'
import { ChevronDown, Search, type LucideIcon } from 'lucide-react'
import { shortcutHint } from './keys.ts'

/**
 * Button (D41, REOS-93): `primary` is filled accent, `secondary` is outlined, `destructive` is danger-coloured text for actions like Rollback.
 * An optional leading Lucide icon is decorative; the text is the name (without an icon the text is the button's own child, so it is also its own text node). `ref` reaches the <button> (React 19 passes it as a prop).
 */
export function Button({ variant = 'secondary', icon: Icon, children, className, type = 'button', ...rest }:
  Omit<ComponentProps<'button'>, 'type'> & { variant?: 'primary' | 'secondary' | 'destructive'; icon?: LucideIcon; type?: 'button' | 'submit'; children: ReactNode }) {
  return (
    <button type={type} className={['os-button', variant, className].filter(Boolean).join(' ')} {...rest}>
      {Icon ? <><Icon aria-hidden="true" size={16} strokeWidth={1.6} /><span>{children}</span></> : children}
    </button>
  )
}

/** Filter dropdown ("All teams"): a native select drawn as the design's button, so keyboard and screen-reader behaviour come from the platform. */
export function FilterSelect({ label, value, options, onChange }: { label: string; value: string; options: { value: string; label: string }[]; onChange: (value: string) => void }) {
  return (
    <label className="os-filter">
      <span className="sr-only">{label}</span>
      <ChevronDown className="os-filter-icon" aria-hidden="true" size={16} strokeWidth={1.6} />
      <select value={value} onChange={e => onChange(e.target.value)}>
        {options.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
      </select>
    </label>
  )
}

/** Search field with a leading icon and an optional shortcut hint. The global ⌘K / Ctrl+K binding belongs to the app shell (REOS-94), which passes `inputRef`. */
export function SearchField({ label, placeholder, value, onChange, shortcut, inputRef }: {
  label: string; placeholder?: string; value: string; onChange: (value: string) => void; shortcut?: boolean; inputRef?: Ref<HTMLInputElement>
}) {
  return (
    <div className="os-search">
      <Search className="os-search-icon" aria-hidden="true" size={16} strokeWidth={1.6} />
      <input ref={inputRef} type="search" aria-label={label} placeholder={placeholder} value={value} onChange={e => onChange(e.target.value)}
        aria-keyshortcuts={shortcut ? 'Control+K Meta+K' : undefined} />
      {shortcut && <kbd className="os-kbd" aria-hidden="true">{shortcutHint()}</kbd>}
    </div>
  )
}

/**
 * Confirmation dialog for destructive or irreversible actions (D41 allows dialogs for these; Rollback, Pause run). A native <dialog> opened as
 * modal: focus starts on Cancel (the safe choice), Escape cancels, and focus returns to the element that opened it. While `pending`, both
 * buttons are disabled so a double click cannot confirm twice.
 */
export function ConfirmDialog({ open, title, children, confirmLabel, pendingLabel, destructive, pending, onConfirm, onCancel }: {
  open: boolean; title: string; children?: ReactNode; confirmLabel: string; pendingLabel?: string; destructive?: boolean; pending?: boolean
  onConfirm: () => void; onCancel: () => void
}) {
  const ref = useRef<HTMLDialogElement>(null)
  const cancel = useRef<HTMLButtonElement>(null)
  const opener = useRef<Element | null>(null)
  const titleId = useId(), bodyId = useId()
  useEffect(() => {
    const d = ref.current
    if (!d) return
    if (open && !d.open) { opener.current = document.activeElement; d.showModal(); cancel.current?.focus() }
    if (!open && d.open) { d.close(); (opener.current as HTMLElement | null)?.focus?.() }
  }, [open])
  return (
    <dialog ref={ref} className="os-dialog" aria-labelledby={titleId} aria-describedby={children ? bodyId : undefined}
      onCancel={e => { e.preventDefault(); if (!pending) onCancel() }}>
      <h2 id={titleId} className="os-dialog-title">{title}</h2>
      {children && <div id={bodyId} className="os-dialog-body">{children}</div>}
      <div className="os-dialog-actions">
        <Button ref={cancel} variant="secondary" disabled={pending} onClick={onCancel}>Cancel</Button>
        <Button variant={destructive ? 'destructive' : 'primary'} className={destructive ? 'filled' : undefined} disabled={pending} onClick={onConfirm}>
          {pending ? (pendingLabel ?? 'Working…') : confirmLabel}
        </Button>
      </div>
    </dialog>
  )
}
