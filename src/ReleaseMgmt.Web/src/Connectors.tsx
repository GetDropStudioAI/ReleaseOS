import { useCallback, useEffect, useId, useRef, useState } from 'react'
import {
  ApiError, clearConnectorCredentials, getConnectors, saveConnector, setConnectorCredentials, syncConnector, testConnector,
  type ConnectorRow, type Me,
} from './api'
import { announce } from './announce'
import { ConfirmInline } from './ConfirmInline'
import { errMsg } from './format'
import { useRowNav } from './rowNav'
import { fmtDayTime, zoneAbbr } from './time'
import { useAction } from './useAction'

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
  const { run: guard, pending: busy } = useAction()
  const [problem, setProblem] = useState<string | null>(null)
  const [saved, setSaved] = useState<string | null>(null)
  const [test, setTest] = useState<Result>(null)
  const [sync, setSync] = useState<Result>(null)
  const userRef = useRef<HTMLInputElement>(null)
  const ids = useId()

  // a different connector (or a reload) resets what was typed; a stored credential is never put back into a field
  useEffect(() => { setUrl(row.baseUrl); setKind(row.credentialKind && labels.kinds.some(k => k.key === row.credentialKind) ? row.credentialKind : labels.kinds[0].key) }, [row.source, row.baseUrl, row.credentialKind, labels.kinds])
  useEffect(() => { setUser(''); setSecret(''); setProblem(null); setSaved(null); setTest(null); setSync(null) }, [row.source])

  // One request at a time (a double click is ignored); `busy` names the running one so its button can say so.
  const run = (name: string, fn: () => Promise<void>) => guard(name, async () => {
    setProblem(null); setSaved(null)
    try { await fn() }
    catch (e) {
      if (e instanceof ApiError && e.status === 409) { setProblem('Someone else changed this connector. It has been reloaded; try again.'); onReload() }
      else setProblem(errMsg(e))
    }
  })
  const done = (text: string) => { setSaved(text); announce(text) }

  const saveUrl = () => run('url', async () => { onRow(await saveConnector(row.source, { baseUrl: url.trim() }, row.version)); done('Base URL saved.') })
  const toggle = () => run('enabled', async () => onRow(await saveConnector(row.source, { isEnabled: !row.isEnabled }, row.version)))
  const saveCreds = () => run('creds', async () => {
    const r = await setConnectorCredentials(row.source, { kind, username: user, secret })
    setUser(''); setSecret('')                      // cleared at once: nothing typed is kept after it is sent
    onRow(r); done('Credentials saved. They are stored encrypted and are not shown again.')
  })
  const clearCreds = () => run('clear', async () => { onRow(await clearConnectorCredentials(row.source)); done('Credentials removed.'); requestAnimationFrame(() => userRef.current?.focus()) })
  const doTest = () => run('test', async () => {
    setTest(null)
    try { const r = await testConnector(row.source); setTest({ ok: true, text: `Connected in ${r.elapsedMs} ms.` }); announce(`Test passed: connected in ${r.elapsedMs} ms.`) }
    catch (e) { if (e instanceof ApiError && e.status === 422) { setTest({ ok: false, text: errMsg(e) }); announce(`Test failed: ${errMsg(e)}`) } else throw e }
  })
  const doSync = () => run('sync', async () => {
    setSync(null)
    const r = await syncConnector(row.source)
    const res = { ok: r.outcome === 'Clean' || r.outcome === 'Idle', text: r.outcome === 'Clean' ? `Read ${r.linksChecked} link${r.linksChecked === 1 ? '' : 's'}.` : r.outcome === 'Idle' ? 'No links to read.' : `${r.outcome}${r.message ? `: ${r.message}` : ''}` }
    setSync(res); announce(`Sync ${res.ok ? 'finished' : 'failed'}: ${res.text}`)
    onReload()
  })

  const configured = row.baseUrl !== ''
  const testReason = !configured ? 'Save the base URL first.' : !row.hasCredentials ? 'Enter credentials first.' : null
  const syncReason = testReason ?? (!row.isEnabled ? 'Enable polling first.' : null)
  const credsMissing = user.trim() === '' || secret === ''

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
      {saved && <p className="ok">✓ {saved}</p>}

      {!canEdit ? <p className="muted">Only an RTE or Release Manager can change connectors.</p> : (
        <>
          <h3 className="cap">Connection</h3>
          <p><label>Base URL <input className="line block" value={url} onChange={e => setUrl(e.target.value)} placeholder={row.source === 'Jira' ? 'https://your-site.atlassian.net' : 'https://your-instance.service-now.com'} spellCheck={false} /></label></p>
          <p className="muted small">https only. Read-only: this app never writes to {row.source}, and stores keys, states and dates only.</p>
          <p className="actions">
            <button type="button" className="text" disabled={busy !== null || url.trim() === '' || url.trim() === row.baseUrl} onClick={saveUrl}>{busy === 'url' ? 'Saving…' : 'Save base URL'}</button>
            {configured && <button type="button" className={row.isEnabled ? 'text destructive' : 'text'} disabled={busy !== null} onClick={toggle}>{busy === 'enabled' ? 'Saving…' : row.isEnabled ? 'Disable polling' : 'Enable polling'}</button>}
          </p>

          <h3 className="cap">Credentials</h3>
          <p className="muted small">{row.hasCredentials ? `● Stored (${row.credentialKind}). Enter new values to replace them.` : '○ None stored.'} Values are encrypted on the server and never displayed.</p>
          {labels.kinds.length > 1 && (
            <p><label>Type <select className="line" value={kind} onChange={e => setKind(e.target.value)}>{labels.kinds.map(k => <option key={k.key} value={k.key}>{k.label}</option>)}</select></label></p>
          )}
          <form onSubmit={e => { e.preventDefault(); void saveCreds() }} autoComplete="off">
            <p><label>{userLabel(row.source, kind)} <input ref={userRef} className="line block" value={user} onChange={e => setUser(e.target.value)} autoComplete="off" spellCheck={false} /></label></p>
            <p><label>{secretLabel(row.source, kind)} <input className="line block" type="password" value={secret} onChange={e => setSecret(e.target.value)} autoComplete="new-password" /></label></p>
            <p className="actions">
              <button type="submit" className="text" disabled={busy !== null || credsMissing} aria-describedby={credsMissing ? `${ids}-creds` : undefined}>{busy === 'creds' ? 'Saving…' : 'Save credentials'}</button>
              {row.hasCredentials && <ConfirmInline key={row.source} label="Remove credentials" question={`Polling ${row.source} stops working until new credentials are entered.`}
                confirmLabel="Confirm remove" pendingLabel="Removing…" disabled={busy !== null} onConfirm={clearCreds} />}
            </p>
            {credsMissing && <p id={`${ids}-creds`} className="muted small">Enter both values to save.</p>}
          </form>

          <h3 className="cap">Check</h3>
          <p className="actions">
            <button type="button" className="text" disabled={busy !== null || testReason !== null} aria-describedby={testReason ? `${ids}-check` : undefined} onClick={doTest}>{busy === 'test' ? 'Testing…' : 'Test connection'}</button>
            <button type="button" className="text" disabled={busy !== null || syncReason !== null} aria-describedby={syncReason ? `${ids}-sync` : undefined} onClick={doSync}>{busy === 'sync' ? 'Syncing…' : 'Sync now'}</button>
          </p>
          {testReason ? <p id={`${ids}-check`} className="muted small">{testReason}</p> : syncReason && <p id={`${ids}-sync`} className="muted small">Sync now: {syncReason.toLowerCase()}</p>}
          {test && <p className={test.ok ? 'ok' : 'bad'} data-testid="test-result">{test.ok ? '✓' : '✗'} {test.text}</p>}
          {sync && <p className={sync.ok ? 'ok' : 'bad'} data-testid="sync-result">{sync.ok ? '✓' : '✗'} {sync.text}</p>}
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

  const pick = (i: number) => { if (rows?.[i]) setSelected(rows[i].source) }
  const [, setNavIndex, nav] = useRowNav(rows?.length ?? 0, pick, pick)
  const sel = rows?.find(r => r.source === selected) ?? null
  const put = (r: ConnectorRow) => setRows(rs => rs && rs.map(x => (x.source === r.source ? r : x)))

  return (
    <section>
      <h1>Connectors</h1>
      <p className="muted">Read-only links to Jira Cloud and ServiceNow. Polled every 5 minutes, every minute while a deployment window is open.</p>
      {error && <p className="bad" role="alert">✗ {error}</p>}
      <div className="split">
        <div>
          <table className="grid" aria-label="Connectors" {...nav}>
            <thead><tr><th>Connector</th><th>Status</th><th>Credentials</th><th>Last success ({zoneAbbr()})</th><th className="n">Failures</th><th className="n">Alerts</th><th className="n">Links</th></tr></thead>
            <tbody>
              {rows?.map((r, i) => {
                const st = statusOf(r.status)
                const on = r.source === selected
                return (
                  <tr key={r.source} className={on ? 'selected' : undefined} onClick={() => { setSelected(r.source); setNavIndex(i) }}>
                    <td><button type="button" className="plainlink" data-rownav aria-current={on ? 'true' : undefined} onClick={() => setSelected(r.source)}>{r.source}</button></td>
                    <td className={st.cls}>{st.glyph} {st.word}</td>
                    <td className={r.hasCredentials ? '' : 'muted'}>{r.hasCredentials ? `● stored (${r.credentialKind})` : '○ none'}</td>
                    <td className="mono">{r.lastSuccessAt ? fmtDayTime(r.lastSuccessAt) : <span className="muted">never</span>}</td>
                    <td className={`n ${r.consecutiveFailures > 0 ? 'bad' : ''}`}>{r.consecutiveFailures}</td>
                    <td className={`n ${r.openAlerts > 0 ? 'warn' : ''}`}>{r.openAlerts}</td>
                    <td className="n">{r.linkCount}{r.staleLinks > 0 && <span className="warn"> · <span aria-hidden="true">▲</span> {r.staleLinks} stale</span>}</td>
                  </tr>
                )
              })}
            </tbody>
          </table>
          {rows === null && !error && <p className="muted">Loading…</p>}
          <p className="muted small">Links between a train and a Jira issue, fix version or ServiceNow change are added on the train. “Stale” marks links not refreshed for three intervals.</p>
        </div>
        {sel ? <Detail row={sel} canEdit={canEdit} onRow={put} onReload={load} /> : <aside className="detail" aria-label="Connector detail"><p className="muted">Select a connector.</p></aside>}
      </div>
    </section>
  )
}
