import { useCallback, useEffect, useState } from 'react'
import {
  api,
  ApiError,
  type ApplyResponse,
  type ConfigResponse,
  type MappingPlan,
  type TargetIssueType,
  type Tenant,
} from './api'
import ApplyResults from './ApplyResults'
import BackToTop from './BackToTop'
import ConnectionPanel, { type ConnectionState } from './ConnectionPanel'
import CredentialsDialog from './CredentialsDialog'
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
  const [dialogDismissed, setDialogDismissed] = useState(false)

  const [projects, setProjects] = useState<Record<Tenant, string>>({ source: '', target: '' })
  const [selected, setSelected] = useState<string[]>([])
  const [issueTypes, setIssueTypes] = useState<TargetIssueType[]>([])
  const [issueTypeOverride, setIssueTypeOverride] = useState('')

  const [plans, setPlans] = useState<MappingPlan[]>([])
  const [planErrors, setPlanErrors] = useState<{ key: string; message: string }[]>([])
  const [results, setResults] = useState<ApplyResponse | null>(null)

  const [includeComments, setIncludeComments] = useState(true)
  const [includeAttachments, setIncludeAttachments] = useState(true)
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
    setDialogDismissed(false)

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

  useEffect(() => {
    api
      .config()
      .then((loaded) => {
        setConfig(loaded)
        setProjects({
          source: loaded.source.projectKey,
          target: loaded.target.projectKey,
        })
        return connect()
      })
      .catch((failure: Error) => setError(failure.message))
  }, [connect])

  const connected =
    connections.source.status === 'connected' && connections.target.status === 'connected'

  const anyFailed =
    connections.source.status === 'failed' || connections.target.status === 'failed'

  const creatable = plans.filter((plan) => plan.canCreate)
  const blocked = plans.filter((plan) => !plan.canCreate)
  const needingAttention = creatable.reduce((sum, plan) => sum + plan.needsAttentionCount, 0)

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
          plans: creatable,
          includeComments,
          includeAttachments,
          skipDuplicates: true,
        }),
      )

      // The plans described a state of the world that no longer holds once the
      // copies exist, so they are cleared rather than left to be applied twice.
      setPlans([])
      setSelected([])
    } catch (failure) {
      setError((failure as Error).message)
    } finally {
      setBusy(null)
    }
  }

  const loadIssueTypes = async () => {
    try {
      setIssueTypes(await api.targetIssueTypes())
    } catch (failure) {
      setError((failure as Error).message)
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
    <main>
      <header className="masthead">
        <div>
          <h1>Ticket Cloner</h1>
          <p className="muted">
            {config.source.host}/{projects.source}
            {/* The arrow is decorative, but hiding it takes its spaces with it
                and the two hosts run together when read aloud. */}
            <span className="sr-only"> copied to </span>
            <span aria-hidden="true"> → </span>
            {config.target.host}/{projects.target}
          </p>
        </div>
        <ThemeToggle theme={theme} onChange={setTheme} />
      </header>

      {error && <p className="error">{error}</p>}

      <ConnectionPanel
        config={config}
        connections={connections}
        projects={projects}
        onProjectChange={(tenant, project) =>
          setProjects((current) => ({ ...current, [tenant]: project }))
        }
      />

      {anyFailed && !dialogDismissed && (
        <CredentialsDialog
          config={config}
          connections={connections}
          onRetry={() => void connect()}
          onClose={() => setDialogDismissed(true)}
        />
      )}

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
              <h2>2 · Preview</h2>
            </div>

            <div className="row">
              <label>
                Target issue type
                <select
                  value={issueTypeOverride}
                  onFocus={() => issueTypes.length === 0 && void loadIssueTypes()}
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
                <h2>3 · Copy</h2>
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
              </div>

              {blocked.length > 0 && (
                <p className="warn">
                  {blocked.length} ticket{blocked.length === 1 ? '' : 's'} will be left out because
                  the plan has blockers: {blocked.map((plan) => plan.sourceKey).join(', ')}.
                </p>
              )}

              {!confirming ? (
                <button
                  type="button"
                  className="primary"
                  disabled={creatable.length === 0 || busy !== null}
                  onClick={() => setConfirming(true)}
                >
                  Copy {creatable.length} ticket{creatable.length === 1 ? '' : 's'}…
                </button>
              ) : (
                <div className="confirm">
                  <p>
                    This creates {creatable.length} issue{creatable.length === 1 ? '' : 's'} in{' '}
                    <strong>
                      {projects.target} on {config.target.host}
                    </strong>
                    .{' '}
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
