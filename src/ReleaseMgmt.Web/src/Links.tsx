import { useEffect, useState } from 'react'
import { addLink, getConnectors, getLinks, getProducts, getSteps, getTrain, removeLink, type ExternalLinkRow, type LinkEntityType } from './api'
import { isConflict } from './Conflict'
import { brokenReason, externalUrl, freshness, keyHint } from './externalLinks'
import { errMsg } from './format'
import { fmtDayTime, zoneAbbr } from './time'

/** A thing on this train a link can attach to. Blockers and known issues are linked from their own panels; here they only get a label. */
interface Target { type: LinkEntityType; id: string; label: string }
const SOURCES = ['Jira', 'ServiceNow'] as const
const TYPE_WORD: Record<LinkEntityType, string> = { Train: 'Train', Product: 'Product', Gate: 'Gate', RunbookStep: 'Runbook step', Blocker: 'Blocker', KnownIssue: 'Known issue' }

/**
 * REOS-82: the train's external links (Jira issues and fix versions, ServiceNow changes and change tasks). Each row: system, key, what it is attached to,
 * sync state as glyph + word, last checked. Admins (RTE, Release Manager) add a link inline and remove one with an inline confirm. A key becomes a link
 * only through externalUrl (the connector's configured base URL), never from the link data alone (SEC-C).
 */
export function Links({ trainId, canEdit, refreshKey, onChanged }: { trainId: string; canEdit: boolean; refreshKey: number; onChanged: () => void }) {
  const [links, setLinks] = useState<ExternalLinkRow[] | null>(null)
  const [bases, setBases] = useState<Record<string, string>>({})
  const [targets, setTargets] = useState<Target[]>([])
  const [err, setErr] = useState<string | null>(null)
  const [adding, setAdding] = useState(false)
  const [source, setSource] = useState<'Jira' | 'ServiceNow'>('Jira')
  const [key, setKey] = useState('')
  const [target, setTarget] = useState(`Train:${trainId}`)
  const [confirm, setConfirm] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [rev, setRev] = useState(0)

  useEffect(() => {
    let live = true
    getLinks(trainId).then(l => { if (live) { setLinks(l); setErr(null) } }).catch(e => live && setErr(errMsg(e, 'The links could not be loaded.')))
    getConnectors().then(cs => live && setBases(Object.fromEntries(cs.map(c => [c.source, c.isEnabled ? c.baseUrl : ''])))).catch(() => live && setBases({}))
    Promise.all([getTrain(trainId), getProducts(trainId).catch(() => null), getSteps(trainId).catch(() => [])]).then(([t, p, s]) => {
      if (!live) return
      setTargets([
        { type: 'Train', id: t.id, label: 'This train' },
        ...(p?.products ?? []).map(x => ({ type: 'Product' as const, id: x.id, label: `${x.name} ${x.versionTag}` })),
        ...t.gates.map(g => ({ type: 'Gate' as const, id: g.id, label: g.name })),
        ...s.map(x => ({ type: 'RunbookStep' as const, id: x.id, label: `${x.stepCode} ${x.title}` })),
      ])
    }).catch(() => live && setTargets([{ type: 'Train', id: trainId, label: 'This train' }]))
    return () => { live = false }
  }, [trainId, refreshKey, rev])

  const attachedTo = (l: ExternalLinkRow) => {
    const t = targets.find(x => x.type === l.entityType && x.id === l.entityId)
    return l.entityType === 'Train' ? 'Train' : `${TYPE_WORD[l.entityType]} · ${t ? t.label : l.entityId.slice(0, 8)}`
  }
  const reload = () => { setRev(n => n + 1); onChanged() }

  const add = async () => {
    const [type, ...rest] = target.split(':')
    setBusy(true); setErr(null)
    try {
      await addLink(trainId, { entityType: type as LinkEntityType, entityId: rest.join(':'), sourceSystem: source, externalKey: key })
      setKey(''); setAdding(false); reload()
    } catch (e) { setErr(errMsg(e, 'The link could not be added.')) }
    finally { setBusy(false) }
  }
  const remove = async (l: ExternalLinkRow) => {
    setBusy(true); setErr(null)
    try { await removeLink(l.id, l.version); setConfirm(null); reload() }
    catch (e) { setErr(isConflict(e) ? `${l.externalKey} changed since you loaded it (it was just checked or edited). Reloaded; remove it again if you still mean to.` : errMsg(e, 'The link could not be removed.')); setConfirm(null); reload() }
    finally { setBusy(false) }
  }

  return (
    <section aria-label="Links" id="links">
      <div className="section-head"><h2 className="cap dark">Links</h2>
        <span className="muted">{links && links.length > 0 && <>{links.length} · </>}{canEdit && !adding && <button type="button" className="text" onClick={() => { setAdding(true); setErr(null) }}>Add link</button>}</span></div>
      {err && <p className="bad" role="alert">✗ {err}</p>}
      {adding && (
        <div className="inline-form" role="group" aria-label="Add link">
          <span className="tabs" role="group" aria-label="System">
            {SOURCES.map(s => <button key={s} type="button" className="choice" aria-pressed={source === s} onClick={() => setSource(s)}>{s}</button>)}
          </span>{' '}
          <label>Key <input className="line mono" value={key} onChange={e => setKey(e.target.value)} placeholder={keyHint(source)} spellCheck={false} autoComplete="off" maxLength={80} /></label>{' '}
          <label>Attach to <select className="line" value={target} onChange={e => setTarget(e.target.value)}>
            {(['Train', 'Product', 'Gate', 'RunbookStep'] as const).map(type => {
              const opts = targets.filter(t => t.type === type)
              if (opts.length === 0) return null
              return type === 'Train'
                ? opts.map(t => <option key={t.id} value={`Train:${t.id}`}>{t.label}</option>)
                : <optgroup key={type} label={`${TYPE_WORD[type]}s`}>{opts.map(t => <option key={t.id} value={`${type}:${t.id}`}>{t.label}</option>)}</optgroup>
            })}
          </select></label>{' '}
          <button type="button" className="text" onClick={() => { setAdding(false); setKey('') }}>Cancel</button>{' '}
          <button type="button" className="text" disabled={busy || !key.trim()} onClick={add}>Add link</button>
          {!key.trim() && <p className="muted small">Enter the {source} key, such as {keyHint(source)}.</p>}
        </div>
      )}
      {links && links.length === 0 && <p className="muted">No links. Link the train, a product, a gate or a runbook step to its Jira issue or ServiceNow change so Sync health can watch it.</p>}
      {links && links.length > 0 && (
        <div className="scroll-x"><table className="grid" aria-label="External links">
          <thead><tr><th>System</th><th>Key</th><th>Attached to</th><th>Sync</th><th>Reported</th><th>Last checked ({zoneAbbr()})</th>{canEdit && <th><span className="sr-only">Actions</span></th>}</tr></thead>
          <tbody>{links.map(l => {
            const f = freshness(l)
            const href = externalUrl(l.sourceSystem, bases[l.sourceSystem], l.externalKey)
            return (
              <tr key={l.id} data-testid="link-row">
                <td>{l.sourceSystem}</td>
                <td className="mono">{href ? <a href={href} target="_blank" rel="noopener noreferrer">{l.externalKey}</a> : l.externalKey}</td>
                <td>{attachedTo(l)}</td>
                <td>
                  <span className={f.cls}>{f.glyph} {f.word}</span>
                  {f.word === 'broken' && <span className="muted">: {brokenReason(l.syncState)}</span>}
                  {l.syncState === 'Mismatch' && <span className="warn"> · ◆ mismatch</span>}
                  {l.warnings.map(w => <div key={w.rule} className="muted small">{w.message}</div>)}
                </td>
                <td>{l.lastSyncedStatus ?? <span className="muted">—</span>}</td>
                <td className="mono">{l.lastSyncedAt ? fmtDayTime(l.lastSyncedAt) : <span className="muted">never</span>}</td>
                {canEdit && (
                  <td className="wrap">
                    {confirm === l.id ? (
                      <span role="group" aria-label={`Confirm remove ${l.externalKey}`}>
                        <span className="warn">▲ Sync health stops watching {l.externalKey} for this {TYPE_WORD[l.entityType].toLowerCase()}.</span>{' '}
                        <button type="button" className="text" onClick={() => setConfirm(null)}>Cancel</button>{' '}
                        <button type="button" className="text destructive" disabled={busy} onClick={() => remove(l)}>Confirm remove</button>
                      </span>
                    ) : <button type="button" className="text" aria-label={`Remove ${l.externalKey}`} onClick={() => setConfirm(l.id)}>Remove</button>}
                  </td>
                )}
              </tr>
            )
          })}</tbody>
        </table></div>
      )}
    </section>
  )
}
