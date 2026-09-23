import { useCallback, useEffect, useState } from 'react'
import {
  api,
  ApiError,
  authStartUrl,
  type BoardSprint,
  type ApplyResponse,
  type AuthStatusResponse,
  type ConfigResponse,
  type MappingPlan,
  type TargetIssueType,
  type Tenant,
} from './api'
import ApplyResults from './ApplyResults'
import BackToTop from './BackToTop'
import ConnectionPanel, { type ConnectionState } from './ConnectionPanel'
import SignIn from './SignIn'
import { sprintDates } from './sprints'

/**
 * The callback comes back as a browser navigation carrying a short reason, so
 * the sentence a person reads is written here rather than in a redirect URL -
 * Atlassian's own wording would otherwise end up in browser history.
 */
const SIGN_IN_FAILURES: Record<string, string> = {
  denied: 'Sign-in was cancelled on Atlassian.',
  state: 'That sign-in did not match one started here. Start again from this page.',
  nocode: 'Atlassian sent back no authorization code.',
  'wrong-site':
    'That sign-in granted access to an Atlassian site this tool does not use. ' +
    'Sign in again and choose one of the two sites named above.',
  resources: 'Could not read which Atlassian sites that sign-in reaches.',
  oauth: 'Atlassian refused the sign-in.',
}

/**
 * What the callback came back with, read off the address bar.
 *
 * Pure, deliberately: it is a state initialiser, so it must not touch history.
 * Null means this page load did not follow a sign-in.
 */
function signInOutcome(): { ok: boolean; text: string } | null {
  const flags = new URLSearchParams(window.location.search)
  const auth = flags.get('auth')

  if (auth === null) {
    return null
  }

  if (auth === 'ok') {
    return { ok: true, text: 'Signed in to both Atlassian sites.' }
  }

  if (auth === 'partial') {
    // Both consents happened and one site still is not on the grant, which
    // means the same one was chosen twice.
    return {
      ok: false,
      text:
        `Signed in, but the grant still does not reach the ${flags.get('missing') ?? 'other'} ` +
        'site. Sign in again and choose the other one when Atlassian asks.',
    }
  }

  return {
    ok: false,
    text: SIGN_IN_FAILURES[flags.get('reason') ?? ''] ?? 'That sign-in did not complete.',
  }
}
import IssuePicker from './IssuePicker'
import PreviewTable from './PreviewTable'
import ThemeToggle from './ThemeToggle'
import { applyTheme, loadTheme, saveTheme, type Theme } from './theme'

const CHECKING: Record<Tenant, ConnectionState> = {
  source: { status: 'checking' },
  target: { status: 'checking' },
}

export default function App() {
  const [theme, setTheme] = useState<Theme>(loadTheme)
  const [config, setConfig] = useState<ConfigResponse | null>(null)
  const [connections, setConnections] = useState<Record<Tenant, ConnectionState>>(CHECKING)
  const [auth, setAuth] = useState<AuthStatusResponse | null>(null)

  // Read once, before the first render, rather than in an effect - there is
  // then no render to correct afterwards.
  const [outcome] = useState(signInOutcome)

  // What the last sign-in attempt did, if this page load followed one.
  const [signIn, setSignIn] = useState<{ ok: boolean; text: string } | null>(outcome)

  /*
   * When THIS window is the sign-in popup, it exists only to carry the outcome
   * back. Hand it to the opener and close, so the tab that started the sign-in
   * keeps whatever was selected, previewed or half-planned.
   */
  const inPopup = outcome !== null && window.opener !== null && window.opener !== window

  const [projects, setProjects] = useState<Record<Tenant, string>>({ source: '', target: '' })
  const [selected, setSelected] = useState<string[]>([])
  const [issueTypes, setIssueTypes] = useState<TargetIssueType[]>([])
  const [issueTypeOverride, setIssueTypeOverride] = useState('')

  const [plans, setPlans] = useState<MappingPlan[]>([])

  // Source keys ticked for copying. Anything already on the target starts
  // unticked, so re-copying it is a deliberate act rather than the default.
  const [chosen, setChosen] = useState<string[]>([])

  // Source epic keys approved for creation. Ticked by default, because a copy
  // landing under the right epic is normally the point of asking.
  const [epicsToCreate, setEpicsToCreate] = useState<string[]>([])
  const [planErrors, setPlanErrors] = useState<{ key: string; message: string }[]>([])
  const [results, setResults] = useState<ApplyResponse | null>(null)

  const [includeComments, setIncludeComments] = useState(true)
  const [includeAttachments, setIncludeAttachments] = useState(true)

  // Which target sprint a copy lands in: '' for none, 'active' for whichever
  // is current when the run happens, or a sprint id chosen from the board's
  // list. None by default - a copy landing in the sprint somebody is working
  // right now is a decision, so it is asked for rather than assumed.
  const [sprintChoice, setSprintChoice] = useState('')
  const [targetSprints, setTargetSprints] = useState<BoardSprint[] | null>(null)
  const [sprintsFailed, setSprintsFailed] = useState(false)
  const [confirming, setConfirming] = useState(false)

  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    applyTheme(theme)
    saveTheme(theme)
  }, [theme])

  /** Both tenants at once - a half-configured instance should say which half. */
  const connect = useCallback(async () => {
    setConnections(CHECKING)

    const check = async (tenant: Tenant): Promise<ConnectionState> => {
      try {
        const identity = await api.whoAmI(tenant)

        return {
          status: 'connected',
          displayName: identity.displayName,
          accountId: identity.accountId,
          emailVisible: identity.emailVisible,
        }
      } catch (failure) {
        const problem = failure as ApiError

        return {
          status: 'failed',
          message: problem.message,
          credentialProblem: problem instanceof ApiError && problem.isCredentialProblem,
        }
      }
    }

    const [source, target] = await Promise.all([check('source'), check('target')])
    setConnections({ source, target })
  }, [])

  /** Separate from connect(): being signed in and being able to reach the
      tenant are different questions, and the panel shows both. */
  const refreshAuth = useCallback(
    () =>
      api
        .auth()
        .then(setAuth)
        // An instance without the endpoint is not an error worth showing; the
        // whole sign-in half simply stays hidden.
        .catch(() => setAuth(null)),
    [],
  )

  useEffect(() => {
    // Nothing below is worth doing in a window that is closing itself.
    if (inPopup) return

    api
      .config()
      .then(async (loaded) => {
        setConfig(loaded)
        setProjects({
          source: loaded.source.projectKey,
          target: loaded.target.projectKey,
        })

        await refreshAuth()
        return connect()
      })
      .catch((failure: Error) => setError(failure.message))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [connect, refreshAuth])

  // Runs before anything else asks the server a question - the window is going
  // away, and those answers belong to the opener.
  useEffect(() => {
    if (!inPopup) return

    // Targeted at our own origin, and the receiver checks it again: the popup
    // has just come back from an authorisation server, and a message from that
    // hop must not be mistaken for one of ours.
    window.opener?.postMessage({ ticketCloner: 'signin', ...outcome }, window.location.origin)
    window.close()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // The other half: a popup finished, so re-read who is signed in rather than
  // reloading the page. Nothing else in this window is touched.
  useEffect(() => {
    const onMessage = (event: MessageEvent) => {
      if (event.origin !== window.location.origin) return

      const data = event.data as { ticketCloner?: string; ok?: boolean; text?: string }

      if (data?.ticketCloner !== 'signin') return

      if (data.text !== undefined) setSignIn({ ok: data.ok === true, text: data.text })

      void refreshAuth()
      void connect()
    }

    window.addEventListener('message', onMessage)
    return () => window.removeEventListener('message', onMessage)
  }, [connect, refreshAuth])

  // The flags have been read; leaving them on the address bar means a refresh
  // replays a message about something that already happened.
  useEffect(() => {
    if (inPopup || outcome === null) return

    window.history.replaceState({}, '', window.location.pathname)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const endSignIn = async () => {
    try {
      await api.signOut()
      setSignIn(null)
      await refreshAuth()
      await connect()
    } catch (failure) {
      setError((failure as Error).message)
    }
  }

  const targetConnected = connections.target.status === 'connected'

  const connected = connections.source.status === 'connected' && targetConnected

  // Loaded the moment the target answers, NOT on focus. It used to be lazy on
  // the <select>'s onFocus, and a click focuses and opens the native picker in
  // one gesture - so the picker rendered before the list arrived, and a native
  // <select> does not redraw a picker that is already open. First click:
  // placeholder only. Second click: fine. Filling the list before anybody
  // reaches for it is the whole fix.
  //
  // Not cleared on disconnect: the list is harmless while the target is
  // unreachable, and reconnecting (a sign-out and back in) flips
  // targetConnected again, which reloads it for whoever is signed in now.
  useEffect(() => {
    if (!targetConnected) return

    let stale = false

    api
      .targetIssueTypes()
      .then((types) => {
        if (!stale) setIssueTypes(types)
      })
      .catch((failure: Error) => {
        if (!stale) setError(failure.message)
      })

    return () => {
      stale = true
    }
  }, [targetConnected])

  // The target board's sprints, for the Copy step's dropdown. Same timing and
  // for the same reason as the issue types above: a native <select> that fills
  // after it has opened shows nothing.
  useEffect(() => {
    if (!targetConnected) return

    let stale = false

    api
      .targetSprints()
      .then((listed) => {
        if (stale) return
        setTargetSprints(listed.sprints)
        setSprintsFailed(false)
      })
      .catch(() => {
        // Not a page-level error: everything else still works, and the
        // dropdown says why it is short.
        if (!stale) setSprintsFailed(true)
      })

    return () => {
      stale = true
    }
  }, [targetConnected])

  const activeSprints = (targetSprints ?? []).filter((sprint) => sprint.state === 'active')

  const creatable = plans.filter((plan) => plan.canCreate)
  const blocked = plans.filter((plan) => !plan.canCreate)

  const picked = creatable.filter((plan) => chosen.includes(plan.sourceKey))

  /** One row per epic, however many picked tickets happen to share it. */
  const missingEpics = [
    ...new Map(
      picked
        .filter((plan) => plan.epic?.willCreate)
        .map((plan) => [plan.epic!.sourceKey, plan.epic!]),
    ).values(),
  ]

  const toggleEpic = (key: string) =>
    setEpicsToCreate((current) =>
      current.includes(key) ? current.filter((k) => k !== key) : [...current, key],
    )
  const needingAttention = picked.reduce((sum, plan) => sum + plan.needsAttentionCount, 0)

  const togglePlan = (key: string) =>
    setChosen((current) =>
      current.includes(key) ? current.filter((k) => k !== key) : [...current, key],
    )

  /** One preview per ticket. A failure on one must not lose the others. */
  const preview = async () => {
    setBusy('preview')
    setError(null)
    setResults(null)
    setConfirming(false)

    const built: MappingPlan[] = []
    const failures: { key: string; message: string }[] = []

    for (const key of selected) {
      try {
        built.push(await api.preview(key, issueTypeOverride || undefined))
      } catch (failure) {
        failures.push({ key, message: (failure as Error).message })
      }
    }

    setPlans(built)

    // Everything creatable is ticked except what is already on the target.
    setChosen(built.filter((plan) => plan.canCreate && !plan.existingCopy).map((p) => p.sourceKey))

    setEpicsToCreate([
      ...new Set(
        built.filter((plan) => plan.epic?.willCreate).map((plan) => plan.epic!.sourceKey),
      ),
    ])

    setPlanErrors(failures)
    setBusy(null)
  }

  const apply = async () => {
    setBusy('apply')
    setError(null)
    setConfirming(false)

    try {
      setResults(
        await api.apply({
          plans: picked,
          includeComments,
          includeAttachments,
          skipDuplicates: true,
          createEpicsFor: epicsToCreate,
          addToActiveSprint: sprintChoice === 'active',
          sprintId: /^\d+$/.test(sprintChoice) ? Number(sprintChoice) : null,
        }),
      )

      // The plans described a state of the world that no longer holds once the
      // copies exist, so they are cleared rather than left to be applied twice.
      setPlans([])
      setSelected([])
      setChosen([])
      setEpicsToCreate([])
    } catch (failure) {
      setError((failure as Error).message)
    } finally {
      setBusy(null)
    }
  }

  if (!config) {
    return (
      <main>
        <h1>Ticket Cloner</h1>
        {error ? <p className="error">{error}</p> : <p className="muted">Loading…</p>}
      </main>
    )
  }

  return (
    <main id="content">
      <a className="skip" href="#content">
        Skip to content
      </a>

      <header className="masthead">
        <div>
          <h1>Ticket Cloner</h1>

          {/*
            Which site is read and which is written to, stated before anything
            else. The project named TGT lives on the target-site site and the
            project named Source Project lives on the source-site
            site, so the pairing is worth showing rather than leaving anyone to
            recall it.
          */}
          <p className="sr-only">
            Copying from {projects.source} on {config.source.host} to {projects.target} on{' '}
            {config.target.host}.
          </p>

        </div>
        <div className="masthead-actions">
          <SignIn
            auth={auth}
            url={authStartUrl()}
            onSignOut={() => void endSignIn()}
          />
          <ThemeToggle theme={theme} onChange={setTheme} />
        </div>
      </header>

      {error && <p className="error">{error}</p>}

      {signIn && <p className={signIn.ok ? 'notice' : 'error'}>{signIn.text}</p>}

      <ConnectionPanel
        config={config}
        connections={connections}
        projects={projects}
        onProjectChange={(tenant, project) =>
          setProjects((current) => ({ ...current, [tenant]: project }))
        }
        auth={auth}
      />

      {/* Everything below needs both tenants, so it stays out of the way until
          the connection works rather than failing one call at a time. */}
      {!connected ? (
        <section className="panel">
          <p className="muted">
            Copying is unavailable until both tenants connect.{' '}
            <button type="button" className="link" onClick={() => void connect()}>
              Try again
            </button>
          </p>
        </section>
      ) : (
        <>
          <IssuePicker selected={selected} onSelectedChange={setSelected} />

          <section className="panel">
            <div className="panel-head">
              <h2><span className="step">2</span>Preview</h2>
            </div>

            <div className="row">
              <label>
                Target issue type
                <select
                  value={issueTypeOverride}
                  onChange={(e) => setIssueTypeOverride(e.target.value)}
                >
                  {/*
                    * The default, and the only option that applies the rule
                    * per ticket: a batch of a Bug and a New Feature resolves to
                    * two different target types, which no single value here can
                    * express. Picking one below overrides that for every ticket
                    * in the selection.
                    */}
                  <option value="">
                    {config.fallbackIssueType
                      ? `Decide per ticket — unmatched types become ${config.fallbackIssueType}`
                      : 'Decide per ticket'}
                  </option>
                  <optgroup label="Or force every ticket to:">
                    {/* Only ever seen if a click beats the request; says why
                      * the group is empty rather than looking broken. */}
                    {issueTypes.length === 0 && (
                      <option disabled value="">
                        {targetConnected ? 'Loading issue types…' : 'Connect the target first'}
                      </option>
                    )}
                    {issueTypes.map((type) => (
                      <option key={type.id} value={type.name}>
                        {type.name}
                      </option>
                    ))}
                  </optgroup>
                </select>
              </label>

              <button
                type="button"
                onClick={() => void preview()}
                disabled={selected.length === 0 || busy !== null}
              >
                {busy === 'preview' ? 'Building…' : `Preview ${selected.length || ''}`.trim()}
              </button>
            </div>

            {planErrors.length > 0 && (
              <div className="error">
                <strong>No plan could be built for:</strong>
                <ul>
                  {planErrors.map((failure) => (
                    <li key={failure.key}>
                      {failure.key} — {failure.message}
                    </li>
                  ))}
                </ul>
              </div>
            )}

            {plans.map((plan) => (
              <PreviewTable key={plan.sourceKey} plan={plan} />
            ))}
          </section>

          {plans.length > 0 && (
            <section className="panel">
              <div className="panel-head">
                <h2><span className="step">3</span>Copy</h2>
              </div>

              <div className="row">
                <label className="inline">
                  <input
                    type="checkbox"
                    checked={includeComments}
                    onChange={(e) => setIncludeComments(e.target.checked)}
                  />
                  Copy comments
                </label>

                <label className="inline">
                  <input
                    type="checkbox"
                    checked={includeAttachments}
                    onChange={(e) => setIncludeAttachments(e.target.checked)}
                  />
                  Copy attachments
                </label>

                <label>
                  Target sprint
                  <select value={sprintChoice} onChange={(e) => setSprintChoice(e.target.value)}>
                    <option value="">Not added to a sprint</option>

                    {/*
                      * "Current" is resolved when the run happens, not now: a
                      * plan made on Monday and applied after a rollover still
                      * lands in the sprint that is current then. It is named
                      * here so nobody has to take it on trust - and disabled
                      * when there is no single current sprint, because the
                      * server would refuse to guess anyway.
                      */}
                    <option value="active" disabled={activeSprints.length !== 1}>
                      {activeSprints.length === 1
                        ? `Current sprint — ${activeSprints[0].name}`
                        : activeSprints.length === 0
                          ? 'Current sprint — the board has none'
                          : `Current sprint — ${activeSprints.length} are active, pick one below`}
                    </option>

                    <optgroup label="Or a specific sprint, newest first:">
                      {targetSprints === null && (
                        <option disabled value="">
                          {sprintsFailed ? 'The board\'s sprints could not be read' : 'Loading sprints…'}
                        </option>
                      )}
                      {targetSprints?.map((sprint) => (
                        <option key={sprint.id} value={String(sprint.id)}>
                          {sprint.name} · {sprintDates(sprint)} · {sprint.state}
                        </option>
                      ))}
                    </optgroup>
                  </select>
                </label>
              </div>

              {blocked.length > 0 && (
                <p className="warn">
                  {blocked.length} ticket{blocked.length === 1 ? '' : 's'} cannot be copied because
                  the plan has blockers: {blocked.map((plan) => plan.sourceKey).join(', ')}.
                </p>
              )}

              <table>
                <thead>
                  <tr>
                    <th aria-label="Copy this one" />
                    <th className="th-source">Ticket</th>
                    <th className="th-target">Will be created as</th>
                    <th className="th-target">Already on the target</th>
                  </tr>
                </thead>
                <tbody>
                  {plans.map((plan) => (
                    <tr
                      key={plan.sourceKey}
                      className={chosen.includes(plan.sourceKey) ? 'chosen' : undefined}
                    >
                      <td>
                        <input
                          type="checkbox"
                          checked={chosen.includes(plan.sourceKey)}
                          disabled={!plan.canCreate}
                          onChange={() => togglePlan(plan.sourceKey)}
                          aria-label={`Copy ${plan.sourceKey}`}
                        />
                      </td>
                      <td>
                        <a
                          className="key-source"
                          href={plan.sourceUrl}
                          target="_blank"
                          rel="noreferrer"
                        >
                          {plan.sourceKey}
                        </a>
                      </td>
                      <td className="muted">
                        {plan.canCreate ? plan.targetIssueTypeName : 'blocked'}
                      </td>
                      <td>
                        {plan.existingCopy ? (
                          <>
                            <a
                              className="key-target"
                              href={plan.existingCopy.url}
                              target="_blank"
                              rel="noreferrer"
                            >
                              {plan.existingCopy.key}
                            </a>
                            {!plan.existingCopy.summaryMatches && (
                              <span className="muted"> (summary differs)</span>
                            )}
                          </>
                        ) : (
                          <span className="muted">—</span>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>

              {missingEpics.length > 0 && (
                <>
                  <h3 className="epics-head">
                    Epics to create first
                  </h3>
                  <p className="muted hint">
                    These have no counterpart on the target yet. Untick one and the tickets under it
                    are still copied, just without a parent.
                  </p>

                  <table>
                    <thead>
                      <tr>
                        <th aria-label="Create this one" />
                        <th className="th-source">Epic</th>
                        <th>Summary</th>
                      </tr>
                    </thead>
                    <tbody>
                      {missingEpics.map((epic) => (
                        <tr
                          key={epic.sourceKey}
                          className={epicsToCreate.includes(epic.sourceKey) ? 'chosen' : undefined}
                        >
                          <td>
                            <input
                              type="checkbox"
                              checked={epicsToCreate.includes(epic.sourceKey)}
                              onChange={() => toggleEpic(epic.sourceKey)}
                              aria-label={`Create a copy of ${epic.sourceKey}`}
                            />
                          </td>
                          <td>
                            <a
                              className="key-source"
                              href={epic.sourceUrl}
                              target="_blank"
                              rel="noreferrer"
                            >
                              {epic.sourceKey}
                            </a>
                          </td>
                          <td className="muted">{epic.summary}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </>
              )}

              {!confirming ? (
                <button
                  type="button"
                  className="primary"
                  disabled={picked.length === 0 || busy !== null}
                  onClick={() => setConfirming(true)}
                >
                  Copy {picked.length} ticket{picked.length === 1 ? '' : 's'}…
                </button>
              ) : (
                <div className="confirm">
                  <p>
                    This creates {picked.length} issue{picked.length === 1 ? '' : 's'} in{' '}
                    <strong>
                      {projects.target} on {config.target.host}
                    </strong>
                    .{' '}
                    {epicsToCreate.length > 0 && missingEpics.length > 0 && (
                      <>
                        {' '}
                        <strong>
                          {missingEpics.filter((e) => epicsToCreate.includes(e.sourceKey)).length}{' '}
                          epic
                          {missingEpics.filter((e) => epicsToCreate.includes(e.sourceKey)).length ===
                          1
                            ? ''
                            : 's'}
                        </strong>{' '}
                        will be created as well, so the copies have somewhere to sit.{' '}
                      </>
                    )}
                    {picked.some((plan) => plan.existingCopy) && (
                      <>
                        <strong>
                          {picked.filter((plan) => plan.existingCopy).length} of them already
                          {picked.filter((plan) => plan.existingCopy).length === 1 ? ' has' : ' have'}{' '}
                          a copy on the target
                        </strong>{' '}
                        and will be copied again.{' '}
                      </>
                    )}
                    {needingAttention > 0 && (
                      <>
                        {needingAttention} field
                        {needingAttention === 1 ? '' : 's'} could not be mapped and will be left
                        unset — they are listed above and will need finishing by hand.
                      </>
                    )}
                  </p>
                  <div className="row">
                    <button
                      type="button"
                      className="primary"
                      onClick={() => void apply()}
                      disabled={busy !== null}
                    >
                      {busy === 'apply' ? 'Copying…' : 'Yes, copy them'}
                    </button>
                    <button type="button" onClick={() => setConfirming(false)} disabled={busy !== null}>
                      Cancel
                    </button>
                  </div>
                </div>
              )}
            </section>
          )}
        </>
      )}

      {results && <ApplyResults results={results} />}

      <BackToTop />
    </main>
  )
}
