import { useEffect, useRef, useState } from 'react'
import { ApiError, get, getMe, type Me } from './api'
import { errMsg } from './format'
import { fmtDayTime } from './time'
import { announce } from './announce'
import { ConfirmInline } from './ConfirmInline'

export interface EvidenceRow {
  id: string; entityType: string; entityId: string; fileName: string; contentType: string; sizeBytes: number; sha256: string
  isLocked: boolean; uploadedByUserId: string; uploadedByName: string | null; uploadedAt: string
}

const MAX_BYTES = 50 * 1024 * 1024
export const fmtSize = (n: number) => n < 1024 ? `${n} B` : n < 1024 * 1024 ? `${(n / 1024).toFixed(1)} KB` : `${(n / 1024 / 1024).toFixed(1)} MB`

async function upload(entityType: string, entityId: string, file: File): Promise<void> {
  // Fields first, file last: the server refuses a file part that arrives before them. No client hash is sent; the server computes it.
  const form = new FormData()
  form.append('entityType', entityType); form.append('entityId', entityId); form.append('file', file, file.name)
  const r = await fetch('/api/v1/attachments', { method: 'POST', body: form })
  if (!r.ok) throw new ApiError(r.status, await r.json().catch(() => null))
}
async function remove(id: string): Promise<void> {
  const r = await fetch(`/api/v1/attachments/${id}`, { method: 'DELETE' })
  if (!r.ok) throw new ApiError(r.status, await r.json().catch(() => null))
}

/**
 * Evidence for one gate (or train/task): upload by text button, list with name, size, short SHA-256, locked state, Download / Delete.
 * Certifying the gate locks its evidence (D-rule in the schema); a locked row shows ✓ Locked and no Delete. Errors show inline, no modals.
 */
export function Evidence({ trainId, entityType, entityId, refreshKey = 0 }: { trainId: string; entityType: 'Gate' | 'Task' | 'Train'; entityId: string; refreshKey?: number }) {
  const [rows, setRows] = useState<EvidenceRow[] | null>(null)
  const [me, setMe] = useState<Me | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [rev, setRev] = useState(0)
  const input = useRef<HTMLInputElement>(null)
  const attach = useRef<HTMLButtonElement>(null)
  const heading = useRef<HTMLHeadingElement>(null)

  useEffect(() => { getMe().then(setMe).catch(() => setMe(null)) }, [])
  useEffect(() => {
    get<EvidenceRow[]>(`/api/v1/trains/${trainId}/attachments?entityType=${entityType}&entityId=${encodeURIComponent(entityId)}`)
      .then(r => { setRows(r); setErr(null) }).catch(e => setErr(errMsg(e)))
  }, [trainId, entityType, entityId, refreshKey, rev])

  const roles = me?.roles ?? []
  const canUpload = roles.some(r => r === 'RTE' || r === 'ReleaseManager' || r === 'GovernanceOfficer')
  const canDelete = (a: EvidenceRow) => !a.isLocked && (a.uploadedByUserId === me?.id || roles.includes('RTE') || roles.includes('ReleaseManager'))

  const pick = async (files: FileList | null) => {
    const file = files?.[0]
    if (input.current) input.current.value = ''   // allow choosing the same file again
    if (!file) return
    if (file.size > MAX_BYTES) { setErr(`${file.name} is ${fmtSize(file.size)}; evidence files can be at most 50 MB.`); return }
    setBusy(true); setErr(null)
    try { await upload(entityType, entityId, file); announce(`${file.name} attached.`); setRev(v => v + 1) } catch (e) { setErr(errMsg(e, 'The upload failed. Check your connection and try again.')) } finally { setBusy(false) }
  }
  const del = async (a: EvidenceRow) => {
    setErr(null)
    try { await remove(a.id); announce(`${a.fileName} deleted.`) } catch (e) { setErr(errMsg(e)) }
    setRev(v => v + 1)
    requestAnimationFrame(() => (attach.current ?? heading.current)?.focus())   // the row is gone: land on Attach file (or the section heading)
  }

  return (
    <section aria-label="Evidence">
      <div className="section-head"><h3 className="cap dark" ref={heading} tabIndex={-1}>Evidence</h3>
        {canUpload && <span>
          <input ref={input} type="file" className="sr-only" tabIndex={-1} aria-label="Choose evidence file" onChange={e => pick(e.target.files)} />
          <button ref={attach} type="button" className="text" disabled={busy} onClick={() => input.current?.click()}>{busy ? 'Uploading…' : 'Attach file'}</button>
        </span>}
      </div>
      {err && <p className="bad" role="alert">✗ {err}</p>}
      {rows === null ? (err ? null : <p className="muted">Loading…</p>) : rows.length === 0
        ? <p className="muted">No evidence attached yet.{canUpload ? ' Files up to 50 MB; certifying the gate locks them.' : ''}</p>
        : (
          <table className="grid">
            <thead><tr><th>File</th><th className="n">Size</th><th>SHA-256</th><th>State</th><th>Added</th><th><span className="sr-only">Actions</span></th></tr></thead>
            <tbody>
              {rows.map(a => (
                <tr key={a.id}>
                  <td>{a.fileName}</td>
                  <td className="mono n nowrap">{fmtSize(a.sizeBytes)}</td>
                  <td className="mono nowrap" title={a.sha256}>{a.sha256.slice(0, 12)}…</td>
                  <td className={a.isLocked ? 'ok nowrap' : 'muted nowrap'}>{a.isLocked ? '✓ Locked' : '○ Open'}</td>
                  <td className="nowrap"><span className="muted">{a.uploadedByName ?? '—'}</span> <span className="mono">{fmtDayTime(a.uploadedAt)}</span></td>
                  <td className="nowrap">
                    <a className="text" href={`/api/v1/attachments/${a.id}`} download={a.fileName} aria-label={`Download ${a.fileName}`}>Download</a>
                    {canDelete(a) && <> <ConfirmInline label="Delete" triggerLabel={`Delete ${a.fileName}`} question={`Delete ${a.fileName}?`} confirmLabel="Confirm delete" pendingLabel="Deleting…" onConfirm={() => del(a)} /></>}
                  </td>
                </tr>))}
            </tbody>
          </table>)}
      {rows?.some(a => a.isLocked) && <p className="muted">Locked evidence belongs to a certified gate and can no longer be changed or deleted.</p>}
    </section>
  )
}
