import { useCallback, useEffect, useState } from 'react'
import { createExportJob, exportJobFileUrl, getMe, listExportJobs, type ExportJob, type ExportKind } from './api'
import { errMsg } from './format'
import { fmtDayTime } from './time'

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
 * the RTE, Release Manager and Governance Officer roles.
 */
export function ExportsPanel({ trainId, refreshKey = 0 }: { trainId: string; refreshKey?: number }) {
  const [jobs, setJobs] = useState<ExportJob[] | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [canAudit, setCanAudit] = useState(false)
  const [rev, setRev] = useState(0)

  useEffect(() => { getMe().then(m => setCanAudit((m?.roles ?? []).some(r => r === 'RTE' || r === 'ReleaseManager' || r === 'GovernanceOfficer'))).catch(() => setCanAudit(false)) }, [])

  const load = useCallback(() => listExportJobs(trainId).then(r => { if (!Array.isArray(r)) throw new Error('Export jobs could not be loaded: unexpected response from the server.'); setJobs(r); setErr(null) }).catch(e => setErr(errMsg(e))), [trainId])
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
    try { await createExportJob(trainId, d.kind, d.format); setRev(v => v + 1) } catch (e) { setErr(errMsg(e, 'The export could not be queued.')) } finally { setBusy(null) }
  }

  return (
    <section id="exports" aria-label="Exports">
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
                    {j.status === 'Failed' && <span className="bad" role="alert"> {j.error ?? 'The export failed.'}</span>}
                  </td>
                  <td className="nowrap"><span className="muted">{j.requestedByName ?? '—'}</span> <span className="mono">{fmtDayTime(j.createdAt)}</span></td>
                  <td className="mono nowrap" title={j.sha256 ?? undefined}>{j.sha256 ? `${j.sha256.slice(0, 12)}…` : <span className="muted">—</span>}</td>
                  <td className="mono n nowrap">{j.sizeBytes != null ? fmtSize(j.sizeBytes) : ''}</td>
                  <td className="nowrap">{j.status === 'Done' && <a className="text" href={exportJobFileUrl(j.id)} download={j.fileName} aria-label={`Download ${j.label} ${j.ref}`}>Download</a>}</td>
                </tr>))}
            </tbody>
          </table>)}
      <p className="muted">Records are kept: an export is never deleted. The evidence pack is regenerated on demand, never cached.</p>
    </section>
  )
}
