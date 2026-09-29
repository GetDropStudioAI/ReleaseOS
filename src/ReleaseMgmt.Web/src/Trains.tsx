import { useEffect, useState } from 'react'
import { advanceTrain, ApiError, getReadiness, getStream, getTrain, type Readiness, type StreamRow, type TrainDetail } from './api'

const GROUPS: { label: string; status: string }[] = [
  { label: 'Executing', status: 'Executing' }, { label: 'Gated', status: 'Gated' },
  { label: 'Planning', status: 'Planning' }, { label: 'Complete · last 30 days', status: 'Complete' },
]
const ORDER = ['Planning', 'Gated', 'Executing', 'Complete']

// Status is words + glyphs + coloured text, never pills or badges (CLAUDE.md rule 9).
const GLYPH: Record<string, { g: string; cls: string; word: string }> = {
  Certified: { g: '●', cls: 'ok', word: 'Certified' }, Waived: { g: '●', cls: 'warn', word: 'Waived' },
  Failed: { g: '✗', cls: 'bad', word: 'Failed' }, InProgress: { g: '◐', cls: 'accent', word: 'In progress' },
  Pending: { g: '○', cls: 'muted', word: 'Pending' },
}
export function Glyph({ status }: { status: string }) {
  const x = GLYPH[status] ?? GLYPH.Pending
  return <span className={x.cls} role="img" aria-label={x.word} title={x.word}>{x.g}</span>
}
const tMinus = (d: number) => d > 0 ? `T−${d}` : d === 0 ? 'T−0' : `T+${-d}`
const day = (iso: string) => new Date(iso + 'T00:00:00Z').toLocaleDateString('en-GB', { weekday: 'short', day: 'numeric', month: 'short', timeZone: 'UTC' })
const splitId = (title: string) => { const m = /^(R\d+\.\d+)\s+(.*)$/.exec(title); return m ? { id: m[1], name: m[2] } : { id: '', name: title } }
const plural = (n: number, w: string) => `${n} ${w}${n === 1 ? '' : 's'}`

export function Stream({ rows, selected, onSelect }: { rows: StreamRow[] | null; selected: string | null; onSelect: (id: string) => void }) {
  const [q, setQ] = useState('')
  const shown = (rows ?? []).filter(r => r.title.toLowerCase().includes(q.trim().toLowerCase()))
  return (
    <>
      <p><label className="cap" htmlFor="flt">Filter trains</label>
        <input id="flt" className="line block" placeholder="Title or release id" value={q} onChange={e => setQ(e.target.value)} /></p>
      {GROUPS.map(g => {
        const list = shown.filter(r => r.status === g.status)
        return (
          <section key={g.label}>
            <h2 className="group-label">{g.label}</h2>
            {rows === null ? <p className="muted empty">Loading…</p> : list.length === 0 ? <p className="muted empty">No trains</p> :
              list.map(r => {
                const { id, name } = splitId(r.title)
                const done = r.status === 'Complete'
                return (
                  <button key={r.id} type="button" className="stream-row" aria-current={selected === r.id ? 'true' : undefined} onClick={() => onSelect(r.id)}>
                    <span className="stream-top"><span className="mono muted">{id}</span>
                      {!done && (r.blockers > 0 ? <span className="mono warn">▲ {plural(r.blockers, 'blocker')}</span> : <span className="mono muted">0 blockers</span>)}</span>
                    <span className="stream-title">{name}</span>
                    {done
                      ? <span className={r.closeCode === 'Successful' ? 'ok stream-meta' : 'warn stream-meta'}>{r.closeCode === 'Successful' ? '✓ ' : ''}{r.closeCode ?? 'Complete'}{r.endedOn ? ` · ${day(r.endedOn)}` : ''}</span>
                      : <span className="stream-bottom"><span className="muted">{day(r.targetReleaseDate)} · {tMinus(r.daysToTarget)}</span>
                          <span className="mono stream-gates" aria-label="Gates">{r.gates.map((s, i) => <Glyph key={i} status={s} />)}</span></span>}
                  </button>
                )
              })}
          </section>
        )
      })}
    </>
  )
}

export function TrainHeader({ id, refreshKey, onChanged }: { id: string; refreshKey: number; onChanged: () => void }) {
  const [t, setT] = useState<TrainDetail | null>(null)
  const [r, setR] = useState<Readiness | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  useEffect(() => {
    let live = true
    setErr(null)
    getTrain(id).then(async d => {
      if (!live) return
      setT(d)
      setR(d.nextStatus ? await getReadiness(id, d.nextStatus).catch(() => null) : null)
    }).catch(e => live && setErr(e.message))
    return () => { live = false }
  }, [id, refreshKey])

  if (err) return <p className="bad" role="alert">✗ {err}</p>
  if (!t) return <p className="muted">Loading…</p>

  const advance = async () => {
    if (!t.nextStatus) return
    setBusy(true); setErr(null)
    try { await advanceTrain(t.id, t.nextStatus, t.version); onChanged() }
    catch (e) { setErr(e instanceof ApiError ? (e.body?.message ?? e.message) : 'Could not advance the train.'); onChanged() }
    finally { setBusy(false) }
  }
  const { id: relId, name } = splitId(t.title)
  const blockers = r?.blockers.length ?? 0
  const gatesBefore = t.nextStatus ? t.gates.filter(g => ORDER.indexOf(g.requiredBeforeStatus) <= ORDER.indexOf(t.nextStatus!)) : []
  const others = (r?.blockers ?? []).filter(b => b.failure.guard !== 'GateLockout')
  const soon = 'Available in a later milestone'

  return (
    <>
      <div className="header-line">
        <span className="cap">Release train{t.changeTicketNumber ? <> · <span className="mono">{t.changeTicketNumber}</span></> : ''} · Risk {t.riskTier} · <span className="lc">v{t.version}</span></span>
        <span className="actions">
          <button type="button" className="text" disabled title={soon}>Record Go/No-Go</button>
          <button type="button" className="text" disabled title={soon}>Communicate</button>
          <button type="button" className="text" disabled title={soon}>Export</button>
          {t.nextStatus && <button type="button" className="text" disabled={busy || blockers > 0} title={blockers > 0 ? `${plural(blockers, 'guard')} not met` : undefined} onClick={advance}>Advance to {t.nextStatus}</button>}
        </span>
      </div>
      <h1>{relId ? `${relId} ${name}` : name}</h1>
      <p className="muted">{t.status} · Target <strong className="label">{day(t.targetReleaseDate)} {t.targetReleaseDate.slice(0, 4)}</strong> · <span className="mono">{tMinus(t.daysToTarget)}</span> business days</p>
      {err && <p className="bad" role="alert">✗ {err}</p>}
      {t.nextStatus && r && (
        <section className="readiness" aria-label="Readiness">
          <span className="cap">To reach {t.nextStatus}</span>
          {gatesBefore.map(g => {
            const ok = g.status === 'Certified' || g.status === 'Waived'
            return <span key={g.id}><span className={ok ? 'ok' : 'bad'}>{ok ? '✓' : '✗'}</span> {g.name}{!ok && <span className="muted"> due {day(g.dueOn)}</span>}</span>
          })}
          {others.map((b, i) => <span key={i}><span className="bad">✗</span> {b.failure.message}</span>)}
          {r.ready && <span className="ok">✓ Ready for {t.nextStatus}</span>}
        </section>
      )}
      <h2 className="group-label">Gates</h2>
      <ul className="plain">
        {t.gates.map(g => (
          <li key={g.id} className="gate-row">
            <Glyph status={g.status} /> <strong>{g.name}</strong> <span className="muted">{g.class} · due {day(g.dueOn)} · {g.tasksDone}/{g.tasksTotal} tasks · {g.status}</span>
          </li>
        ))}
      </ul>
    </>
  )
}

export function useStream(enabled: boolean) {
  const [rows, setRows] = useState<StreamRow[] | null>(null)
  const [tick, setTick] = useState(0)
  useEffect(() => { if (enabled) getStream().then(setRows).catch(() => setRows([])) }, [tick, enabled])
  return { rows, reload: () => setTick(n => n + 1) }
}
