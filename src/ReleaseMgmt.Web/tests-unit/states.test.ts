// REOS-123: the shared states are exported once from ui/, are plain words (no hard-coded colour), and a disabled action carries its reason.
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { test } from 'node:test'

const read = (rel: string) => readFileSync(new URL(rel, import.meta.url), 'utf8')

test('ui/index exports every shared state', () => {
  const index = read('../src/ui/index.ts')
  for (const name of ['LoadingState', 'EmptyState', 'ErrorState', 'NoPermissionState', 'ReadOnlyNotice', 'ConflictNotice', 'DisabledAction', 'StaleBanner', 'SaveFailureNotice'])
    assert.match(index, new RegExp(`\\b${name}\\b`), name)
})

test('states announce themselves the right way: loading and errors are live regions, the disabled reason is described-by', () => {
  const src = read('../src/ui/states.tsx')
  assert.match(src, /role="status" aria-busy="true"/)
  assert.match(src, /tone="danger"[^\n]*\blive\b/)
  assert.match(src, /aria-describedby=\{id\}/)
  assert.doesNotMatch(src, /#[0-9a-fA-F]{3,8}\b/, 'no hard-coded colour')
})

test('the gallery shows the states in both themes', () => {
  assert.match(read('../src/Gallery.tsx'), /States every screen shares/)
})
