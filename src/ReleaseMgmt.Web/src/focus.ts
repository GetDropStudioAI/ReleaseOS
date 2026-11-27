import { useEffect, useRef } from 'react'

// REOS-152: Safari (macOS WebKit) does not focus a button when it is clicked, so after a mouse click `document.activeElement` is <body>
// and a dialog or drawer that remembers it cannot give focus back on close. Remember the last control pressed with a pointer as well.
let pressed: Element | null = null
if (typeof document !== 'undefined') {
  document.addEventListener('pointerdown', e => { pressed = e.target instanceof Element ? e.target.closest('button, a[href], summary, [role="button"], [tabindex]') : null }, true)
  document.addEventListener('keydown', () => { pressed = null }, true)
}

/** What to give focus back to when a dialog or drawer closes: the focused control, else (Safari, after a click) the control that was just pressed. */
export function focusOpener(): HTMLElement | null {
  const a = document.activeElement as HTMLElement | null
  if (a && a !== document.body && a.isConnected) return a
  return pressed instanceof HTMLElement && pressed.isConnected ? pressed : a
}

/** Focus the element when `when` becomes true: an inline form's first field, a confirm button, a drawer heading (WCAG 2.4.3). */
export function useFocusWhen<T extends HTMLElement>(when: boolean) {
  const ref = useRef<T>(null)
  useEffect(() => { if (when) ref.current?.focus() }, [when])
  return ref
}

/**
 * For a drawer or inline panel: on mount remember what had focus, on unmount give it back if focus fell to <body>.
 * The opener may have been re-rendered while the panel was open (its screen reloaded), so `fallback` names the trigger by selector:
 * when the remembered element is gone, focus goes to whatever matches it now.
 */
export function useRestoreFocus(fallback?: string) {
  useEffect(() => {
    const opener = focusOpener()
    return () => {
      // Decide on the next frame: at unmount time the browser may still report the removed heading as the active element.
      requestAnimationFrame(() => {
        const a = document.activeElement
        const lost = !a || a === document.body || !a.isConnected
        if (!lost) return
        const target = opener && opener !== document.body && opener.isConnected ? opener : fallback ? document.querySelector<HTMLElement>(fallback) : null
        target?.focus()
      })
    }
  }, [fallback])
}

/** Move focus without scrolling the page twice (the caller has already scrolled) and without animating when reduced motion is asked for. */
export function scrollAndFocus(el: HTMLElement | null) {
  if (!el) return
  const reduce = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches
  el.scrollIntoView({ behavior: reduce ? 'auto' : 'smooth', block: 'start' })
  if (!el.hasAttribute('tabindex')) el.setAttribute('tabindex', '-1')
  el.focus({ preventScroll: true })
}
