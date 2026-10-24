import type { ContingencyView, WindowCollision } from './api'
import { dur } from './format'
import { rangeText, timeText } from './utcTime'

/** What a collision shares, in words: "CI ledger-db, Payments API". */
export const sharedText = (c: WindowCollision) => [...c.sharedCis.map(x => `CI ${x}`), ...c.sharedProducts].join(', ')
export const collisionText = (c: WindowCollision) => `overlaps ${c.otherTitle} ${rangeText(c.otherStartsAt, c.otherEndsAt)} · ${sharedText(c)}`

/**
 * REOS-88: the Runbook's contingency line. Spare time before the rollback deadline over the planned execution time, against the minimum
 * (Runbook:MinContingencyPct). Advisory: it never disables an action. Status is a glyph, a word and colour (UI rule).
 */
export function ContingencyLine({ c }: { c: ContingencyView }) {
  if (c.state === 'unknown') return <p className="muted contingency">Contingency not computed: {c.reason}.</p>
  const details = <span className="muted"> · planned <span className="mono">{dur(c.plannedMin!)}</span> · rollback <span className="mono">{dur(c.rollbackMin)}</span> · rollback deadline <span className="mono">{timeText(c.rollbackDeadline!, { date: true })}</span> · minimum {c.minPercent}%</span>
  if (c.state === 'over') return <p className="contingency"><span className="bad">✗ The plan ends <span className="mono">{dur(-c.spareMin!)}</span> after the rollback deadline</span>{details}</p>
  return (
    <p className="contingency">
      {c.state === 'low'
        ? <span className="warn">▲ Contingency {c.percent}%, below the {c.minPercent}% minimum</span>
        : <span className="ok">✓ Contingency {c.percent}%</span>}
      <span className="muted"> · <span className="mono">{dur(c.spareMin!)}</span> spare</span>{details}
    </p>)
}

/** The same advisories as readiness-line items, after the guards: ▲ in warning colour and the word "advisory", so they never read as blockers. */
export function AdvisoryItems({ contingency, collisions, item: Item = 'span' }: { contingency: ContingencyView | null; collisions: WindowCollision[]; item?: 'span' | 'li' }) {
  return (
    <>
      {contingency?.state === 'low' && <Item><span className="warn" aria-hidden="true">▲</span><span className="sr-only">Advisory:</span> Contingency {contingency.percent}%, below {contingency.minPercent}% <span className="muted">(advisory)</span></Item>}
      {contingency?.state === 'over' && <Item><span className="warn" aria-hidden="true">▲</span><span className="sr-only">Advisory:</span> The plan ends {dur(-contingency.spareMin!)} after the rollback deadline <span className="muted">(advisory)</span></Item>}
      {collisions.map(c => <Item key={c.otherTrainId}><span className="warn" aria-hidden="true">▲</span><span className="sr-only">Advisory:</span> Window {collisionText(c)} <span className="muted">(advisory)</span></Item>)}
    </>)
}
