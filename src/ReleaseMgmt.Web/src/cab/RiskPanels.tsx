import { DataTable, EmptyState, Panel, StatusChip, type Column } from '../ui'
import type { Risk, RiskRule, RiskRules } from './cabApi.ts'
import { riskSentence, riskTone, riskWord } from './cabLogic.ts'
import './cab.css'

/**
 * "Why this risk level" (REOS-176): every rule that was checked, whether it applied, what it added and what it found, in words. The level is the band of the sum of the points that applied,
 * so the list always explains the level. There is no hidden model: the same five rules are listed on the change list, and the thresholds are settings with defaults (D56).
 */
export function RiskPanel({ risk, level, legacy }: { risk: Risk | null; level: string; legacy: string | null }) {
  return (
    <Panel title="Why this risk level" subtitle="Five visible rules, added up" meta={<StatusChip tone={riskTone(level)}>{riskWord(level)}</StatusChip>}>
      {!risk ? (
        <EmptyState title="Risk factors were written before the rules existed">
          {legacy ? <pre className="cab-pre">{legacy}</pre> : 'No factors were recorded.'}
          <p className="mg-note">Saving the change runs the rules and replaces this with the list.</p>
        </EmptyState>
      ) : (
        <div className="mg-stack">
          <p className="cab-sum" data-testid="risk-sentence">{riskSentence(risk)}</p>
          <ul className="cab-factors" aria-label="Risk factors">
            {risk.factors.map(f => (
              <li key={f.rule} className={`cab-factor${f.applied ? '' : ' off'}`} data-rule={f.rule} data-applied={f.applied}>
                <span className="cab-factor-name">
                  <span>{f.label}</span>
                  <StatusChip tone={f.applied ? 'warning' : 'neutral'}>{f.applied ? `Applied, +${f.points}` : 'Not applied'}</StatusChip>
                </span>
                <p className="cab-factor-why">{f.why}</p>
              </li>
            ))}
          </ul>
          <p className="mg-note">{risk.levelRule}</p>
        </div>
      )}
    </Panel>
  )
}

/** The rule set before any change exists: what each rule looks at and what it adds, with the thresholds in force. Shown under the list. */
export function RiskRulesPanel({ rules }: { rules: RiskRules | null }) {
  const columns: Column<RiskRule>[] = [
    { key: 'rule', header: 'Rule', cell: r => r.label },
    { key: 'points', header: 'Adds to the score', cell: r => r.points },
    { key: 'what', header: 'What it looks at', cell: r => r.description },
  ]
  return (
    <Panel title="How risk is worked out" subtitle="The same rules are applied to every change; there is no hidden model">
      {!rules ? <p className="mg-note">The rules could not be loaded.</p> : (
        <div className="mg-stack">
          <DataTable caption="Risk rules" columns={columns} rows={rules.rules} rowKey={r => r.rule} />
          <p className="mg-note">{rules.levelRule} The score is the sum of the rules that applied. The margins are settings: freeze margin {rules.thresholds.nearFreezeHours} hours, large scope {rules.thresholds.largeScopeProducts} products, prior failure look-back {rules.thresholds.priorFailureLookbackDays} days.</p>
        </div>
      )}
    </Panel>
  )
}
