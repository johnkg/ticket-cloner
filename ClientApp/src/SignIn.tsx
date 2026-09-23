import type { AuthStatusResponse } from './api'

interface Props {
  /** Null while it is still being read, or if the call failed. */
  auth: AuthStatusResponse | null

  /** Where the sign-in starts. Navigated to, never fetched. */
  url: string

  onSignOut: () => void
}

/**
 * Sign-in, in the masthead beside the theme control.
 *
 * It lives up here rather than in a dialog because it is not an error to be
 * recovered from - it is the ordinary way in, available at any moment, and a
 * modal that appears when something has already failed is a worse place to put
 * the thing you were always going to press.
 *
 * Renders nothing at all when no app is registered: without one this is not a
 * choice anybody has, and a dead button is worse than no button.
 */
export default function SignIn({ auth, url, onSignOut }: Props) {
  if (!auth?.available) {
    return null
  }

  const both = auth.source.signedIn && auth.target.signedIn
  const some = auth.source.signedIn || auth.target.signedIn

  if (both) {
    return (
      <div className="signin">
        <button type="button" className="link" onClick={onSignOut}>
          Sign out
        </button>
      </div>
    )
  }

  return (
    <div className="signin">
      {/*
        * A POPUP, not a navigation in this tab.
        *
        * Signing in has to leave this origin, and script cannot follow a
        * cross-origin redirect - so it was a plain <a href>. But that unloads
        * the SPA, and with it whatever was selected, previewed or half-planned.
        * Signing in mid-run cost the whole preview.
        *
        * A popup leaves this window alone: it comes back to our own origin,
        * hands the outcome to the opener and closes itself. See App.tsx.
        *
        * Still an <a>, so it keeps the keyboard and middle-click behaviour of a
        * link, and so a blocked popup falls back to the old full navigation
        * rather than doing nothing at all.
        *
        * The popup is also where the SECOND consent happens. Atlassian grants
        * one site at a time, so the callback starts another round itself - that
        * navigation stays inside this window, and only the final outcome comes
        * back to the opener.
        */}
      <a
        className="button primary"
        href={url}
        onClick={(e) => {
          // Let a modified click (new tab, new window) do what it normally does.
          if (e.ctrlKey || e.metaKey || e.shiftKey || e.altKey || e.button !== 0) return

          const popup = window.open(url, 'ticketcloner-signin', 'width=620,height=760')

          // Blocked. Fall through to the href, which still works.
          if (popup === null) return

          e.preventDefault()
          popup.focus()
        }}
      >
        {some ? 'Finish signing in' : 'Sign in with Atlassian'}
      </a>
    </div>
  )
}
