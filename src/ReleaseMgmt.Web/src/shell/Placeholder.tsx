import type { ReactNode } from 'react'
import { Button, Notice, PageHeading } from '../ui'
import { pageName, type View } from './nav.ts'

/** What each unbuilt destination will be, and where its work is done today. Copy for the descriptions follows the redesign. */
const PAGES: Partial<Record<View, { description: string; today: string; links: { view: View; label: string }[] }>> = {
  services: {
    description: 'Every service your releases ship, with its versions, owners and environments.',
    today: 'Products and their versions are recorded on each release today.',
    links: [{ view: 'trains', label: 'Releases' }],
  },
  environments: {
    description: 'Production, staging and their regions, with health and the releases in each.',
    today: 'Environments are not recorded yet.',
    links: [],
  },
}

/** A destination with no screen yet (REOS-94 acceptance): the page heading, a note that it is coming, and where the work is done today. Never a 404. */
export function Placeholder({ view, onGo }: { view: View; onGo: (v: View) => void }) {
  const name = pageName(view)
  const page = PAGES[view]
  return (
    <div className="os-page">
      <PageHeading trail={['Workspace', name]} title={name} description={page?.description} />
      <Notice tone="info" title="This screen is not built yet">
        <p>{page?.today}</p>
        {page && page.links.length > 0 && <p className="os-page-links">{page.links.map(l => <Button key={l.view} onClick={() => onGo(l.view)}>{l.label}</Button>)}</p>}
      </Notice>
    </div>
  )
}

export function Page({ children }: { children: ReactNode }) { return <div className="os-page">{children}</div> }
