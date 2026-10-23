import { useId, useRef, type KeyboardEvent, type ReactNode } from 'react'
import type { Tone } from './tone.ts'

/**
 * Panel with a heading row (D41, REOS-93): title, optional subtitle, and right-aligned meta (a count, a chip or a link). `inverse` is the dark panel
 * of the design (Go/no-go, Deployment events), dark in both themes. The heading names the section for screen readers.
 */
export function Panel({ title, subtitle, meta, footer, inverse, level = 2, children }: {
  title: ReactNode; subtitle?: ReactNode; meta?: ReactNode; footer?: ReactNode; inverse?: boolean; level?: 2 | 3 | 4; children?: ReactNode
}) {
  const id = useId()
  const H = level === 2 ? 'h2' : level === 3 ? 'h3' : 'h4'
  return (
    <section className={`os-panel${inverse ? ' inverse' : ''}`} aria-labelledby={id}>
      <header className="os-panel-head">
        <div><H id={id} className="os-panel-title">{title}</H>{subtitle && <p className="os-panel-subtitle">{subtitle}</p>}</div>
        {meta && <div className="os-panel-meta">{meta}</div>}
      </header>
      {children !== undefined && <div className="os-panel-body">{children}</div>}
      {footer && <footer className="os-panel-foot">{footer}</footer>}
    </section>
  )
}

/**
 * Page heading (REOS-94, Figma node 6:4773): a breadcrumb trail, the page title (the page's only h1), a one-line description, and up to two page actions on
 * the right (a secondary and a primary Button). The breadcrumb is a labelled navigation list; its last entry is the current page.
 */
export function PageHeading({ trail, title, description, actions }: { trail: string[]; title: string; description?: ReactNode; actions?: ReactNode }) {
  return (
    <div className="os-page-heading">
      <nav aria-label="Breadcrumb"><ol className="os-breadcrumb">{trail.map((t, i) => <li key={i} aria-current={i === trail.length - 1 ? 'page' : undefined}>{t}</li>)}</ol></nav>
      <div className="os-page-title-row">
        <div className="os-page-intro"><h1 className="os-page-title">{title}</h1>{description && <p className="os-page-description">{description}</p>}</div>
        {actions && <div className="os-page-actions">{actions}</div>}
      </div>
    </div>
  )
}

/** Metric card: label, large value with an optional unit, and a detail line whose colour repeats its meaning (amber for attention, green for better). */
export function MetricCard({ label, value, unit, detail, tone }: { label: string; value: ReactNode; unit?: string; detail?: ReactNode; tone?: Tone | 'accent' }) {
  return (
    <div className="os-metric" role="group" aria-label={label}>
      <p className="os-metric-label">{label}</p>
      <p className="os-metric-value">{value}{unit && <span className="os-metric-unit"> {unit}</span>}</p>
      {detail && <p className={`os-metric-detail ${tone ?? 'neutral'}`}>{detail}</p>}
    </div>
  )
}

/** Property list: label left, value right (Release properties, Event details). `mono` sets identifiers, versions and times in the monospace face. */
export function PropertyList({ items }: { items: { label: string; value: ReactNode; mono?: boolean }[] }) {
  return (
    <dl className="os-props">
      {items.map(i => <div key={i.label} className="os-prop"><dt>{i.label}</dt><dd className={i.mono ? 'mono' : undefined}>{i.value}</dd></div>)}
    </dl>
  )
}

/** First cell of a two-line row: name on the first line, ID and context on the second (Checkout resilience / RLS-284 · Commerce · 6 services). */
export function TwoLine({ primary, secondary }: { primary: ReactNode; secondary?: ReactNode }) {
  return <span className="os-twoline"><span className="primary">{primary}</span>{secondary && <span className="secondary">{secondary}</span>}</span>
}

export interface TabItem { id: string; label: string; count?: number; disabled?: boolean }

/**
 * Tabs with optional counts (ARIA tabs pattern): one tab stop, arrow keys, Home and End move and select; the selected tab has the accent rule.
 * Render the content in a <TabPanel> with the same `id` and `group`.
 */
export function Tabs({ tabs, value, onChange, label, group }: { tabs: TabItem[]; value: string; onChange: (id: string) => void; label: string; group: string }) {
  const refs = useRef<(HTMLButtonElement | null)[]>([])
  const enabled = tabs.map((t, i) => (t.disabled ? -1 : i)).filter(i => i >= 0)
  const move = (e: KeyboardEvent, from: number) => {
    const at = enabled.indexOf(from)
    const to = e.key === 'ArrowRight' ? enabled[(at + 1) % enabled.length] : e.key === 'ArrowLeft' ? enabled[(at - 1 + enabled.length) % enabled.length]
      : e.key === 'Home' ? enabled[0] : e.key === 'End' ? enabled[enabled.length - 1] : undefined
    if (to === undefined) return
    e.preventDefault()
    refs.current[to]?.focus()
    onChange(tabs[to].id)
  }
  return (
    <div className="os-tabs" role="tablist" aria-label={label}>
      {tabs.map((t, i) => (
        <button key={t.id} ref={el => { refs.current[i] = el }} type="button" role="tab" id={`${group}-tab-${t.id}`} aria-controls={`${group}-panel-${t.id}`}
          aria-selected={t.id === value} tabIndex={t.id === value ? 0 : -1} disabled={t.disabled} className="os-tab"
          onClick={() => onChange(t.id)} onKeyDown={e => move(e, i)}>
          {t.label}{t.count !== undefined && <span className="os-tab-count"> {t.count}</span>}
        </button>
      ))}
    </div>
  )
}

export function TabPanel({ id, group, children }: { id: string; group: string; children: ReactNode }) {
  return <div role="tabpanel" id={`${group}-panel-${id}`} aria-labelledby={`${group}-tab-${id}`} className="os-tabpanel">{children}</div>
}

export interface Column<T> { key: string; header: ReactNode; cell: (row: T) => ReactNode; align?: 'start' | 'end'; mono?: boolean; width?: string }

/**
 * Data table (D41): column headings, rows, an optional footer with the record count and sync time. With `onSelect`, rows are selectable: each row
 * is focusable, Up and Down move between rows, Enter or Space selects, and the selected row carries the selection tint and aria-current.
 */
export function DataTable<T>({ columns, rows, rowKey, caption, footer, selectedKey, onSelect, empty }: {
  columns: Column<T>[]; rows: T[]; rowKey: (row: T) => string; caption: string; footer?: ReactNode; selectedKey?: string | null
  onSelect?: (row: T) => void; empty?: ReactNode
}) {
  const keys = (e: KeyboardEvent<HTMLTableRowElement>, row: T) => {
    if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onSelect?.(row); return }
    if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return
    e.preventDefault()
    const sibling = (e.key === 'ArrowDown' ? e.currentTarget.nextElementSibling : e.currentTarget.previousElementSibling) as HTMLElement | null
    sibling?.focus()
  }
  return (
    <div className="os-table-wrap">
      <table className={`os-table${onSelect ? ' selectable' : ''}`}>
        <caption className="sr-only">{caption}</caption>
        <thead><tr>{columns.map(c => <th key={c.key} scope="col" className={c.align === 'end' ? 'end' : undefined} style={c.width ? { width: c.width } : undefined}>{c.header}</th>)}</tr></thead>
        <tbody>
          {rows.length === 0 && <tr><td colSpan={columns.length} className="os-table-empty">{empty ?? 'Nothing to show.'}</td></tr>}
          {rows.map(row => {
            const k = rowKey(row)
            const selected = selectedKey === k
            return (
              <tr key={k} aria-current={selected ? 'true' : undefined} className={selected ? 'selected' : undefined}
                tabIndex={onSelect ? 0 : undefined} onClick={onSelect ? () => onSelect(row) : undefined} onKeyDown={onSelect ? e => keys(e, row) : undefined}>
                {columns.map(c => <td key={c.key} className={[c.align === 'end' ? 'end' : '', c.mono ? 'mono' : ''].join(' ').trim() || undefined}>{c.cell(row)}</td>)}
              </tr>
            )
          })}
        </tbody>
      </table>
      {footer && <div className="os-table-foot">{footer}</div>}
    </div>
  )
}
