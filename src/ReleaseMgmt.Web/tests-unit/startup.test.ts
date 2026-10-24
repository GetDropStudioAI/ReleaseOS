// REOS-135: which addresses name a page. Anything else gets the unknown-path page instead of silently opening Releases.
import assert from 'node:assert/strict'
import { test } from 'node:test'
import { isKnownPath } from '../src/route.ts'

test('the root, every destination and the train and run paths are known', () => {
  for (const p of ['/', '', '/trains/abc', '/trains/abc/gates/g1', '/orchestration/abc/live', '/calendar', '/approvals', '/deployments/DEP-1', '/gallery']) assert.ok(isKnownPath(p), p)
})

test('anything else is an unknown path', () => {
  for (const p of ['/nope', '/trains-old', '/calendar2', '/a/b/c', '/%E0%A4%A']) assert.equal(isKnownPath(p), false, p)
})
