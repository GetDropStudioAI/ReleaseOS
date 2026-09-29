export type ThemeChoice = 'auto' | 'light' | 'dark'
const KEY = 'releasemgmt.theme'

/** Applies the choice to <html data-theme>; 'auto' removes it so prefers-color-scheme decides (tokens.css). */
export function applyTheme(choice: ThemeChoice) {
  const root = document.documentElement
  if (choice === 'auto') root.removeAttribute('data-theme')
  else root.setAttribute('data-theme', choice)
}

export function loadTheme(): ThemeChoice {
  try {
    const v = localStorage.getItem(KEY)
    if (v === 'light' || v === 'dark' || v === 'auto') return v
  } catch { /* storage unavailable: fall back to auto */ }
  return 'auto'
}

export function saveTheme(choice: ThemeChoice) {
  try { localStorage.setItem(KEY, choice) } catch { /* not persisted; still applied for this session */ }
  applyTheme(choice)
}
