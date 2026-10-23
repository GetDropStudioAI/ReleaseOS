import { useEffect, useRef, useState } from 'react'
import { errMsg } from '../format'

/**
 * Load one resource for the release page: `data` is null until the first answer, `error` is the server's words (kept beside the last good `data`, so a
 * refresh that fails does not blank the screen), and `reload` asks again. `keys` are what the request depends on (the release id and the refresh key).
 */
export function useLoad<T>(load: () => Promise<T>, keys: unknown[]) {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [tick, setTick] = useState(0)
  const fn = useRef(load)
  fn.current = load
  useEffect(() => {
    let live = true
    fn.current().then(d => { if (live) { setData(d); setError(null) } }).catch(e => { if (live) setError(errMsg(e)) })
    return () => { live = false }
  }, [...keys, tick])   // eslint-disable-line react-hooks/exhaustive-deps
  return { data, error, reload: () => setTick(n => n + 1) }
}
