import { Fragment, useCallback, useEffect, useId, useMemo, useRef, useState, type MouseEvent, type ReactNode } from 'react'
import { ApiError, get, getTemplates, post, seedCommSchedule, type TemplateRow } from './api'
import { announce } from './announce'
import { useFocusWhen, useRestoreFocus } from './focus'
import { errMsg } from './format'
import { useAction } from './useAction'
import { useDraft } from './session'
import { fmtDayTime, fmtHM, utcToZonedInput } from './time'

/** Session draft for the drawer (`comms:{trainId}`): App and the header's Communicate button read it to open and close the drawer. */
export interface CommsDraft { open: boolean; tab?: Tab; templateId?: string; scheduleItemId?: string; webhookId?: string }
export const commsKey = (trainId: string) => `comms:${trainId}`

type Tab = 'message' | 'schedule' | 'log'
type Target = 'PlainText' | 'Markdown' | 'Html' | 'JsonString'

// ---- API (REOS-45 dispatch endpoints; the preview is REOS-44's contract, typed exactly) -----------------------------------
export interface CommTemplate { id: string; templateType: string; audience: string; subjectLine: string; markdownBody: string; version: number }
export interface ScheduleItem { id: string; commTemplateId: string; templateType: string; audience: string; subjectLine: string; dueAt: string; sentAt: string | null; dispatchId: string | null; state: 'sent' | 'sentLate' | 'overdue' | 'ready' | 'scheduled'; version: number }
export interface WebhookTarget { id: string; name: string; kind: string; host: string }
export interface DispatchContext { asOf: string; trainVersion: number; trainTitle: string; targetReleaseDate: string; templates: CommTemplate[]; schedule: ScheduleItem[]; webhookTargets: WebhookTarget[] }
/** POST /api/v1/trains/{id}/comms:preview {templateId | text, target} -> */
export interface CommPreview { text: string; tokenErrors: string[]; asOf: string; trainVersion: number }
export interface Dispatched {
  id: string; trainId: string; templateId: string; channel: 'Copy' | 'Mailto' | 'Webhook'; format: 'RichText' | 'Markdown' | null; webhookName: string | null
  subject: string; body: string; bodySha256: string; dispatchedByName: string | null; dispatchedAt: string; isRehearsal: boolean
  outcome: 'Handed' | 'Delivered' | 'Failed'; failureReason: string | null; scheduleItemId: string | null; dueAt: string | null; sentAt: string | null; late: boolean | null
}
export interface DispatchLogRow { id: string; templateType: string; channel: string; webhookName: string | null; subject: string; dispatchedByName: string | null; dispatchedAt: string; isRehearsal: boolean; outcome: string; bodyLength: number }
export interface DispatchLogPage { items: DispatchLogRow[]; nextCursor: string | null; limit: number }
export interface DispatchBody { templateId?: string; scheduleItemId?: string; channel: 'Copy' | 'Mailto' | 'Webhook'; format?: 'RichText' | 'Markdown'; webhookDestinationId?: string; isRehearsal?: boolean }

export const getDispatchContext = (trainId: string) => get<DispatchContext>(`/api/v1/trains/${trainId}/comms/dispatch-context`)
export const previewComm = (trainId: string, src: { templateId: string } | { text: string }, target: Target) => post<CommPreview>(`/api/v1/trains/${trainId}/comms:preview`, { ...src, target })
export const dispatchComm = (trainId: string, body: DispatchBody, trainVersion: number) => post<Dispatched>(`/api/v1/trains/${trainId}/comms:dispatch`, body, trainVersion)
export const getDispatchLog = (trainId: string, cursor?: string) => get<DispatchLogPage>(`/api/v1/trains/${trainId}/comms/dispatches?limit=25${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`)
export const getDispatch = (id: string) => get<Dispatched>(`/api/v1/comms/dispatches/${id}`)

// ---- helpers ------------------------------------------------------------------------------------------------------------
const TOKEN = /(\{[A-Za-z0-9_]+\})/g
const tokensOf = (s: string) => [...new Set(s.match(TOKEN) ?? [])]
const dayOf = (iso: string) => utcToZonedInput(iso).slice(0, 10)
const daysBetween = (a: string, b: string) => Math.round((Date.parse(a + 'T00:00:00Z') - Date.parse(b + 'T00:00:00Z')) / 86_400_000)
const tMinusLabel = (target: string, dueIso: string) => { const d = daysBetween(target, dayOf(dueIso)); return d > 0 ? `T−${d}` : d === 0 ? 'T0' : `T+${-d}` }
const htmlToText = (html: string) => html.replace(/<br\s*\/?>/gi, '\n').replace(/<\/(p|div|li|h\d|tr)>/gi, '\n').replace(/<[^>]+>/g, '').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&amp;/g, '&')
const mailtoHref = (subject: string, body: string) => `mailto:?subject=${encodeURIComponent(subject)}&body=${encodeURIComponent(body).replace(/%0A/g, '%0D%0A')}`
const short = (s: string) => s.slice(0, 12)

/** Highlights {Tokens} in the template source; a token the preview reported as unknown is marked in red (glyph ✗ as well as colour). */
function Source({ text, errors }: { text: string; errors: string[] }) {
  return <>{text.split(TOKEN).map((p, i) => i % 2 === 1
    ? (errors.some(e => e.includes(p)) ? <span key={i} className="tk unknown">✗{p}</span> : <span key={i} className="tk">{p}</span>)
    : <Fragment key={i}>{p}</Fragment>)}</>
}

/** A safe, tiny Markdown view of the preview: **bold** and "- " bullets only, everything else literal text (never innerHTML). Runs of bullets are a real list. */
function PreviewText({ text }: { text: string }) {
  const BULLET = /^[-*•]\s+/
  const inline = (line: string) => line.replace(BULLET, '').split(/(\*\*[^*]+\*\*)/g).map((p, j) => /^\*\*[^*]+\*\*$/.test(p) ? <strong key={j}>{p.slice(2, -2)}</strong> : <Fragment key={j}>{p}</Fragment>)
  const out: ReactNode[] = []
  let list: string[] = []
  const flush = (k: number) => { if (list.length) out.push(<ul key={`l${k}`} className="plain" style={{ margin: 0 }}>{list.map((l, i) => <li key={i}><span aria-hidden="true">• </span>{inline(l)}</li>)}</ul>); list = [] }
  text.split('\n').forEach((line, i) => {
    if (BULLET.test(line)) { list.push(line); return }
    flush(i)
    out.push(<div key={i} className={line.trim() === '' ? 'gap' : undefined}>{inline(line)}</div>)
  })
  flush(-1)
  return <div className="preview-body">{out}</div>
}

const OUTCOME: Record<string, { g: string; cls: string; word: string }> = {
  Handed: { g: '✓', cls: 'ok', word: 'handed off' }, Delivered: { g: '✓', cls: 'ok', word: 'delivered' }, Failed: { g: '✗', cls: 'bad', word: 'failed' },
}

function ScheduleState({ s }: { s: ScheduleItem }) {
  const at = s.sentAt ? fmtHM(s.sentAt) : ''
  switch (s.state) {
    case 'sent': return <span className="ok">✓ sent {at}</span>
    case 'sentLate': return <span className="warn">✓ sent {fmtDayTime(s.sentAt!)} late</span>
    case 'overdue': return <span className="bad">▲ overdue</span>
    case 'ready': return <span className="accent">ready to send</span>
    default: return <span className="muted">scheduled</span>
  }
}

/** The empty schedule's next action (UX review #16): seed it here from a train template's T-minus plan, the same call the Comm library makes. */
function SeedSchedule({ trainId, canSeed, onSeeded }: { trainId: string; canSeed: boolean; onSeeded: () => void }) {
  const [templates, setTemplates] = useState<TemplateRow[] | null>(null)
  const [pick, setPick] = useState('')
  const [err, setErr] = useState<string | null>(null)
  const { run, pending } = useAction()
  useEffect(() => {
    if (!canSeed) return
    let live = true
    getTemplates().then(t => { if (!live) return; const usable = t.filter(x => x.status !== 'Retired' && x.scheduleCount > 0); setTemplates(usable); setPick(usable[0]?.id ?? '') })
      .catch(e => live && setErr(errMsg(e, 'Could not load the train templates.')))
    return () => { live = false }
  }, [canSeed])
  const seed = () => run('seed', async () => {
    setErr(null)
    try { await seedCommSchedule(trainId, pick); announce('Schedule created.'); onSeeded() } catch (e) { setErr(errMsg(e, 'Could not create the schedule.')) }
  })
  return (
    <>
      <p className="muted">No T-minus schedule for this train yet.{!canSeed && ' An RTE or Release Manager can create it from a train template.'}</p>
      {canSeed && templates?.length === 0 && <p className="muted small">No train template has a T-minus plan to seed from. Add messages to a template&apos;s T-minus plan first.</p>}
      {canSeed && !!templates?.length && (
        <p className="inline-form">
          <label>Seed from template{' '}
            <select className="line" value={pick} onChange={e => setPick(e.target.value)}>
              {templates.map(t => <option key={t.id} value={t.id}>{t.name} ({t.scheduleCount} messages)</option>)}
            </select>
          </label>{' '}
          <button type="button" className="text strong" disabled={!!pending || !pick} onClick={() => void seed()}>{pending ? 'Creating…' : 'Create schedule'}</button>
        </p>
      )}
      {err && <p className="bad" role="alert">✗ {err}</p>}
    </>
  )
}

function ScheduleTable({ ctx, selectedId, onPick, empty }: { ctx: DispatchContext; selectedId?: string; onPick: (s: ScheduleItem) => void; empty: ReactNode }) {
  if (ctx.schedule.length === 0) return <>{empty}</>
  return (
    <table className="grid comms-schedule">
      <thead><tr><th scope="col">When</th><th scope="col">Message</th><th scope="col">Due</th><th scope="col">State</th></tr></thead>
      <tbody>
        {ctx.schedule.map(s => (
          <tr key={s.id} className={s.id === selectedId ? 'selected' : undefined} aria-selected={s.id === selectedId}>
            <td className="mono">{tMinusLabel(ctx.targetReleaseDate, s.dueAt)}</td>
            <td><button type="button" className="text plainlink" onClick={() => onPick(s)}>{s.templateType} · {s.audience}</button></td>
            <td className="mono nowrap">{fmtDayTime(s.dueAt)}</td>
            <td className="nowrap"><ScheduleState s={s} /></td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}

function SentLog({ trainId, refreshKey }: { trainId: string; refreshKey: number }) {
  const [rows, setRows] = useState<DispatchLogRow[] | null>(null)
  const [next, setNext] = useState<string | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const [open, setOpen] = useState<string | null>(null)
  const [detail, setDetail] = useState<Dispatched | null>(null)
  useEffect(() => {
    let live = true
    setErr(null)
    getDispatchLog(trainId).then(p => { if (live) { setRows(p.items); setNext(p.nextCursor) } }).catch(e => live && setErr(errMsg(e, 'Could not load the sent log.')))
    return () => { live = false }
  }, [trainId, refreshKey])
  useEffect(() => {
    setDetail(null)
    if (!open) return
    let live = true
    getDispatch(open).then(d => live && setDetail(d)).catch(e => live && setErr(errMsg(e, 'Could not load that message.')))
    return () => { live = false }
  }, [open])
  const { run, pending } = useAction()
  const more = () => run('more', async () => {
    if (!next) return
    try { const p = await getDispatchLog(trainId, next); setRows(r => [...(r ?? []), ...p.items]); setNext(p.nextCursor) } catch (e) { setErr(errMsg(e, 'Could not load more.')) }
  })
  if (err) return <p className="bad" role="alert">✗ {err}</p>
  if (!rows) return <p className="muted">Loading…</p>
  if (rows.length === 0) return <p className="muted">Nothing has been sent for this train yet. Each send stores the exact text, channel and time here, and cannot be edited or deleted.</p>
  return (
    <>
      <p className="muted small">Immutable: each row is the exact text that was sent.</p>
      <table className="grid comms-log">
        <thead><tr><th scope="col">Time</th><th scope="col">By</th><th scope="col">Channel</th><th scope="col">Subject</th></tr></thead>
        <tbody>
          {rows.map(r => {
            const o = OUTCOME[r.outcome] ?? OUTCOME.Handed
            const sel = open === r.id
            return (
              <Fragment key={r.id}>
                <tr className={sel ? 'selected' : undefined} aria-selected={sel}>
                  <td className="mono nowrap">{fmtDayTime(r.dispatchedAt)}</td>
                  <td>{r.dispatchedByName ?? '—'}</td>
                  <td className="nowrap"><span className={o.cls}>{o.g}</span> {r.channel === 'Webhook' ? `webhook ${r.webhookName ?? ''}` : r.channel === 'Mailto' ? 'mail' : 'copy'} <span className={o.cls}>{o.word}</span>{r.isRehearsal && <span className="muted"> · rehearsal</span>}</td>
                  <td><button type="button" className="text plainlink" aria-expanded={sel} onClick={() => setOpen(sel ? null : r.id)}>{r.subject}</button></td>
                </tr>
                {sel && (
                  <tr><td colSpan={4}>
                    {!detail ? <span className="muted">Loading…</span> : (
                      <>
                        <pre className="audit-json" role="group" aria-label="Stored message">{detail.body}</pre>
                        <p className="muted small">Stored exactly as sent · {detail.body.length.toLocaleString()} characters · sha256 <span className="mono">{short(detail.bodySha256)}…</span> · <a href={`/api/v1/comms/dispatches/${detail.id}/body`} target="_blank" rel="noreferrer">raw text</a></p>
                      </>
                    )}
                  </td></tr>
                )}
              </Fragment>
            )
          })}
        </tbody>
      </table>
      {next && <p><button type="button" className="text" disabled={!!pending} onClick={() => void more()}>{pending ? 'Loading…' : 'Show older'}</button></p>}
    </>
  )
}

/** Writes to the clipboard inside the click's user gesture even though the text only exists once the dispatch returns (ClipboardItem accepts promises). */
async function copyLater(kind: 'html' | 'text', text: Promise<string>, alt?: Promise<string>): Promise<void> {
  const blob = (t: Promise<string>, type: string) => t.then(s => new Blob([s], { type }))
  if (typeof ClipboardItem !== 'undefined' && navigator.clipboard?.write) {
    const items: Record<string, Promise<Blob>> = kind === 'html'
      ? { 'text/html': blob(text, 'text/html'), 'text/plain': blob(alt ?? text, 'text/plain') }
      : { 'text/plain': blob(text, 'text/plain') }
    await navigator.clipboard.write([new ClipboardItem(items)])
    return
  }
  const s = await text
  if (!navigator.clipboard?.writeText) throw new Error('clipboard unavailable')
  await navigator.clipboard.writeText(s)
}

/**
 * The Communication drawer (PROJECT_SCOPE section 7 #4, mockups/Comms.html): Message (template source with token highlighting beside the hydrated
 * preview, T-minus schedule, send actions), Schedule and Sent log. Every send stores the exact text through POST comms:dispatch; nothing here edits a sent message.
 */
export function CommsDrawer({ trainId, canDispatch, refreshKey, onClose, onChanged }: { trainId: string; canDispatch: boolean; refreshKey: number; onClose: () => void; onChanged: () => void }) {
  const [draft, setDraft] = useDraft<CommsDraft>(commsKey(trainId))
  const d: CommsDraft = draft ?? { open: true }
  const set = (patch: Partial<CommsDraft>) => setDraft({ ...d, ...patch, open: true })
  const tab: Tab = d.tab ?? 'message'
  const [ctx, setCtx] = useState<DispatchContext | null>(null)
  const [ctxErr, setCtxErr] = useState<string | null>(null)
  const [rev, setRev] = useState(0)
  const [logRev, setLogRev] = useState(0)
  const [body, setBody] = useState<CommPreview | null>(null)
  const [subject, setSubject] = useState<CommPreview | null>(null)
  const [previewErr, setPreviewErr] = useState<string | null>(null)
  const { run, pending: busy } = useAction()
  const [err, setErr] = useState<string | null>(null)
  const [result, setResult] = useState<Dispatched | null>(null)
  const [fallback, setFallback] = useState<{ text: string; note: string } | null>(null)
  const fallbackRef = useRef<HTMLTextAreaElement>(null)
  const ids = useId()
  const headingId = `${ids}-title`, whyId = `${ids}-why`

  // focus the drawer's heading when it opens, and give focus back to the opener ("Communicate") when it closes (WCAG 2.4.3)
  useRestoreFocus('[data-comms-trigger]')   // first: it must record the opener before the heading takes focus
  const headingRef = useFocusWhen<HTMLHeadingElement>(true)

  useEffect(() => {
    let live = true
    getDispatchContext(trainId).then(c => { if (live) { setCtx(c); setCtxErr(null) } }).catch(e => live && setCtxErr(errMsg(e, 'Could not load the communications.')))
    return () => { live = false }
  }, [trainId, refreshKey, rev])

  useEffect(() => {
    // Esc closes the drawer, but not while typing or choosing in a field (the key belongs to the field there)
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape' || e.defaultPrevented) return
      const t = e.target as HTMLElement | null
      if (t && (t.closest('input, select, textarea') || t.isContentEditable)) return
      onClose()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])
  useEffect(() => { if (fallback) { fallbackRef.current?.focus(); fallbackRef.current?.select() } }, [fallback])

  // The template in view: the draft's choice, else the schedule's next unsent item, else the first template.
  const next = ctx?.schedule.find(s => !s.sentAt)
  const chosen = ctx?.templates.find(t => t.id === d.templateId)
  const template = useMemo(() => {
    if (!ctx) return undefined
    return chosen ?? ctx.templates.find(t => t.id === next?.commTemplateId) ?? ctx.templates[0]
  }, [ctx, chosen, next?.commTemplateId])
  // The schedule item a send is logged against: the one picked, else the next unsent item when its template is the one in view by default (UX review #15).
  const item = ctx?.schedule.find(s => s.id === d.scheduleItemId && !s.sentAt && s.commTemplateId === template?.id)
    ?? (!chosen && !d.scheduleItemId && next && next.commTemplateId === template?.id ? next : undefined)

  const loadPreview = useCallback(() => {
    if (!template) return () => {}
    let live = true
    setPreviewErr(null)
    Promise.all([previewComm(trainId, { templateId: template.id }, 'Markdown'), previewComm(trainId, { text: template.subjectLine }, 'PlainText')])
      .then(([b, s]) => { if (live) { setBody(b); setSubject(s) } })
      .catch(e => { if (live) { setBody(null); setSubject(null); setPreviewErr(errMsg(e, 'The preview is not available.')) } })
    return () => { live = false }
  }, [trainId, template?.id, template?.version]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => loadPreview(), [loadPreview, refreshKey, rev])

  const errors = useMemo(() => [...new Set([...(body?.tokenErrors ?? []), ...(subject?.tokenErrors ?? [])])], [body, subject])
  const stale = !!(ctx && body && ctx.trainVersion !== body.trainVersion)
  useEffect(() => { if (stale) announce('The train moved since this preview. Refresh the preview before sending.') }, [stale])
  const nTokens = template ? tokensOf(template.subjectLine + '\n' + template.markdownBody).length : 0
  const why = !canDispatch ? 'Only an RTE or Release Manager sends communications.'
    : !template ? 'This train has no communication templates.'
    : previewErr ? 'The preview is unavailable, so nothing can be sent.'
    : !body ? 'Loading the preview…'
    : errors.length > 0 ? `Unknown tokens block sending: ${errors.join('; ')}`
    : stale ? `The train moved to v${ctx!.trainVersion} since this preview (v${body.trainVersion}). Refresh the preview first.`
    : busy ? 'Sending…' : null
  const noHooks = !why && !!ctx && ctx.webhookTargets.length === 0
  const whyWebhook = why ?? (noHooks ? 'No webhook destinations are approved yet; an administrator adds them to the allowlist on Sync health.' : null)
  const webhook = ctx?.webhookTargets.find(w => w.id === d.webhookId) ?? ctx?.webhookTargets[0]
  const toSync = (e: MouseEvent<HTMLAnchorElement>) => {
    // client-side navigation: the router listens to popstate (route.ts)
    e.preventDefault(); window.history.pushState(null, '', '/sync'); window.dispatchEvent(new PopStateEvent('popstate'))
  }

  const afterSend = () => { setLogRev(n => n + 1); setRev(n => n + 1); onChanged() }
  const request = (b: Omit<DispatchBody, 'templateId' | 'scheduleItemId'>): DispatchBody => ({ ...b, templateId: template!.id, scheduleItemId: item?.id })

  // one send at a time: `run` ignores a second click while the first is in flight (and calls `fn` synchronously, so the clipboard keeps the click's gesture)
  const send = (label: string, fn: (v: number) => Promise<void>) => run(label, async () => {
    if (!body || why) return
    setErr(null); setResult(null); setFallback(null)
    try { await fn(body.trainVersion) }
    catch (e) {
      if (e instanceof ApiError && e.status === 409) { setErr('The train changed since this preview, so nothing was sent. The preview is refreshed: check it and send again.'); setRev(n => n + 1) }
      else setErr(errMsg(e, 'Could not send.'))
    }
  })
  const done = (x: Dispatched) => { setResult(x); afterSend(); announce(resultText(x)) }

  const copy = (format: 'RichText' | 'Markdown') => send(format === 'RichText' ? 'Copy rich text' : 'Copy Markdown', async v => {
    // The dispatch runs inside the click's clipboard gesture: the promise is handed to the clipboard, which waits for it.
    const sent = dispatchComm(trainId, request({ channel: 'Copy', format }), v)
    const text = sent.then(x => x.body)
    let clipboardFailed = false
    try { await (format === 'RichText' ? copyLater('html', text, text.then(htmlToText)) : copyLater('text', text)) } catch { clipboardFailed = true }
    const x = await sent   // a dispatch failure surfaces here (and rejected the clipboard write above)
    done(x)
    if (clipboardFailed) setFallback({ text: x.body, note: 'The browser blocked the clipboard. The message is recorded as sent; select the text below and copy it yourself.' })
  })

  const mail = () => send('Open mail', async v => {
    const x = await dispatchComm(trainId, request({ channel: 'Mailto' }), v)
    done(x)
    const href = mailtoHref(x.subject, x.body)
    if (href.length > 1800) setFallback({ text: x.body, note: 'This message is long and some mail apps cut long links. It is recorded in full; if the mail is incomplete, copy the text below.' })
    window.location.href = href
  })

  const post_ = () => send('Post to webhook', async v => {
    if (!webhook) return
    const x = await dispatchComm(trainId, request({ channel: 'Webhook', webhookDestinationId: webhook.id }), v)
    done(x)
  })

  const pick = (s: ScheduleItem) => set({ templateId: s.commTemplateId, scheduleItemId: s.sentAt ? undefined : s.id })
  const seed = <SeedSchedule trainId={trainId} canSeed={canDispatch} onSeeded={() => { setRev(n => n + 1); onChanged() }} />
  const label = (name: string, text: string, busyText: string) => (busy === name ? busyText : text)

  return (
    <section className="comms-drawer" aria-labelledby={headingId}>
      <div className="section-head"><span className="cap" aria-hidden="true">Drawer · communicate</span>
        <span><button type="button" className="text quiet" onClick={onClose}>Close</button></span></div>
      <h2 id={headingId} ref={headingRef} tabIndex={-1}>{template ? `${template.templateType}` : 'Communications'}</h2>
      {template && <div className="muted">{template.audience}{item ? ` · for ${tMinusLabel(ctx!.targetReleaseDate, item.dueAt)}, due ${fmtDayTime(item.dueAt)}` : ''}</div>}
      {ctxErr && <p className="bad" role="alert">✗ {ctxErr}</p>}
      {!ctx && !ctxErr && <p className="muted">Loading…</p>}

      {ctx && (
        <>
          <div className="tabs comms-tabs" role="group" aria-label="Drawer sections">
            {(['message', 'schedule', 'log'] as const).map(t => (
              <button key={t} type="button" className="choice" aria-pressed={tab === t} onClick={() => set({ tab: t })}>{t === 'message' ? 'Message' : t === 'schedule' ? 'Schedule' : 'Sent log'}</button>
            ))}
          </div>

          {tab === 'message' && (
            <div className="tab-body">
              {ctx.templates.length === 0 ? <p className="muted">This train has no communication templates yet.</p> : (
                <>
                  <p className="inline-form"><label>Template <select className="line" value={template?.id ?? ''} onChange={e => set({ templateId: e.target.value, scheduleItemId: undefined })}>
                    {ctx.templates.map(t => <option key={t.id} value={t.id}>{t.templateType} · {t.audience}</option>)}</select></label></p>
                  <div className="comms-cols">
                    <div>
                      <div className="header-line"><h3 className="cap">Template</h3>
                        {errors.length > 0 ? <span className="bad small">✗ {errors.length} unknown {errors.length === 1 ? 'token' : 'tokens'}</span>
                          : body ? <span className="ok small">{nTokens} tokens · all known ✓</span> : <span className="muted small">{nTokens} tokens</span>}</div>
                      <div className="src comms-rule" role="group" aria-label="Template source">{template && <>Subject: <Source text={template.subjectLine} errors={errors} />{'\n\n'}<Source text={template.markdownBody} errors={errors} /></>}</div>
                      <div className="muted small">An unknown token, such as a typo, blocks sending. Empty lists print “None”.</div>
                    </div>
                    <div>
                      <div className="header-line"><h3 className="cap">Preview</h3>
                        <span className="muted small">{body ? <>data as of <span className="mono">{fmtHM(body.asOf)}</span> · train <span className="mono">v{body.trainVersion}</span></> : '—'}</span></div>
                      {previewErr && <p className="bad" role="alert">✗ {previewErr}</p>}
                      <div className="comms-rule" role="group" aria-label="Hydrated preview">
                        {subject && <div className="preview-subject"><span className="sr-only">Subject: </span>{subject.text}</div>}
                        {body && <PreviewText text={body.text} />}
                        {errors.length > 0 && <ul className="plain bad small">{errors.map((e, i) => <li key={i}>✗ {e}</li>)}</ul>}
                      </div>
                      {stale && <p className="warn small">▲ The train moved to <span className="mono">v{ctx.trainVersion}</span> since this preview. <button type="button" className="text" onClick={() => setRev(n => n + 1)}>Refresh preview</button></p>}
                    </div>
                  </div>
                </>
              )}

              <div className="comms-block">
                <h3 className="cap">T−minus schedule for {ctx.trainTitle}</h3>
                <ScheduleTable ctx={ctx} selectedId={item?.id} onPick={pick} empty={seed} />
              </div>

              <div className="comms-block">
                <h3 className="cap">Send</h3>
                <div className="comms-send">
                  <button type="button" className="text" disabled={!!why} aria-describedby={why ? whyId : undefined} onClick={() => void copy('RichText')}>{label('Copy rich text', 'Copy rich text', 'Copying…')}</button>
                  <button type="button" className="text" disabled={!!why} aria-describedby={why ? whyId : undefined} onClick={() => void copy('Markdown')}>{label('Copy Markdown', 'Copy Markdown', 'Copying…')}</button>
                  <button type="button" className="text" disabled={!!why} aria-describedby={why ? whyId : undefined} onClick={() => void mail()}>{label('Open mail', 'Open mailto', 'Opening mail…')}</button>
                  <span className="inline-form">
                    <label className="sr-only" htmlFor="comms-hook">Webhook destination</label>
                    <select id="comms-hook" className="line" value={webhook?.id ?? ''} disabled={!ctx.webhookTargets.length} onChange={e => set({ webhookId: e.target.value })}>
                      {ctx.webhookTargets.length === 0 ? <option value="">no destinations</option> : ctx.webhookTargets.map(w => <option key={w.id} value={w.id}>{w.name} ({w.host})</option>)}
                    </select>
                    <button type="button" className="text" disabled={!!whyWebhook} aria-describedby={whyWebhook ? whyId : undefined} onClick={() => void post_()}>{label('Post to webhook', webhook ? `Post to ${webhook.name}` : 'Post to webhook', 'Posting…')}</button>
                  </span>
                </div>
                {whyWebhook && <div id={whyId} className="muted small">{whyWebhook}{noHooks && <> <a href="/sync" onClick={toSync}>Open Sync health</a></>}</div>}
                <div className="muted small">Each send stores the exact text, channel and time in the sent log. Webhooks go only to admin-approved https destinations{item ? '; sending marks this schedule item sent' : ''}.</div>
                {err && <p className="bad" role="alert">✗ {err}</p>}
                {result && (
                  <p className={OUTCOME[result.outcome].cls} data-testid="comms-result">
                    {OUTCOME[result.outcome].g} {resultText(result)}
                  </p>
                )}
                {fallback && (
                  <>
                    <p className="warn" role="alert">▲ {fallback.note}</p>
                    <textarea ref={fallbackRef} className="bulk-text" readOnly rows={8} value={fallback.text} aria-label="Sent message text" onFocus={e => e.currentTarget.select()} />
                  </>
                )}
              </div>
            </div>
          )}

          {tab === 'schedule' && (
            <div className="tab-body">
              <h3 className="cap">T−minus schedule for {ctx.trainTitle}</h3>
              <ScheduleTable ctx={ctx} selectedId={item?.id} onPick={s => set({ tab: 'message', templateId: s.commTemplateId, scheduleItemId: s.sentAt ? undefined : s.id })} empty={seed} />
              {ctx.schedule.length > 0 && <p className="muted small">Choose a row to write and send it. Sent and late come from the recorded send time against the due time.</p>}
            </div>
          )}

          {tab === 'log' && <div className="tab-body"><SentLog trainId={trainId} refreshKey={logRev + refreshKey} /></div>}
        </>
      )}
    </section>
  )
}

/** The one sentence a send reports, shown under the actions and spoken through the app's live region. */
function resultText(r: Dispatched): string {
  const o = OUTCOME[r.outcome] ?? OUTCOME.Handed
  const what = r.channel === 'Webhook' ? `Webhook ${r.webhookName}: ${o.word}` : r.channel === 'Mailto' ? 'Recorded as sent by mail' : r.format === 'Markdown' ? 'Markdown copied and recorded' : 'Rich text copied and recorded'
  if (r.outcome === 'Failed') return `${what}: ${r.failureReason ?? 'delivery failed'}. It is recorded in the sent log and raised as a sync alert.`
  return `${what}${r.sentAt && r.dueAt ? (r.late ? ' · sent late' : ' · on time') : ''}`
}
