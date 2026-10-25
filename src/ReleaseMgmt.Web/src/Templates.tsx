import { useCallback, useEffect, useRef, useState } from 'react'
import {
  ApiError, SECTIONS, createTemplate, getLibraryOptions, getOwners, getTemplate, getTemplates, templateAction, updateTemplate,
  type LibraryOption, type Me, type Owner, type TemplateChange, type TemplateDetail, type TemplateInput, type TemplateRow, type TemplateVersionLine,
} from './api'
import { isConflict } from './Conflict'
import { day } from './format'
import { useAction } from './useAction'
import { announce } from './announce'
import {
  Button, ConfirmDialog, ConflictNotice, DataTable, DisabledAction, EmptyState, ErrorState, LoadingState, Notice, PageHeading, Panel, PropertyList, StatusChip, Time, TwoLine,
  type Column, type Tone,
} from './ui'
import { Field, Problem, SelectInput, TextInput } from './mgmt/forms'
import { addTask, approveBlock, blankInput, count, draftProblem, milestoneOffset, milestoneSentence, moveTask, removeTask, taskLines, toInput, updateTask } from './templateLogic'

// REOS-38 / REOS-129 (G7): governed train templates (Draft -> Approved -> Retired) as a table with a panel; the selected template shows its gates, runbook
// skeleton and T-minus plan as tables. Retiring asks for confirmation in a dialog (D41).
// REOS-122 (RT7): a template also carries checklist tasks (on its gates) and milestones (days from the target); an Approved template gets a new version, whose approval retires
// the old one (a confirmation dialog); the approver is not the author; the versions and the changes from the previous one are listed.

const TIERS = ['Low', 'Moderate', 'High', 'VeryHigh']
const CLASSES = ['Standard', 'Compliance']
const BEFORE = ['Gated', 'Executing', 'Complete']
const STATUS_TONE: Record<string, Tone> = { Draft: 'neutral', Approved: 'success', Retired: 'neutral' }
const statusChip = (status: string) => <StatusChip tone={STATUS_TONE[status] ?? 'neutral'}>{status}</StatusChip>
const tMinus = (d: number) => (d < 0 ? `T−${-d}` : d === 0 ? 'T−0' : `T+${d}`)
const clock = (min: number) => `${min < 0 ? '−' : '+'}${Math.floor(Math.abs(min) / 60)}h ${String(Math.abs(min) % 60).padStart(2, '0')}m`

function ReviewDue({ t }: { t: Pick<TemplateRow, 'status' | 'reviewDueOn' | 'reviewOverdue'> }) {
  if (t.status === 'Retired') return <span className="os-muted">retired</span>
  if (!t.reviewDueOn) return <span className="os-muted">not approved yet</span>
  return t.reviewOverdue
    ? <span className="mg-warn">▲ review overdue since {day(t.reviewDueOn)}</span>
    : <span>{day(t.reviewDueOn)}</span>
}

function TeamSelect({ label, value, teams, onChange }: { label: string; value: string | null; teams: Owner[]; onChange: (v: string | null) => void }) {
  return <SelectInput aria-label={label} value={value ?? ''} onChange={e => onChange(e.target.value || null)} options={[{ value: '', label: 'none' }, ...teams.map(t => ({ value: t.id, label: t.name }))]} />
}

const remove = (label: string, onClick: () => void) => <Button variant="destructive" aria-label={label} onClick={onClick}>Remove</Button>
const actionsHead = <span className="sr-only">Actions</span>

/** The editable gates, checklist tasks, milestones, runbook skeleton and T-minus plan of a draft: panels of tables whose cells are the fields. */
function Editor({ value, onChange, teams, library }: { value: TemplateInput; onChange: (v: TemplateInput) => void; teams: Owner[]; library: LibraryOption[] }) {
  const set = <K extends keyof TemplateInput>(k: K, v: TemplateInput[K]) => onChange({ ...value, [k]: v })
  type Rows = 'gates' | 'steps' | 'schedule' | 'milestones'
  const upd = <K extends Rows>(k: K, i: number, patch: Partial<TemplateInput[K][number]>) =>
    set(k, value[k].map((x, n) => (n === i ? { ...x, ...patch } : x)) as TemplateInput[K])
  const drop = <K extends Rows>(k: K, i: number) => set(k, value[k].filter((_, n) => n !== i) as TemplateInput[K])
  const num = (s: string) => (s === '' || s === '-' ? 0 : Number(s))
  const indexed = <T,>(rows: T[]) => rows.map((r, i) => ({ r, i }))
  const gateOptions = value.gates.map((g, i) => ({ value: String(i), label: `${i + 1}. ${g.gateName.trim() || 'unnamed gate'}` }))

  const gates: Column<{ r: TemplateInput['gates'][number]; i: number }>[] = [
    { key: 'n', header: '#', cell: ({ i }) => i + 1, mono: true },
    { key: 'name', header: 'Gate', cell: ({ r: g, i }) => <TextInput className="mid" aria-label={`Gate ${i + 1} name`} value={g.gateName} onChange={e => upd('gates', i, { gateName: e.target.value })} /> },
    { key: 'class', header: 'Class', cell: ({ r: g, i }) => <SelectInput aria-label={`Gate ${i + 1} class`} value={g.gateClass} onChange={e => upd('gates', i, { gateClass: e.target.value })} options={CLASSES.map(c => ({ value: c, label: c }))} /> },
    { key: 'off', header: 'Business days before target', cell: ({ r: g, i }) => <TextInput className="narrow" type="number" min={0} aria-label={`Gate ${i + 1} offset in business days`} value={g.offsetDays} onChange={e => upd('gates', i, { offsetDays: num(e.target.value) })} /> },
    { key: 'before', header: 'Required before', cell: ({ r: g, i }) => <SelectInput aria-label={`Gate ${i + 1} required before`} value={g.requiredBeforeStatus} onChange={e => upd('gates', i, { requiredBeforeStatus: e.target.value })} options={BEFORE.map(c => ({ value: c, label: c }))} /> },
    { key: 'team', header: 'Owner team', cell: ({ r: g, i }) => <TeamSelect label={`Gate ${i + 1} owner team`} value={g.ownerTeamId} teams={teams} onChange={v => upd('gates', i, { ownerTeamId: v })} /> },
    { key: 'x', header: actionsHead, align: 'end', cell: ({ i }) => remove(`Remove gate ${i + 1}`, () => drop('gates', i)) },
  ]
  const tasks: Column<ReturnType<typeof taskLines>[number]>[] = [
    { key: 'gate', header: 'Gate', cell: l => <SelectInput aria-label={`Task ${l.taskIndex + 1} of gate ${l.gateIndex + 1}: gate`} value={String(l.gateIndex)} onChange={e => set('gates', moveTask(value.gates, l.gateIndex, l.taskIndex, Number(e.target.value)))} options={gateOptions} /> },
    { key: 'task', header: 'Task', cell: l => <TextInput className="mid" aria-label={`Gate ${l.gateIndex + 1} task ${l.taskIndex + 1} description`} value={l.task.taskDescription} onChange={e => set('gates', updateTask(value.gates, l.gateIndex, l.taskIndex, { taskDescription: e.target.value }))} /> },
    { key: 'team', header: 'Owner team', cell: l => <TeamSelect label={`Gate ${l.gateIndex + 1} task ${l.taskIndex + 1} owner team`} value={l.task.ownerTeamId} teams={teams} onChange={v => set('gates', updateTask(value.gates, l.gateIndex, l.taskIndex, { ownerTeamId: v }))} /> },
    { key: 'x', header: actionsHead, align: 'end', cell: l => remove(`Remove gate ${l.gateIndex + 1} task ${l.taskIndex + 1}`, () => set('gates', removeTask(value.gates, l.gateIndex, l.taskIndex))) },
  ]
  const milestones: Column<{ r: TemplateInput['milestones'][number]; i: number }>[] = [
    { key: 'name', header: 'Milestone', cell: ({ r: m, i }) => <TextInput className="mid" aria-label={`Milestone ${i + 1} name`} value={m.name} onChange={e => upd('milestones', i, { name: e.target.value })} /> },
    { key: 'off', header: 'Days from target (negative is before)', cell: ({ r: m, i }) => <TextInput className="narrow" type="number" aria-label={`Milestone ${i + 1} offset in days`} value={m.offsetDays} onChange={e => upd('milestones', i, { offsetDays: num(e.target.value) })} /> },
    { key: 'when', header: 'Reads as', cell: ({ r: m }) => <span className="mono">{milestoneOffset(m.offsetDays)}</span>, mono: true },
    { key: 'team', header: 'Owner team', cell: ({ r: m, i }) => <TeamSelect label={`Milestone ${i + 1} owner team`} value={m.ownerTeamId} teams={teams} onChange={v => upd('milestones', i, { ownerTeamId: v })} /> },
    { key: 'x', header: actionsHead, align: 'end', cell: ({ i }) => remove(`Remove milestone ${i + 1}`, () => drop('milestones', i)) },
  ]
  const steps: Column<{ r: TemplateInput['steps'][number]; i: number }>[] = [
    { key: 'code', header: 'Code', cell: ({ r: s, i }) => <TextInput className="narrow" aria-label={`Step ${i + 1} code`} value={s.stepCode} onChange={e => upd('steps', i, { stepCode: e.target.value })} /> },
    { key: 'section', header: 'Section', cell: ({ r: s, i }) => <SelectInput aria-label={`Step ${i + 1} section`} value={s.section} onChange={e => upd('steps', i, { section: e.target.value })} options={SECTIONS.map(c => ({ value: c, label: c }))} /> },
    { key: 'title', header: 'Title', cell: ({ r: s, i }) => <TextInput className="mid" aria-label={`Step ${i + 1} title`} value={s.title} onChange={e => upd('steps', i, { title: e.target.value })} /> },
    { key: 'off', header: 'Minutes from window start', cell: ({ r: s, i }) => <TextInput className="narrow" type="number" aria-label={`Step ${i + 1} offset in minutes`} value={s.offsetMinutes} onChange={e => upd('steps', i, { offsetMinutes: num(e.target.value) })} /> },
    { key: 'dur', header: 'Duration (min)', cell: ({ r: s, i }) => <TextInput className="narrow" type="number" min={1} aria-label={`Step ${i + 1} duration in minutes`} value={s.plannedDurationMin} onChange={e => upd('steps', i, { plannedDurationMin: num(e.target.value) })} /> },
    { key: 'team', header: 'Owner team', cell: ({ r: s, i }) => <TeamSelect label={`Step ${i + 1} owner team`} value={s.ownerTeamId} teams={teams} onChange={v => upd('steps', i, { ownerTeamId: v })} /> },
    { key: 'x', header: actionsHead, align: 'end', cell: ({ i }) => remove(`Remove step ${i + 1}`, () => drop('steps', i)) },
  ]
  const schedule: Column<{ r: TemplateInput['schedule'][number]; i: number }>[] = [
    { key: 'msg', header: 'Message', cell: ({ r: c, i }) => <SelectInput aria-label={`T-minus item ${i + 1} message`} value={c.libraryTemplateId} onChange={e => upd('schedule', i, { libraryTemplateId: e.target.value })} options={library.map(l => ({ value: l.id, label: l.name }))} /> },
    { key: 'off', header: 'Days from target (negative is before)', cell: ({ r: c, i }) => <TextInput className="narrow" type="number" aria-label={`T-minus item ${i + 1} offset in days`} value={c.offsetDays} onChange={e => upd('schedule', i, { offsetDays: num(e.target.value) })} /> },
    { key: 'x', header: actionsHead, align: 'end', cell: ({ i }) => remove(`Remove T-minus item ${i + 1}`, () => drop('schedule', i)) },
  ]
  return (
    <div className="mg-sections">
      <Panel title="Gates" subtitle="Due dates count business days back from the target date. A Compliance gate is locked on every release made from the approved template"
        meta={<Button onClick={() => set('gates', [...value.gates, { gateName: '', gateClass: 'Standard', offsetDays: 1, requiredBeforeStatus: 'Gated', ownerTeamId: null, tasks: [] }])}>Add gate</Button>}>
        <DataTable caption="Gates" columns={gates} rows={indexed(value.gates)} rowKey={x => String(x.i)} empty="No gates." />
      </Panel>
      <Panel title="Checklist tasks" subtitle="Each task goes onto its gate on every release made from the template"
        meta={value.gates.length > 0 ? <Button onClick={() => set('gates', addTask(value.gates, value.gates.length - 1))}>Add task</Button> : undefined}>
        {value.gates.length === 0
          ? <EmptyState title="Add a gate first">Tasks belong to a gate.</EmptyState>
          : <DataTable caption="Checklist tasks" columns={tasks} rows={taskLines(value.gates)} rowKey={l => `${l.gateIndex}-${l.taskIndex}`} empty="No tasks." />}
      </Panel>
      <Panel title="Milestones" subtitle="Dated from the target in calendar days, so they move with it. A negative number is before the target"
        meta={<Button onClick={() => set('milestones', [...value.milestones, { name: '', offsetDays: -14, ownerTeamId: null }])}>Add milestone</Button>}>
        <DataTable caption="Milestones" columns={milestones} rows={indexed(value.milestones)} rowKey={x => String(x.i)} empty="No milestones." />
      </Panel>
      <Panel title="Runbook skeleton" subtitle="Steps, in minutes from the start of the deployment window"
        meta={<Button onClick={() => set('steps', [...value.steps, { stepCode: '', section: 'Deploy', title: '', offsetMinutes: 0, plannedDurationMin: 15, ownerTeamId: null }])}>Add step</Button>}>
        <DataTable caption="Runbook skeleton" columns={steps} rows={indexed(value.steps)} rowKey={x => String(x.i)} empty="No steps." />
      </Panel>
      <Panel title="T-minus plan" subtitle="Messages from the comm library, scheduled by days from the target"
        meta={library.length > 0 ? <Button onClick={() => set('schedule', [...value.schedule, { libraryTemplateId: library[0].id, offsetDays: -7 }])}>Add message</Button> : undefined}>
        {library.length === 0
          ? <EmptyState title="No message templates yet">The T-minus plan uses the comm library (added with Communications).</EmptyState>
          : <DataTable caption="T-minus plan" columns={schedule} rows={indexed(value.schedule)} rowKey={x => String(x.i)} empty="No scheduled messages." />}
      </Panel>
    </div>
  )
}

const CHANGE_TONE: Record<TemplateChange['kind'], Tone> = { Added: 'success', Removed: 'danger', Changed: 'warning' }

function Skeleton({ d, onOpenVersion }: { d: TemplateDetail; onOpenVersion: (id: string) => void }) {
  const gates: Column<TemplateDetail['gates'][number]>[] = [
    { key: 'n', header: '#', cell: g => g.sequenceOrder, mono: true },
    { key: 'name', header: 'Gate', cell: g => g.gateName },
    { key: 'class', header: 'Class', cell: g => g.gateClass === 'Compliance' ? <StatusChip tone="info">Compliance · locked on releases</StatusChip> : g.gateClass },
    { key: 'due', header: 'Due', cell: g => `${tMinus(-g.offsetDays)} business days`, mono: true },
    { key: 'before', header: 'Required before', cell: g => g.requiredBeforeStatus },
    { key: 'tasks', header: 'Tasks', cell: g => (g.tasks ?? []).length, mono: true },
    { key: 'team', header: 'Owner team', cell: g => g.ownerTeamName ?? <span className="os-muted">none</span> },
  ]
  const tasks = d.gates.flatMap(g => (g.tasks ?? []).map(t => ({ gate: g.gateName, ...t })))
  const taskColumns: Column<(typeof tasks)[number]>[] = [
    { key: 'gate', header: 'Gate', cell: t => t.gate },
    { key: 'task', header: 'Task', cell: t => t.taskDescription },
    { key: 'team', header: 'Owner team', cell: t => t.ownerTeamName ?? <span className="os-muted">the person who creates the release</span> },
  ]
  const milestones: Column<TemplateDetail['milestones'][number]>[] = [
    { key: 'when', header: 'When', cell: m => milestoneOffset(m.offsetDays), mono: true },
    { key: 'name', header: 'Milestone', cell: m => <TwoLine primary={m.name} secondary={milestoneSentence(m.offsetDays)} /> },
    { key: 'team', header: 'Owner team', cell: m => m.ownerTeamName ?? <span className="os-muted">none</span> },
  ]
  const steps: Column<TemplateDetail['steps'][number]>[] = [
    { key: 'code', header: 'Code', cell: s => s.stepCode, mono: true },
    { key: 'section', header: 'Section', cell: s => s.section },
    { key: 'title', header: 'Title', cell: s => s.title },
    { key: 'from', header: 'From window start', cell: s => clock(s.offsetMinutes), mono: true },
    { key: 'dur', header: 'Duration', cell: s => `${s.plannedDurationMin} min`, mono: true },
    { key: 'team', header: 'Owner team', cell: s => s.ownerTeamName ?? <span className="os-muted">none</span> },
  ]
  const schedule: Column<TemplateDetail['schedule'][number]>[] = [
    { key: 'when', header: 'When', cell: c => tMinus(c.offsetDays), mono: true },
    { key: 'msg', header: 'Message', cell: c => c.libraryName },
    { key: 'type', header: 'Type', cell: c => c.templateType },
  ]
  const versions: Column<TemplateVersionLine>[] = [
    { key: 'v', header: 'Version', cell: v => v.id === d.template.id ? `${v.versionNo} (this one)` : v.versionNo, mono: true },
    { key: 'status', header: 'Status', cell: v => statusChip(v.status) },
    { key: 'by', header: 'Approved by', cell: v => v.approvedByName ?? <span className="os-muted">none</span> },
    { key: 'at', header: 'Approved', cell: v => v.approvedAt ? <Time at={v.approvedAt} date /> : <span className="os-muted">not approved</span> },
  ]
  const changes: Column<TemplateChange>[] = [
    { key: 'kind', header: 'Change', cell: c => <StatusChip tone={CHANGE_TONE[c.kind]}>{c.kind}</StatusChip> },
    { key: 'area', header: 'In', cell: c => c.area },
    { key: 'subject', header: 'What', cell: c => <TwoLine primary={c.subject} secondary={c.compliance ? 'Compliance gate' : undefined} /> },
    { key: 'detail', header: 'Detail', cell: c => c.detail },
  ]
  const previous = d.versions.find(v => v.id === d.template.supersedesTemplateId)
  return (
    <div className="mg-sections">
      {d.template.supersedesTemplateId && (
        <Panel title={`Changes from version ${previous?.versionNo ?? d.template.versionNo - 1}`} meta={`${d.changes.length}`}>
          <DataTable caption="Changes from the previous version" columns={changes} rows={d.changes} rowKey={c => `${c.area}|${c.kind}|${c.subject}|${c.detail}`} empty="No differences from the previous version yet." />
        </Panel>
      )}
      <Panel title="Gates" meta={`${d.gates.length}`}><DataTable caption="Gates" columns={gates} rows={d.gates} rowKey={g => String(g.sequenceOrder)} empty="No gates." /></Panel>
      <Panel title="Checklist tasks" meta={`${tasks.length}`}><DataTable caption="Checklist tasks" columns={taskColumns} rows={tasks} rowKey={t => `${t.gate}|${t.sequenceOrder}`} empty="No tasks." /></Panel>
      <Panel title="Milestones" subtitle="Calendar days from the target" meta={`${d.milestones.length}`}><DataTable caption="Milestones" columns={milestones} rows={d.milestones} rowKey={m => `${m.sequenceOrder}`} empty="No milestones." /></Panel>
      <Panel title="Runbook skeleton" meta={`${d.steps.length}`}><DataTable caption="Runbook skeleton" columns={steps} rows={d.steps} rowKey={s => s.stepCode} empty="No steps." /></Panel>
      <Panel title="T-minus plan" meta={`${d.schedule.length}`}><DataTable caption="T-minus plan" columns={schedule} rows={d.schedule} rowKey={c => `${c.libraryTemplateId}-${c.offsetDays}`} empty="No scheduled messages." /></Panel>
      {d.versions.length > 1 && (
        <Panel title="Versions" subtitle="A new approved version never changes releases already made" meta={`${d.versions.length}`}>
          <DataTable caption="Versions of this template" columns={versions} rows={d.versions} rowKey={v => v.id} selectedKey={d.template.id} onSelect={v => onOpenVersion(v.id)} empty="No other versions." />
        </Panel>
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
  const [loadError, setLoadError] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [conflict, setConflict] = useState<ApiError | null>(null)
  const [confirmRetire, setConfirmRetire] = useState(false)
  const [confirmApprove, setConfirmApprove] = useState(false)
  const [teams, setTeams] = useState<Owner[]>([])
  const [library, setLibrary] = useState<LibraryOption[]>([])
  const { run: once, pending } = useAction()
  const busy = !!pending
  const nameInput = useRef<HTMLInputElement>(null), editBtn = useRef<HTMLButtonElement>(null), newBtn = useRef<HTMLButtonElement>(null)
  // The editor opens beside the list: focus its name field on open, and the Edit (or New template) button when it closes (WCAG 2.4.3).
  const openForm = (m: Mode) => { setProblem(null); setConflict(null); setMode(m); requestAnimationFrame(() => nameInput.current?.focus()) }
  const closeForm = () => { setMode({ kind: 'view' }); requestAnimationFrame(() => (editBtn.current ?? newBtn.current)?.focus()) }

  const load = useCallback(async (id: string | null) => {
    try {
      setRows(await getTemplates())
      setDetail(id ? await getTemplate(id) : null)
      setLoadError(null)
    } catch (e) { setLoadError((e as Error).message) }
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
  const block = detail ? approveBlock(me, detail) : null
  const previous = detail?.versions.find(v => v.id === detail.template.supersedesTemplateId)
  const form = mode.kind === 'view' ? null : mode.draft

  const columns: Column<TemplateRow>[] = [
    { key: 'name', header: 'Name', cell: r => <TwoLine primary={r.name} secondary={[count(r.gateCount, 'gate'), count(r.taskCount, 'task'), count(r.milestoneCount, 'milestone'), count(r.stepCount, 'step')].join(' · ')} /> },
    { key: 'version', header: 'Version', cell: r => r.versionNo, mono: true },
    { key: 'status', header: 'Status', cell: r => statusChip(r.status) },
    { key: 'risk', header: 'Default risk', cell: r => r.defaultRiskTier },
    { key: 'review', header: 'Review due', cell: r => <ReviewDue t={r} /> },
    { key: 'by', header: 'Approved by', cell: r => r.approvedByName ?? <span className="os-muted">none</span> },
  ]

  return (
    <div className="os-page">
      <PageHeading trail={['Management', 'Templates']} title="Train templates"
        description="A template is drafted, approved by a Release Manager or Governance Officer, then retired. Only a draft can be edited. Approval sets a review date twelve months out."
        actions={canDraft && mode.kind === 'view' ? <Button ref={newBtn} variant="primary" onClick={() => openForm({ kind: 'create', draft: blankInput() })}>New template</Button> : undefined} />
      {loadError && <ErrorState what="The templates" message={loadError} onRetry={() => void load(selected)} />}
      <Problem message={problem} />
      {conflict && <ConflictNotice onUseTheirs={() => { setConflict(null); void load(selected) }} />}
      {rows === null && !loadError && <LoadingState what="the templates" />}

      {rows && (
        <div className="mg-split">
          <Panel title="Templates" meta={`${rows.length} ${rows.length === 1 ? 'template' : 'templates'}`}>
            <DataTable caption="Train templates" columns={columns} rows={rows} rowKey={r => r.id} selectedKey={selected} onSelect={r => select(r.id)} empty="No templates yet." />
          </Panel>

          {form ? (
            <Panel title={mode.kind === 'create' ? 'New template' : `Edit ${t?.name ?? 'template'}`} subtitle="Saved as a draft; approval comes after">
              <div className="mg-form">
                <Field label="Name" hint={t?.supersedesTemplateId && mode.kind === 'edit' ? 'A new version keeps the name of the template it replaces.' : undefined}>
                  <TextInput ref={nameInput} value={form.name} readOnly={mode.kind === 'edit' && !!t?.supersedesTemplateId} onChange={e => setMode({ kind: mode.kind as 'create' | 'edit', draft: { ...form, name: e.target.value } })} />
                </Field>
                <Field label="Default risk">
                  <SelectInput value={form.defaultRiskTier} onChange={e => setMode({ kind: mode.kind as 'create' | 'edit', draft: { ...form, defaultRiskTier: e.target.value } })} options={TIERS.map(x => ({ value: x, label: x }))} />
                </Field>
                <div className="mg-actions">
                  {draftProblem(form)
                    ? <DisabledAction variant="primary" reason={`${mode.kind === 'create' ? 'Create draft' : 'Save draft'} is off: ${draftProblem(form)}.`}>{mode.kind === 'create' ? 'Create draft' : 'Save draft'}</DisabledAction>
                    : <Button variant="primary" disabled={busy}
                        onClick={() => void (mode.kind === 'create' ? run('Creating…', () => createTemplate(form), 'Draft created.') : run('Saving…', () => updateTemplate(t!.id, form, t!.version), 'Draft saved.'))}>
                        {pending ?? (mode.kind === 'create' ? 'Create draft' : 'Save draft')}
                      </Button>}
                  <Button disabled={busy} onClick={() => { closeForm(); setProblem(null); setConflict(null) }}>Cancel</Button>
                </div>
              </div>
            </Panel>
          ) : detail && t ? (
            <Panel title={t.name} subtitle={t.approvedByName ? `Approved by ${t.approvedByName}` : undefined} meta={<>{statusChip(t.status)}</>}>
              <div className="mg-stack">
                <PropertyList items={[
                  { label: 'Template version', value: String(t.versionNo), mono: true },
                  { label: 'Default risk', value: t.defaultRiskTier },
                  { label: 'Review', value: t.status === 'Retired' ? 'Retired, read only' : <ReviewDue t={t} /> },
                  { label: 'Author', value: t.createdByName ?? <span className="os-muted">not recorded</span> },
                  { label: 'Last changed by', value: t.lastEditedByName ?? <span className="os-muted">not recorded</span> },
                  { label: 'Record', value: String(t.version), mono: true },
                ]} />
                {t.status === 'Draft' && detail.needsGovernanceApproval && (
                  <Notice tone="info" title="A Governance Officer approves this draft">
                    {t.supersedesTemplateId ? 'It adds or changes a Compliance gate.' : 'It has a Compliance gate that no approved version has had.'}
                  </Notice>)}
                {t.supersededByTemplateId && t.status !== 'Draft' && <p className="mg-note">A newer version exists; open it from the versions list below.</p>}
                <div className="mg-actions">
                  {canDraft && t.status === 'Draft' && <Button ref={editBtn} disabled={busy} onClick={() => openForm({ kind: 'edit', draft: toInput(detail) })}>Edit</Button>}
                  {canApprove && t.status === 'Draft' && (block
                    ? <DisabledAction variant="primary" reason={block}>Approve</DisabledAction>
                    : <Button variant="primary" disabled={busy}
                        onClick={() => (previous?.status === 'Approved' ? setConfirmApprove(true) : void run('Approving…', () => templateAction(t.id, 'approve', t.version), 'Template approved.'))}>
                        {pending === 'Approving…' ? 'Approving…' : 'Approve'}</Button>)}
                  {canDraft && t.status === 'Approved' && !t.supersededByTemplateId && <Button disabled={busy} onClick={() => void run('Starting…', () => templateAction(t.id, 'new-version', t.version), 'New version started as a draft.')}>{pending === 'Starting…' ? 'Starting…' : 'New version'}</Button>}
                  {canApprove && t.status === 'Approved' && <Button variant="destructive" disabled={busy} onClick={() => setConfirmRetire(true)}>Retire</Button>}
                </div>
                {t.status === 'Draft' && !canApprove && <p className="mg-note">A Release Manager or Governance Officer approves drafts.</p>}
                {t.status === 'Approved' && canDraft && !t.supersededByTemplateId && <p className="mg-note">To change an approved template, start a new version. Releases already made keep what they were made from.</p>}
              </div>
            </Panel>
          ) : (
            <Panel title="Template detail"><EmptyState title="Select a template">Pick a row to see its gates, runbook skeleton and T-minus plan.</EmptyState></Panel>
          )}
        </div>
      )}

      {form ? <Editor value={form} onChange={v => setMode({ kind: mode.kind as 'create' | 'edit', draft: v })} teams={teams} library={library} />
        : detail && <Skeleton d={detail} onOpenVersion={select} />}

      <ConfirmDialog open={confirmApprove} title={`Approve version ${t?.versionNo ?? ''} of ${t?.name ?? 'this template'}?`} confirmLabel="Approve and retire the old version" pendingLabel="Approving…" destructive pending={pending === 'Approving…'}
        onCancel={() => setConfirmApprove(false)}
        onConfirm={() => void run('Approving…', () => templateAction(t!.id, 'approve', t!.version), 'Template approved; the previous version is retired.').then(() => setConfirmApprove(false))}>
        Version {previous?.versionNo} is retired in the same step and release types that start from it start from this version. Releases already made keep the version they were made from.
      </ConfirmDialog>
      <ConfirmDialog open={confirmRetire} title={`Retire ${t?.name ?? 'this template'}?`} confirmLabel="Retire template" pendingLabel="Retiring…" destructive pending={pending === 'Retiring…'}
        onCancel={() => setConfirmRetire(false)}
        onConfirm={() => void run('Retiring…', () => templateAction(t!.id, 'retire', t!.version), 'Template retired.').then(() => setConfirmRetire(false))}>
        It becomes read only. Releases already made from it are not affected.
      </ConfirmDialog>
    </div>
  )
}
