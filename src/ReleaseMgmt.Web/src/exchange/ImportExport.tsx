import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError, type Me } from '../api'
import { errMsg, plural } from '../format'
import { announce } from '../announce'
import { useAction } from '../useAction'
import {
  commitImport, errorReportCsv, getImportRows, getImports, getKinds, previewImport,
  type ImportCommit, type ImportJob, type ImportPreview, type KindInfo, type PlanRow,
} from '../exchangeApi'
import {
  Button, ConfirmDialog, DataTable, ErrorState, LoadingState, Notice, PageHeading, Panel, PropertyList, ReadOnlyNotice, ScrollRegion, StatusChip, TwoLine, type Column,
} from '../ui'
import { GridExports } from './GridExports'
import { ExportJobs } from './ExportJobs'
import { MARK, kb, resultOf, shortSha, stamp } from './exchangeLogic'
import './exchange.css'

const MODES = ['Append', 'Upsert'] as const
type Mode = typeof MODES[number]
const PAGE = 500

/**
 * Imports & exports (PROJECT_SCOPE 9, REOS-48/49, REOS-133): choose a kind and a file, preview every row with its diff marker (+ new, ~ updated, = unchanged, ✗ error, each a symbol and a word),
 * commit all or nothing; the error table, recent imports, the grid catalogue and the recent export jobs sit on the same page. Nothing about validation, previews or the commit
 * changed with the redesign: the same calls, the same guards. Committing a file that decertifies gates asks in a confirmation dialog first.
 */
export function ImportExport({ me }: { me: Me }) {
  const canImport = me.roles.includes('RTE') || me.roles.includes('ReleaseManager')
  const [kinds, setKinds] = useState<KindInfo[]>([])
  const [kind, setKind] = useState('Trains')
  const [mode, setMode] = useState<Mode>('Upsert')
  const [file, setFile] = useState<File | null>(null)
  const [preview, setPreview] = useState<ImportPreview | null>(null)
  const [rows, setRows] = useState<PlanRow[]>([])
  const [busy, setBusy] = useState<'preview' | 'commit' | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const [stale, setStale] = useState(false)
  const [done, setDone] = useState<ImportCommit | null>(null)
  const [needAck, setNeedAck] = useState<string[] | null>(null)
  const [confirming, setConfirming] = useState(false)
  const [history, setHistory] = useState<ImportJob[] | null>(null)
  const [historyErr, setHistoryErr] = useState<string | null>(null)
  const input = useRef<HTMLInputElement>(null)
  const seq = useRef(0)

  const loadHistory = useCallback(() => { getImports().then(h => { setHistory(h); setHistoryErr(null) }).catch(e => setHistoryErr(errMsg(e))) }, [])
  useEffect(() => { if (!canImport) return; getKinds().then(setKinds).catch(e => setErr(errMsg(e))); loadHistory() }, [canImport, loadHistory])

  const run = useCallback(async (f: File, k: string, m: Mode) => {
    const mine = ++seq.current
    setBusy('preview'); setErr(null); setStale(false); setDone(null); setNeedAck(null)
    try {
      const p = await previewImport(k, m, f)
      if (mine !== seq.current) return       // a newer choice superseded this request
      setPreview(p); setRows(p.rows)
      const pc = p.counts
      announce(`Preview of ${p.fileName}: ${pc.new} new, ${pc.updated} updated, ${pc.unchanged} unchanged, ${plural(pc.errors, 'error')}.`)
    } catch (e) {
      if (mine !== seq.current) return
      setPreview(null); setRows([]); setErr(errMsg(e))
    } finally { if (mine === seq.current) { setBusy(null); loadHistory() } }
  }, [loadHistory])

  const choose = (f: File | undefined) => { if (!f) return; setFile(f); run(f, kind, mode) }
  const pickKind = (k: string) => { setKind(k); if (file) run(file, k, mode) }
  const pickMode = (m: Mode) => { setMode(m); if (file) run(file, kind, m) }

  const { run: act, pending } = useAction()
  const more = () => act('more', async () => {
    if (!preview) return
    try { setRows([...rows, ...await getImportRows(preview.jobId, rows.length, PAGE)]) } catch (e) { setErr(errMsg(e)) }
  })

  const commit = async (ack: boolean) => {
    if (!preview) return
    if (busy) return
    setBusy('commit'); setErr(null); setStale(false)
    try {
      const r = await commitImport(preview.jobId, preview.version, ack)
      seq.current++
      announce(`Committed ${r.kind}: ${r.inserted} new, ${r.updated} updated, ${r.unchanged} unchanged.${r.decertifiedGates.length ? ` Decertified: ${r.decertifiedGates.join(', ')}.` : ''}`)
      setDone(r); setPreview(null); setRows([]); setFile(null); setNeedAck(null)
      if (input.current) input.current.value = ''
      loadHistory()
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) { setStale(true); setErr(e.body?.message ?? 'The data changed since the preview.') }
      else if (e instanceof ApiError && e.body?.guard === 'DecertifyNotAcknowledged') setNeedAck(preview.decertifiesGates)
      else setErr(errMsg(e))
      loadHistory()
    } finally { setBusy(null); setConfirming(false) }
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
  const asks = decertifies.length > 0 && !!c && c.errors === 0   // the commit decertifies gates: the person confirms first

  const historyColumns: Column<ImportJob>[] = [
    { key: 'when', header: 'When (UTC)', cell: j => { const t = stamp(j.createdAt); return <TwoLine primary={t.utc} secondary={t.local ? `${t.local} for you` : undefined} /> } },
    { key: 'kind', header: 'Kind', cell: j => <TwoLine primary={j.kind} secondary={j.mode ?? undefined} /> },
    { key: 'file', header: 'File', cell: j => <TwoLine primary={<span className="ex-name">{j.fileName}</span>} secondary={j.uploadedBy ? `by ${j.uploadedBy}` : undefined} /> },
    { key: 'rows', header: 'Rows', align: 'end', cell: j => j.rowCount },
    { key: 'sha', header: 'sha256', cell: j => <span className="ex-mono" title={j.sha256}>{shortSha(j.sha256)}</span> },
    { key: 'result', header: 'Result', cell: j => { const r = resultOf(j); return <TwoLine primary={<StatusChip tone={r.tone}>{r.word}</StatusChip>} secondary={r.detail || undefined} /> } },
  ]
  const errorColumns: Column<ImportPreview['errors'][number] & { i: number }>[] = [
    { key: 'row', header: 'Row', cell: e => (e.row === 0 ? 'Header' : String(e.row)) },
    { key: 'col', header: 'Column', mono: true, cell: e => e.column },
    { key: 'msg', header: 'Message', cell: e => e.message },
  ]

  return (
    <div className="os-page">
      <PageHeading trail={['More', 'Imports & exports']} title="Imports & exports"
        description="Bring data in from CSV with a preview of every row first, and take every grid and release document out." />
      <div className="ex-split">
        <div className="ex-col">
          {!canImport ? <ReadOnlyNotice>Importing is available to Release Train Engineers and Release Managers. Every grid can be exported from the catalogue on the right.</ReadOnlyNotice> : (
            <>
              <Panel label="Import" title={`Import · ${info?.label ?? kind}`} subtitle="UTF-8 CSV, header row required, up to 10,000 rows and 5 MB.">
                <div className="ex-stack">
                  {kinds.length === 0 && !err ? <LoadingState what="the import kinds" /> : (
                    <div className="ex-kinds" role="group" aria-label="Import kind">
                      {kinds.map(k => <Button key={k.kind} aria-pressed={kind === k.kind} onClick={() => pickKind(k.kind)}>{k.label}</Button>)}
                    </div>
                  )}
                  {info && (
                    <p className="ex-help">
                      Upsert key <span className="ex-mono">{info.key.join(' + ')}</span> · required <span className="ex-mono">{info.columns.filter(x => x.required).map(x => x.name).join(', ')}</span>
                      {info.columns.some(x => !x.required) && <> · optional <span className="ex-mono">{info.columns.filter(x => !x.required).map(x => x.name).join(', ')}</span></>}
                    </p>
                  )}
                  <div className="ex-row">
                    <Button variant={preview ? 'secondary' : 'primary'} disabled={!!busy} onClick={() => input.current?.click()}>{busy === 'preview' ? 'Reading the file…' : file ? 'Choose another file…' : 'Choose CSV file…'}</Button>
                    <input ref={input} type="file" hidden accept=".csv,text/csv" aria-label="CSV file to import" onChange={e => choose(e.target.files?.[0])} />
                  </div>
                  {err && <Notice tone="danger" title={stale ? 'The data changed since the preview' : 'The import could not continue'} live
                    action={stale && file ? <Button disabled={!!busy} onClick={() => run(file, kind, mode)}>Preview again</Button> : undefined}>{err}</Notice>}
                  {done && (
                    <Notice tone="success" title={`Committed ${done.kind}: ${done.inserted} new, ${done.updated} updated, ${done.unchanged} unchanged.`} live>
                      {done.decertifiedGates.length > 0 && <>▲ Decertified: {done.decertifiedGates.join(', ')}.</>}
                    </Notice>
                  )}
                </div>
              </Panel>

              {preview && c && (
                <>
                  <Panel label="File" title={<span className="ex-file">{preview.fileName}</span>}>
                    <PropertyList items={[
                      { label: 'Rows', value: plural(preview.rowCount, 'row') },
                      { label: 'Size', value: kb(preview.byteCount) },
                      { label: 'sha256', mono: true, value: <span title={preview.sha256}>{shortSha(preview.sha256)}</span> },
                      { label: 'Uploaded (UTC)', mono: true, value: (() => { const t = stamp(preview.createdAt); return `${t.utc}${t.local ? ` (${t.local} for you)` : ''} by ${me.name}` })() },
                      { label: 'Columns', value: <>UTF-8 · {plural(preview.columns.length, 'column')} recognised{preview.skippedColumns.length > 0 && <>, {preview.skippedColumns.length} skipped (#)</>}</> },
                    ]} />
                  </Panel>

                  <Panel label="Import summary" title="Review and commit" subtitle={why ?? (mode === 'Append' ? 'Append: a row whose key already exists is an error.' : 'Upsert: existing keys are updated; every change is audited with before and after.')}>
                    <div className="ex-stack">
                      <div className="import-bar">
                        <div className="mode" role="group" aria-label="Import mode"><span className="ex-fine">Mode</span>
                          {MODES.map(m => <Button key={m} aria-pressed={mode === m} disabled={!!busy} onClick={() => pickMode(m)}>{m}</Button>)}</div>
                        <ul className="counts">
                          <li className="success"><span className="big">{c.new}</span> new</li>
                          <li className="warning"><span className="big">{c.updated}</span> updated</li>
                          <li><span className="big">{c.unchanged}</span> unchanged</li>
                          <li className={c.errors > 0 ? 'danger' : undefined}><span className="big">{c.errors}</span> {c.errors === 1 ? 'error' : 'errors'}</li>
                        </ul>
                        <span className="ex-grow" />
                        {c.errors > 0 && <Button onClick={downloadErrors}>Download error report</Button>}
                        <Button variant="primary" disabled={!!busy || c.errors > 0 || writes === 0} aria-describedby="commit-why"
                          onClick={() => (asks ? setConfirming(true) : commit(false))}>
                          {busy === 'commit' ? 'Committing…' : `Commit ${plural(writes, 'row')}${asks ? ' and decertify' : ''}`}
                        </Button>
                      </div>
                      <p id="commit-why" className="ex-help">{why ?? (asks ? 'This commit decertifies gates; you are asked to confirm.' : 'Every valid row is written in one transaction, or none are.')}</p>
                      {needAck && <Notice tone="warning" title="Committing decertifies gates" live>
                        {needAck.join(', ')}: its certification is removed and the gate goes back to In progress. Commit again to confirm.</Notice>}
                      {asks && !needAck && <Notice tone="warning" title="Committing decertifies gates">▲ Committing decertifies: {decertifies.join(', ')}.</Notice>}
                    </div>
                  </Panel>

                  {preview.errors.length > 0 && (
                    <Panel label="Errors" title="Errors" subtitle="The whole file is rejected until these are fixed." meta={<StatusChip tone="danger">{plural(preview.errors.length, 'error')}</StatusChip>}>
                      <DataTable caption="Import errors" columns={errorColumns} rows={preview.errors.map((e, i) => ({ ...e, i }))} rowKey={e => String(e.i)} />
                    </Panel>
                  )}
                  {preview.warnings.length > 0 && (
                    <Panel label="Warnings" title="Warnings">
                      <ul className="ex-warnings">{preview.warnings.map((w, i) => (
                        <li key={i}><StatusChip tone="warning">Warning</StatusChip><span><span className="ex-mono">row {w.row} {w.column}</span> {w.message}</span></li>))}</ul>
                    </Panel>
                  )}

                  {rows.length > 0 && (
                    <Panel label="Preview" title="Preview"
                      subtitle={<>Markers: <span className="ex-mono">+</span> new · <span className="ex-mono">~</span> updated (old value struck through, new value underlined) · <span className="ex-mono">=</span> unchanged · <span className="ex-mono">✗</span> error</>}
                      footer={<span>
                        Showing {rows.length.toLocaleString('en-US')} of {preview.rowsTotal.toLocaleString('en-US')} rows
                        {rows.length < preview.rowsTotal && <> · <Button disabled={!!busy || !!pending} onClick={() => void more()}>{pending ? 'Loading more rows…' : `Show ${Math.min(PAGE, preview.rowsTotal - rows.length)} more`}</Button></>}
                        {' '}· Unknown columns are errors; columns starting with # are skipped · Plan imports are refused once a train is Executing.</span>}>
                      <ScrollRegion label="Preview rows">
                        <table className="os-table ex-preview">
                          <caption className="sr-only">Preview rows with their change markers</caption>
                          <thead><tr><th scope="col">Change</th><th scope="col" className="end">Row</th>{preview.columns.map(col => <th key={col} scope="col">{col}</th>)}</tr></thead>
                          <tbody>{rows.map(r => {
                            const m = MARK[r.op]
                            return (
                              <tr key={r.row} className={`op-${r.op}`}>
                                <td><span className={`ex-mark ${m.tone}`}><span aria-hidden="true">{m.glyph}</span> {m.word}</span></td>
                                <td className="num">{r.row}</td>
                                {preview.columns.map(col => (
                                  <td key={col} className={col === 'Title' || col === 'Description' || col === 'Instructions' ? undefined : 'mono'}>
                                    {r.op === 'updated' && r.changed.includes(col)
                                      ? <><del><span className="sr-only">was </span>{r.before?.[col] || '—'}</del> <ins><span className="sr-only">now </span>{r.cells[col] || '—'}</ins></>
                                      : (r.cells[col] || <><span aria-hidden="true">—</span><span className="sr-only">empty</span></>)}
                                  </td>
                                ))}
                              </tr>)
                          })}</tbody>
                        </table>
                      </ScrollRegion>
                    </Panel>
                  )}
                </>
              )}

              <Panel label="Recent imports" title="Recent imports" subtitle="The newest 15 files, committed or not.">
                {historyErr && <ErrorState what="Recent imports" message={historyErr} onRetry={loadHistory} />}
                {history === null ? (historyErr ? null : <LoadingState what="recent imports" />)
                  : <DataTable caption="Recent imports" columns={historyColumns} rows={history} rowKey={j => j.id} empty="No imports yet." />}
              </Panel>
            </>
          )}
        </div>
        <aside className="ex-col" aria-label="Exports">
          <GridExports me={me} />
          <ExportJobs />
        </aside>
      </div>

      <ConfirmDialog open={confirming} title="Commit and decertify?" confirmLabel={`Commit ${plural(writes, 'row')} and decertify`} pendingLabel="Committing…" destructive pending={busy === 'commit'}
        onConfirm={() => void commit(true)} onCancel={() => setConfirming(false)}>
        Committing this file removes the certification of {decertifies.length === 1 ? 'this gate' : 'these gates'}; {decertifies.length === 1 ? 'it goes' : 'they go'} back to In progress and must be certified again.
        <ul className="ex-gate-list">{decertifies.map(g => <li key={g}>{g}</li>)}</ul>
      </ConfirmDialog>
    </div>
  )
}
