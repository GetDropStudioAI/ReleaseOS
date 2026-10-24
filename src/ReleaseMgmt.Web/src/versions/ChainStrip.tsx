import { StatusChip } from '../ui'
import { briefText, chainWords } from './versionsLogic.ts'
import type { ChainBrief, ChainStep } from './versionsApi.ts'
import './versions.css'

/**
 * A version's chain (REOS-117, RT2, D48) as a row of steps: Beta 1, Beta 2, CERT, PenTest Cert, Production. Every step says its label, its status in words (a chip with a dot,
 * never colour alone) and its target date; the release being looked at is marked "this release"; a step planned too soon after the one before it carries "▲ Advisory: too soon",
 * with the full sentence in `warnings`. `compact` is the calendar's and the overview's version: label and status only, dates in the tooltip.
 */
export function ChainStrip({ steps, name, currentId, warnings, compact, onOpen }: {
  steps: ChainStep[]; name: string; currentId?: string; warnings?: Record<string, string>; compact?: boolean; onOpen?: (trainId: string) => void
}) {
  return (
    <ol className={`vc-chain${compact ? ' compact' : ''}`} aria-label={`${name}: ${chainWords(steps)}`}>
      {steps.map((s, i) => {
        const here = s.trainId === currentId
        const warn = warnings?.[s.trainId]
        return (
          <li key={s.trainId} className={`vc-step${here ? ' here' : ''}`} aria-current={here ? 'step' : undefined} title={`${s.label}: target ${s.target}`}>
            {i > 0 && <span className="vc-arrow" aria-hidden="true">→</span>}
            <span className="vc-label">
              {onOpen && !here ? <button type="button" className="os-link os-link-button" onClick={() => onOpen(s.trainId)}>{s.label}</button> : s.label}
              {here && <span className="vc-here"> (this release)</span>}
            </span>
            <StatusChip>{s.status}</StatusChip>
            {!compact && <span className="vc-date mono">{s.target}</span>}
            {warn && <span className="vc-warn" title={warn}><span aria-hidden="true">▲ </span><span className="sr-only">Advisory: </span>too soon</span>}
          </li>
        )
      })}
    </ol>
  )
}

/**
 * A release's place in its version, compact, for the calendar and the overview: "Orion 5.2 · Beta 2, 2 of 4" and the chain beneath (labels and statuses), with "▲ too soon"
 * on a step planned too soon after the one before it. No links here: these sit inside rows that already open the release.
 */
export function ChainBriefView({ brief, currentId }: { brief: ChainBrief; currentId: string }) {
  const warn = brief.warning ? Object.fromEntries(brief.steps.filter(s => s.trainId === currentId).map(s => [s.trainId, brief.warning!])) : undefined
  return (
    <div className="vc-brief">
      <p className="vc-brief-line">{briefText(brief)}</p>
      <ChainStrip steps={brief.steps} name={brief.versionName} currentId={currentId} warnings={warn} compact />
    </div>
  )
}
