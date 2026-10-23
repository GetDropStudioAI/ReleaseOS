// REOS-93: the redesign's tokens come from the Figma export, the components never hard-code a colour, and chips take their tone from the word.
import assert from 'node:assert/strict'
import { readdirSync, readFileSync } from 'node:fs'
import { test } from 'node:test'
import { chipTone } from '../src/ui/tone.ts'
import { shortcutHint } from '../src/ui/keys.ts'

const read = (rel: string) => readFileSync(new URL(rel, import.meta.url), 'utf8')

test('every status word in the design gets its tone, and an unknown word is neutral', () => {
  const expected: Record<string, string[]> = {
    success: ['Successful', 'Passed', 'Approved', 'Healthy', 'Recorded'],
    info: ['Running', 'Live', 'Canary active'],
    warning: ['Pending', 'Blocked', 'Medium risk', 'Conditional go'],
    danger: ['Failed'],
    neutral: ['Planned', 'Queued', 'Something new'],
  }
  for (const [tone, words] of Object.entries(expected)) for (const w of words) assert.equal(chipTone(w), tone, w)
  assert.equal(chipTone('  running '), 'info')   // trimmed, any case
})

test('the shortcut hint names the key the platform uses', () => {
  assert.equal(shortcutHint('MacIntel'), '⌘ K')
  assert.equal(shortcutHint('Win32'), 'Ctrl K')
  assert.equal(shortcutHint('Linux x86_64'), 'Ctrl K')
})

test('tokens.css holds every Figma colour variable, light by default and dark under the dark theme', () => {
  const json = JSON.parse(read('../../../docs/ui/redesign-tokens.json')) as { variables: { css: string; light: string; dark: string }[] }
  const css = read('../../../docs/ui/tokens.css')
  const block = css.slice(css.indexOf('/* BEGIN redesign tokens'), css.indexOf('/* END redesign tokens */'))
  assert.ok(block.length > 0, 'the generated block is missing: run python3 tools/gen_tokens.py')
  const rule = (selector: string) => block.slice(block.indexOf(selector), block.indexOf('}', block.indexOf(selector)))
  const light = rule(':root, [data-theme="light"] {'), dark = rule('[data-theme="dark"] {')
  for (const v of json.variables) {
    assert.ok(light.includes(`${v.css}: ${v.light.toLowerCase()};`), `${v.css} light`)
    assert.ok(dark.includes(`${v.css}: ${v.dark.toLowerCase()};`), `${v.css} dark`)
  }
})

test('the components and the gallery use tokens only: no colour literal anywhere', () => {
  const files = readdirSync(new URL('../src/ui/', import.meta.url)).map(f => `../src/ui/${f}`).concat('../src/Gallery.tsx')
  const literal = /#[0-9a-fA-F]{3,8}\b|\brgba?\(|\bhsla?\(/
  for (const f of files) {
    const hits = read(f).split('\n').filter(l => literal.test(l) && !l.trim().startsWith('//') && !l.trim().startsWith('*'))
    assert.deepEqual(hits, [], f)
  }
})
