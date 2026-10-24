import { useCallback, useEffect, useState } from 'react'
import { exportJobFileUrl, type ExportJob } from '../api'
import { getRecentExportJobs } from '../exchangeApi'
import { errMsg } from '../format'
import { DataTable, ErrorState, LoadingState, Panel, StatusChip, TwoLine, type Column } from '../ui'
import { kb, shortSha, stamp } from './exchangeLogic'

const POLL_MS = 3000

/**
 * Recent export jobs (REOS-133): the newest PDF and ZIP jobs across every release, from GET /export-jobs. Documents are queued from a release's Exports panel;
 * this lists them with state, who asked and when (UTC with the person's time beside it), and a Download link once a job is Done. Queued and Running jobs refresh every few seconds.
 */
export function ExportJobs() {
  const [jobs, setJobs] = useState<ExportJob[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(() => {
    getRecentExportJobs().then(r => { if (!Array.isArray(r)) throw new Error('unexpected response from the server'); setJobs(r); setError(null) }).catch(e => setError(errMsg(e)))
  }, [])
  useEffect(load, [load])
  const open = jobs?.some(j => j.status === 'Queued' || j.status === 'Running') ?? false
  useEffect(() => {
    if (!open) return
    const t = setInterval(load, POLL_MS)
    return () => clearInterval(t)
  }, [open, load])

  const columns: Column<ExportJob>[] = [
    { key: 'doc', header: 'Document', cell: j => <TwoLine primary={`${j.label}${j.format === 'zip' ? ' · ZIP with files' : ''}`} secondary={<><span className="ex-mono">{j.ref}</span>{j.trainTitle ? ` · ${j.trainTitle}` : ''}</>} /> },
    { key: 'state', header: 'State', cell: j => <TwoLine primary={<StatusChip>{j.status}</StatusChip>} secondary={j.status === 'Failed' ? (j.error ?? 'The export failed.') : undefined} /> },
    { key: 'when', header: 'Requested (UTC)', cell: j => { const t = stamp(j.createdAt); return <TwoLine primary={t.utc} secondary={<>{t.local ? `${t.local} for you · ` : ''}{j.requestedByName ?? '—'}</>} /> } },
    { key: 'file', header: 'File', cell: j => j.sizeBytes != null ? <TwoLine primary={kb(j.sizeBytes)} secondary={j.sha256 ? <span className="ex-mono" title={j.sha256}>{shortSha(j.sha256)}</span> : undefined} /> : '—' },
    { key: 'dl', header: <span className="sr-only">Download</span>, cell: j => j.status !== 'Done' ? null : j.expiredAt
      ? <span title={`The file was deleted on ${stamp(j.expiredAt).utc} after the retention period; generate it again`}>File deleted (retention)</span>
      : <a className="os-link" href={exportJobFileUrl(j.id)} download={j.fileName} aria-label={`Download ${j.label} ${j.ref}`}>Download</a> },
  ]

  return (
    <Panel label="Recent export jobs" title="Recent export jobs" subtitle="Documents generated in the background, newest first."
      footer="Records are kept: an export is never deleted, and its file is kept for the retention period (365 days unless configured otherwise).">
      {error && <ErrorState what="Export jobs" message={error} onRetry={load} />}
      {jobs === null ? (error ? null : <LoadingState what="export jobs" />)
        : <DataTable caption="Recent export jobs" columns={columns} rows={jobs} rowKey={j => j.id} empty="No exports yet. Generate a release report, run sheet or scorecard from a release's Exports panel." />}
    </Panel>
  )
}
