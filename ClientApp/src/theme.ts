export type Theme = 'light' | 'dark' | 'system'

const STORAGE_KEY = 'ticketcloner.theme'

/**
 * The choice, not the appearance. 'system' is the default and means "decide at
 * apply time" - see applyTheme.
 */
export function loadTheme(): Theme {
  try {
    const stored = window.localStorage.getItem(STORAGE_KEY)
    return stored === 'light' || stored === 'dark' ? stored : 'system'
  } catch {
    // Private browsing throws on access; the default is still usable.
    return 'system'
  }
}

export function saveTheme(theme: Theme): void {
  try {
    if (theme === 'system') {
      window.localStorage.removeItem(STORAGE_KEY)
    } else {
      window.localStorage.setItem(STORAGE_KEY, theme)
    }
  } catch {
    // Storage full or unavailable - the choice still applies for this session.
  }
}

/**
 * Applied to the root element rather than to a React tree, so the page
 * background and the native controls follow too. Removing the attribute hands
 * the decision back to prefers-color-scheme.
 */
export function applyTheme(theme: Theme): void {
  const root = document.documentElement

  if (theme === 'system') {
    root.removeAttribute('data-theme')
  } else {
    root.setAttribute('data-theme', theme)
  }
}
