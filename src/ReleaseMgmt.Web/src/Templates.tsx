import { useCallback, useEffect, useId, useRef, useState } from 'react'
import {
  ApiError, SECTIONS, createTemplate, getLibraryOptions, getOwners, getTemplate, getTemplates, templateAction, updateTemplate,
  type LibraryOption, type Me, type Owner, type TemplateDetail, type TemplateInput, type TemplateRow,
} from './api'
import { Conflict, isConflict } from './Conflict'
import { day } from './format'
import { useAction } from './useAction'
import { ConfirmInline } from './ConfirmInline'
import { announce } from './announce'

// REOS-38: governed train templates (Draft -> Approved -> Retired). UI.md: template table (status, review due); the selected template
// shows gates, runbook skeleton and T-minus plan as tables. Status is words and glyphs, actions are text buttons, no modals.

const TIERS = ['Low', 'Moderate', 'High', 'VeryHigh']
const CLASSES = ['Standard', 'Compliance']
const BEFORE = ['Gated', 'Executing', 'Complete']
const blank = (): TemplateInput => ({
  name: '', defaultRiskTier: 'Moderate', schedule: [], steps: [],
  gates: [{ gateName: '', gateClass: 'Standard', offsetDays: 5, requiredBeforeStatus: 'Gated', ownerTeamId: null }],
})
const toInput = (d: TemplateDetail): TemplateInput => ({
  name: d.template.name, defaultRiskTier: d.template.defaultRiskTier,
  gates: d.gates.map(g => ({ gateName: g.gateName, gateClass: g.gateClass, offsetDays: g.offsetDays, requiredBeforeStatus: g.requiredBeforeStatus, ownerTeamId: g.ownerTeamId })),
  steps: d.steps.map(s => ({ stepCode: s.stepCode, section: s.section, title: s.title, offsetMinutes: s.offsetMinutes, plannedDurationMin: s.plannedDurationMin, ownerTeamId: s.ownerTeamId })),
  schedule: d.schedule.map(c => ({ libraryTemplateId: c.libraryTemplateId, offsetDays: c.offsetDays })),
})

const STATUS: Record<string, { glyph: string; cls: string }> = {
  Draft: { glyph: '○', cls: 'muted' },
  Approved: { glyph: '✓', cls: 'ok' },
  Retired: { glyph: '✗', cls: 'muted' },
}
const tMinus = (d: number) => (d < 0 ? `T−${-d}` : d === 0 ? 'T−0' : `T+${d}`)
const clock = (min: number) => `${min < 0 ? '−' : '+'}${Math.floor(Math.abs(min) / 60)}h ${String(Math.abs(min) % 60).padStart(2, '0')}m`

function ReviewDue({ t }: { t: TemplateRow }) {
  if (t.status === 'Retired') return <span className="muted">retired</span>
  if (!t.reviewDueOn) return <span className="muted">not approved yet</span>
  return t.reviewOverdue
    ? <span className="warn">▲ review overdue since {day(t.reviewDueOn)}</span>
    : <span>{day(t.reviewDueOn)}</span>
}

function TeamSelect({ label, value, teams, onChange }: { label: string; value: string | null; teams: Owner[]; onChange: (v: string | null) => void }) {
  return (
    <select className="line" aria-label={label} value={value ?? ''} onChange={e => onChange(e.target.value || null)}>
      <option value="">none</option>
      {teams.map(t => <option key={t.id} value={t.id}>{t.name}</option>)}
    </select>
  )
}

function Editor({ value, onChange, teams, library }: { value: TemplateInput; onChange: (v: TemplateInput) => void; teams: Owner[]; library: LibraryOption[] }) {
  const set = <K extends keyof TemplateInput>(k: K, v: TemplateInput[K]) => onChange({ ...value, [k]: v })
  const upd = <K extends 'gates' | 'steps' | 'schedule'>(k: K, i: number, patch: Partial<TemplateInput[K][number]>) =>
    set(k, value[k].map((x, n) => (n === i ? { ...x, ...patch } : x)) as TemplateInput[K])
  const drop = <K extends 'gates' | 'steps' | 'schedule'>(k: K, i: number) => set(k, value[k].filter((_, n) => n !== i) as TemplateInput[K])
  const num = (s: string) => (s === '' || s === '-' ? 0 : Number(s))
  const id = useId()
  return (
    <div>
      <p className="inline-form">
        <label>Name <input className="line wide" value={value.name} onChange={e => set('name', e.target.value)} /></label>
        <label>Default risk{' '}
          <select className="line" value={value.defaultRiskTier} onChange={e => set('defaultRiskTier', e.target.value)}>
            {TIERS.map(t => <option key={t}>{t}</option>)}
          </select>
        </label>
      </p>

      <h3 id={`${id}-g`} className="cap">Gates</h3>
      <table className="grid" aria-labelledby={`${id}-g`}>
        <thead><tr><th>#</th><th>Gate</th><th>Class</th><th className="n">Business days before target</th><th>Required before</th><th>Owner team</th><th><span className="sr-only">Actions</span></th></tr></thead>
        <tbody>
          {value.gates.map((g, i) => (
            <tr key={i}>
              <td className="n">{i + 1}</td>
              <td><input className="line mid" aria-label={`Gate ${i + 1} name`} value={g.gateName} onChange={e => upd('gates', i, { gateName: e.target.value })} /></td>
              <td><select className="line" aria-label={`Gate ${i + 1} class`} value={g.gateClass} onChange={e => upd('gates', i, { gateClass: e.target.value })}>{CLASSES.map(c => <option key={c}>{c}</option>)}</select></td>
              <td className="n"><input className="line narrow" type="number" min={0} aria-label={`Gate ${i + 1} offset in business days`} value={g.offsetDays} onChange={e => upd('gates', i, { offsetDays: num(e.target.value) })} /></td>
              <td><select className="line" aria-label={`Gate ${i + 1} required before`} value={g.requiredBeforeStatus} onChange={e => upd('gates', i, { requiredBeforeStatus: e.target.value })}>{BEFORE.map(c => <option key={c}>{c}</option>)}</select></td>
              <td><TeamSelect label={`Gate ${i + 1} owner team`} value={g.ownerTeamId} teams={teams} onChange={v => upd('gates', i, { ownerTeamId: v })} /></td>
              <td><button type="button" className="text destructive" aria-label={`Remove gate ${i + 1}`} onClick={() => drop('gates', i)}>Remove</button></td>
            </tr>
          ))}
        </tbody>
      </table>
      <p><button type="button" className="text" onClick={() => set('gates', [...value.gates, { gateName: '', gateClass: 'Standard', offsetDays: 1, requiredBeforeStatus: 'Gated', ownerTeamId: null }])}>Add gate</button></p>

      <h3 id={`${id}-s`} className="cap">Runbook skeleton</h3>
      {value.steps.length > 0 && (
        <table className="grid" aria-labelledby={`${id}-s`}>
          <thead><tr><th>Code</th><th>Section</th><th>Title</th><th className="n">Minutes from window start</th><th className="n">Duration (min)</th><th>Owner team</th><th><span className="sr-only">Actions</span></th></tr></thead>
          <tbody>
            {value.steps.map((s, i) => (
              <tr key={i}>
                <td><input className="line narrow" aria-label={`Step ${i + 1} code`} value={s.stepCode} onChange={e => upd('steps', i, { stepCode: e.target.value })} /></td>
                <td><select className="line" aria-label={`Step ${i + 1} section`} value={s.section} onChange={e => upd('steps', i, { section: e.target.value })}>{SECTIONS.map(c => <option key={c}>{c}</option>)}</select></td>
                <td><input className="line mid" aria-label={`Step ${i + 1} title`} value={s.title} onChange={e => upd('steps', i, { title: e.target.value })} /></td>
                <td className="n"><input className="line narrow" type="number" aria-label={`Step ${i + 1} offset in minutes`} value={s.offsetMinutes} onChange={e => upd('steps', i, { offsetMinutes: num(e.target.value) })} /></td>
                <td className="n"><input className="line narrow" type="number" min={1} aria-label={`Step ${i + 1} duration in minutes`} value={s.plannedDurationMin} onChange={e => upd('steps', i, { plannedDurationMin: num(e.target.value) })} /></td>
                <td><TeamSelect label={`Step ${i + 1} owner team`} value={s.ownerTeamId} teams={teams} onChange={v => upd('steps', i, { ownerTeamId: v })} /></td>
                <td><button type="button" className="text destructive" aria-label={`Remove step ${i + 1}`} onClick={() => drop('steps', i)}>Remove</button></td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <p><button type="button" className="text" onClick={() => set('steps', [...value.steps, { stepCode: '', section: 'Deploy', title: '', offsetMinutes: 0, plannedDurationMin: 15, ownerTeamId: null }])}>Add step</button></p>

      <h3 id={`${id}-t`} className="cap">T-minus plan</h3>
      {library.length === 0
        ? <p className="muted">No message templates exist yet. The T-minus plan uses the comm library (added with Communications).</p>
        : (
          <>
            {value.schedule.length > 0 && (
              <table className="grid" aria-labelledby={`${id}-t`}>
                <thead><tr><th>Message</th><th className="n">Days from target (negative is before)</th><th><span className="sr-only">Actions</span></th></tr></thead>
                <tbody>
                  {value.schedule.map((c, i) => (
                    <tr key={i}>
                      <td><select className="line" aria-label={`T-minus item ${i + 1} message`} value={c.libraryTemplateId} onChange={e => upd('schedule', i, { libraryTemplateId: e.target.value })}>{library.map(l => <option key={l.id} value={l.id}>{l.name}</option>)}</select></td>
                      <td className="n"><input className="line narrow" type="number" aria-label={`T-minus item ${i + 1} offset in days`} value={c.offsetDays} onChange={e => upd('schedule', i, { offsetDays: num(e.target.value) })} /></td>
                      <td><button type="button" className="text destructive" aria-label={`Remove T-minus item ${i + 1}`} onClick={() => drop('schedule', i)}>Remove</button></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
            <p><button type="button" className="text" onClick={() => set('schedule', [...value.schedule, { libraryTemplateId: library[0].id, offsetDays: -7 }])}>Add message</button></p>
          </>
        )}
    </div>
  )
}

function Skeleton({ d }: { d: TemplateDetail }) {
  const id = useId()
  return (
    <div>
      <h3 id={`${id}-g`} className="cap">Gates</h3>
      {d.gates.length === 0 ? <p className="muted">No gates.</p> : (
        <table className="grid" aria-labelledby={`${id}-g`}>
          <thead><tr><th>#</th><th>Gate</th><th>Class</th><th className="n">Due</th><th>Required before</th><th>Owner team</th></tr></thead>
          <tbody>
            {d.gates.map(g => (
              <tr key={g.id}>
                <td className="n">{g.sequenceOrder}</td><td>{g.gateName}</td><td>{g.gateClass}</td>
                <td className="n">{tMinus(-g.offsetDays)} business days</td><td>{g.requiredBeforeStatus}</td>
                <td>{g.ownerTeamName ?? <span className="muted">none</span>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <h3 id={`${id}-s`} className="cap">Runbook skeleton</h3>
      {d.steps.length === 0 ? <p className="muted">No steps.</p> : (
        <table className="grid" aria-labelledby={`${id}-s`}>
          <thead><tr><th>Code</th><th>Section</th><th>Title</th><th className="n">From window start</th><th className="n">Duration</th><th>Owner team</th></tr></thead>
          <tbody>
            {d.steps.map(s => (
              <tr key={s.id}>
                <td className="mono">{s.stepCode}</td><td>{s.section}</td><td>{s.title}</td>
                <td className="n">{clock(s.offsetMinutes)}</td><td className="n">{s.plannedDurationMin} min</td>
                <td>{s.ownerTeamName ?? <span className="muted">none</span>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <h3 id={`${id}-t`} className="cap">T-minus plan</h3>
      {d.schedule.length === 0 ? <p className="muted">No scheduled messages.</p> : (
        <table className="grid" aria-labelledby={`${id}-t`}>
          <thead><tr><th>When</th><th>Message</th><th>Type</th></tr></thead>
          <tbody>
            {d.schedule.map(c => <tr key={c.id}><td className="n">{tMinus(c.offsetDays)}</td><td>{c.libraryName}</td><td>{c.templateType}</td></tr>)}
          </tbody>
        </table>
      )}
    </div>
  )
}

type Mode = { kind: 'view' } | { kind: 'create'; draft: TemplateInput } | { kind: 'edit'; draft: TemplateInput }

export function Templates({ me }: { me: Me }) {
  const canDraft = me.roles.includes('RTE') || me.roles.includes('ReleaseManager')
  const canApprove = me.roles.includes('ReleaseManager') || me.roles.includes('GovernanceOfficer')
  const [rows, setRows] = useState<TemplateRow[] | null>(null)
  const [selected, setSelected] = useState<string | null>(null)
  const [detail, setDetail] = useState<TemplateDetail | null>(null)
  const [mode, setMode] = useState<Mode>({ kind: 'view' })
  const [problem, setProblem] = useState<string | null>(null)
  const [conflict, setConflict] = useState<ApiError | null>(null)
  const [teams, setTeams] = useState<Owner[]>([])
  const [library, setLibrary] = useState<LibraryOption[]>([])
  const { run: once, pending } = useAction()
  const busy = !!pending
  const formHead = useRef<HTMLHeadingElement>(null), detailHead = useRef<HTMLHeadingElement>(null), newBtn = useRef<HTMLButtonElement>(null)
  const whyId = useId()
  // The inline editor replaces the detail: focus its title on open, and the detail title (or New template) when it closes (WCAG 2.4.3).
  const openForm = (m: Mode) => { setProblem(null); setConflict(null); setMode(m); requestAnimationFrame(() => formHead.current?.focus()) }
  const closeForm = () => { setMode({ kind: 'view' }); requestAnimationFrame(() => (detailHead.current ?? newBtn.current)?.focus()) }

  const load = useCallback(async (id: string | null) => {
    try {
      setRows(await getTemplates())
      setDetail(id ? await getTemplate(id) : null)
    } catch (e) { setProblem((e as Error).message) }
  }, [])
  useEffect(() => { void load(null) }, [load])
  useEffect(() => {
    getOwners().then(o => setTeams(o.filter(x => x.kind === 'team'))).catch(() => setTeams([]))
    getLibraryOptions().then(setLibrary).catch(() => setLibrary([]))
  }, [])

  const select = (id: string) => { setSelected(id); setMode({ kind: 'view' }); setProblem(null); setConflict(null); void load(id) }
  const fail = (e: unknown) => {
    if (isConflict(e)) { setConflict(e); setProblem(null) }
    else { setConflict(null); setProblem(e instanceof ApiError ? (e.body?.message ?? e.message) : (e as Error).message) }
  }
  const run = (label: string, f: () => Promise<TemplateDetail>, said: string) => once(label, async () => {
    setProblem(null); setConflict(null)
    try {
      const d = await f()
      setSelected(d.template.id); setDetail(d); if (mode.kind !== 'view') closeForm()
      announce(said)
      setRows(await getTemplates())
    } catch (e) { fail(e) }
  })
  const t = detail?.template
  const form = mode.kind === 'view' ? null : mode.draft

  return (
    <section>
      <h1>Train templates</h1>
      <p className="muted">A template is drafted, approved by a Release Manager or Governance Officer, then retired. Only a draft can be edited. Approval sets a review date twelve months out.</p>
      {problem && <p className="bad" role="alert">✗ {problem}</p>}
      {conflict && (
        <Conflict error={conflict} what="template">
          <button type="button" className="text" onClick={() => { setConflict(null); void load(selected) }}>Reload</button>
        </Conflict>
      )}
      {canDraft && mode.kind === 'view' && <p><button ref={newBtn} type="button" className="text" onClick={() => openForm({ kind: 'create', draft: blank() })}>New template</button></p>}

      <table className="grid" aria-label="Train templates">
        <thead><tr><th>Name</th><th>Status</th><th>Default risk</th><th>Review due</th><th>Approved by</th><th className="n">Gates</th><th className="n">Steps</th></tr></thead>
        <tbody>
          {rows?.map(r => {
            const s = STATUS[r.status] ?? STATUS.Draft
            return (
              <tr key={r.id} className={r.id === selected ? 'selected' : undefined}>
                <td><button type="button" className="text plainlink" aria-current={r.id === selected ? 'true' : undefined} onClick={() => select(r.id)}>{r.name}</button></td>
                <td className={s.cls}>{s.glyph} {r.status}</td>
                <td>{r.defaultRiskTier}</td>
                <td><ReviewDue t={r} /></td>
                <td>{r.approvedByName ?? <span className="muted">none</span>}</td>
                <td className="n">{r.gateCount}</td><td className="n">{r.stepCount}</td>
              </tr>
            )
          })}
        </tbody>
      </table>
      {rows?.length === 0 && <p className="muted">No templates yet.</p>}

      {form && (
        <div>
          <h2 ref={formHead} tabIndex={-1}>{mode.kind === 'create' ? 'New template' : `Edit ${t?.name ?? 'template'}`}</h2>
          <Editor value={form} onChange={v => setMode({ kind: mode.kind as 'create' | 'edit', draft: v })} teams={teams} library={library} />
          <p>
            <button type="button" className="text strong" disabled={busy || !form.name.trim()} aria-describedby={!form.name.trim() ? whyId : undefined}
              onClick={() => void (mode.kind === 'create'
                ? run('Creating…', () => createTemplate(form), 'Draft created.')
                : run('Saving…', () => updateTemplate(t!.id, form, t!.version), 'Draft saved.'))}>
              {pending ?? (mode.kind === 'create' ? 'Create draft' : 'Save draft')}
            </button>{' '}
            <button type="button" className="text" disabled={busy} onClick={() => { closeForm(); setProblem(null); setConflict(null) }}>Cancel</button>
          </p>
          {!form.name.trim() && <p id={whyId} className="muted">{mode.kind === 'create' ? 'Create draft' : 'Save draft'} is off until the template has a name.</p>}
        </div>
      )}

      {!form && detail && t && (
        <div>
          <h2 ref={detailHead} tabIndex={-1}>{t.name}</h2>
          <p>
            <span className={STATUS[t.status]?.cls}>{STATUS[t.status]?.glyph} {t.status}</span>
            {t.approvedByName && <span className="muted"> · approved by {t.approvedByName}</span>}
            {t.status === 'Approved' && <span> · review <ReviewDue t={t} /></span>}
            {t.status === 'Retired' && <span className="muted"> · retired, read only</span>}
          </p>
          <p>
            {canDraft && t.status === 'Draft' && <button type="button" className="text" disabled={busy} onClick={() => openForm({ kind: 'edit', draft: toInput(detail) })}>Edit</button>}{' '}
            {canApprove && t.status === 'Draft' && <button type="button" className="text strong" disabled={busy} onClick={() => void run('Approving…', () => templateAction(t.id, 'approve', t.version), 'Template approved.')}>{pending === 'Approving…' ? 'Approving…' : 'Approve'}</button>}{' '}
            {canApprove && t.status === 'Approved' && <ConfirmInline label="Retire" question="Retire this template? It becomes read only." confirmLabel="Retire template" pendingLabel="Retiring…" disabled={busy}
              onConfirm={() => run('Retiring…', () => templateAction(t.id, 'retire', t.version), 'Template retired.')} />}
          </p>
          {t.status === 'Draft' && !canApprove && <p className="muted">A Release Manager or Governance Officer approves drafts.</p>}
          <Skeleton d={detail} />
        </div>
      )}
      {!form && !detail && <p className="muted">Select a template to see its gates, runbook skeleton and T-minus plan.</p>}
    </section>
  )
}
