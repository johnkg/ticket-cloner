import { useEffect, useState } from 'react'

/**
 * A preview of a dozen tickets runs to hundreds of rows, while every control
 * lives at the top of the page. This puts the top back within one click.
 */
export default function BackToTop() {
  const [shown, setShown] = useState(false)

  useEffect(() => {
    // Only once there is a screenful above, so it does not float over a page
    // that has nowhere to scroll back to.
    const onScroll = () => setShown(window.scrollY > window.innerHeight)

    onScroll()
    window.addEventListener('scroll', onScroll, { passive: true })
    window.addEventListener('resize', onScroll)

    return () => {
      window.removeEventListener('scroll', onScroll)
      window.removeEventListener('resize', onScroll)
    }
  }, [])

  if (!shown) {
    return null
  }

  const toTop = () => {
    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches

    window.scrollTo({ top: 0, behavior: reduced ? 'auto' : 'smooth' })
  }

  return (
    <button type="button" className="to-top" aria-label="Back to top" title="Back to top" onClick={toTop}>
      <svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true" focusable="false">
        <path
          d="M12 19V6M12 5l-6 6M12 5l6 6"
          fill="none"
          stroke="currentColor"
          strokeWidth="2"
          strokeLinecap="round"
          strokeLinejoin="round"
        />
      </svg>
    </button>
  )
}
