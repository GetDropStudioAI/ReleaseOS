import { useEffect, useState } from 'react'
import { getReadiness, getStream, getTrain, type Readiness, type StreamRow, type TrainDetail } from './api'

const GROUPS: { label: string; status: string }[] = [
  { label: 'Executing', status: 'Executing' }, { label: 'Gated', status: 'Gated' },
  { label: 'Planning', status: 'Planning' }, { label: 'Complete (30 days)', status: 'Complete' },
]

// Status is words + glyphs + coloured text, never pills or badges (CLAUDE.md rule 9).
const GLYPH: Record<string, { g: string; cls: string; word: string }> = {
  Certified: { g: '✓', cls: 'ok', word: 'Certified' }, Waived: { g: '▲', cls: 'warn', word: 'Waived' },
  Failed: { g: '✗', cls: 'bad', word: 'Failed' }, InProgress: { g: '◐', cls: 'accent', word: 'In progress' },
  Pending: { g: '○', cls: 'muted', word: 'Pending' },
}
export function Glyph({ status }: { status: string }) {
  const x = GLYPH[status] ?? GLYPH.Pending
  return <span className={x.cls} role="img" aria-label={x.word} title={x.word}>{x.g}</span>
}
const tMinus = (d: number) => d > 0 ? `T-${d}` : d === 0 ? 'T-0' : `T+${-d}`

export function Stream({ rows, selected, onSelect }: { rows: StreamRow[] | null; selected: string | null; onSelect: (id: string) => void }) {
  return (
    <>
      {GROUPS.map(g => {
        const list = (rows ?? []).filter(r => r.status === g.status)
        return (
          <section key={g.label}>
            <h2 className="group-label">{g.label}</h2>
            {rows === null ? <p className="muted empty">Loading…</p> : list.length === 0 ? <p className="muted empty">No trains</p> :
              list.map(r => (
                <button key={r.id} type="button" className="stream-row" aria-current={selected === r.id ? 'true' : undefined} onClick={() => onSelect(r.id)}>
                  <span className="stream-title">{r.title}</span>
                  <span className="muted stream-meta">{r.targetReleaseDate} · {tMinus(r.daysToTarget)}
                    {r.blockers > 0 && <> · <span className="warn">▲ {r.blockers} blocking</span></>}</span>
                  <span className="stream-gates" aria-label="Gates">{r.gates.map((s, i) => <Glyph key={i} status={s} />)}</span>
                </button>
              ))}
          </section>
        )
      })}
    </>
  )
}

export function TrainHeader({ id, refreshKey }: { id: string; refreshKey: number }) {
  const [t, setT] = useState<TrainDetail | null>(null)
  const [r, setR] = useState<Readiness | null>(null)
  const [err, setErr] = useState<string | null>(null)
  useEffect(() => {
    setErr(null)
    getTrain(id).then(setT).catch(e => setErr(e.message))
    getReadiness(id).then(setR).catch(() => setR(null))
  }, [id, refreshKey])
  if (err) return <p className="bad" role="alert">✗ {err}</p>
  if (!t) return <p className="muted">Loading…</p>
  return (
    <>
      <h1>{t.title}</h1>
      <p className="muted">{t.status} · {t.riskTier} risk · target {t.targetReleaseDate} ({tMinus(t.daysToTarget)}){t.changeTicketNumber ? ` · ${t.changeTicketNumber}` : ''}</p>
      {r && (r.ready
        ? <p className="ok" role="status">✓ Ready for {r.target}</p>
        : r.blockers.length > 0 && (
          <section aria-label="Readiness">
            <p role="status"><strong>To reach {r.target}</strong></p>
            <ul className="plain">
              {r.blockers.map((b, i) => (
                <li key={i} className="warn">▲ {b.failure.message}{b.failure.items?.length ? ` — ${b.failure.items.join(', ')}` : ''}</li>
              ))}
            </ul>
          </section>
        ))}
      <h2 className="group-label">Gates</h2>
      <ul className="plain">
        {t.gates.map(g => (
          <li key={g.id} className="gate-row">
            <Glyph status={g.status} /> <strong>{g.name}</strong> <span className="muted">{g.class} · due {g.dueOn} · {g.tasksDone}/{g.tasksTotal} tasks · {g.status}</span>
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
