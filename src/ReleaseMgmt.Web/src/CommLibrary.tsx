import { useCallback, useEffect, useRef, useState } from 'react'
import {
  ApiError, COMM_AUDIENCES, copyToTrain, createLibraryTemplate, getCommLibrary, getCommTokens, getStream, getTrain, getTrainMessages, previewComm,
  updateLibraryTemplate, updateTrainMessage,
  type CommPreview, type CommTarget, type CommToken, type LibraryTemplate, type Me, type StreamRow, type TrainMessage,
} from './api'
import { CommSchedule } from './CommSchedule'
import { scan } from './commTokens'
import { Conflict, isConflict } from './Conflict'
import { fmtHM } from './time'

// REOS-43/44: the comm template library, full width (UI.md: "as Comms drawer, full width"; layout and copy from mockups/Comms.html).
// Library entries are shared source text; a train gets its own editable copy that locks once anything was sent from it.
// Status is words, glyphs and coloured text; actions are text buttons; the selected row is tinted; nothing is a box, pill or modal.

interface Draft { name: string; templateType: string; audience: string; subjectLine: string; markdownBody: string }
type Sel = { kind: 'library'; id: string } | { kind: 'new' } | { kind: 'train'; id: string } | null

const blank = (): Draft => ({ name: '', templateType: '', audience: 'All', subjectLine: '', markdownBody: '' })
const TARGETS: { value: CommTarget; label: string }[] = [
  { value: 'PlainText', label: 'Plain text (mailto)' },
  { value: 'Markdown', label: 'Markdown' },
  { value: 'Html', label: 'Rich text (HTML)' },
  { value: 'JsonString', label: 'Webhook (JSON string)' },
]

/** The template source with allowlisted tokens in accent text and problems in the error colour, underlined so colour is never the only cue. */
function TokenCheck({ text, allow, label }: { text: string; allow: string[]; label: string }) {
  const s = scan(text, allow)
  return (
    <div>
      <p className="token-summary">
        <span className="cap">{label}</span>{' '}
        {s.problems.length === 0
          ? <span className="ok small">{s.known} {s.known === 1 ? 'token' : 'tokens'} · all known ✓</span>
          : <span className="bad small">✗ {s.problems.length} {s.problems.length === 1 ? 'problem' : 'problems'}: this cannot be sent until fixed</span>}
      </p>
      <pre className="src" role="group" aria-label={`${label}, tokens marked`}>
        {s.segs.map((g, i) => g.kind === 'token' ? <span key={i} className="tk">{g.text}</span> : g.kind === 'bad' ? <span key={i} className="tkbad">{g.text}</span> : g.text)}
        {text === '' && <span className="muted">Nothing yet.</span>}
      </pre>
      {s.problems.length > 0 && <ul className="plain small">{s.problems.map((p, i) => <li key={i} className="bad">✗ {p.message}</li>)}</ul>}
    </div>
  )
}

function Preview({ trainId, draft, onAsk }: { trainId: string; draft: Draft; onAsk?: () => void }) {
  const [target, setTarget] = useState<CommTarget>('PlainText')
  const [p, setP] = useState<CommPreview | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [moved, setMoved] = useState<number | null>(null)
  const [tick, setTick] = useState(0)

  useEffect(() => {
    if (!trainId) { setP(null); return }
    let live = true
    const t = window.setTimeout(() => {
      previewComm(trainId, { subject: draft.subjectLine, text: draft.markdownBody, target })
        .then(r => { if (live) { setP(r); setProblem(null); setMoved(null) } })
        .catch(e => { if (live) setProblem(e instanceof ApiError ? (e.body?.message ?? e.message) : (e as Error).message) })
    }, 350)
    return () => { live = false; window.clearTimeout(t) }
  }, [trainId, draft.subjectLine, draft.markdownBody, target, tick])

  // The train can move while this screen is open: compare its version with the one the preview was built from.
  useEffect(() => {
    if (!trainId || !p) return
    const h = window.setInterval(() => {
      getTrain(trainId).then(t => setMoved(t.version !== p.trainVersion ? t.version : null)).catch(() => undefined)
    }, 20000)
    return () => window.clearInterval(h)
  }, [trainId, p])

  return (
    <div>
      <p className="token-summary">
        <span className="cap">Preview</span>{' '}
        {p && <span className="muted small">data as of {fmtHM(p.asOf)} · train v{p.trainVersion}</span>}{' '}
        <button type="button" className="text" onClick={() => { setTick(n => n + 1); onAsk?.() }}>Refresh</button>
      </p>
      <p className="inline-form small">
        <label>Show as{' '}
          <select className="line" value={target} onChange={e => setTarget(e.target.value as CommTarget)}>
            {TARGETS.map(t => <option key={t.value} value={t.value}>{t.label}</option>)}
          </select>
        </label>
      </p>
      {!trainId && <p className="muted">Choose a train above to preview this message with its live data.</p>}
      {problem && <p className="bad" role="alert">✗ {problem}</p>}
      {moved !== null && p && (
        <p className="warn small" role="status">▲ The train changed since this preview (v{p.trainVersion}, now v{moved}). <button type="button" className="text" onClick={() => setTick(n => n + 1)}>Refresh the preview</button></p>
      )}
      {p && (
        <>
          {p.tokenErrors.length > 0 && <p className="bad small">✗ Cannot be sent: {p.tokenErrors.length} template {p.tokenErrors.length === 1 ? 'problem' : 'problems'} (see the source on the left).</p>}
          {p.subject !== null && <p className="preview-subject"><span className="muted small">Subject</span><br /><strong>{p.subject || <span className="muted">Empty</span>}</strong></p>}
          <pre className={target === 'PlainText' ? 'preview' : 'preview src'} role="group" aria-label="Hydrated message">{p.text || 'Empty'}</pre>
        </>
      )}
    </div>
  )
}

function Tokens({ tokens, onInsert, canInsert }: { tokens: CommToken[]; onInsert: (t: string) => void; canInsert: boolean }) {
  const [open, setOpen] = useState(false)
  return (
    <div>
      <p><button type="button" className="text" aria-expanded={open} onClick={() => setOpen(o => !o)}>{open ? 'Hide tokens' : 'Show the tokens you can use'}</button></p>
      {open && (
        <table className="grid">
          <thead><tr><th>Token</th><th>What it prints</th><th><span className="sr-only">Insert</span></th></tr></thead>
          <tbody>
            {tokens.map(t => (
              <tr key={t.name}>
                <td className="mono tk">{`{${t.name}}`}</td><td>{t.description}</td>
                <td>{canInsert && <button type="button" className="text" aria-label={`Insert ${t.name}`} onClick={() => onInsert(`{${t.name}}`)}>Insert</button>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}

export function CommLibrary({ me }: { me: Me }) {
  const canEdit = me.roles.includes('RTE') || me.roles.includes('ReleaseManager')
  const [tokens, setTokens] = useState<CommToken[]>([])
  const [library, setLibrary] = useState<LibraryTemplate[] | null>(null)
  const [trains, setTrains] = useState<StreamRow[]>([])
  const [trainId, setTrainId] = useState('')
  const [messages, setMessages] = useState<TrainMessage[]>([])
  const [sel, setSel] = useState<Sel>(null)
  const [draft, setDraft] = useState<Draft>(blank())
  const [source, setSource] = useState<{ version: number; draft: Draft } | null>(null)
  const [override, setOverride] = useState<number | null>(null)
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [conflict, setConflict] = useState<ApiError | null>(null)
  const [scheduleKey, setScheduleKey] = useState(0)
  const body = useRef<HTMLTextAreaElement>(null)
  const caret = useRef<number | null>(null)

  const allow = tokens.map(t => t.name)
  const inTrain = sel?.kind === 'train' ? messages.find(m => m.id === sel.id) : undefined
  const locked = !!inTrain?.dispatched
  const editable = canEdit && !locked
  const dirty = !!source && JSON.stringify(source.draft) !== JSON.stringify(draft)

  const loadLibrary = useCallback(async () => {
    try { setLibrary(await getCommLibrary()) } catch (e) { setProblem((e as Error).message) }
  }, [])
  const loadMessages = useCallback(async (id: string) => {
    if (!id) { setMessages([]); return [] as TrainMessage[] }
    try { const m = await getTrainMessages(id); setMessages(m); return m } catch (e) { setProblem((e as Error).message); return [] as TrainMessage[] }
  }, [])

  useEffect(() => {
    void loadLibrary()
    getCommTokens().then(setTokens).catch(e => setProblem((e as Error).message))
    getStream().then(r => { setTrains(r); setTrainId(t => t || r[0]?.id || '') }).catch(() => setTrains([]))
  }, [loadLibrary])
  useEffect(() => { setMessages([]); if (sel?.kind === 'train') { setSel(null); setSource(null) } void loadMessages(trainId) }, [trainId, loadMessages]) // eslint-disable-line react-hooks/exhaustive-deps

  // put the caret back after a token was inserted
  useEffect(() => { if (caret.current !== null && body.current) { body.current.focus(); body.current.setSelectionRange(caret.current, caret.current); caret.current = null } }, [draft.markdownBody])

  const choose = (s: Sel, d: Draft, version: number) => { setSel(s); setDraft(d); setSource({ version, draft: d }); setOverride(null); setProblem(null); setConflict(null) }
  const pickLibrary = (l: LibraryTemplate) => choose({ kind: 'library', id: l.id }, { name: l.name, templateType: l.templateType, audience: l.audience, subjectLine: l.subjectLine, markdownBody: l.markdownBody }, l.version)
  const pickMessage = (m: TrainMessage) => choose({ kind: 'train', id: m.id }, { name: m.libraryName ?? m.templateType, templateType: m.templateType, audience: m.audience, subjectLine: m.subjectLine, markdownBody: m.markdownBody }, m.version)
  const startNew = () => choose({ kind: 'new' }, blank(), 0)

  const fail = (e: unknown) => {
    if (isConflict(e)) { setConflict(e); setProblem(null) }
    else { setConflict(null); setProblem(e instanceof ApiError ? (e.body?.message ?? e.message) : (e as Error).message) }
  }
  const run = async (f: () => Promise<void>) => { setBusy(true); setProblem(null); setConflict(null); try { await f() } catch (e) { fail(e) } finally { setBusy(false) } }

  const save = () => run(async () => {
    if (!sel || !source) return
    const version = override ?? source.version
    if (sel.kind === 'new') {
      const l = await createLibraryTemplate(draft)
      await loadLibrary(); pickLibrary(l)
    } else if (sel.kind === 'library') {
      const l = await updateLibraryTemplate(sel.id, draft, version)
      await loadLibrary(); pickLibrary(l)
    } else {
      const m = await updateTrainMessage(sel.id, { audience: draft.audience, subjectLine: draft.subjectLine, markdownBody: draft.markdownBody }, version)
      await loadMessages(trainId); pickMessage(m)
    }
  })

  const addToTrain = () => run(async () => {
    if (sel?.kind !== 'library' || !trainId) return
    const m = await copyToTrain(trainId, sel.id)
    await Promise.all([loadMessages(trainId), loadLibrary()]); pickMessage(m)
  })

  const reload = async () => {
    setConflict(null)
    if (sel?.kind === 'library') { const l = (await getCommLibrary()).find(x => x.id === sel.id); setLibrary(await getCommLibrary()); if (l) pickLibrary(l) }
    else if (sel?.kind === 'train') { const m = (await loadMessages(trainId)).find(x => x.id === sel.id); if (m) pickMessage(m) }
  }
  const keepMine = () => {
    const cur = conflict?.body?.current as { version?: number } | undefined
    if (cur?.version !== undefined) setOverride(cur.version)
    setConflict(null)
  }

  const insert = (t: string) => {
    const el = body.current
    const s = el?.selectionStart ?? draft.markdownBody.length, e = el?.selectionEnd ?? s
    caret.current = s + t.length
    setDraft(d => ({ ...d, markdownBody: d.markdownBody.slice(0, s) + t + d.markdownBody.slice(e) }))
  }

  const valid = draft.subjectLine.trim() !== '' && draft.markdownBody.trim() !== '' && (sel?.kind === 'train' || (draft.name.trim() !== '' && draft.templateType.trim() !== ''))
  const set = (k: keyof Draft, v: string) => setDraft(d => ({ ...d, [k]: v }))
  const trainTitle = trains.find(t => t.id === trainId)?.title
  const tokenState = (ok: boolean, n: number) => ok ? <span className="ok">✓ all known</span> : <span className="bad">✗ {n} {n === 1 ? 'problem' : 'problems'}</span>

  return (
    <section>
      <h1>Comm library</h1>
      <p className="muted">Message templates for stakeholder updates. A train gets its own copy to tailor; tokens such as {'{ReleaseTitle}'} are filled from the train&apos;s live data. An unknown token blocks sending.</p>
      {problem && <p className="bad" role="alert">✗ {problem}</p>}

      <p className="inline-form">
        <label>Preview and schedule for train{' '}
          <select className="line" value={trainId} onChange={e => setTrainId(e.target.value)} disabled={trains.length === 0}>
            {trains.length === 0 && <option value="">No trains</option>}
            {trains.map(t => <option key={t.id} value={t.id}>{t.title} ({t.status})</option>)}
          </select>
        </label>
      </p>

      <div className="section-head">
        <h2 className="cap">Library templates</h2>
        {canEdit && <button type="button" className="text" onClick={startNew}>New template</button>}
      </div>
      <table className="grid">
        <thead><tr><th>Name</th><th>Type</th><th>Audience</th><th>Subject</th><th>Tokens</th><th className="n">In trains</th><th className="n">Version</th></tr></thead>
        <tbody>
          {library?.map(l => {
            const on = sel?.kind === 'library' && sel.id === l.id
            return (
              <tr key={l.id} className={on ? 'selected' : undefined} aria-selected={on} tabIndex={0} onClick={() => pickLibrary(l)} onKeyDown={e => { if (e.key === 'Enter') pickLibrary(l) }}>
                <td>{l.name}</td><td className="mono">{l.templateType}</td><td>{l.audience}</td><td>{l.subjectLine}</td>
                <td>{tokenState(l.valid, l.tokenErrors.length)}</td><td className="n">{l.trainCopies}</td><td className="n">{l.version}</td>
              </tr>
            )
          })}
        </tbody>
      </table>
      {library?.length === 0 && <p className="muted">The library is empty. {canEdit ? 'Create the first template.' : 'An RTE or Release Manager can add templates.'}</p>}

      {sel && source && (
        <>
          <div className="section-head">
            <h2 className="cap">
              {sel.kind === 'new' ? 'New library template' : sel.kind === 'library' ? `Edit ${source.draft.name}` : `${trainTitle ?? 'Train'} · ${inTrain?.libraryName ?? inTrain?.templateType ?? 'message'}`}
            </h2>
            <span className="muted small">{sel.kind === 'train' ? 'This train\'s own copy' : sel.kind === 'library' ? `Version ${source.version}` : ''}</span>
          </div>
          {locked && <p className="warn" role="status">▲ Sent {inTrain!.dispatchCount} {inTrain!.dispatchCount === 1 ? 'time' : 'times'}, so this copy is read only. Copy the library template into the train again for a new message.</p>}
          {!canEdit && <p className="muted">Only an RTE or Release Manager edits templates.</p>}
          {conflict && (
            <Conflict error={conflict} what={sel.kind === 'train' ? 'message' : 'template'}>
              <button type="button" className="text" onClick={() => void reload()}>Reload theirs</button>
              <button type="button" className="text" onClick={keepMine}>Keep mine and save over it</button>
            </Conflict>
          )}
          <div className="comm-cols">
            <div>
              <div className="inline-form stack">
                {sel.kind !== 'train' && (
                  <label>Name <input className="line wide" value={draft.name} readOnly={!editable} onChange={e => set('name', e.target.value)} /></label>
                )}
                {sel.kind !== 'train' && (
                  <label>Type <input className="line" list="comm-types" value={draft.templateType} readOnly={!editable} onChange={e => set('templateType', e.target.value)} />
                    <datalist id="comm-types"><option value="Tminus7" /><option value="Tminus3" /><option value="GoNoGo" /><option value="Cutover" /><option value="Complete" /><option value="HypercareExit" /></datalist>
                  </label>
                )}
                <label>Audience{' '}
                  <select className="line" value={draft.audience} disabled={!editable} onChange={e => set('audience', e.target.value)}>
                    {COMM_AUDIENCES.map(a => <option key={a}>{a}</option>)}
                  </select>
                </label>
                <label>Subject <input className="line wide" value={draft.subjectLine} readOnly={!editable} onChange={e => set('subjectLine', e.target.value)} /></label>
                <label className="block">Body (Markdown, use ** for bold)
                  <textarea ref={body} className="bulk-text" rows={16} value={draft.markdownBody} readOnly={!editable} onChange={e => set('markdownBody', e.target.value)} />
                </label>
              </div>
              <TokenCheck text={`Subject: ${draft.subjectLine}\n${draft.markdownBody}`} allow={allow} label="Template" />
              <Tokens tokens={tokens} onInsert={insert} canInsert={editable} />
              <p>
                {editable && <button type="button" className="text strong" disabled={busy || !valid || (!dirty && sel.kind !== 'new')} onClick={save}>{sel.kind === 'new' ? 'Create template' : 'Save changes'}</button>}{' '}
                {editable && dirty && sel.kind !== 'new' && <button type="button" className="text" onClick={() => { setDraft(source.draft); setConflict(null); setProblem(null) }}>Discard changes</button>}{' '}
                {sel.kind === 'library' && canEdit && trainId && <button type="button" className="text" disabled={busy || dirty} title={dirty ? 'Save your changes first' : undefined} onClick={addToTrain}>Add to {trainTitle ?? 'the train'}</button>}{' '}
                <button type="button" className="text" onClick={() => { setSel(null); setSource(null); setConflict(null); setProblem(null) }}>Close</button>
              </p>
              {sel.kind === 'library' && dirty && <p className="muted small">Add to train copies the saved version, not your unsaved changes.</p>}
            </div>
            <Preview trainId={trainId} draft={draft} />
          </div>
        </>
      )}

      {trainId && (
        <>
          <div className="section-head"><h2 className="cap">Messages in {trainTitle ?? 'this train'}</h2></div>
          <table className="grid">
            <thead><tr><th>Message</th><th>Audience</th><th>Subject</th><th>Tokens</th><th>State</th></tr></thead>
            <tbody>
              {messages.map(m => {
                const on = sel?.kind === 'train' && sel.id === m.id
                return (
                  <tr key={m.id} className={on ? 'selected' : undefined} aria-selected={on} tabIndex={0} onClick={() => pickMessage(m)} onKeyDown={e => { if (e.key === 'Enter') pickMessage(m) }}>
                    <td>{m.libraryName ?? m.templateType}</td><td>{m.audience}</td><td>{m.subjectLine}</td><td>{tokenState(m.valid, m.tokenErrors.length)}</td>
                    <td>{m.dispatched ? <span className="muted">● sent {m.dispatchCount}×, read only</span> : <span>○ editable</span>}</td>
                  </tr>
                )
              })}
            </tbody>
          </table>
          {messages.length === 0 && <p className="muted">No messages in this train yet. Select a library template and choose Add to train, or create the schedule below from a train template.</p>}

          <div className="section-head"><h2 className="cap">T-minus schedule · {trainTitle}</h2></div>
          <CommSchedule trainId={trainId} me={me} refreshKey={scheduleKey} onChanged={() => { setScheduleKey(k => k + 1); void loadMessages(trainId); void loadLibrary() }} />
        </>
      )}
    </section>
  )
}
