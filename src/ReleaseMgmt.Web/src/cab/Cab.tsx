import { useCallback, useEffect, useState } from 'react'
import { ApiError, getStream, type Me, type UserRow } from '../api.ts'
import { announce } from '../announce.tsx'
import { errMsg } from '../format.ts'
import { useAction } from '../useAction.ts'
import { zonedInputToUtc } from '../time.ts'
import { NoPermissionState, PageHeading } from '../ui'
import { createChange, getUsers } from './cabApi.ts'
import { canPlan, emptyForm, newBody, type ChangeForm } from './cabLogic.ts'
import { ChangeDetail } from './ChangeDetail.tsx'
import { ChangeEditor, type Choice } from './ChangeEditor.tsx'
import { ChangeList } from './ChangeList.tsx'
import { RiskRulesPanel } from './RiskPanels.tsx'
import { getRiskRules, type RiskRules } from './cabApi.ts'

/**
 * The Change advisory board page (REOS-176, D56, D63). It exists only while the CAB module is enabled in the workspace: the shell leaves the navigation entry and the route out otherwise, and
 * the API answers 404. Addresses: /cab (the list), /cab/new (raise a change), /cab/{id} (one change). The page reads the address itself and moves with the browser's history.
 */
export function Cab({ me }: { me: Me }) {
  const [, bump] = useState(0)
  useEffect(() => { const f = () => bump(n => n + 1); window.addEventListener('popstate', f); return () => window.removeEventListener('popstate', f) }, [])
  const go = (path: string) => { if (path !== window.location.pathname) window.history.pushState(null, '', path); bump(n => n + 1); window.scrollTo?.(0, 0) }
  const seg = window.location.pathname.split('/').filter(Boolean).map(decodeURIComponent)
  if (seg[1] === 'new') return <NewChange me={me} onDone={id => go(`/cab/${encodeURIComponent(id)}`)} onCancel={() => go('/cab')} />
  if (seg[1]) return <ChangeDetail key={seg[1]} me={me} id={seg[1]} onBack={() => go('/cab')} />
  return <ChangeList me={me} onOpen={id => go(`/cab/${encodeURIComponent(id)}`)} onNew={() => go('/cab/new')} />
}

function NewChange({ me, onDone, onCancel }: { me: Me; onDone: (id: string) => void; onCancel: () => void }) {
  const [form, setForm] = useState<ChangeForm>(() => emptyForm(me.id))
  const [releases, setReleases] = useState<Choice[]>([])
  const [people, setPeople] = useState<UserRow[]>([])
  const [rules, setRules] = useState<RiskRules | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const { run, pending } = useAction()
  useEffect(() => {
    getStream().then(r => setReleases(r.map(t => ({ id: t.id, title: t.title })))).catch(() => setReleases([]))
    getUsers().then(setPeople).catch(() => setPeople([]))
    getRiskRules().then(setRules).catch(() => setRules(null))
  }, [])
  const save = useCallback(() => run('Creating…', async () => {
    setProblem(null)
    try {
      const r = await createChange(newBody(form, me, zonedInputToUtc))
      announce(`${r.key} raised. Risk is ${r.riskLevel}`)
      onDone(r.id)
    } catch (e) { setProblem(e instanceof ApiError && e.status === 422 ? (e.body?.message ?? 'The change was refused.') : errMsg(e)) }
  }), [form, me, onDone, run])
  if (!canPlan(me)) return <div className="os-page"><PageHeading trail={['Workspace', 'Change advisory board', 'New']} title="New change request" /><NoPermissionState what="raise a change request" who="an RTE or Release Manager" /></div>
  return (
    <div className="os-page">
      <PageHeading trail={['Workspace', 'Change advisory board', 'New change request']} title="New change request" description="The risk level is worked out from the rules below once you save; you will see each rule that applied." />
      <div className="mg-split">
        <ChangeEditor title="Change details" form={form} onChange={setForm} releases={releases} people={people} canPickRequester saveLabel="Raise change request" pendingLabel="Creating…" pending={pending === 'Creating…'}
          problem={problem} onSave={() => void save()} onCancel={onCancel} />
        <div className="mg-side"><RiskRulesPanel rules={rules} /></div>
      </div>
    </div>
  )
}
