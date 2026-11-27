import { useCallback, useEffect, useState } from 'react'
import { announce } from '../announce.tsx'
import { errMsg } from '../format.ts'
import { isConflict } from '../Conflict.tsx'
import { useAction } from '../useAction.ts'
import { DataTable, ErrorState, LoadingState, Notice, PageHeading, Panel, StatusChip, TwoLine, type Column } from '../ui'
import { SelectInput } from '../mgmt/forms.tsx'
import '../mgmt/mgmt.css'
import './email.css'
import { getPreferences, setPreference } from './emailApi.ts'
import { CATEGORY_HELP, OFF_NOTICE, WEBHOOK_NOTE, digestNote, modeOptions, prefOf, type Channel, type PreferencesResponse } from './emailLogic.ts'

/**
 * Notification settings (REOS-167, D61): where each kind of notification reaches the signed-in person. Any role, their own choices only. The in-app inbox is always on (escalation and My work depend on it), so it is a
 * fact in the table, not a choice; email can be off, immediate or in a daily digest; the team webhook can be off or immediate. Selects only (rule 9, no checkboxes or switches): a change is saved at once, announced, and
 * a stale screen gets the other tab's choice back (409) instead of overwriting it. The screen says plainly when email delivery is off on the host.
 */
export function NotificationSettings() {
  const [data, setData] = useState<PreferencesResponse | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [saved, setSaved] = useState<string | null>(null)
  const { run, pending } = useAction()

  const load = useCallback(() => getPreferences().then(d => { setData(d); setError(null) }).catch(e => setError(errMsg(e, 'Could not load your notification settings.'))), [])
  useEffect(() => { void load() }, [load])

  const change = (category: string, channel: Channel, label: string, mode: string, version: number) => run('Saving…', async () => {
    setProblem(null); setSaved(null)
    try {
      const p = await setPreference({ category, channel, mode, version })
      setData(d => d && { ...d, preferences: d.preferences.map(x => (x.category === p.category && x.channel === p.channel ? p : x)) })
      const msg = `${label}, ${channel === 'Email' ? 'email' : 'team webhook'}: ${modeOptions(channel).find(o => o.value === mode)?.label.toLowerCase()}. Saved.`
      setSaved(msg); announce(msg)
    } catch (e) {
      if (isConflict(e)) { setProblem('Another tab or device changed this setting. It is shown as it is now; choose again if you still want to change it.'); await load() }
      else setProblem(errMsg(e, 'Could not save the choice.'))
    }
  })

  const trail = ['Account', 'Notification settings']
  if (error) return <div className="os-page"><PageHeading trail={trail} title="Notification settings" /><ErrorState what="Your notification settings" message={error} onRetry={() => void load()} /></div>
  if (!data) return <div className="os-page"><PageHeading trail={trail} title="Notification settings" /><LoadingState what="your notification settings" /></div>

  const columns: Column<{ key: string; label: string }>[] = [
    { key: 'kind', header: 'Notification', cell: c => <TwoLine primary={c.label} secondary={CATEGORY_HELP[c.key]} /> },
    { key: 'inbox', header: 'Inbox', cell: () => <StatusChip tone="success">Always on</StatusChip> },
    ...(['Email', 'Webhook'] as Channel[]).map(ch => ({
      key: ch, header: ch === 'Email' ? 'Email' : 'Team webhook',
      cell: (c: { key: string; label: string }) => {
        const p = prefOf(data.preferences, c.key, ch)
        return p ? (
          <SelectInput className="em-select" aria-label={`${ch === 'Email' ? 'Email' : 'Team webhook'} for ${c.label}`} value={p.mode} disabled={pending !== null}
            options={modeOptions(ch)} onChange={e => void change(c.key, ch, c.label, e.target.value, p.version)} />
        ) : null
      },
    })),
  ]

  return (
    <div className="os-page">
      <PageHeading trail={trail} title="Notification settings" description="Choose how each kind of notification reaches you. The inbox always has all of them; email and the team webhook are yours to set." />
      <div className="em-page">
        {!data.emailEnabled && <Notice tone="warning" title="Email delivery is off">{OFF_NOTICE}</Notice>}
        {problem && <Notice tone="danger" title="Not saved" live>{problem}</Notice>}
        <Panel title="Channels" subtitle="One row per kind of notification" meta={saved ? undefined : undefined}>
          <div className="mg-tablewrap">
            <DataTable caption="Notification channels" columns={columns} rows={data.categories} rowKey={c => c.key} empty="No notification kinds." />
          </div>
          {saved && <p className="em-saved" role="status">{saved}</p>}
        </Panel>
        <Panel title="How this works" level={3}>
          <ul className="mg-list">
            <li>{digestNote(data.digestHourUtc)} Times in an email are UTC, with the local time of the host's display zone beside them.</li>
            <li>An email says what kind of notification it is, which release, and links to it. It never carries ticket text, notes or customer data: open the link for the details.</li>
            <li>{WEBHOOK_NOTE}</li>
          </ul>
        </Panel>
      </div>
    </div>
  )
}
