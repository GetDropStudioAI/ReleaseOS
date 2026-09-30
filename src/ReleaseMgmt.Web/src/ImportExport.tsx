import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError, type Me } from './api'
import { errMsg, plural } from './format'
import { fmtDay, fmtHM } from './time'
import { GridExports } from './GridExports'
import {
  commitImport, errorReportCsv, getImportRows, getImports, getKinds, previewImport,
  type ImportCommit, type ImportJob, type ImportPreview, type KindInfo, type PlanRow,
} from './exchangeApi'

const MODES = ['Append', 'Upsert'] as const
type Mode = typeof MODES[number]
const PAGE = 500

const kb = (n: number) => n < 1024 ? `${n} B` : n < 1048576 ? `${(n / 1024).toFixed(1)} KB` : `${(n / 1048576).toFixed(1)} MB`
const shortSha = (s: string) => `${s.slice(0, 4)}…${s.slice(-4)}`

/** Glyph + word for a preview row (the glyph alone never carries the meaning). */
const MARK: Record<PlanRow['op'], { glyph: string; word: string; cls: string }> = {
  new: { glyph: '+', word: 'new', cls: 'accent' },
  updated: { glyph: '~', word: 'updated', cls: 'warn' },
  unchanged: { glyph: '=', word: 'unchanged', cls: 'muted' },
  error: { glyph: '✗', word: 'error', cls: 'bad' },
}

function Result({ job }: { job: ImportJob }) {
  if (job.status === 'Committed') return <span className="ok">✓ Committed{job.counts ? <span className="muted"> · {job.counts.new} new, {job.counts.updated} updated, {job.counts.unchanged} unchanged</span> : null}</span>
  if (job.status === 'Rejected') return <span className="bad">✗ Rejected <span className="muted">· {job.errors[0]?.message ?? 'file refused'}</span></span>
  if (job.status === 'Expired') return <span className="muted">○ Expired</span>
  return job.errorCount > 0 ? <span className="bad">✗ {plural(job.errorCount, 'error')} <span className="muted">· not committed</span></span> : <span className="accent">● Previewed <span className="muted">· not committed</span></span>
}

/** Imports & exports (PROJECT_SCOPE 9, mockups/ImportExport.html, REOS-48/49): choose a kind and a file, preview every row, commit all or nothing; the export catalogue sits beside it. */
export function ImportExport({ me }: { me: Me }) {
  const canImport = me.roles.includes('RTE') || me.roles.includes('ReleaseManager')
  const [kinds, setKinds] = useState<KindInfo[]>([])
  const [kind, setKind] = useState('Trains')
  const [mode, setMode] = useState<Mode>('Upsert')
  const [file, setFile] = useState<File | null>(null)
  const [preview, setPreview] = useState<ImportPreview | null>(null)
  const [rows, setRows] = useState<PlanRow[]>([])
  const [busy, setBusy] = useState(false)
  const [err, setErr] = useState<string | null>(null)
  const [stale, setStale] = useState(false)
  const [done, setDone] = useState<ImportCommit | null>(null)
  const [needAck, setNeedAck] = useState<string[] | null>(null)
  const [history, setHistory] = useState<ImportJob[]>([])
  const input = useRef<HTMLInputElement>(null)
  const seq = useRef(0)

  const loadHistory = useCallback(() => { getImports().then(setHistory).catch(() => setHistory([])) }, [])
  useEffect(() => { if (!canImport) return; getKinds().then(setKinds).catch(e => setErr(errMsg(e))); loadHistory() }, [canImport, loadHistory])

  const run = useCallback(async (f: File, k: string, m: Mode) => {
    const mine = ++seq.current
    setBusy(true); setErr(null); setStale(false); setDone(null); setNeedAck(null)
    try {
      const p = await previewImport(k, m, f)
      if (mine !== seq.current) return       // a newer choice superseded this request
      setPreview(p); setRows(p.rows)
    } catch (e) {
      if (mine !== seq.current) return
      setPreview(null); setRows([]); setErr(errMsg(e))
    } finally { if (mine === seq.current) { setBusy(false); loadHistory() } }
  }, [loadHistory])

  const choose = (f: File | undefined) => { if (!f) return; setFile(f); run(f, kind, mode) }
  const pickKind = (k: string) => { setKind(k); if (file) run(file, k, mode) }
  const pickMode = (m: Mode) => { setMode(m); if (file) run(file, kind, m) }

  const more = async () => {
    if (!preview) return
    try { setRows([...rows, ...await getImportRows(preview.jobId, rows.length, PAGE)]) } catch (e) { setErr(errMsg(e)) }
  }

  const commit = async (ack: boolean) => {
    if (!preview) return
    setBusy(true); setErr(null); setStale(false)
    try {
      const r = await commitImport(preview.jobId, preview.version, ack)
      seq.current++
      setDone(r); setPreview(null); setRows([]); setFile(null); setNeedAck(null)
      if (input.current) input.current.value = ''
      loadHistory()
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) { setStale(true); setErr(e.body?.message ?? 'The data changed since the preview.') }
      else if (e instanceof ApiError && e.body?.guard === 'DecertifyNotAcknowledged') setNeedAck(preview.decertifiesGates)
      else setErr(errMsg(e))
      loadHistory()
    } finally { setBusy(false) }
  }

  const downloadErrors = () => {
    if (!preview) return
    const url = URL.createObjectURL(new Blob(['﻿' + errorReportCsv(preview.errors)], { type: 'text/csv;charset=utf-8' }))
    const a = document.createElement('a')
    a.href = url; a.download = `${preview.fileName.replace(/\.csv$/i, '')}-errors.csv`; a.click()
    URL.revokeObjectURL(url)
  }

  const info = kinds.find(k => k.kind === kind)
  const c = preview?.counts
  const writes = c ? c.new + c.updated : 0
  const decertifies = preview?.decertifiesGates ?? []
  let why: string | null = null
  if (preview && c) why = c.errors > 0 ? 'Commit is off while the file has errors: nothing is imported until every row is valid.' : writes === 0 ? 'Nothing to commit: every row is unchanged.' : null

  return (
    <div className="split">
      <div>
        <h1>Imports &amp; exports</h1>
        {!canImport ? <p className="muted" role="status">Importing is available to Release Train Engineers and Release Managers. Every grid can be exported from the list on the right.</p> : (
          <>
            <section aria-label="Import">
              <div className="cap">Import · {info?.label ?? kind}</div>
              <nav className="tabs kind-tabs" aria-label="Import kind">
                {(kinds.length ? kinds : []).map(k => <button key={k.kind} type="button" className="choice" aria-pressed={kind === k.kind} onClick={() => pickKind(k.kind)}>{k.label}</button>)}
              </nav>
              {info && (
                <p className="muted small kind-help">
                  Upsert key <span className="mono">{info.key.join(' + ')}</span> · required <span className="mono">{info.columns.filter(x => x.required).map(x => x.name).join(', ')}</span>
                  {info.columns.some(x => !x.required) && <> · optional <span className="mono">{info.columns.filter(x => !x.required).map(x => x.name).join(', ')}</span></>}
                </p>
              )}
              <p className="actions-col">
                <button type="button" className="text" disabled={busy} onClick={() => input.current?.click()}>{file ? 'Choose another file…' : 'Choose CSV file…'}</button>
                <input ref={input} type="file" hidden accept=".csv,text/csv" aria-label="CSV file to import" onChange={e => choose(e.target.files?.[0])} />
                <span className="muted small">UTF-8 CSV, header row required, up to 10,000 rows and 5 MB.</span>
              </p>
              {err && <p className="bad" role="alert">✗ {err}{stale && file && <> <button type="button" className="text" onClick={() => run(file, kind, mode)}>Preview again</button></>}</p>}
              {done && (
                <p className="ok" role="status">✓ Committed {done.kind}: {done.inserted} new, {done.updated} updated, {done.unchanged} unchanged.
                  {done.decertifiedGates.length > 0 && <span className="warn"> ▲ Decertified: {done.decertifiedGates.join(', ')}.</span>}</p>
              )}
            </section>

            {preview && c && (
              <>
                <section aria-label="File">
                  <h2 className="file-name">{preview.fileName}</h2>
                  <div className="muted">{plural(preview.rowCount, 'row')} · {kb(preview.byteCount)} · sha256 <span className="mono" title={preview.sha256}>{shortSha(preview.sha256)}</span> · uploaded {fmtHM(preview.createdAt)} by {me.name} · UTF-8 · {plural(preview.columns.length, 'column')} recognised{preview.skippedColumns.length > 0 && <>, {preview.skippedColumns.length} skipped (#)</>}</div>
                </section>

                <section className="import-bar" aria-label="Import summary">
                  <nav className="tabs" aria-label="Import mode"><span className="cap">Mode</span>
                    {MODES.map(m => <button key={m} type="button" className="choice" aria-pressed={mode === m} disabled={busy} onClick={() => pickMode(m)}>{m}</button>)}</nav>
                  <span role="status" className="counts">
                    <span><span className="big mono accent">{c.new}</span> new</span>{' '}
                    <span><span className="big mono warn">{c.updated}</span> updated</span>{' '}
                    <span><span className="big mono muted">{c.unchanged}</span> unchanged</span>{' '}
                    <span><span className={`big mono ${c.errors > 0 ? 'bad' : 'muted'}`}>{c.errors}</span> {c.errors === 1 ? 'error' : 'errors'}</span>
                  </span>
                  <span className="spacer" />
                  {c.errors > 0 && <button type="button" className="text" onClick={downloadErrors}>Download error report</button>}
                  {decertifies.length > 0 && c.errors === 0
                    ? <button type="button" className="text" disabled={busy || writes === 0} aria-describedby="commit-why" onClick={() => commit(true)}>Commit {plural(writes, 'row')} and decertify</button>
                    : <button type="button" className="text" disabled={busy || c.errors > 0 || writes === 0} aria-describedby="commit-why" onClick={() => commit(false)}>Commit {plural(writes, 'row')}</button>}
                </section>
                <p id="commit-why" className="muted small">{why ?? (mode === 'Append' ? 'Append: a row whose key already exists is an error.' : 'Upsert: existing keys are updated; every change is audited with before and after.')}</p>

                {needAck && <p className="warn" role="alert">▲ Committing decertifies {needAck.join(', ')}: its certification is removed and the gate goes back to In progress. Commit again to confirm.</p>}
                {decertifies.length > 0 && !needAck && c.errors === 0 && <p className="warn">▲ Committing decertifies: {decertifies.join(', ')}.</p>}

                {preview.errors.length > 0 && (
                  <section aria-label="Errors">
                    <div className="cap">Errors · the whole file is rejected until these are fixed</div>
                    <table className="grid">
                      <thead><tr><th className="n">Row</th><th>Column</th><th>Message</th></tr></thead>
                      <tbody>{preview.errors.map((e, i) => (
                        <tr key={i} className="bad"><td className="n">{e.row === 0 ? 'Header' : e.row}</td><td className="mono">{e.column}</td><td className="wrap">{e.message}</td></tr>))}</tbody>
                    </table>
                  </section>
                )}
                {preview.warnings.length > 0 && (
                  <section aria-label="Warnings">
                    <div className="cap">Warnings</div>
                    <ul className="plain">{preview.warnings.map((w, i) => <li key={i} className="warn">▲ <span className="mono">row {w.row} {w.column}</span> {w.message}</li>)}</ul>
                  </section>
                )}

                {rows.length > 0 && (
                  <section aria-label="Preview">
                    <div className="cap">Preview</div>
                    <div className="scroll-x" tabIndex={0} role="region" aria-label="Preview rows">
                      <table className="grid preview-table">
                        <thead><tr><th><span className="sr-only">Change</span></th><th className="n">Row</th>{preview.columns.map(col => <th key={col}>{col}</th>)}</tr></thead>
                        <tbody>{rows.map(r => {
                          const m = MARK[r.op]
                          return (
                            <tr key={r.row} className={r.op === 'error' ? 'bad' : r.op === 'unchanged' ? 'muted' : undefined}>
                              <td className={m.cls}><span aria-hidden="true">{m.glyph}</span><span className="sr-only">{m.word}</span></td>
                              <td className="n muted">{r.row}</td>
                              {preview.columns.map(col => (
                                <td key={col} className={col === 'Title' || col === 'Description' || col === 'Instructions' ? undefined : 'mono'}>
                                  {r.op === 'updated' && r.changed.includes(col) ? <><span className="old">{r.before?.[col] || '—'}</span> {r.cells[col] || '—'}</> : (r.cells[col] || <span className="muted">—</span>)}
                                </td>
                              ))}
                            </tr>)
                        })}</tbody>
                      </table>
                    </div>
                    <p className="muted small">
                      Showing {rows.length.toLocaleString('en-US')} of {preview.rowsTotal.toLocaleString('en-US')} rows
                      {rows.length < preview.rowsTotal && <> · <button type="button" className="text" onClick={more}>Show {Math.min(PAGE, preview.rowsTotal - rows.length)} more</button></>}
                      {' '}· Unknown columns are errors; columns starting with # are skipped · Plan imports are refused once a train is Executing.
                    </p>
                  </section>
                )}
              </>
            )}

            <section aria-label="Recent imports">
              <div className="cap">Recent imports</div>
              <table className="grid">
                <thead><tr><th>When</th><th>Kind</th><th>File</th><th className="n">Rows</th><th>sha256</th><th>Result</th></tr></thead>
                <tbody>
                  {history.map(j => (
                    <tr key={j.id}>
                      <td className="mono muted">{fmtDay(j.createdAt)} {fmtHM(j.createdAt)}</td>
                      <td>{j.kind}{j.mode ? <span className="muted"> · {j.mode}</span> : null}</td>
                      <td className="wrap">{j.fileName}</td>
                      <td className="n">{j.rowCount}</td>
                      <td className="mono muted" title={j.sha256}>{shortSha(j.sha256)}</td>
                      <td><Result job={j} /></td>
                    </tr>))}
                </tbody>
              </table>
              {history.length === 0 && <p className="muted">No imports yet.</p>}
            </section>
          </>
        )}
      </div>
      <aside className="detail" aria-label="Exports"><GridExports me={me} /></aside>
    </div>
  )
}
