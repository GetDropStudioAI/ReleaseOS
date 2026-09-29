import { useState } from 'react'
import { ApiError, commitTasks, parseTasks, type GateRow, type ParsePreview } from './api'
import { errMsg, plural } from './format'
import { Conflict, isConflict } from './Conflict'
import { useDraft } from './session'

export interface BulkDraft { text: string; gateId: string; open: boolean }
export const bulkKey = (trainId: string) => `bulk:${trainId}`

const GRAMMAR = `# Gate name
- task text @owner [Product]
// comment`

/** The "Paste tasks" drawer (PROJECT_SCOPE 5.1, mockups/BulkParser.html). The pasted text is a session draft: it survives a closed tab until it is committed or discarded. */
export function BulkDrawer({ trainId, gates, onClose, onChanged }: { trainId: string; gates: GateRow[]; onClose: () => void; onChanged: () => void }) {
  const [draft, setDraft] = useDraft<BulkDraft>(bulkKey(trainId))
  const [preview, setPreview] = useState<ParsePreview | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const [conflict, setConflict] = useState<ApiError | null>(null)
  const [busy, setBusy] = useState(false)
  const [needAck, setNeedAck] = useState<string[] | null>(null)
  const d: BulkDraft = draft ?? { text: '', gateId: '', open: true }
  const set = (patch: Partial<BulkDraft>) => { setDraft({ ...d, ...patch }); setPreview(null); setNeedAck(null); setConflict(null) }   // any edit makes the old preview stale

  const parse = async () => {
    setErr(null); setConflict(null); setNeedAck(null); setBusy(true)
    try { setPreview(await parseTasks(trainId, d.text, d.gateId || null)) } catch (e) { setErr(errMsg(e)) } finally { setBusy(false) }
  }
  const commit = async (ack: boolean) => {
    if (!preview) return
    setErr(null); setConflict(null); setBusy(true)
    try {
      await commitTasks(trainId, preview.previewId, ack)
      setDraft(undefined); setPreview(null); onChanged(); onClose()          // committed: the draft is done with
    } catch (e) {
      if (isConflict(e)) setConflict(e)
      else if (e instanceof ApiError && e.body?.guard === 'DecertifyNotAcknowledged') setNeedAck(preview.decertifiesGates)
      else setErr(errMsg(e))
    } finally { setBusy(false) }
  }

  return (
    <>
      <div className="section-head"><span className="cap">Drawer · paste tasks</span>
        <span><button type="button" className="text quiet" onClick={() => { setDraft({ ...d, open: false }); onClose() }}>Close</button></span></div>
      <h2>Paste tasks</h2>
      <p className="muted">One task per line. The text you type here is kept until you commit or discard it.</p>
      <pre className="grammar">{GRAMMAR}</pre>
      <p className="inline-form"><label>Lines before any # go to <select className="line" value={d.gateId} onChange={e => set({ gateId: e.target.value })}>
        <option value="">(no default: start with a # line)</option>{gates.map(g => <option key={g.id} value={g.id}>{g.name}</option>)}</select></label></p>
      <p><label className="cap" htmlFor="bulk-text">Tasks</label>
        <textarea id="bulk-text" className="bulk-text" rows={12} spellCheck={false} value={d.text} onChange={e => set({ text: e.target.value })} placeholder={'# CAB Approval\n- CAB minutes attached @marcus [Payments API]'} /></p>
      <p className="actions-col">
        <button type="button" className="text" disabled={busy || d.text.trim() === ''} onClick={parse}>Parse</button>
        <button type="button" className="text quiet" onClick={() => { setDraft(undefined); setPreview(null); setErr(null); onClose() }}>Discard</button>
      </p>
      {err && <p className="bad" role="alert">✗ {err}</p>}
      {conflict && <Conflict error={conflict} what="train">
        <button type="button" className="text" onClick={parse}>Parse again</button></Conflict>}

      {preview && (
        <section aria-label="Preview">
          <p role="status"><strong>{plural(preview.tasks.length, 'task')}</strong> ready · <span className={preview.warningCount ? 'warn' : 'muted'}>{plural(preview.warningCount, 'warning')}</span> · <span className={preview.errorCount ? 'bad' : 'muted'}>{plural(preview.errorCount, 'error')}</span></p>
          {preview.issues.length > 0 && (<>
            <p className="cap">Fix these first</p>
            <ul className="plain">{preview.issues.map((i, k) => (
              <li key={k} className="bad">✗ <span className="mono">line {i.line}</span> {i.message}{i.suggestions.length > 0 && <span className="muted"> Did you mean: {i.suggestions.join(', ')}?</span>}</li>))}</ul></>)}
          {preview.tasks.length > 0 && (<>
            <p className="cap">Will be added on commit</p>
            <ul className="plain">{preview.tasks.map(t => (
              <li key={t.line}><span className="accent">+</span> {t.description} <span className="muted">· {t.gateName} · {t.ownerName ?? '—'}{t.productName ? ` · ${t.productName}` : ''}</span>
                {t.warnings.map((w, k) => <div key={k} className="warn">▲ {w}</div>)}</li>))}</ul></>)}
          {preview.decertifiesGates.length > 0 && preview.errorCount === 0 && <p className="warn">▲ Committing decertifies: {preview.decertifiesGates.join(', ')}.</p>}
          {needAck && <p className="warn" role="alert">▲ This decertifies {needAck.join(', ')}. Its certification is removed and the gate goes back to In progress.</p>}
          <p className="actions-col">
            {needAck || preview.decertifiesGates.length > 0
              ? <button type="button" className="text" disabled={busy || preview.errorCount > 0 || preview.tasks.length === 0} onClick={() => commit(true)}>Commit and decertify</button>
              : <button type="button" className="text" disabled={busy || preview.errorCount > 0 || preview.tasks.length === 0} onClick={() => commit(false)}>Commit {plural(preview.tasks.length, 'task')}</button>}
          </p>
          {preview.errorCount > 0 && <p className="muted">Commit is off while there are errors: nothing is added until every line is valid.</p>}
        </section>)}
    </>
  )
}
