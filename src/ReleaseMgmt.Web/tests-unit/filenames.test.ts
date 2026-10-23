// CI builds on macOS and Windows, whose file systems ignore case: there an import without an extension can resolve to a module whose name differs only
// in case (`./shell/Approvals` found shell/approvals.ts before shell/Approvals.tsx and broke the build), while a Linux build passes. Names are compared
// without their module extension, as imports name them. This catches it on any platform.
import assert from 'node:assert/strict'
import { readdirSync } from 'node:fs'
import { join } from 'node:path'
import { test } from 'node:test'

const root = join(import.meta.dirname, '..')

function walk(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap(e => e.isDirectory() ? [join(dir, e.name), ...walk(join(dir, e.name))] : [join(dir, e.name)])
}

test('no two modules, files or folders differ only in letter case', () => {
  for (const top of ['src', 'tests-unit']) {
    const seen = new Map<string, string>()
    const clashes: string[] = []
    for (const path of walk(join(root, top))) {
      const key = path.replace(/\.(tsx?|jsx?|mts|cts)$/, '').toLowerCase()
      const other = seen.get(key)
      if (other !== undefined) clashes.push(`${other} and ${path}`)
      else seen.set(key, path)
    }
    assert.deepEqual(clashes, [], `names that clash on a case-insensitive file system under ${top}/`)
  }
})
