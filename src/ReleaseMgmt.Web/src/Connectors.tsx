import { useCallback, useEffect, useState } from 'react'
import {
  ApiError, clearConnectorCredentials, getConnectors, saveConnector, setConnectorCredentials, syncConnector, testConnector,
  type ConnectorRow, type Me,
} from './api'
import { errMsg } from './format'
import { useRowNav } from './rowNav'
import { fmtDayTime, zoneAbbr } from './time'

// REOS-39: connectors admin (docs/UI.md, "Connectors, webhook allowlist"): a table of connectors with state from ConnectorState, credentials entered in the
// detail pane and never displayed back. Status is a glyph and a word; actions are text buttons; no modals.

const STATUS: Record<string, { glyph: string; word: string; cls: string; hint: string }> = {
  Ok: { glyph: '●', word: 'OK', cls: 'ok', hint: 'The last cycle read every link.' },
  NeverSynced: { glyph: '○', word: 'Never synced', cls: 'muted', hint: 'Configured, waiting for the first cycle (or no links yet).' },
  NotConfigured: { glyph: '○', word: 'Not configured', cls: 'muted', hint: 'Enter the base URL and credentials.' },
  NoCredentials: { glyph: '▲', word: 'No credentials', cls: 'warn', hint: 'Enter credentials before links can be read.' },
  Failing: { glyph: '✗', word: 'Failing', cls: 'bad', hint: 'The last cycle could not read the system. See Sync health for the alert.' },
  Stalled: { glyph: '✗', word: 'Stalled', cls: 'bad', hint: 'No cycle has completed for three intervals: the poller is stopped or stuck.' },
  Disabled: { glyph: '○', word: 'Disabled', cls: 'muted', hint: 'Polling is turned off for this connector.' },
}
const statusOf = (s: string) => STATUS[s] ?? { glyph: '○', word: s, cls: 'muted', hint: '' }

const LABELS: Record<string, { user: string; secret: string; kinds: { key: string; label: string }[] }> = {
  Jira: { user: 'Account email', secret: 'API token', kinds: [{ key: 'ApiToken', label: 'API token' }] },
  ServiceNow: { user: 'Client id', secret: 'Client secret', kinds: [{ key: 'OAuth', label: 'OAuth client credentials' }, { key: 'Basic', label: 'Basic authentication' }] },
}
const userLabel = (source: string, kind: string) => (kind === 'Basic' ? 'User name' : LABELS[source].user)
const secretLabel = (source: string, kind: string) => (kind === 'Basic' ? 'Password' : LABELS[source].secret)

const when = (iso: string | null) => (iso ? `${fmtDayTime(iso)} ${zoneAbbr(iso)}` : 'never')

type Result = { ok: boolean; text: string } | null

function Detail({ row, canEdit, onRow, onReload }: { row: ConnectorRow; canEdit: boolean; onRow: (r: ConnectorRow) => void; onReload: () => void }) {
  const st = statusOf(row.status)
  const labels = LABELS[row.source]
  const [url, setUrl] = useState(row.baseUrl)
  const [kind, setKind] = useState(labels.kinds[0].key)
  const [user, setUser] = useState('')
  const [secret, setSecret] = useState('')
  const [busy, setBusy] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [saved, setSaved] = useState<string | null>(null)
  const [test, setTest] = useState<Result>(null)
  const [sync, setSync] = useState<Result>(null)
  const [confirmClear, setConfirmClear] = useState(false)

  // a different connector (or a reload) resets what was typed; a stored credential is never put back into a field
  useEffect(() => { setUrl(row.baseUrl); setKind(row.credentialKind && labels.kinds.some(k => k.key === row.credentialKind) ? row.credentialKind : labels.kinds[0].key) }, [row.source, row.baseUrl, row.credentialKind, labels.kinds])
  useEffect(() => { setUser(''); setSecret(''); setProblem(null); setSaved(null); setTest(null); setSync(null); setConfirmClear(false) }, [row.source])

  const run = async (name: string, fn: () => Promise<void>) => {
    setBusy(name); setProblem(null); setSaved(null)
    try { await fn() }
    catch (e) {
      if (e instanceof ApiError && e.status === 409) { setProblem('Someone else changed this connector. It has been reloaded; try again.'); onReload() }
      else setProblem(errMsg(e))
    } finally { setBusy(null) }
  }

  const saveUrl = () => run('url', async () => { onRow(await saveConnector(row.source, { baseUrl: url.trim() }, row.version)); setSaved('Base URL saved.') })
  const toggle = () => run('enabled', async () => onRow(await saveConnector(row.source, { isEnabled: !row.isEnabled }, row.version)))
  const saveCreds = () => run('creds', async () => {
    const r = await setConnectorCredentials(row.source, { kind, username: user, secret })
    setUser(''); setSecret('')                      // cleared at once: nothing typed is kept after it is sent
    onRow(r); setSaved('Credentials saved. They are stored encrypted and are not shown again.')
  })
  const clearCreds = () => run('clear', async () => { onRow(await clearConnectorCredentials(row.source)); setConfirmClear(false); setSaved('Credentials removed.') })
  const doTest = () => run('test', async () => {
    setTest(null)
    try { const r = await testConnector(row.source); setTest({ ok: true, text: `Connected in ${r.elapsedMs} ms.` }) }
    catch (e) { if (e instanceof ApiError && e.status === 422) setTest({ ok: false, text: errMsg(e) }); else throw e }
  })
  const doSync = () => run('sync', async () => {
    setSync(null)
    const r = await syncConnector(row.source)
    setSync({ ok: r.outcome === 'Clean' || r.outcome === 'Idle', text: r.outcome === 'Clean' ? `Read ${r.linksChecked} link${r.linksChecked === 1 ? '' : 's'}.` : r.outcome === 'Idle' ? 'No links to read.' : `${r.outcome}${r.message ? `: ${r.message}` : ''}` })
    onReload()
  })

  const configured = row.baseUrl !== ''
  const testReason = !configured ? 'Save the base URL first.' : !row.hasCredentials ? 'Enter credentials first.' : null

  return (
    <aside className="detail" aria-label={`${row.source} connector`}>
      <h2>{row.source}</h2>
      <p className={st.cls}>{st.glyph} {st.word}</p>
      <p className="muted small">{st.hint}</p>
      <dl className="facts">
        <dt>Last success</dt><dd className="mono">{when(row.lastSuccessAt)}</dd>
        <dt>Last cycle</dt><dd className="mono">{when(row.lastCycleCompletedAt)}</dd>
        <dt>Failed cycles in a row</dt><dd className={`mono ${row.consecutiveFailures > 0 ? 'bad' : ''}`}>{row.consecutiveFailures}</dd>
        <dt>Open alerts</dt><dd className={`mono ${row.openAlerts > 0 ? 'warn' : ''}`}>{row.openAlerts}</dd>
        <dt>Links</dt><dd className="mono">{row.linkCount}{row.staleLinks > 0 && <span className="warn"> · ▲ {row.staleLinks} stale</span>}{row.mismatchLinks > 0 && <span className="warn"> · ◆ {row.mismatchLinks} mismatch</span>}</dd>
      </dl>
      {problem && <p className="bad" role="alert">✗ {problem}</p>}
      {saved && <p className="ok" role="status">✓ {saved}</p>}

      {!canEdit ? <p className="muted">Only an RTE or Release Manager can change connectors.</p> : (
        <>
          <h3 className="cap">Connection</h3>
          <p><label>Base URL <input className="line block" value={url} onChange={e => setUrl(e.target.value)} placeholder={row.source === 'Jira' ? 'https://your-site.atlassian.net' : 'https://your-instance.service-now.com'} spellCheck={false} /></label></p>
          <p className="muted small">https only. Read-only: this app never writes to {row.source}, and stores keys, states and dates only.</p>
          <p className="actions">
            <button type="button" className="text" disabled={busy !== null || url.trim() === '' || url.trim() === row.baseUrl} onClick={saveUrl}>Save base URL</button>
            {configured && <button type="button" className={row.isEnabled ? 'text destructive' : 'text'} disabled={busy !== null} onClick={toggle}>{row.isEnabled ? 'Disable polling' : 'Enable polling'}</button>}
          </p>

          <h3 className="cap">Credentials</h3>
          <p className="muted small">{row.hasCredentials ? `● Stored (${row.credentialKind}). Enter new values to replace them.` : '○ None stored.'} Values are encrypted on the server and never displayed.</p>
          {labels.kinds.length > 1 && (
            <p><label>Type <select className="line" value={kind} onChange={e => setKind(e.target.value)}>{labels.kinds.map(k => <option key={k.key} value={k.key}>{k.label}</option>)}</select></label></p>
          )}
          <form onSubmit={e => { e.preventDefault(); void saveCreds() }} autoComplete="off">
            <p><label>{userLabel(row.source, kind)} <input className="line block" value={user} onChange={e => setUser(e.target.value)} autoComplete="off" spellCheck={false} /></label></p>
            <p><label>{secretLabel(row.source, kind)} <input className="line block" type="password" value={secret} onChange={e => setSecret(e.target.value)} autoComplete="new-password" /></label></p>
            <p className="actions">
              <button type="submit" className="text" disabled={busy !== null || user.trim() === '' || secret === ''}>Save credentials</button>
              {row.hasCredentials && !confirmClear && <button type="button" className="text destructive" disabled={busy !== null} onClick={() => setConfirmClear(true)}>Remove credentials</button>}
              {confirmClear && <>
                <span className="muted">Polling {row.source} stops working until new credentials are entered.</span>
                <button type="button" className="text destructive" disabled={busy !== null} onClick={clearCreds}>Confirm remove</button>
                <button type="button" className="text" onClick={() => setConfirmClear(false)}>Cancel</button>
              </>}
            </p>
            {(user.trim() === '' || secret === '') && <p className="muted small">Enter both values to save.</p>}
          </form>

          <h3 className="cap">Check</h3>
          <p className="actions">
            <button type="button" className="text" disabled={busy !== null || testReason !== null} onClick={doTest}>Test connection</button>
            <button type="button" className="text" disabled={busy !== null || testReason !== null || !row.isEnabled} onClick={doSync}>Sync now</button>
          </p>
          {testReason && <p className="muted small">{testReason}</p>}
          {busy === 'test' && <p className="muted" role="status">Testing…</p>}
          {test && <p className={test.ok ? 'ok' : 'bad'} role="status">{test.ok ? '✓' : '✗'} {test.text}</p>}
          {busy === 'sync' && <p className="muted" role="status">Syncing…</p>}
          {sync && <p className={sync.ok ? 'ok' : 'bad'} role="status">{sync.ok ? '✓' : '✗'} {sync.text}</p>}
        </>
      )}
    </aside>
  )
}

export function Connectors({ me }: { me: Me }) {
  const canEdit = me.roles.includes('RTE') || me.roles.includes('ReleaseManager')
  const [rows, setRows] = useState<ConnectorRow[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [selected, setSelected] = useState<string | null>(null)
  const load = useCallback(() => { getConnectors().then(r => { setRows(r); setError(null) }).catch(e => setError(errMsg(e, 'Could not load the connectors.'))) }, [])
  useEffect(() => { load(); const t = window.setInterval(load, 15_000); return () => window.clearInterval(t) }, [load])   // state is bookkeeping the poller keeps current

  const [navIndex, setNavIndex] = useRowNav(rows?.length ?? 0, i => rows && setSelected(rows[i].source))
  useEffect(() => { if (navIndex !== null && rows?.[navIndex]) setSelected(rows[navIndex].source) }, [navIndex, rows])
  const sel = rows?.find(r => r.source === selected) ?? null
  const put = (r: ConnectorRow) => setRows(rs => rs && rs.map(x => (x.source === r.source ? r : x)))

  return (
    <section>
      <h1>Connectors</h1>
      <p className="muted">Read-only links to Jira Cloud and ServiceNow. Polled every 5 minutes, every minute while a deployment window is open.</p>
      {error && <p className="bad" role="alert">✗ {error}</p>}
      <div className="split">
        <div>
          <table className="grid">
            <thead><tr><th>Connector</th><th>Status</th><th>Credentials</th><th>Last success ({zoneAbbr()})</th><th className="n">Failures</th><th className="n">Alerts</th><th className="n">Links</th></tr></thead>
            <tbody>
              {rows?.map((r, i) => {
                const st = statusOf(r.status)
                const on = r.source === selected
                return (
                  <tr key={r.source} className={on ? 'selected' : undefined} aria-selected={on} onClick={() => { setSelected(r.source); setNavIndex(i) }}>
                    <td><button type="button" className="plainlink" onClick={() => setSelected(r.source)}>{r.source}</button></td>
                    <td className={st.cls}>{st.glyph} {st.word}</td>
                    <td className={r.hasCredentials ? '' : 'muted'}>{r.hasCredentials ? `● stored (${r.credentialKind})` : '○ none'}</td>
                    <td className="mono">{r.lastSuccessAt ? fmtDayTime(r.lastSuccessAt) : <span className="muted">never</span>}</td>
                    <td className={`n ${r.consecutiveFailures > 0 ? 'bad' : ''}`}>{r.consecutiveFailures}</td>
                    <td className={`n ${r.openAlerts > 0 ? 'warn' : ''}`}>{r.openAlerts}</td>
                    <td className="n">{r.linkCount}{r.staleLinks > 0 && <span className="warn"> ▲{r.staleLinks}</span>}</td>
                  </tr>
                )
              })}
            </tbody>
          </table>
          {rows === null && !error && <p className="muted">Loading…</p>}
          <p className="muted small">Links between a train and a Jira issue, fix version or ServiceNow change are added on the train. ▲ marks links not refreshed for three intervals.</p>
        </div>
        {sel ? <Detail row={sel} canEdit={canEdit} onRow={put} onReload={load} /> : <aside className="detail" aria-label="Connector detail"><p className="muted">Select a connector.</p></aside>}
      </div>
    </section>
  )
}
