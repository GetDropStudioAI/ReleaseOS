import { useCallback, useEffect, useState } from 'react'
import { errMsg } from '../format.ts'
import { onCabChanged } from '../live.ts'
import { Button, DataTable, EmptyState, ErrorState, LoadingState, PageHeading, Panel, StatusChip, TimeRange, TwoLine, type Column } from '../ui'
import { listMeetings, type Meeting } from './workbenchApi.ts'
import { meetingStatusWord, meetingTone } from './workbenchLogic.ts'
import './cab.css'

/** The CAB meetings (REOS-179): open one to run it in the workbench. Scheduling and invitations are a separate story; this lists the meetings that exist. */
export function MeetingList({ onOpen, onBack }: { onOpen: (id: string) => void; onBack: () => void }) {
  const [rows, setRows] = useState<Meeting[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const reload = useCallback(() => { listMeetings().then(r => { setRows(r); setError(null) }).catch(e => setError(errMsg(e))) }, [])
  useEffect(() => { reload() }, [reload])
  useEffect(() => onCabChanged(reload), [reload])
  const columns: Column<Meeting>[] = [
    { key: 'meeting', header: 'Meeting', cell: r => <TwoLine primary={r.title} secondary={r.location ?? undefined} /> },
    { key: 'status', header: 'Status', cell: r => <StatusChip tone={meetingTone(r.status)}>{meetingStatusWord(r.status)}</StatusChip> },
    { key: 'when', header: 'When', cell: r => <TimeRange from={r.startsAt} to={r.endsAt} /> },
    { key: 'chair', header: 'Chair', cell: r => r.chair.name },
    { key: 'agenda', header: 'Agenda', cell: r => `${r.agenda.length} change${r.agenda.length === 1 ? '' : 's'}` },
    { key: 'people', header: 'Attendees', cell: r => String(r.attendees.length) },
  ]
  return (
    <div className="os-page">
      <PageHeading trail={['Workspace', 'Change advisory board', 'Meetings']} title="CAB meetings" description="Open a meeting to run its agenda, record decisions and keep the roll." actions={<Button onClick={onBack}>All changes</Button>} />
      <Panel title="Meetings" subtitle="Newest first">
        {error && !rows ? <ErrorState what="CAB meetings" message={error} onRetry={reload} />
          : !rows ? <LoadingState what="CAB meetings" />
          : rows.length === 0 ? <EmptyState title="No CAB meetings yet">There is no meeting to run. Once a meeting is scheduled it appears here.</EmptyState>
          : <DataTable caption="CAB meetings" columns={columns} rows={rows} rowKey={r => r.id} onSelect={r => onOpen(r.id)} empty="No meetings." />}
      </Panel>
    </div>
  )
}
