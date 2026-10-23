import { useState, type ReactNode } from 'react'
import { CalendarDays, Pencil, Plus, RotateCcw, ShieldCheck } from 'lucide-react'
import tokens from '../../../docs/ui/redesign-tokens.json'
import {
  Button, ConfirmDialog, CountBadge, DataTable, FilterSelect, MetricCard, Notice, Panel, ProgressBar, PropertyList, SearchField, StatusChip,
  TabPanel, Tabs, TwoLine, type Column,
} from './ui'

/**
 * The living component gallery (REOS-93, route /gallery): every redesign component and variant, once in the light theme and once in the dark,
 * side by side, for review against the Figma frames. The data is illustrative, as in the design. Each half sets data-theme, so the tokens
 * resolve for that theme whatever the app theme is.
 */
export function Gallery() {
  return (
    <div className="os-gallery-page">
      <h1 className="sr-only">Component gallery</h1>
      <div className="os-gallery">
        <Specimens theme="light" />
        <Specimens theme="dark" />
      </div>
    </div>
  )
}

interface Release { id: string; name: string; team: string; services: number; status: string; env: string; owner: string; window: string }
const RELEASES: Release[] = [
  { id: 'RLS-284', name: 'Checkout resilience', team: 'Commerce', services: 6, status: 'Running', env: 'Production', owner: 'Maya Chen', window: 'Today, 14:00' },
  { id: 'RLS-285', name: 'Identity federation', team: 'Identity', services: 4, status: 'Blocked', env: 'Production', owner: 'Alex Rivera', window: 'Today, 16:00' },
  { id: 'RLS-286', name: 'Autumn storefront', team: 'Experience', services: 8, status: 'Planned', env: 'Production', owner: 'Noah Patel', window: '26 Oct, 09:00' },
  { id: 'RLS-283', name: 'Ledger reconciliation', team: 'Finance', services: 3, status: 'Successful', env: 'Production', owner: 'Priya Shah', window: 'Today, 12:18' },
  { id: 'RLS-282', name: 'Search index v4', team: 'Discovery', services: 2, status: 'Failed', env: 'Staging', owner: 'Eli Brooks', window: 'Today, 11:06' },
]
const COLUMNS: Column<Release>[] = [
  { key: 'release', header: 'Release', cell: r => <TwoLine primary={r.name} secondary={`${r.id} · ${r.team} · ${r.services} services`} /> },
  { key: 'status', header: 'Status', cell: r => <StatusChip>{r.status}</StatusChip> },
  { key: 'env', header: 'Environment', cell: r => r.env },
  { key: 'owner', header: 'Owner', cell: r => r.owner },
  { key: 'window', header: 'Window', cell: r => r.window, mono: true },
]
const CHIPS = ['Successful', 'Passed', 'Approved', 'Healthy', 'Recorded', 'Running', 'Live', 'Canary active', 'Pending', 'Blocked', 'Medium risk', 'Conditional go', 'Failed', 'Planned', 'Queued']
const TABS = [{ id: 'overview', label: 'Overview & readiness' }, { id: 'services', label: 'Services', count: 6 }, { id: 'deps', label: 'Dependencies', count: 3 }, { id: 'approvals', label: 'Approvals', count: 4 }, { id: 'activity', label: 'Activity' }]

function Specimens({ theme }: { theme: 'light' | 'dark' }) {
  const [tab, setTab] = useState('overview')
  const [team, setTeam] = useState('all')
  const [query, setQuery] = useState('')
  const [selected, setSelected] = useState<string | null>('RLS-284')
  const [confirming, setConfirming] = useState(false)
  const label = theme === 'light' ? 'Light' : 'Dark'
  return (
    <section data-theme={theme} className="os-gallery-theme os-surface" aria-label={`${label} theme`}>
      <h2>{label}</h2>

      <Section title="Buttons">
        <div className="os-gallery-row">
          <Button variant="primary" icon={Plus}>Create release</Button>
          <Button icon={CalendarDays}>Last 14 days</Button>
          <Button icon={Pencil}>Edit release</Button>
          <Button variant="destructive" icon={RotateCcw} onClick={() => setConfirming(true)}>Rollback</Button>
          <Button variant="primary" disabled>Approve &amp; record</Button>
        </div>
        <ConfirmDialog open={confirming} title="Roll back checkout-api in Production EU?" confirmLabel="Roll back" destructive
          onConfirm={() => setConfirming(false)} onCancel={() => setConfirming(false)}>
          Traffic returns to v3.17.2. The rollback is recorded in the audit history.
        </ConfirmDialog>
      </Section>

      <Section title="Status chips">
        <div className="os-gallery-row">{CHIPS.map(c => <StatusChip key={c}>{c}</StatusChip>)}</div>
      </Section>

      <Section title="Count badge">
        <div className="os-gallery-row"><span>Approvals</span><CountBadge count={4} label="approvals pending" /><span>Zero shows nothing:</span><CountBadge count={0} label="approvals pending" /></div>
      </Section>

      <Section title="Metric cards">
        <div className="os-gallery-grid">
          <MetricCard label="Active releases" value="24" detail="Across 8 teams · 42 services" />
          <MetricCard label="Running now" value="3" detail="2 production · 1 staging" tone="accent" />
          <MetricCard label="Need attention" value="4" detail="2 gate failures · 2 approvals" tone="warning" />
          <MetricCard label="p95 latency" value="184" unit="ms" detail="↑ 1.2% vs. previous period" tone="success" />
        </div>
      </Section>

      <Section title="Tabs">
        <Tabs label="Release sections" group={`${theme}-release`} tabs={TABS} value={tab} onChange={setTab} />
        <TabPanel id={tab} group={`${theme}-release`}><p>{TABS.find(t => t.id === tab)?.label} content.</p></TabPanel>
      </Section>

      <Section title="Operational notices">
        <Notice tone="warning" title="EU rollout is running. Production US is on hold." action={<a className="os-link" href="#blockers">Review blockers →</a>}>
          18 of 20 readiness checks passed. SRE approval and the US change record must clear before the next wave.
        </Notice>
        <Notice tone="danger" title="Security gate failed">Critical CVE in auth-proxy:2.8.0</Notice>
        <Notice tone="success" title="All health gates passing">Auto-promote on</Notice>
        <Notice tone="info" title="Next change freeze">30 Oct → 02 Nov</Notice>
      </Section>

      <Section title="Progress bar">
        <ProgressBar value={0.9} label="Readiness" /><p className="os-gallery-caption">90% ready</p>
        <ProgressBar value={0.25} label="Canary traffic" tone="info" />
      </Section>

      <Section title="Panel, data table, filter and search">
        <div className="os-gallery-row">
          <FilterSelect label="Team" value={team} onChange={setTeam} options={[{ value: 'all', label: 'All teams' }, { value: 'platform', label: 'Platform' }, { value: 'commerce', label: 'Commerce' }]} />
          <SearchField label="Search releases" placeholder="Search releases, services…" value={query} onChange={setQuery} shortcut />
        </div>
        <Panel level={4} title="Release activity" meta={<a className="os-link" href="#releases">View all releases →</a>}>
          <DataTable caption="Release activity" columns={COLUMNS} rows={RELEASES} rowKey={r => r.id} selectedKey={selected} onSelect={r => setSelected(r.id)}
            footer={<><span>Showing 5 of 24 active and recent releases</span><span className="os-mono">SYNCED 14:32:08</span></>} />
        </Panel>
      </Section>

      <Section title="Property list and dark panel">
        <div className="os-gallery-grid">
          <Panel level={4} title="Release properties">
            <PropertyList items={[
              { label: 'Target environments', value: 'Production EU + US' }, { label: 'Change record', value: 'CHG-1042', mono: true },
              { label: 'Release branch', value: 'release/2026.10.23', mono: true }, { label: 'Rollback plan', value: 'Validated · 8 min RTO' },
            ]} />
          </Panel>
          <Panel level={4} inverse title="Proceed in EU. Hold production US." subtitle="Go / no-go" meta={<StatusChip>Conditional go</StatusChip>}
            footer={<span>Maya Chen · 14:05 UTC</span>}>
            <p>Canary health is within SLO. The US wave remains locked until both outstanding checks are complete.</p>
            <Button variant="primary" icon={ShieldCheck}>Review approvals</Button>
          </Panel>
        </div>
      </Section>

      <Section title="Colour tokens">
        <div className="os-swatches">
          {tokens.variables.map(v => (
            <div key={v.name} className="os-swatch"><span className="os-swatch-chip" style={{ background: `var(${v.css})` }} />{v.name}</div>
          ))}
        </div>
      </Section>
    </section>
  )
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return <div className="os-gallery-section"><h3>{title}</h3>{children}</div>
}
