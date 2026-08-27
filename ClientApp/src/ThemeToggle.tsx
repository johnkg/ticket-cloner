import type { Theme } from './theme'

interface Props {
  theme: Theme
  onChange: (theme: Theme) => void
}

const OPTIONS: { key: Theme; label: string; glyph: string }[] = [
  { key: 'light', label: 'Light', glyph: '☀' },
  { key: 'dark', label: 'Dark', glyph: '☾' },
  { key: 'system', label: 'System', glyph: '◐' },
]

/**
 * Three states rather than two: someone who never chooses should keep following
 * the operating system when it switches at dusk.
 */
export default function ThemeToggle({ theme, onChange }: Props) {
  return (
    <div className="theme" role="group" aria-label="Colour theme">
      {OPTIONS.map(({ key, label, glyph }) => (
        <button
          key={key}
          type="button"
          className={theme === key ? 'swatch active' : 'swatch'}
          aria-pressed={theme === key}
          title={`${label} theme`}
          onClick={() => onChange(key)}
        >
          <span aria-hidden="true">{glyph}</span>
          <span className="sr-only">{label} theme</span>
        </button>
      ))}
    </div>
  )
}
