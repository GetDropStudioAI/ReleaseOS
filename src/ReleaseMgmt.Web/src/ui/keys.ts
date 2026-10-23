/** "⌘ K" on Apple platforms, "Ctrl K" elsewhere: the hint shows the key the person will actually press. */
export function shortcutHint(platform = typeof navigator === 'undefined' ? '' : navigator.platform): string {
  return /Mac|iPhone|iPad/i.test(platform) ? '⌘ K' : 'Ctrl K'
}
