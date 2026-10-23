import { useCallback, useEffect, useId, useRef, useState } from 'react'
import {
  ApiError, clearConnectorCredentials, getConnectors, saveConnector, setConnectorCredentials, syncConnector, testConnector,
  type ConnectorRow, type Me,
} from './api'
import { announce } from './announce'
import { errMsg } from './format'
import { useAction } from './useAction'
import { Button, ConfirmDialog, DataTable, DisabledAction, EmptyState, ErrorState, LoadingState, PageHeading, Panel, PropertyList, StatusChip, TwoLine, type Column, type Tone } from './ui'
import { Field, Problem, SelectInput, TextInput, When } from './mgmt/forms'

// REOS-39 / REOS-129 (G7): connectors admin as a table with a panel (docs/UI.md): state from ConnectorState, credentials entered in the panel and never
// displayed back. Status is a chip (dot and word); confirmation for removing credentials is a dialog (D41).

const STATUS: Record<string, { word: string; tone: Tone; hint: string }> = {
  Ok: { word: 'OK', tone: 'success', hint: 'The last cycle read every link.' },
  NeverSynced: { word: 'Never synced', tone: 'neutral', hint: 'Configured, waiting for the first cycle (or no links yet).' },
  NotConfigured: { word: 'Not configured', tone: 'neutral', hint: 'Enter the base URL and credentials.' },
  NoCredentials: { word: 'No credentials', tone: 'warning', hint: 'Enter credentials before links can be read.' },
  Failing: { word: 'Failing', tone: 'danger', hint: 'The last cycle could not read the system. See Sync health for the alert.' },
  Stalled: { word: 'Stalled', tone: 'danger', hint: 'No cycle has completed for three intervals: the poller is stopped or stuck.' },
  Disabled: { word: 'Disabled', tone: 'neutral', hint: 'Polling is turned off for this connector.' },
}
const statusOf = (s: string) => STATUS[s] ?? { word: s, tone: 'neutral' as Tone, hint: '' }

const LABELS: Record<string, { user: string; secret: string; kinds: { key: string; label: string }[] }> = {
  Jira: { user: 'Account email', secret: 'API token', kinds: [{ key: 'ApiToken', label: 'API token' }] },
  ServiceNow: { user: 'Client id', secret: 'Client secret', kinds: [{ key: 'OAuth', label: 'OAuth client credentials' }, { key: 'Basic', label: 'Basic authentication' }] },
}
const userLabel = (source: string, kind: string) => (kind === 'Basic' ? 'User name' : LABELS[source].user)
const secretLabel = (source: string, kind: string) => (kind === 'Basic' ? 'Password' : LABELS[source].secret)

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
  const [confirmClear, setConfirmClear] = useState(false)
  const userRef = useRef<HTMLInputElement>(null)
  const ids = useId()

  // a different connector (or a reload) resets what was typed; a stored credential is never put back into a field
  useEffect(() => { setUrl(row.baseUrl); setKind(row.credentialKind && labels.kinds.some(k => k.key === row.credentialKind) ? row.credentialKind : labels.kinds[0].key) }, [row.source, row.baseUrl, row.credentialKind, labels.kinds])
  useEffect(() => { setUser(''); setSecret(''); setProblem(null); setSaved(null); setTest(null); setSync(null); setConfirmClear(false) }, [row.source])

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
  const clearCreds = () => run('clear', async () => { onRow(await clearConnectorCredentials(row.source)); setConfirmClear(false); done('Credentials removed.'); requestAnimationFrame(() => userRef.current?.focus()) })
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
    <div role="region" aria-label={`${row.source} connector`} className="mg-side">
      <Panel title={row.source} subtitle={st.hint} meta={<StatusChip tone={st.tone}>{st.word}</StatusChip>}>
        <div className="mg-stack">
          <PropertyList items={[
            { label: 'Last success', value: <When iso={row.lastSuccessAt} /> },
            { label: 'Last cycle', value: <When iso={row.lastCycleCompletedAt} /> },
            { label: 'Failed cycles in a row', value: String(row.consecutiveFailures), mono: true },
            { label: 'Open alerts', value: String(row.openAlerts), mono: true },
            { label: 'Links', value: `${row.linkCount}${row.staleLinks > 0 ? ` · ▲ ${row.staleLinks} stale` : ''}${row.mismatchLinks > 0 ? ` · ◆ ${row.mismatchLinks} mismatch` : ''}`, mono: true },
          ]} />
          <Problem message={problem} />
          {saved && <p className="mg-ok" role="status"><span aria-hidden="true">✓ </span>{saved}</p>}
        </div>
      </Panel>

      {!canEdit ? <Panel title="Changes"><p className="mg-note">Only an RTE or Release Manager can change connectors.</p></Panel> : (
        <>
          <Panel title="Connection" subtitle={`https only. Read-only: this app never writes to ${row.source}, and stores keys, states and dates only.`}>
            <div className="mg-form">
              <Field label="Base URL">
                <TextInput value={url} onChange={e => setUrl(e.target.value)} placeholder={row.source === 'Jira' ? 'https://your-site.atlassian.net' : 'https://your-instance.service-now.com'} spellCheck={false} />
              </Field>
              <div className="mg-actions">
                <Button variant="primary" disabled={busy !== null || url.trim() === '' || url.trim() === row.baseUrl} onClick={saveUrl}>{busy === 'url' ? 'Saving…' : 'Save base URL'}</Button>
                {configured && <Button variant={row.isEnabled ? 'destructive' : 'secondary'} disabled={busy !== null} onClick={toggle}>{busy === 'enabled' ? 'Saving…' : row.isEnabled ? 'Disable polling' : 'Enable polling'}</Button>}
              </div>
            </div>
          </Panel>

          <Panel title="Credentials" subtitle={`${row.hasCredentials ? `Stored (${row.credentialKind}). Enter new values to replace them.` : 'None stored.'} Values are encrypted on the server and never displayed.`}>
            <form className="mg-form" onSubmit={e => { e.preventDefault(); void saveCreds() }} autoComplete="off">
              {labels.kinds.length > 1 && (
                <Field label="Type"><SelectInput value={kind} onChange={e => setKind(e.target.value)} options={labels.kinds.map(k => ({ value: k.key, label: k.label }))} /></Field>
              )}
              <Field label={userLabel(row.source, kind)}><TextInput ref={userRef} value={user} onChange={e => setUser(e.target.value)} autoComplete="off" spellCheck={false} /></Field>
              <Field label={secretLabel(row.source, kind)}><TextInput type="password" value={secret} onChange={e => setSecret(e.target.value)} autoComplete="new-password" /></Field>
              <div className="mg-actions">
                <Button variant="primary" type="submit" disabled={busy !== null || credsMissing} aria-describedby={credsMissing ? `${ids}-creds` : undefined}>{busy === 'creds' ? 'Saving…' : 'Save credentials'}</Button>
                {row.hasCredentials && <Button variant="destructive" disabled={busy !== null} onClick={() => setConfirmClear(true)}>Remove credentials</Button>}
              </div>
              {credsMissing && <p id={`${ids}-creds`} className="mg-note">Enter both values to save.</p>}
            </form>
          </Panel>

          <Panel title="Check" subtitle="Test the connection, or read the links now instead of waiting for the next cycle.">
            <div className="mg-stack">
              <div className="mg-actions">
                {testReason ? <DisabledAction reason={testReason}>Test connection</DisabledAction> : <Button disabled={busy !== null} onClick={doTest}>{busy === 'test' ? 'Testing…' : 'Test connection'}</Button>}
                {syncReason ? <DisabledAction reason={syncReason}>Sync now</DisabledAction> : <Button disabled={busy !== null} onClick={doSync}>{busy === 'sync' ? 'Syncing…' : 'Sync now'}</Button>}
              </div>
              {test && <p className={test.ok ? 'mg-ok' : 'mg-problem'} data-testid="test-result">{test.ok ? '✓' : '✗'} {test.text}</p>}
              {sync && <p className={sync.ok ? 'mg-ok' : 'mg-problem'} data-testid="sync-result">{sync.ok ? '✓' : '✗'} {sync.text}</p>}
            </div>
          </Panel>
        </>
      )}
      <ConfirmDialog open={confirmClear} title={`Remove ${row.source} credentials?`} confirmLabel="Remove credentials" pendingLabel="Removing…" destructive pending={busy === 'clear'}
        onCancel={() => setConfirmClear(false)} onConfirm={clearCreds}>
        Polling {row.source} stops working until new credentials are entered.
      </ConfirmDialog>
    </div>
  )
}

export function Connectors({ me }: { me: Me }) {
  const canEdit = me.roles.includes('RTE') || me.roles.includes('ReleaseManager')
  const [rows, setRows] = useState<ConnectorRow[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [selected, setSelected] = useState<string | null>(null)
  const load = useCallback(() => { getConnectors().then(r => { setRows(r); setError(null) }).catch(e => setError(errMsg(e, 'Could not load the connectors.'))) }, [])
  useEffect(() => { load(); const t = window.setInterval(load, 15_000); return () => window.clearInterval(t) }, [load])   // state is bookkeeping the poller keeps current

  const sel = rows?.find(r => r.source === selected) ?? null
  const put = (r: ConnectorRow) => setRows(rs => rs && rs.map(x => (x.source === r.source ? r : x)))

  const columns: Column<ConnectorRow>[] = [
    { key: 'source', header: 'Connector', cell: r => <TwoLine primary={r.source} secondary={r.baseUrl || 'No base URL yet'} /> },
    { key: 'status', header: 'Status', cell: r => { const s = statusOf(r.status); return <StatusChip tone={s.tone}>{s.word}</StatusChip> } },
    { key: 'creds', header: 'Credentials', cell: r => (r.hasCredentials ? `Stored (${r.credentialKind})` : 'None') },
    { key: 'last', header: 'Last success', cell: r => <When iso={r.lastSuccessAt} /> },
    { key: 'failures', header: 'Failures', align: 'end', mono: true, cell: r => r.consecutiveFailures },
    { key: 'alerts', header: 'Alerts', align: 'end', mono: true, cell: r => r.openAlerts },
    { key: 'links', header: 'Links', align: 'end', mono: true, cell: r => <>{r.linkCount}{r.staleLinks > 0 && <> · <span aria-hidden="true">▲</span> {r.staleLinks} stale</>}</> },
  ]

  return (
    <div className="os-page">
      <PageHeading trail={['Management', 'Connectors']} title="Connectors"
        description="Read-only links to Jira Cloud and ServiceNow. Polled every 5 minutes, every minute while a deployment window is open." />
      {error && <ErrorState what="The connectors" message={error} onRetry={load} />}
      {rows === null && !error && <LoadingState what="the connectors" />}
      {rows && (
        <div className="mg-split">
          <Panel title="Connectors" subtitle="State from the poller; “stale” marks links not refreshed for three intervals" meta={`${rows.length} configured`}
            footer="Links between a release and a Jira issue, fix version or ServiceNow change are added on the release.">
            <DataTable caption="Connectors" columns={columns} rows={rows} rowKey={r => r.source} selectedKey={selected} onSelect={r => setSelected(r.source)} empty="No connectors." />
          </Panel>
          {sel ? <Detail row={sel} canEdit={canEdit} onRow={put} onReload={load} />
            : <Panel title="Connector detail"><EmptyState title="Select a connector">Pick a row to see its state, set its address and credentials, and test it.</EmptyState></Panel>}
        </div>
      )}
    </div>
  )
}
