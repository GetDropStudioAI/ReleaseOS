import { useEffect, useState } from 'react'
import {
  approveWaiver, waiveGate, createFreeze, getFreezes, getMe, getOwners, getWaivers, grantOverride, requestOverride, requestWaiver,
  type ApiError, type FreezeOverride, type FreezeRow, type Owner, type Waiver,
} from './api'
import { errMsg } from './format'
import { fmtDayTime, serverNow, utcToZonedInput, zonedInputToUtc } from './time'
import { Conflict, isConflict } from './Conflict'

const MIN_REASON = 20
const hasRole = (roles: string[], ...r: string[]) => r.some(x => roles.includes(x))

/** The signed-in user's id and roles; the server enforces every rule, this only decides which actions to offer. */
function useWho() {
  const [who, setWho] = useState<{ id: string; roles: string[] }>({ id: '', roles: [] })
  useEffect(() => { getMe().then(m => m && setWho({ id: m.id, roles: m.roles })).catch(() => setWho({ id: '', roles: [] })) }, [])
  return who
}
function useOwners() {
  const [owners, setOwners] = useState<Owner[]>([])
  useEffect(() => { getOwners().then(o => setOwners(o.filter(x => x.kind === 'user'))).catch(() => setOwners([])) }, [])
  return { owners, name: (id: string) => owners.find(o => o.id === id)?.name ?? '—' }
}

type Form = { kind: 'grant' | 'request'; windowId: string; reason: string; expires: string; requester: string } | null

/** Freeze windows for one train: which ones apply, who has granted an override, and inline request/grant/renew (D29: overrides are immutable, renew inserts a new row). */
export function FreezesPanel({ trainId, refreshKey, onChanged }: { trainId: string; refreshKey: number; onChanged: () => void }) {
  const who = useWho()
  const { owners, name } = useOwners()
  const [rows, setRows] = useState<FreezeRow[] | null>(null)
  const [open, setOpen] = useState<string | null>(null)
  const [form, setForm] = useState<Form>(null)
  const [creating, setCreating] = useState<{ name: string; kind: string; starts: string; ends: string; pattern: string } | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const [note, setNote] = useState<string | null>(null)
  useEffect(() => { getFreezes(trainId).then(setRows).catch(e => setErr(errMsg(e))) }, [trainId, refreshKey])
  if (!rows) return err ? <p className="bad" role="alert">✗ {err}</p> : null

  const canApprove = hasRole(who.roles, 'ReleaseManager', 'GovernanceOfficer')
  const canRequest = hasRole(who.roles, 'RTE', 'ReleaseManager')
  const now = serverNow()
  const live = (o: FreezeOverride) => new Date(o.expiresAt).getTime() > now
  const run = async (fn: () => Promise<unknown>, done?: string) => {
    setErr(null); setNote(null)
    try { await fn(); setForm(null); setCreating(null); if (done) setNote(done); onChanged() } catch (e) { setErr(errMsg(e)) }
  }
  const startForm = (kind: 'grant' | 'request', windowId: string) => setForm({ kind, windowId, reason: '', requester: owners.find(o => o.id !== who.id)?.id ?? '', expires: utcToZonedInput(new Date(now + 8 * 3600e3).toISOString()) })
  const submit = () => {
    if (!form) return
    const expiresAt = form.expires ? zonedInputToUtc(form.expires) : ''
    if (form.kind === 'grant') return run(() => grantOverride(form.windowId, { trainId, requestedByUserId: form.requester, reason: form.reason, expiresAt }), 'Override granted.')
    return run(() => requestOverride(form.windowId, { trainId, reason: form.reason, expiresAt: expiresAt || null }), 'Request sent to the Release Managers and Governance Officers.')
  }

  const stateOf = (r: FreezeRow) => r.active ? { g: '●', cls: r.window.kind === 'Freeze' ? 'bad' : 'warn', w: r.window.kind === 'Freeze' ? 'in force' : 'chill in force' }
    : new Date(r.window.startsAt).getTime() > now ? { g: '○', cls: 'muted', w: 'upcoming' } : { g: '✓', cls: 'muted', w: 'ended' }
  const trainCell = (r: FreezeRow) => {
    if (r.window.kind === 'Chill') return <span className="muted">Advisory only</span>
    if (!r.coversTrain) return <span className="muted">Does not cover this train</span>
    const mine = r.overrides.filter(o => o.releaseTrainId === trainId)
    const valid = mine.filter(live).sort((a, b) => b.expiresAt.localeCompare(a.expiresAt))[0]
    if (valid) return <span className="ok">✓ Override until <span className="mono">{fmtDayTime(valid.expiresAt)}</span></span>
    return <span className={r.active ? 'bad' : 'warn'}>▲ {r.active ? 'Blocks Deploy steps: no valid override' : 'Will block Deploy steps: no override yet'}</span>
  }
  const reasonShort = form ? form.reason.trim().length < MIN_REASON : false

  return (
    <section aria-label="Freeze windows">
      <div className="section-head"><h2 className="cap dark">Freeze windows</h2>
        <span className="muted">{canApprove && <button type="button" className="text" onClick={() => setCreating(creating ? null : { name: '', kind: 'Freeze', starts: '', ends: '', pattern: '' })}>{creating ? 'Cancel' : 'New freeze window'}</button>}</span></div>
      {creating && (
        <div className="inline-form stack">
          <label>Name <input className="line wide" value={creating.name} onChange={e => setCreating({ ...creating, name: e.target.value })} /></label>
          <span>{(['Freeze', 'Chill'] as const).map(k => <span key={k}><button type="button" className={creating.kind === k ? 'text strong' : 'text'} aria-pressed={creating.kind === k} onClick={() => setCreating({ ...creating, kind: k })}>{creating.kind === k ? '● ' : '○ '}{k}</button>{' '}</span>)}
            <span className="muted">A Freeze blocks Live Deploy steps; a Chill only warns.</span></span>
          <label>Starts <input className="line" type="datetime-local" value={creating.starts} onChange={e => setCreating({ ...creating, starts: e.target.value })} /></label>
          <label>Ends <input className="line" type="datetime-local" value={creating.ends} onChange={e => setCreating({ ...creating, ends: e.target.value })} /></label>
          <label>Products (pattern, blank = all) <input className="line" value={creating.pattern} onChange={e => setCreating({ ...creating, pattern: e.target.value })} placeholder="Billing*" /></label>
          <span><button type="button" className="text" disabled={!creating.name.trim() || !creating.starts || !creating.ends}
            onClick={() => run(() => createFreeze({ name: creating.name, kind: creating.kind, startsAt: zonedInputToUtc(creating.starts), endsAt: zonedInputToUtc(creating.ends), productPattern: creating.pattern || null }), 'Freeze window created.')}>Create window</button></span>
          {(!creating.name.trim() || !creating.starts || !creating.ends) && <span className="muted">Needs a name, a start and an end.</span>}
        </div>)}
      {err && <p className="bad" role="alert">✗ {err}</p>}
      {note && <p className="ok" role="status">✓ {note}</p>}
      {rows.length === 0 ? <p className="muted">No freeze windows defined.</p> : (
        <table className="grid">
          <thead><tr><th>State</th><th>Window</th><th>Applies to</th><th>This train</th><th><span className="sr-only">Actions</span></th></tr></thead>
          <tbody>
            {rows.map(r => {
              const s = stateOf(r), mine = r.overrides.filter(o => o.releaseTrainId === trainId)
              const blocking = r.window.kind === 'Freeze' && !!r.coversTrain
              return (
                <RowGroup key={r.window.id}>
                  <tr className={open === r.window.id ? 'selected' : undefined}>
                    <td className={`${s.cls} nowrap`}>{s.g} {s.w}</td>
                    <td>{r.window.name} <span className="muted">· {r.window.kind}</span><br /><span className="mono muted">{fmtDayTime(r.window.startsAt)} to {fmtDayTime(r.window.endsAt)}</span></td>
                    <td className="muted">{r.window.productPattern ? <>Products matching <span className="mono">{r.window.productPattern}</span></> : 'All products'}</td>
                    <td>{trainCell(r)}</td>
                    <td className="nowrap">
                      {mine.length > 0 && <button type="button" className="text" aria-expanded={open === r.window.id} onClick={() => setOpen(open === r.window.id ? null : r.window.id)}>Overrides ({mine.length})</button>}{' '}
                      {blocking && canRequest && <button type="button" className="text" onClick={() => startForm('request', r.window.id)}>Request override</button>}{' '}
                      {blocking && canApprove && <button type="button" className="text" onClick={() => startForm('grant', r.window.id)}>{mine.length > 0 ? 'Renew override' : 'Grant override'}</button>}
                    </td>
                  </tr>
                  {open === r.window.id && (
                    <tr><td colSpan={5}>
                      <table className="grid">
                        <thead><tr><th>State</th><th>Reason</th><th>Requested by</th><th>Approved by</th><th>Approved</th><th>Expires</th></tr></thead>
                        <tbody>{mine.map(o => (
                          <tr key={o.id}>
                            <td className={live(o) ? 'ok nowrap' : 'muted nowrap'}>{live(o) ? '✓ valid' : '○ expired'}</td>
                            <td className="wrap">{o.reason}</td><td>{name(o.requestedByUserId)}</td><td>{name(o.approvedByUserId)}</td>
                            <td className="mono">{fmtDayTime(o.approvedAt)}</td><td className="mono">{fmtDayTime(o.expiresAt)}</td>
                          </tr>))}</tbody>
                      </table>
                      <p className="muted">Overrides never change. A renewal adds a new row.</p>
                    </td></tr>)}
                  {form?.windowId === r.window.id && (
                    <tr><td colSpan={5}>
                      <div className="inline-form stack" role="group" aria-label={form.kind === 'grant' ? 'Grant override' : 'Request override'}>
                        <span className="cap">{form.kind === 'grant' ? (mine.length > 0 ? 'Renew override' : 'Grant override') : 'Request override'} · {r.window.name}</span>
                        {form.kind === 'grant' && <label>Requested by <select className="line" value={form.requester} onChange={e => setForm({ ...form, requester: e.target.value })}>
                          {owners.filter(o => o.id !== who.id).map(o => <option key={o.id} value={o.id}>{o.name}</option>)}</select>
                          <span className="muted"> An RTE or Release Manager other than you.</span></label>}
                        <label>Reason <input className="line wide" value={form.reason} onChange={e => setForm({ ...form, reason: e.target.value })} /></label>
                        <label>Expires <input className="line" type="datetime-local" value={form.expires} onChange={e => setForm({ ...form, expires: e.target.value })} /></label>
                        <span>
                          <button type="button" className="text" disabled={reasonShort || !form.expires || (form.kind === 'grant' && !form.requester)} onClick={submit}>{form.kind === 'grant' ? 'Grant override' : 'Send request'}</button>{' '}
                          <button type="button" className="text" onClick={() => setForm(null)}>Cancel</button>
                        </span>
                        {reasonShort && <span className="muted">The reason needs at least {MIN_REASON} characters ({form.reason.trim().length} so far).</span>}
                        {form.kind === 'request' && <span className="muted">A Release Manager or Governance Officer other than you grants it.</span>}
                      </div>
                    </td></tr>)}
                </RowGroup>)
            })}
          </tbody>
        </table>)}
    </section>
  )
}

function RowGroup({ children }: { children: React.ReactNode }) { return <>{children}</> }

/** Waivers of one gate for the Inspector: request (Governance Officer) and approve (a different Governance Officer), inline (D10). */
export function WaiversPanel({ gateId, gateStatus, gateVersion, refreshKey, onChanged }: { gateId: string; gateStatus: string; gateVersion: number; refreshKey: number; onChanged: () => void }) {
  const who = useWho()
  const { name } = useOwners()
  const [list, setList] = useState<Waiver[] | null>(null)
  const [reason, setReason] = useState<string | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const [conflict, setConflict] = useState<ApiError | null>(null)
  useEffect(() => { getWaivers(gateId).then(setList).catch(e => setErr(errMsg(e))) }, [gateId, refreshKey])
  if (!list) return err ? <p className="bad" role="alert">✗ {err}</p> : null

  const isGo = who.roles.includes('GovernanceOfficer')
  const act = async (fn: () => Promise<unknown>) => {
    setErr(null); setConflict(null)
    try { await fn(); setReason(null); onChanged() } catch (e) { if (isConflict(e)) setConflict(e); else setErr(errMsg(e)); onChanged() }
  }
  const short = (reason ?? '').trim().length < MIN_REASON
  return (
    <section aria-label="Waivers">
      <h3 className="cap">Waivers</h3>
      {list.length === 0 && <p className="muted">No waiver requested.</p>}
      {list.map(w => (
        <div key={w.id}>
          <p><span className={w.approvedByUserId ? 'ok' : 'warn'}>{w.approvedByUserId ? '✓ Approved' : '◐ Awaiting a second Governance Officer'}</span> <span className="muted">· requested by {name(w.requestedByUserId)} <span className="mono">{fmtDayTime(w.requestedAt)}</span>
            {w.approvedByUserId && w.approvedAt && <> · approved by {name(w.approvedByUserId)} <span className="mono">{fmtDayTime(w.approvedAt)}</span></>}</span></p>
          <p>{w.reason}</p>
          {!w.approvedByUserId && isGo && (w.requestedByUserId === who.id
            ? <p className="muted">You requested this waiver; another Governance Officer must approve it.</p>
            : <p><button type="button" className="text" onClick={() => act(() => approveWaiver(w.id, w.version))}>Approve waiver</button></p>)}
        </div>))}
      {isGo && gateStatus === 'InProgress' && list.some(w => w.approvedByUserId) && <p><button type="button" className="text" onClick={() => act(() => waiveGate(gateId, gateVersion))}>Waive gate</button></p>}
      {isGo && (reason === null
        ? <p><button type="button" className="text" onClick={() => setReason('')}>Request waiver</button></p>
        : <div className="inline-form stack">
            <label>Reason <input className="line wide" value={reason} onChange={e => setReason(e.target.value)} /></label>
            <span><button type="button" className="text" disabled={short} onClick={() => act(() => requestWaiver(gateId, reason))}>Request waiver</button>{' '}<button type="button" className="text" onClick={() => setReason(null)}>Cancel</button></span>
            {short && <span className="muted">The reason needs at least {MIN_REASON} characters ({reason.trim().length} so far).</span>}
          </div>)}
      {err && <p className="bad" role="alert">✗ {err}</p>}
      {conflict && <Conflict error={conflict} what="waiver" />}
      <p className="muted">Waiving a gate needs a written reason and an approver other than the requester. Waived is not Certified.</p>
    </section>
  )
}
