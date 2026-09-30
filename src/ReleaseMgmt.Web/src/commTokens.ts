// Client-side read of {Token} placeholders, for highlighting while typing. Same grammar as the server's TokenParser
// (ReleaseMgmt.Domain/Services/CommTokens.cs): a token is { + letters/digits/underscore starting with a letter + }; anything else between
// braces, an unclosed { or a stray } is a problem. The server stays the authority: the preview call returns its own tokenErrors.

export type Seg =
  | { kind: 'text'; text: string }
  | { kind: 'token'; text: string }
  | { kind: 'bad'; text: string }

export interface Problem { line: number; column: number; token: string; message: string }
export interface Scan { segs: Seg[]; problems: Problem[]; known: number }

const ident = /^[A-Za-z][A-Za-z0-9_]*$/

function distance(a: string, b: string): number {
  let prev = Array.from({ length: b.length + 1 }, (_, j) => j)
  for (let i = 1; i <= a.length; i++) {
    const cur = [i]
    for (let j = 1; j <= b.length; j++) cur[j] = Math.min(cur[j - 1] + 1, prev[j] + 1, prev[j - 1] + (a[i - 1] === b[j - 1] ? 0 : 1))
    prev = cur
  }
  return prev[b.length]
}

export function suggest(name: string, allow: string[]): string | null {
  let best: string | null = null, bestD = 3
  for (const t of allow) { const d = distance(t.toLowerCase(), name.toLowerCase()); if (d < bestD) { bestD = d; best = t } }
  return best
}

export function scan(text: string, allow: string[]): Scan {
  const ok = new Set(allow)
  const segs: Seg[] = [], problems: Problem[] = []
  let lit = '', line = 1, col = 1, known = 0
  const flush = () => { if (lit) { segs.push({ kind: 'text', text: lit }); lit = '' } }
  const adv = (s: string) => { for (const ch of s) { if (ch === '\n') { line++; col = 1 } else col++ } }
  let i = 0
  while (i < text.length) {
    const c = text[i]
    if (c === '{') {
      const close = text.indexOf('}', i + 1), open = text.indexOf('{', i + 1)
      if (close < 0 || (open >= 0 && open < close)) {
        problems.push({ line, column: col, token: '{', message: `'{' at line ${line}, column ${col} has no closing '}'. Braces are only for tokens; there is no way to print a literal brace` })
        flush(); segs.push({ kind: 'bad', text: '{' }); adv('{'); i++; continue
      }
      const raw = text.slice(i, close + 1), name = raw.slice(1, -1)
      flush()
      if (ident.test(name) && ok.has(name)) { segs.push({ kind: 'token', text: raw }); known++ }
      else {
        const s = ident.test(name) ? suggest(name, allow) : null
        problems.push({
          line, column: col, token: raw,
          message: ident.test(name)
            ? `Unknown token ${raw} at line ${line}, column ${col}${s ? `. Did you mean {${s}}?` : ''}`
            : `${raw} at line ${line}, column ${col} is not a token. A token is a name in braces with no spaces, for example {Status}`,
        })
        segs.push({ kind: 'bad', text: raw })
      }
      adv(raw); i = close + 1; continue
    }
    if (c === '}') {
      problems.push({ line, column: col, token: '}', message: `'}' at line ${line}, column ${col} closes nothing. Braces are only for tokens; there is no way to print a literal brace` })
      flush(); segs.push({ kind: 'bad', text: '}' }); adv('}'); i++; continue
    }
    lit += c; adv(c); i++
  }
  flush()
  return { segs, problems, known }
}
