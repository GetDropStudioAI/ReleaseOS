import { useCallback, useEffect, useRef, useState } from 'react'
import {
  ApiError, COMM_AUDIENCES, copyToTrain, createLibraryTemplate, getCommLibrary, getCommTokens, getStream, getTrain, getTrainMessages, previewComm,
  updateLibraryTemplate, updateTrainMessage,
  type CommPreview, type CommTarget, type CommToken, type LibraryTemplate, type Me, type StreamRow, type TrainMessage,
} from './api'
import { announce } from './announce'
import { CommSchedule } from './CommSchedule'
import { scan } from './commTokens'
import { isConflict } from './Conflict'
import { useFocusWhen } from './focus'
import { Button, ConflictNotice, DataTable, ErrorState, FilterSelect, LoadingState, PageHeading, Panel, StatusChip, TwoLine, type Column } from './ui'
import { Field, Problem, SelectInput, TextInput } from './mgmt/forms'

// REOS-43/44: the comm template library, full width (UI.md: "as Comms drawer, full width"; layout and copy from mockups/Comms.html).
// Library entries are shared source text; a train gets its own editable copy that locks once anything was sent from it.
// REOS-129 (G7): restyled as tables and panels (D41). Status is a chip or words, never colour alone; the token problems are underlined as well as coloured.

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
  const summary = s.problems.length === 0 ? `${s.known} ${s.known === 1 ? 'token' : 'tokens'} · all known ✓` : `✗ ${s.problems.length} ${s.problems.length === 1 ? 'problem' : 'problems'}: this cannot be sent until fixed`
  // the count changes while typing: speak it once typing pauses, and only when it changed (WCAG 4.1.3); the first render is not announced
  const said = useRef(summary)
  useEffect(() => {
    if (said.current === summary) return
    const t = window.setTimeout(() => { said.current = summary; announce(`${label}: ${summary.replace(/ ?[✓✗] ?/g, ' ').trim()}`) }, 900)
    return () => window.clearTimeout(t)
  }, [summary, label])
  return (
    <div className="mg-stack">
      <div className="mg-summary token-summary">
        <h3 className="mg-subhead">{label}</h3>
        <span className={s.problems.length === 0 ? 'mg-ok' : 'mg-problem'}>{summary}</span>
      </div>
      <pre className="mg-src" role="group" aria-label={`${label}, tokens marked`}>
        {s.segs.map((g, i) => g.kind === 'token' ? <span key={i} className="mg-tk tk">{g.text}</span> : g.kind === 'bad' ? <span key={i} className="mg-tkbad tkbad">{g.text}</span> : g.text)}
        {text === '' && <span className="os-muted">Nothing yet.</span>}
      </pre>
      {s.problems.length > 0 && <ul className="mg-list">{s.problems.map((p, i) => <li key={i} className="mg-problem">✗ {p.message}</li>)}</ul>}
    </div>
  )
}

function Preview({ trainId, draft, onAsk }: { trainId: string; draft: Draft; onAsk?: () => void }) {
  const [target, setTarget] = useState<CommTarget>('PlainText')
  const [p, setP] = useState<CommPreview | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [moved, setMoved] = useState<number | null>(null)
  useEffect(() => { if (moved !== null) announce(`The train changed since this preview: now version ${moved}. Refresh the preview.`) }, [moved])
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
    <div className="mg-stack">
      <div className="mg-summary token-summary">
        <h3 className="mg-subhead">Preview</h3>
        {p && <span className="mg-note">data as of {p.asOf.slice(11, 16)} UTC · train v{p.trainVersion}</span>}
      </div>
      <div className="mg-toolbar">
        <FilterSelect label="Show as" value={target} onChange={v => setTarget(v as CommTarget)} options={TARGETS.map(t => ({ value: t.value, label: t.label }))} />
        <Button onClick={() => { setTick(n => n + 1); onAsk?.() }}>Refresh</Button>
      </div>
      {!trainId && <p className="mg-note">Choose a train above to preview this message with its live data.</p>}
      <Problem message={problem} />
      {moved !== null && p && (
        <p className="mg-problem">▲ The train changed since this preview (v{p.trainVersion}, now v{moved}). <Button onClick={() => setTick(n => n + 1)}>Refresh the preview</Button></p>
      )}
      {p && (
        <>
          {p.tokenErrors.length > 0 && <p className="mg-problem">✗ Cannot be sent: {p.tokenErrors.length} template {p.tokenErrors.length === 1 ? 'problem' : 'problems'} (see the source on the left).</p>}
          {p.subject !== null && <p className="preview-subject"><span className="mg-note">Subject</span><span className="sr-only">:</span><br /><strong>{p.subject || <span className="os-muted">Empty</span>}</strong></p>}
          <pre className={target === 'PlainText' ? 'mg-preview' : 'mg-preview src'} role="group" aria-label="Hydrated message">{p.text || 'Empty'}</pre>
        </>
      )}
    </div>
  )
}

function Tokens({ tokens, onInsert, canInsert }: { tokens: CommToken[]; onInsert: (t: string) => void; canInsert: boolean }) {
  const [open, setOpen] = useState(false)
  const columns: Column<CommToken>[] = [
    { key: 'token', header: 'Token', cell: t => <span className="mg-tk tk">{`{${t.name}}`}</span>, mono: true },
    { key: 'what', header: 'What it prints', cell: t => t.description },
    { key: 'ins', header: <span className="sr-only">Insert</span>, align: 'end', cell: t => (canInsert ? <Button aria-label={`Insert ${t.name}`} onClick={() => onInsert(`{${t.name}}`)}>Insert</Button> : null) },
  ]
  return (
    <div className="mg-stack">
      <div className="mg-actions"><Button aria-expanded={open} aria-controls={open ? 'comm-token-list' : undefined} onClick={() => setOpen(o => !o)}>{open ? 'Hide tokens' : 'Show the tokens you can use'}</Button></div>
      {open && <div id="comm-token-list"><DataTable caption="Tokens you can use" columns={columns} rows={tokens} rowKey={t => t.name} /></div>}
    </div>
  )
}

/** `trainId`: the train the user is working on, when the caller knows it; preview and schedule default to it instead of the first train. */
export function CommLibrary({ me, trainId: current }: { me: Me; trainId?: string | null }) {
  const canEdit = me.roles.includes('RTE') || me.roles.includes('ReleaseManager')
  const [tokens, setTokens] = useState<CommToken[]>([])
  const [library, setLibrary] = useState<LibraryTemplate[] | null>(null)
  const [libraryError, setLibraryError] = useState<string | null>(null)
  const [trains, setTrains] = useState<StreamRow[]>([])
  const [trainId, setTrainId] = useState('')
  const [messages, setMessages] = useState<TrainMessage[]>([])
  const [sel, setSel] = useState<Sel>(null)
  const [draft, setDraft] = useState<Draft>(blank())
  const [source, setSource] = useState<{ version: number; draft: Draft } | null>(null)
  const [override, setOverride] = useState<number | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [conflict, setConflict] = useState<ApiError | null>(null)
  const [scheduleKey, setScheduleKey] = useState(0)
  const body = useRef<HTMLTextAreaElement>(null)
  const caret = useRef<number | null>(null)
  const nameRef = useFocusWhen<HTMLInputElement>(sel?.kind === 'new')

  const allow = tokens.map(t => t.name)
  const inTrain = sel?.kind === 'train' ? messages.find(m => m.id === sel.id) : undefined
  const locked = !!inTrain?.dispatched
  const editable = canEdit && !locked
  const dirty = !!source && JSON.stringify(source.draft) !== JSON.stringify(draft)

  const loadLibrary = useCallback(async () => {
    try { setLibrary(await getCommLibrary()); setLibraryError(null) } catch (e) { setLibraryError((e as Error).message) }
  }, [])
  const loadMessages = useCallback(async (id: string) => {
    if (!id) { setMessages([]); return [] as TrainMessage[] }
    try { const m = await getTrainMessages(id); setMessages(m); return m } catch (e) { setProblem((e as Error).message); return [] as TrainMessage[] }
  }, [])

  // Retry asks again for everything the page read at start (library, tokens, the train's messages), so the error from any of them goes away with it (REOS-142).
  const retryAll = useCallback(() => {
    setProblem(null)
    void loadLibrary()
    getCommTokens().then(setTokens).catch(e => setProblem((e as Error).message))
  }, [loadLibrary])
  useEffect(() => {
    retryAll()
    getStream().then(r => { setTrains(r); setTrainId(t => t || (current && r.some(x => x.id === current) ? current : r[0]?.id) || '') }).catch(() => setTrains([]))
  }, [retryAll]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { setMessages([]); if (sel?.kind === 'train') { setSel(null); setSource(null) } void loadMessages(trainId) }, [trainId, loadMessages]) // eslint-disable-line react-hooks/exhaustive-deps

  // put the caret back after a token was inserted
  useEffect(() => { if (caret.current !== null && body.current) { body.current.focus(); body.current.setSelectionRange(caret.current, caret.current); caret.current = null } }, [draft.markdownBody])

  // the control that opened the editor (a row's name or New template), so Close can give focus back to it (WCAG 2.4.3)
  const opener = useRef<HTMLElement | null>(null)
  const choose = (s: Sel, d: Draft, version: number) => {
    const a = document.activeElement as HTMLElement | null
    if (a && a !== document.body && !document.getElementById('comm-editor')?.contains(a)) opener.current = a
    setSel(s); setDraft(d); setSource({ version, draft: d }); setOverride(null); setProblem(null); setConflict(null)
  }
  const close = () => { setSel(null); setSource(null); setConflict(null); setProblem(null); requestAnimationFrame(() => { if (opener.current?.isConnected) opener.current.focus() }) }
  const pickLibrary = (l: LibraryTemplate) => choose({ kind: 'library', id: l.id }, { name: l.name, templateType: l.templateType, audience: l.audience, subjectLine: l.subjectLine, markdownBody: l.markdownBody }, l.version)
  const pickMessage = (m: TrainMessage) => choose({ kind: 'train', id: m.id }, { name: m.libraryName ?? m.templateType, templateType: m.templateType, audience: m.audience, subjectLine: m.subjectLine, markdownBody: m.markdownBody }, m.version)
  const startNew = () => choose({ kind: 'new' }, blank(), 0)

  const fail = (e: unknown) => {
    if (isConflict(e)) { setConflict(e); setProblem(null) }
    else { setConflict(null); setProblem(e instanceof ApiError ? (e.body?.message ?? e.message) : (e as Error).message) }
  }
  const run = async (label: string, f: () => Promise<void>) => {
    if (busy) return
    setBusy(label); setProblem(null); setConflict(null)
    try { await f() } catch (e) { fail(e) } finally { setBusy(null) }
  }

  const save = () => run(sel?.kind === 'new' ? 'Creating…' : 'Saving…', async () => {
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
    announce(sel.kind === 'new' ? 'Template created.' : 'Changes saved.')
  })

  const addToTrain = () => run('Adding…', async () => {
    if (sel?.kind !== 'library' || !trainId) return
    const m = await copyToTrain(trainId, sel.id)
    await Promise.all([loadMessages(trainId), loadLibrary()]); pickMessage(m)
    announce(`Added to ${trainTitle ?? 'the train'}. Its own copy is open below.`)
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
  const tokenState = (ok: boolean, n: number) => <StatusChip tone={ok ? 'success' : 'danger'}>{ok ? 'All known' : `${n} ${n === 1 ? 'problem' : 'problems'}`}</StatusChip>

  const columnsLib: Column<LibraryTemplate>[] = [
    { key: 'name', header: 'Name', cell: l => <TwoLine primary={l.name} secondary={l.templateType} /> },
    { key: 'aud', header: 'Audience', cell: l => l.audience },
    { key: 'subject', header: 'Subject', cell: l => l.subjectLine },
    { key: 'tokens', header: 'Tokens', cell: l => tokenState(l.valid, l.tokenErrors.length) },
    { key: 'trains', header: 'In trains', align: 'end', mono: true, cell: l => l.trainCopies },
    { key: 'version', header: 'Version', align: 'end', mono: true, cell: l => l.version },
  ]
  const columnsMsg: Column<TrainMessage>[] = [
    { key: 'name', header: 'Message', cell: m => m.libraryName ?? m.templateType },
    { key: 'aud', header: 'Audience', cell: m => m.audience },
    { key: 'subject', header: 'Subject', cell: m => m.subjectLine },
    { key: 'tokens', header: 'Tokens', cell: m => tokenState(m.valid, m.tokenErrors.length) },
    { key: 'state', header: 'State', cell: m => (m.dispatched ? <StatusChip tone="neutral">{`Sent ${m.dispatchCount}×, read only`}</StatusChip> : <StatusChip tone="info">Editable</StatusChip>) },
  ]
  const editorTitle = !sel ? '' : sel.kind === 'new' ? 'New library template' : sel.kind === 'library' ? `Edit ${source?.draft.name ?? ''}` : `${trainTitle ?? 'Train'} · ${inTrain?.libraryName ?? inTrain?.templateType ?? 'message'}`

  return (
    <div className="os-page">
      <PageHeading trail={['Management', 'Comm library']} title="Comm library"
        description="Message templates for stakeholder updates. A train gets its own copy to tailor; tokens such as {ReleaseTitle} are filled from the train's live data. An unknown token blocks sending."
        actions={canEdit ? <Button variant="primary" onClick={startNew}>New template</Button> : undefined} />
      <Problem message={problem} />

      <div className="mg-toolbar">
        <FilterSelect label="Preview and schedule for train" value={trainId} onChange={setTrainId}
          options={trains.length === 0 ? [{ value: '', label: 'No trains' }] : trains.map(t => ({ value: t.id, label: `${t.title} (${t.status})` }))} />
      </div>

      <Panel title="Library templates" subtitle="Shared source text; each train gets its own editable copy" meta={library ? `${library.length} ${library.length === 1 ? 'template' : 'templates'}` : undefined}>
        {library === null ? (libraryError ? <ErrorState what="The library" message={libraryError} onRetry={() => { retryAll(); void loadMessages(trainId) }} /> : <LoadingState what="the library" />) : (
          <DataTable caption="Library templates" columns={columnsLib} rows={library ?? []} rowKey={l => l.id} selectedKey={sel?.kind === 'library' ? sel.id : null} onSelect={pickLibrary}
            empty={`The library is empty. ${canEdit ? 'Create the first template.' : 'An RTE or Release Manager can add templates.'}`} />
        )}
      </Panel>

      {sel && source && (
        <div id="comm-editor">
          <Panel title={editorTitle} subtitle={sel.kind === 'train' ? "This train's own copy" : sel.kind === 'library' ? `Version ${source.version}` : undefined}>
            <div className="mg-stack">
              {locked && <p className="mg-problem">▲ Sent {inTrain!.dispatchCount} {inTrain!.dispatchCount === 1 ? 'time' : 'times'}, so this copy is read only. Copy the library template into the train again for a new message.</p>}
              {!canEdit && <p className="mg-note">Only an RTE or Release Manager edits templates.</p>}
              {conflict && <ConflictNotice onUseTheirs={() => void reload()} onOverwrite={keepMine} />}
              <div className="mg-cols comm-cols">
                <div className="mg-stack">
                  <div className="mg-form">
                    {sel.kind !== 'train' && <Field label="Name"><TextInput ref={nameRef} value={draft.name} readOnly={!editable} onChange={e => set('name', e.target.value)} /></Field>}
                    {sel.kind !== 'train' && (
                      <Field label="Type">
                        <TextInput list="comm-types" value={draft.templateType} readOnly={!editable} onChange={e => set('templateType', e.target.value)} />
                        <datalist id="comm-types"><option value="Tminus7" /><option value="Tminus3" /><option value="GoNoGo" /><option value="Cutover" /><option value="Complete" /><option value="HypercareExit" /></datalist>
                      </Field>
                    )}
                    <Field label="Audience"><SelectInput value={draft.audience} disabled={!editable} onChange={e => set('audience', e.target.value)} options={COMM_AUDIENCES.map(a => ({ value: a, label: a }))} /></Field>
                    <Field label="Subject"><TextInput value={draft.subjectLine} readOnly={!editable} onChange={e => set('subjectLine', e.target.value)} /></Field>
                    <Field label="Body (Markdown, use ** for bold)">
                      <textarea ref={body} className="mg-input mg-body" rows={16} value={draft.markdownBody} readOnly={!editable} onChange={e => set('markdownBody', e.target.value)} />
                    </Field>
                  </div>
                  <TokenCheck text={`Subject: ${draft.subjectLine}\n${draft.markdownBody}`} allow={allow} label="Template" />
                  <Tokens tokens={tokens} onInsert={insert} canInsert={editable} />
                  <div className="mg-actions">
                    {editable && <Button variant="primary" disabled={!!busy || !valid || (!dirty && sel.kind !== 'new')} aria-describedby={!valid ? 'comm-why' : undefined} onClick={() => void save()}>{busy === 'Creating…' || busy === 'Saving…' ? busy : sel.kind === 'new' ? 'Create template' : 'Save changes'}</Button>}
                    {editable && dirty && sel.kind !== 'new' && <Button onClick={() => { setDraft(source.draft); setConflict(null); setProblem(null) }}>Discard changes</Button>}
                    {sel.kind === 'library' && canEdit && trainId && <Button disabled={!!busy || dirty} aria-describedby={dirty ? 'comm-add-why' : undefined} onClick={() => void addToTrain()}>{busy === 'Adding…' ? busy : `Add to ${trainTitle ?? 'the train'}`}</Button>}
                    <Button onClick={close}>Close</Button>
                  </div>
                  {editable && !valid && <p id="comm-why" className="mg-note">{sel.kind === 'train' ? 'Subject and body are required.' : 'Name, type, subject and body are required.'}</p>}
                  {sel.kind === 'library' && dirty && <p id="comm-add-why" className="mg-note">Save your changes first: Add to train copies the saved version, not your unsaved changes.</p>}
                </div>
                <Preview trainId={trainId} draft={draft} />
              </div>
            </div>
          </Panel>
        </div>
      )}

      {trainId && (
        <>
          <Panel title={`Messages in ${trainTitle ?? 'this train'}`} subtitle="The train's own copies of library templates" meta={`${messages.length}`}>
            <DataTable caption="Messages in this train" columns={columnsMsg} rows={messages} rowKey={m => m.id} selectedKey={sel?.kind === 'train' ? sel.id : null} onSelect={pickMessage}
              empty="No messages in this train yet. Select a library template and choose Add to train, or create the schedule below from a train template." />
          </Panel>
          <Panel title={`T-minus schedule · ${trainTitle ?? ''}`} subtitle="Messages due before and after the target date">
            <CommSchedule trainId={trainId} me={me} refreshKey={scheduleKey} onChanged={() => { setScheduleKey(k => k + 1); void loadMessages(trainId); void loadLibrary() }} />
          </Panel>
        </>
      )}
    </div>
  )
}
