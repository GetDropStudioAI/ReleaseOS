import { useCallback, useEffect, useRef, useState } from 'react'
import { Plus } from 'lucide-react'
import { ApiError, type Me } from '../api.ts'
import { announce } from '../announce.tsx'
import { errMsg } from '../format.ts'
import { useAction } from '../useAction.ts'
import { isConflict } from '../Conflict.tsx'
import { canAdmin } from '../shell/nav.ts'
import { Button, ConfirmDialog, ConflictNotice, DataTable, DisabledAction, EmptyState, ErrorState, FilterSelect, LoadingState, NoPermissionState, Notice, PageHeading, Panel, PropertyList, SidePanel, StatusChip, Time, TwoLine, type Column } from '../ui'
import { Field, Problem, TextInput } from '../mgmt/forms.tsx'
import '../mgmt/mgmt.css'
import './tokens.css'
import { listTokens, makeToken, revokeToken, rotateToken } from './tokensApi.ts'
import {
  COPY_FALLBACK, DEFAULT_PRESET, EXPIRY_OPTIONS, KNOWN_SCOPES, REVOKED_REASON, defaultRotateExpiry, expiryIso, footerText, makeBody, makeProblem, presetsFor, revokeImpact, rotateBody,
  rotateImpact, rotateOptionsFor, scopesText, sortTokens, stateTone, whenText, type ApiToken, type ExpiryChoice,
} from './tokensLogic.ts'

/**
 * API tokens (REOS-162, Management group): the tokens that call the public API at /api/public/v1. RTE and Release Manager only (the API's Admin policy); anyone else gets the
 * no-permission page and the API is not called. Make and rotate return the secret once; it lives in this component's state only, is shown with a Copy button, and is dropped when the
 * panel is closed or another row is selected (never stored, logged or put in the URL). Rotate and Revoke ask first and send the row's Version as If-Match.
 */
type Panelled = { kind: 'none' } | { kind: 'make' } | { kind: 'row'; id: string }
/** The one-time secret and the token it belongs to. */
interface Shown { tokenId: string; secret: string; how: 'made' | 'rotated' }

const WHO = 'Release Train Engineers and Release Managers'

export function ApiTokens({ me }: { me: Me }) {
  if (!canAdmin(me.roles)) {
    return <div className="os-page"><PageHeading trail={['Management', 'API tokens']} title="API tokens" /><NoPermissionState what="manage API tokens" who={WHO} /></div>
  }
  return <TokensScreen me={me} />
}

function TokensScreen({ me }: { me: Me }) {
  const [rows, setRows] = useState<ApiToken[] | null>(null)
  const [known, setKnown] = useState<string[]>([...KNOWN_SCOPES])
  const [error, setError] = useState<string | null>(null)
  const [denied, setDenied] = useState(false)
  const [panel, setPanel] = useState<Panelled>({ kind: 'none' })
  const [shown, setShown] = useState<Shown | null>(null)
  const [confirm, setConfirm] = useState<{ kind: 'rotate' | 'revoke'; row: ApiToken } | null>(null)
  const [rotateExpiry, setRotateExpiry] = useState<ExpiryChoice>('keep')
  const [problem, setProblem] = useState<string | null>(null)
  const [conflict, setConflict] = useState<ApiError | null>(null)
  const { run, pending } = useAction()
  const makeButton = useRef<HTMLButtonElement>(null)

  const reload = useCallback(() => listTokens().then(l => { setRows(sortTokens(l.tokens)); if (l.scopes.length) setKnown(l.scopes); setError(null) })
    .catch(e => { if (e instanceof ApiError && e.status === 403) setDenied(true); else setError(errMsg(e, 'Could not load the API tokens.')) }), [])
  useEffect(() => { void reload() }, [reload])

  const put = (t: ApiToken) => setRows(rs => rs && sortTokens(rs.some(x => x.id === t.id) ? rs.map(x => (x.id === t.id ? t : x)) : [...rs, t]))
  const sel = panel.kind === 'row' ? rows?.find(r => r.id === panel.id) ?? null : null
  /** Every way out of a panel drops the secret: closing, opening Make, selecting another row. */
  const open = (p: Panelled) => {
    setProblem(null); setConflict(null)
    if (!(p.kind === 'row' && panel.kind === 'row' && p.id === panel.id)) setShown(null)
    setPanel(p)
  }
  const close = () => { setShown(null); setProblem(null); setConflict(null); setPanel({ kind: 'none' }) }

  const fail = (e: unknown) => { if (isConflict(e)) { setConflict(e); setProblem(null); void reload() } else { setConflict(null); setProblem(errMsg(e)) } }

  const make = (name: string, presetId: string, expiry: ExpiryChoice) => run('Making…', async () => {
    setProblem(null)
    try {
      const r = await makeToken(makeBody(name, presetId, expiry, new Date()))
      put(r.token); setShown({ tokenId: r.token.id, secret: r.secret, how: 'made' }); setPanel({ kind: 'row', id: r.token.id })
      announce(`Token ${r.token.name} made. Copy it now: it is shown once.`)
    } catch (e) { fail(e) }
  })
  const rotate = (t: ApiToken) => run('Rotating…', async () => {
    setProblem(null); setConflict(null)
    try {
      const r = await rotateToken(t.id, rotateBody(rotateExpiry, new Date()), t.version)
      put(r.token); setShown({ tokenId: r.token.id, secret: r.secret, how: 'rotated' }); setConfirm(null)
      announce(`Token ${r.token.name} rotated. Copy the new secret now: it is shown once.`)
    } catch (e) { setConfirm(null); setShown(null); fail(e) }
  })
  const revoke = (t: ApiToken) => run('Revoking…', async () => {
    setProblem(null); setConflict(null)
    try { const r = await revokeToken(t.id, t.version); put(r); setShown(null); setConfirm(null); announce(`Token ${t.name} revoked`) }
    catch (e) { setConfirm(null); fail(e) }
  })
  const ask = (kind: 'rotate' | 'revoke', row: ApiToken) => { setRotateExpiry(defaultRotateExpiry(row)); setConfirm({ kind, row }) }

  const columns: Column<ApiToken>[] = [
    { key: 'name', header: 'Name', cell: t => <TwoLine primary={t.name} secondary={<span className="mg-mono">{t.tokenPrefix}</span>} /> },
    { key: 'scopes', header: 'Access', cell: t => scopesText(t.scopes) },
    { key: 'state', header: 'State', cell: t => <StatusChip tone={stateTone(t.state)}>{t.state}</StatusChip> },
    { key: 'owner', header: 'Acts as', cell: t => t.ownerName ?? <span className="os-muted">Unknown</span> },
    { key: 'expires', header: 'Expires', cell: t => (t.expiresAt ? <Time at={t.expiresAt} date /> : <span className="os-muted">Never</span>) },
    { key: 'used', header: 'Last used', cell: t => (t.lastUsedAt ? <Time at={t.lastUsedAt} date /> : <span className="os-muted">Never used</span>) },
  ]

  if (denied) return <div className="os-page"><PageHeading trail={['Management', 'API tokens']} title="API tokens" /><NoPermissionState what="manage API tokens" who={WHO} /></div>

  const makeAction = <Button ref={makeButton} variant="primary" icon={Plus} onClick={() => open({ kind: 'make' })}>Make token</Button>
  let body
  if (!rows) body = error ? null : <LoadingState what="the API tokens" />
  else if (rows.length === 0) {
    body = <Panel title="API tokens"><EmptyState title="No API tokens yet" action={<Button variant="primary" icon={Plus} onClick={() => open({ kind: 'make' })}>Make token</Button>}>Make one to call the public API.</EmptyState></Panel>
  } else {
    body = (
      <Panel title="API tokens" subtitle="A token calls the public API as the person who made or last rotated it; the secret is never shown again" meta={`${rows.filter(r => r.state === 'Active').length} active`}>
        <div className="mg-tablewrap">
          <DataTable caption="API tokens" columns={columns} rows={rows} rowKey={t => t.id} selectedKey={panel.kind === 'row' ? panel.id : null} onSelect={t => open({ kind: 'row', id: t.id })}
            footer={footerText(rows)} empty="No API tokens." />
        </div>
      </Panel>
    )
  }

  return (
    <div className="os-page">
      <PageHeading trail={['Management', 'API tokens']} title="API tokens"
        description="Tokens call the public API at /api/public/v1. A token acts as the person who made or last rotated it, and its secret is shown once."
        actions={makeAction} />
      {error && <ErrorState what="The API tokens" message={error} onRetry={() => void reload()} />}
      {body && (
        <div className="mg-split">
          {body}
          <div className="mg-side">
            {panel.kind === 'make' && <MakePanel key="make" known={known} busy={pending !== null} problem={problem} onMake={make} onClose={() => { close(); makeButton.current?.focus() }} />}
            {panel.kind === 'row' && sel && (
              <DetailPanel key={sel.id} row={sel} shown={shown?.tokenId === sel.id ? shown : null} busy={pending !== null} problem={problem} conflict={conflict}
                onUseTheirs={() => { setConflict(null); void reload() }} onRotate={() => ask('rotate', sel)} onRevoke={() => ask('revoke', sel)} onClose={close} />
            )}
            {(panel.kind === 'none' || (panel.kind === 'row' && !sel)) && rows && rows.length > 0 && (
              <Panel title="Token detail"><EmptyState title="Select a token">Pick a row to see when it was made and last used, and to rotate or revoke it.</EmptyState></Panel>
            )}
          </div>
        </div>
      )}
      <ConfirmDialog open={confirm?.kind === 'revoke'} title={`Revoke ${confirm?.row.name ?? ''}?`} confirmLabel="Revoke token" pendingLabel="Revoking…" destructive pending={pending !== null}
        onCancel={() => setConfirm(null)} onConfirm={() => void revoke(confirm!.row)}>
        {confirm ? revokeImpact(confirm.row) : ''}
      </ConfirmDialog>
      <ConfirmDialog open={confirm?.kind === 'rotate'} title={`Rotate ${confirm?.row.name ?? ''}?`} confirmLabel="Rotate token" pendingLabel="Rotating…" pending={pending !== null}
        onCancel={() => setConfirm(null)} onConfirm={() => void rotate(confirm!.row)}>
        {confirm ? rotateImpact(confirm.row, me.name) : ''}
        {confirm && (
          <div className="tk-rotate-field mg-field">
            <span aria-hidden="true">Expires</span>
            <FilterSelect label="Expires" value={rotateExpiry} options={rotateOptionsFor(confirm.row)} onChange={v => setRotateExpiry(v as ExpiryChoice)} />
            {confirm.row.state === 'Expired' && <span className="mg-hint">This token has expired, so it needs a new expiry.</span>}
          </div>
        )}
      </ConfirmDialog>
    </div>
  )
}

/** The form that makes a token. Its fields are its own state; the secret comes back to the screen, not here. */
function MakePanel({ known, busy, problem, onMake, onClose }: { known: string[]; busy: boolean; problem: string | null; onMake: (name: string, presetId: string, expiry: ExpiryChoice) => void; onClose: () => void }) {
  const [name, setName] = useState('')
  const presets = presetsFor(known)
  const [access, setAccess] = useState(presets.some(p => p.id === DEFAULT_PRESET) ? DEFAULT_PRESET : presets[0]?.id ?? '')
  const [expiry, setExpiry] = useState<ExpiryChoice>('never')
  const reason = makeProblem(name, access)
  const at = expiryIso(expiry, new Date())
  return (
    <SidePanel title="Make token" subtitle="A name, what it may do, and when it stops working" closeLabel="Close the make token form" onClose={onClose}>
      <form className="mg-form" aria-label="Make token" onSubmit={e => { e.preventDefault(); if (!reason && !busy) onMake(name, access, expiry) }}>
        <Field label="Name" hint="Say what uses it, for example Build server."><TextInput value={name} onChange={e => setName(e.target.value)} maxLength={100} autoComplete="off" /></Field>
        <div className="tk-access"><span aria-hidden="true">Access</span>
          <FilterSelect label="Access" value={access} options={presets.map(p => ({ value: p.id, label: p.label }))} onChange={setAccess} /></div>
        <div className="tk-access"><span aria-hidden="true">Expires</span>
          <FilterSelect label="Expires" value={expiry} options={EXPIRY_OPTIONS} onChange={v => setExpiry(v as ExpiryChoice)} />
          <span className="mg-hint">{at ? `Stops working ${whenText(at, '')}.` : 'It keeps working until you revoke it.'}</span></div>
        <Problem message={problem} />
        <div className="mg-actions">
          {reason ? <DisabledAction variant="primary" reason={reason}>Make token</DisabledAction>
            : <Button variant="primary" type="submit" disabled={busy}>{busy ? 'Making…' : 'Make token'}</Button>}
        </div>
      </form>
    </SidePanel>
  )
}

function DetailPanel({ row, shown, busy, problem, conflict, onUseTheirs, onRotate, onRevoke, onClose }: {
  row: ApiToken; shown: Shown | null; busy: boolean; problem: string | null; conflict: ApiError | null
  onUseTheirs: () => void; onRotate: () => void; onRevoke: () => void; onClose: () => void
}) {
  const revoked = row.state === 'Revoked'
  return (
    <SidePanel title={row.name} subtitle={<StatusChip tone={stateTone(row.state)}>{row.state}</StatusChip>} closeLabel={`Close ${row.name}`} onClose={onClose}>
      <div className="mg-stack" role="group" aria-label="Token detail">
        {shown && <SecretNotice shown={shown} />}
        {problem && <Problem message={problem} />}
        {conflict && <ConflictNotice onUseTheirs={onUseTheirs} />}
        <PropertyList items={[
          { label: 'Id', value: row.id, mono: true },
          { label: 'Prefix', value: row.tokenPrefix, mono: true },
          { label: 'Access', value: scopesText(row.scopes) },
          { label: 'Made', value: <Time at={row.createdAt} date /> },
          { label: 'Rotated', value: row.rotatedAt ? <Time at={row.rotatedAt} date /> : 'Never rotated' },
          { label: 'Last used', value: row.lastUsedAt ? <Time at={row.lastUsedAt} date /> : 'Never used' },
          { label: 'Expires', value: row.expiresAt ? <Time at={row.expiresAt} date /> : 'Never' },
          ...(row.revokedAt ? [{ label: 'Revoked', value: <Time at={row.revokedAt} date /> }] : []),
          { label: 'Acts as', value: row.ownerName ?? 'Unknown' },
          { label: 'Version', value: String(row.version), mono: true },
        ]} />
        <div className="mg-actions">
          {revoked
            ? <><DisabledAction reason={REVOKED_REASON}>Rotate</DisabledAction><DisabledAction variant="destructive" reason={REVOKED_REASON}>Revoke</DisabledAction></>
            : <><Button disabled={busy} onClick={onRotate}>Rotate</Button><Button variant="destructive" disabled={busy} onClick={onRevoke}>Revoke</Button></>}
        </div>
      </div>
    </SidePanel>
  )
}

/** The one-time secret: a live Notice, the token in a selectable mono element, and Copy. Nothing here logs, stores or sends the secret anywhere. */
function SecretNotice({ shown }: { shown: Shown }) {
  const [copied, setCopied] = useState<'yes' | 'no' | null>(null)
  const copy = async () => {
    try {
      if (!navigator.clipboard?.writeText) throw new Error('no clipboard')
      await navigator.clipboard.writeText(shown.secret)
      setCopied('yes'); announce('Copied')
    } catch { setCopied('no'); announce(COPY_FALLBACK) }
  }
  return (
    <Notice tone="success" live title="Copy this token now">
      {shown.how === 'rotated' ? 'The token was rotated and the old secret has stopped working. ' : ''}It is shown once and cannot be recovered. If you lose it, rotate the token or make a new one.
      <p className="tk-secret" data-testid="token-secret">{shown.secret}</p>
      <div className="tk-secret-actions">
        <Button onClick={() => void copy()}>Copy token</Button>
        {copied === 'yes' && <span className="tk-copied">Copied</span>}
        {copied === 'no' && <span className="tk-copied">{COPY_FALLBACK}</span>}
      </div>
    </Notice>
  )
}
