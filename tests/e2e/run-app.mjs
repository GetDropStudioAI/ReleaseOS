// Starts the app for the e2e run on a throwaway database, through the same start.py a person uses (so the run also exercises it).
import { spawn } from 'node:child_process'
import { rmSync, mkdirSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..')
const data = join(root, 'data', 'e2e')
rmSync(data, { recursive: true, force: true })
mkdirSync(data, { recursive: true })

const env = { ...process.env, Db__Path: join(data, 'app.db'), Backup__Directory: join(data, 'backups'), Seed__Demo: 'true' }
const py = process.platform === 'win32' ? ['python'] : ['python3']
const child = spawn(py[0], [join(root, 'start.py'), '--no-browser'], { cwd: root, env, stdio: 'inherit' })
// Playwright sends SIGTERM when the run ends. Ask the supervisor to stop both processes and exit only when it has, so no port is left
// held (a run started straight afterwards would otherwise reuse a server that is still shutting down).
let stopping = false
const stop = () => {
  if (stopping) return
  stopping = true
  spawn(py[0], [join(root, 'start.py'), 'stop'], { cwd: root, env, stdio: 'ignore' })
  setTimeout(() => process.exit(1), 60_000).unref()   // safety net: never hang the test run
}
process.on('SIGTERM', stop); process.on('SIGINT', stop)
child.on('exit', code => process.exit(code ?? 0))
