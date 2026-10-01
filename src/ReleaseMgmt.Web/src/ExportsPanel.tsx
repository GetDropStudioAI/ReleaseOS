import { useCallback, useEffect, useRef, useState } from 'react'
import { createExportJob, exportJobFileUrl, getMe, listExportJobs, type ExportJob, type ExportKind } from './api'
import { errMsg } from './format'
import { fmtDayTime } from './time'
import { announce } from './announce'

const POLL_MS = 3000
const fmtSize = (n: number) => n < 1024 ? `${n} B` : n < 1024 * 1024 ? `${(n / 1024).toFixed(1)} KB` : `${(n / 1024 / 1024).toFixed(1)} MB`

/** State as glyph + word + colour (no pills). */
function State({ job }: { job: ExportJob }) {
  switch (job.status) {
    case 'Done': return <span className="ok">✓ Done</span>
    case 'Running': return <span className="accent">◐ Running</span>
    case 'Failed': return <span className="bad">✗ Failed</span>
    default: return <span className="muted">○ Queued</span>
  }
}

const DOCS: { kind: ExportKind; format: 'pdf' | 'zip'; label: string; audit: boolean }[] = [
  { kind: 'ReleaseReport', format: 'pdf', label: 'Generate release report', audit: false },
  { kind: 'RunSheet', format: 'pdf', label: 'Generate run sheet', audit: false },
  { kind: 'EvidencePack', format: 'pdf', label: 'Generate evidence pack', audit: true },
  { kind: 'EvidencePack', format: 'zip', label: 'Evidence pack with files (ZIP)', audit: true },
  { kind: 'Scorecard', format: 'pdf', label: 'Generate scorecard', audit: false },
]

/**
 * PDF exports for one train (REOS-50): buttons to queue each document, a list of jobs with state, and a Download link once a job is Done, showing the file's SHA-256
 * (short; the full value is the title and is also sent as X-Content-SHA256 on download so it can be checked). Queued and Running jobs refresh every few seconds and
 * whenever the train changes over SignalR (refreshKey). A failed job shows its reason inline; nothing is a modal. The evidence pack needs audit rights and is only offered to
 * the RTE, Release Manager and Governance Officer roles. A job finishing while the panel is open is announced once ("… ready to download"); when it is one this user
 * just queued and focus is still in the panel, focus moves to its Download link (nothing downloads without a click). Only a failure seen happening is an alert.
 */
export function ExportsPanel({ trainId, refreshKey = 0 }: { trainId: string; refreshKey?: number }) {
  const [jobs, setJobs] = useState<ExportJob[] | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [canAudit, setCanAudit] = useState(false)
  const [rev, setRev] = useState(0)
  const [fresh, setFresh] = useState<ReadonlySet<string>>(new Set())   // failures that happened while this panel was open: those alone are alerts
  const seen = useRef<{ trainId: string; status: Map<string, ExportJob['status']> } | null>(null)
  const mine = useRef(new Set<string>())   // jobs this user queued from this panel
  const links = useRef(new Map<string, HTMLAnchorElement>())
  const panel = useRef<HTMLElement>(null)

  /** Compare with the previous poll: announce jobs that finished, and offer the download of one this user just queued. */
  const track = useCallback((r: ExportJob[]) => {
    const before = seen.current?.trainId === trainId ? seen.current.status : null
    seen.current = { trainId, status: new Map(r.map(j => [j.id, j.status])) }
    if (!before) return   // first load: history, nothing to announce
    const changed = (j: ExportJob, to: ExportJob['status']) => j.status === to && before.get(j.id) !== to && (before.has(j.id) || mine.current.has(j.id))
    const failed = r.filter(j => changed(j, 'Failed')), done = r.filter(j => changed(j, 'Done'))
    if (failed.length) setFresh(f => new Set([...f, ...failed.map(j => j.id)]))
    if (!done.length) return
    announce(done.map(j => `${j.label} ready to download`).join('. ') + '.')
    const offer = done.find(j => mine.current.has(j.id))
    if (!offer) return
    mine.current.delete(offer.id)
    requestAnimationFrame(() => {
      const a = document.activeElement
      if (!a || a === document.body || panel.current?.contains(a)) links.current.get(offer.id)?.focus()   // never pull focus away from somewhere else
    })
  }, [trainId])

  useEffect(() => { getMe().then(m => setCanAudit((m?.roles ?? []).some(r => r === 'RTE' || r === 'ReleaseManager' || r === 'GovernanceOfficer'))).catch(() => setCanAudit(false)) }, [])

  const load = useCallback(() => listExportJobs(trainId).then(r => { if (!Array.isArray(r)) throw new Error('Export jobs could not be loaded: unexpected response from the server.'); setJobs(r); setErr(null); track(r) }).catch(e => setErr(errMsg(e))), [trainId, track])
  useEffect(() => { void load() }, [load, refreshKey, rev])

  const open = jobs?.some(j => j.status === 'Queued' || j.status === 'Running') ?? false
  useEffect(() => {
    if (!open) return
    const t = setInterval(() => { void load() }, POLL_MS)
    return () => clearInterval(t)
  }, [open, load])

  const generate = async (d: (typeof DOCS)[number]) => {
    const key = `${d.kind}/${d.format}`
    setBusy(key); setErr(null)
    try { const job = await createExportJob(trainId, d.kind, d.format); mine.current.add(job.id); announce(`${job.label} queued.`); setRev(v => v + 1) } catch (e) { setErr(errMsg(e, 'The export could not be queued.')) } finally { setBusy(null) }
  }

  return (
    <section id="exports" aria-label="Exports" ref={panel}>
      <div className="section-head"><h2 className="cap dark">Exports</h2>
        <span className="actions-col">
          {DOCS.filter(d => canAudit || !d.audit).map(d => (
            <button key={`${d.kind}/${d.format}`} type="button" className="text" disabled={busy !== null} onClick={() => generate(d)}>{busy === `${d.kind}/${d.format}` ? 'Queuing…' : d.label}</button>
          ))}
        </span>
      </div>
      {err && <p className="bad" role="alert">✗ {err}</p>}
      {jobs === null ? (err ? null : <p className="muted">Loading…</p>) : jobs.length === 0
        ? <p className="muted">No exports yet. PDFs are generated in the background and listed here when ready.</p>
        : (
          <table className="grid">
            <thead><tr><th>Document</th><th>State</th><th>Requested</th><th>SHA-256</th><th className="n">Size</th><th><span className="sr-only">Actions</span></th></tr></thead>
            <tbody>
              {jobs.map(j => (
                <tr key={j.id}>
                  <td>{j.label}{j.format === 'zip' ? ' · ZIP with files' : ''} <span className="mono muted">{j.ref}</span></td>
                  <td className="nowrap"><State job={j} />
                    {j.status === 'Failed' && <span className="bad" role={fresh.has(j.id) ? 'alert' : undefined}> {j.error ?? 'The export failed.'}</span>}
                  </td>
                  <td className="nowrap"><span className="muted">{j.requestedByName ?? '—'}</span> <span className="mono">{fmtDayTime(j.createdAt)}</span></td>
                  <td className="mono nowrap" title={j.sha256 ?? undefined}>{j.sha256 ? `${j.sha256.slice(0, 12)}…` : <span className="muted">—</span>}</td>
                  <td className="mono n nowrap">{j.sizeBytes != null ? fmtSize(j.sizeBytes) : ''}</td>
                  <td className="nowrap">{j.status === 'Done' && <a ref={el => { if (el) links.current.set(j.id, el); else links.current.delete(j.id) }} className="text" href={exportJobFileUrl(j.id)} download={j.fileName} aria-label={`Download ${j.label} ${j.ref}`}>Download</a>}</td>
                </tr>))}
            </tbody>
          </table>)}
      <p className="muted">Records are kept: an export is never deleted. The evidence pack is regenerated on demand, never cached.</p>
    </section>
  )
}
