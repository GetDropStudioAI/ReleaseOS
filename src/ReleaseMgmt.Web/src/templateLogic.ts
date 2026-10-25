import type { Me, TemplateDetail, TemplateGate, TemplateInput, TemplateRow, TemplateTask } from './api'

// REOS-122 (RT7): the pure parts of the Templates screen (tasks on gates, milestones in days from the target, versions, who may approve), kept out of the component so they are unit tested.

export const blankInput = (): TemplateInput => ({
  name: '', defaultRiskTier: 'Moderate', schedule: [], steps: [], milestones: [],
  gates: [{ gateName: '', gateClass: 'Standard', offsetDays: 5, requiredBeforeStatus: 'Gated', ownerTeamId: null, tasks: [] }],
})

/** The editable form of a saved template: ids dropped, tasks kept on their gates. */
export const toInput = (d: TemplateDetail): TemplateInput => ({
  name: d.template.name, defaultRiskTier: d.template.defaultRiskTier,
  gates: d.gates.map(g => ({
    gateName: g.gateName, gateClass: g.gateClass, offsetDays: g.offsetDays, requiredBeforeStatus: g.requiredBeforeStatus, ownerTeamId: g.ownerTeamId,
    tasks: (g.tasks ?? []).map(t => ({ taskDescription: t.taskDescription, ownerTeamId: t.ownerTeamId })),
  })),
  steps: d.steps.map(s => ({ stepCode: s.stepCode, section: s.section, title: s.title, offsetMinutes: s.offsetMinutes, plannedDurationMin: s.plannedDurationMin, ownerTeamId: s.ownerTeamId })),
  schedule: d.schedule.map(c => ({ libraryTemplateId: c.libraryTemplateId, offsetDays: c.offsetDays })),
  milestones: d.milestones.map(m => ({ name: m.name, offsetDays: m.offsetDays, ownerTeamId: m.ownerTeamId })),
})

/** "1 task", "2 tasks" */
export const count = (n: number, word: string) => `${n} ${word}${n === 1 ? '' : 's'}`

/** "Monthly · version 2" */
export const versionLabel = (t: Pick<TemplateRow, 'name' | 'versionNo'>) => `${t.name} · version ${t.versionNo}`

/** A milestone's place against the target, in calendar days: T−14 before it, T+7 after it, T−0 on it (the T-minus plan writes it the same way). */
export const milestoneOffset = (days: number) => (days === 0 ? 'T−0' : days < 0 ? `T−${-days}` : `T+${days}`)
export const milestoneSentence = (days: number) => (days === 0 ? 'On the target date' : days < 0 ? `${-days} ${-days === 1 ? 'day' : 'days'} before the target` : `${days} ${days === 1 ? 'day' : 'days'} after the target`)

// ---- checklist tasks, listed as one table but kept on their gates

export interface TaskLine { gateIndex: number; taskIndex: number; gateName: string; task: TemplateTask }

export function taskLines(gates: TemplateGate[]): TaskLine[] {
  return gates.flatMap((g, gi) => (g.tasks ?? []).map((task, ti) => ({ gateIndex: gi, taskIndex: ti, gateName: g.gateName, task })))
}

const withTasks = (gates: TemplateGate[], gi: number, f: (tasks: TemplateTask[]) => TemplateTask[]) =>
  gates.map((g, n) => (n === gi ? { ...g, tasks: f(g.tasks ?? []) } : g))

export const addTask = (gates: TemplateGate[], gi: number): TemplateGate[] => withTasks(gates, gi, t => [...t, { taskDescription: '', ownerTeamId: null }])
export const updateTask = (gates: TemplateGate[], gi: number, ti: number, patch: Partial<TemplateTask>): TemplateGate[] =>
  withTasks(gates, gi, t => t.map((x, n) => (n === ti ? { ...x, ...patch } : x)))
export const removeTask = (gates: TemplateGate[], gi: number, ti: number): TemplateGate[] => withTasks(gates, gi, t => t.filter((_, n) => n !== ti))

/** Moves a task to the end of another gate's list. */
export function moveTask(gates: TemplateGate[], from: number, ti: number, to: number): TemplateGate[] {
  if (from === to) return gates
  const task = (gates[from]?.tasks ?? [])[ti]
  if (!task) return gates
  return withTasks(withTasks(gates, from, t => t.filter((_, n) => n !== ti)), to, t => [...t, task])
}

// ---- who may approve, in the words the server answers with

/**
 * Why this person cannot approve the draft right now, or null. Mirrors the service's order and wording (separation of duties, then the Governance Officer rule) so the screen says it
 * before the person presses the button; the server still decides.
 */
export function approveBlock(me: Me, d: TemplateDetail): string | null {
  const t = d.template
  if (t.status !== 'Draft') return null
  if (t.createdByUserId === me.id) return 'You created this template, so someone else approves it (separation of duties).'
  if (t.lastEditedByUserId === me.id) return 'You last changed this template, so someone else approves it (separation of duties).'
  if (d.needsGovernanceApproval && !me.roles.includes('GovernanceOfficer'))
    return d.template.supersedesTemplateId
      ? 'This version changes a Compliance gate, so a Governance Officer approves it.'
      : 'This template has Compliance gates, so a Governance Officer approves it.'
  return null
}

/** A client-side hint for the editor's Save: the first thing missing, in words, or null. The server validates everything again. */
export function draftProblem(i: TemplateInput): string | null {
  if (!i.name.trim()) return 'the template has no name'
  const gi = i.gates.findIndex(g => !g.gateName.trim())
  if (gi >= 0) return `gate ${gi + 1} has no name`
  const names = new Set<string>()
  for (const [n, g] of i.gates.entries()) {
    const key = g.gateName.trim().toLowerCase()
    if (names.has(key)) return `gate ${n + 1} repeats the name ${g.gateName.trim()}`
    names.add(key)
    const ti = (g.tasks ?? []).findIndex(t => !t.taskDescription.trim())
    if (ti >= 0) return `task ${ti + 1} of gate ${n + 1} has no description`
  }
  const mi = i.milestones.findIndex(m => !m.name.trim())
  if (mi >= 0) return `milestone ${mi + 1} has no name`
  return null
}

