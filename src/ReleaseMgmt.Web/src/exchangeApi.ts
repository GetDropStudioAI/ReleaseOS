import { ApiError } from './api'

/** REOS-48/49: typed calls for the CSV import and the grid exports (kept out of api.ts so the shared file stays untouched). */
export interface ImportError { row: number; column: string; message: string }
export interface ImportCounts { new: number; updated: number; unchanged: number; errors: number }
export interface PlanRow { row: number; op: 'new' | 'updated' | 'unchanged' | 'error'; key: string; cells: Record<string, string>; before: Record<string, string> | null; changed: string[] }
export interface ImportPreview {
  jobId: string; kind: string; mode: string; fileName: string; sha256: string; byteCount: number; rowCount: number
  columns: string[]; skippedColumns: string[]; counts: ImportCounts; errors: ImportError[]; warnings: ImportError[]; decertifiesGates: string[]
  rows: PlanRow[]; rowsTotal: number; status: string; createdAt: string; expiresAt: string; version: number
}
export interface ImportJob {
  id: string; kind: string; mode: string | null; trainId: string | null; fileName: string; sha256: string; rowCount: number; errorCount: number; errors: ImportError[]
  counts: ImportCounts | null; status: 'Previewed' | 'Committed' | 'Rejected' | 'Expired'; uploadedByUserId: string; uploadedBy: string | null; createdAt: string; committedAt: string | null; version: number
}
export interface ImportCommit { jobId: string; kind: string; mode: string; inserted: number; updated: number; unchanged: number; decertifiedGates: string[]; version: number }
export interface KindInfo { kind: string; label: string; grid: string; plan: boolean; admin: boolean; key: string[]; columns: { name: string; required: boolean; type: string; allowed: string[] | null }[] }
export interface GridInfo { key: string; label: string; description: string; importKind: string | null; filters: string[]; formats: string[]; policy: string }

async function json<T>(r: Response): Promise<T> {
  if (!r.ok) throw new ApiError(r.status, await r.json().catch(() => null))
  return await r.json() as T
}

export const getKinds = () => fetch('/api/v1/imports/kinds').then(r => json<KindInfo[]>(r))
export const getGrids = () => fetch('/api/v1/exports/grids').then(r => json<GridInfo[]>(r))
export const getImports = () => fetch('/api/v1/imports?limit=15').then(r => json<ImportJob[]>(r))
export const getImportRows = (jobId: string, skip: number, take: number) => fetch(`/api/v1/imports/${encodeURIComponent(jobId)}/rows?skip=${skip}&take=${take}`).then(r => json<PlanRow[]>(r))

/** The file goes up as multipart, so a 5 MB CSV is never a JSON string; the server hashes and stores it. */
export function previewImport(kind: string, mode: string, file: File): Promise<ImportPreview> {
  const form = new FormData()
  form.append('file', file, file.name)
  return fetch(`/api/v1/imports/${encodeURIComponent(kind)}:preview?mode=${encodeURIComponent(mode)}`, { method: 'POST', body: form }).then(r => json<ImportPreview>(r))
}

/** Commit carries the job's Version as If-Match (a stale job is a 409), and the decertify acknowledgement. */
export const commitImport = (jobId: string, version: number, acknowledgeDecertify: boolean) =>
  fetch(`/api/v1/imports/${encodeURIComponent(jobId)}:commit`, {
    method: 'POST', headers: { 'Content-Type': 'application/json', 'If-Match': String(version) }, body: JSON.stringify({ acknowledgeDecertify }),
  }).then(r => json<ImportCommit>(r))

/** OWASP CSV-injection escape, the same rule as the server's CsvSafety: one leading apostrophe on = + - @ tab CR, and on an apostrophe. */
export const csvSafe = (s: string) => (s.length > 0 && '=+-@\t\r\''.includes(s[0]) ? `'${s}` : s)

/** The error table as a CSV the person can hand to whoever owns the file. */
export function errorReportCsv(errors: ImportError[]): string {
  const cell = (s: string) => (/[",\r\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s)
  return ['Row,Column,Message', ...errors.map(e => [String(e.row), csvSafe(e.column), csvSafe(e.message)].map(cell).join(','))].join('\r\n') + '\r\n'
}
