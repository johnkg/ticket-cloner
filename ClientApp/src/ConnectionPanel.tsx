import type { AuthStatusResponse, ConfigResponse, Tenant } from './api'

export type ConnectionState =
  | { status: 'checking' }
  | { status: 'connected'; displayName: string; accountId: string; emailVisible: boolean }
  | { status: 'failed'; message: string; credentialProblem: boolean }

interface Props {
  config: ConfigResponse
  connections: Record<Tenant, ConnectionState>
  projects: Record<Tenant, string>
  onProjectChange: (tenant: Tenant, project: string) => void

  /** Null while it is still being read, or if the call failed. */
  auth: AuthStatusResponse | null
}

function Status({ state }: { state: ConnectionState }) {
  if (state.status === 'checking') {
    return <p className="muted">Connecting…</p>
  }

  if (state.status === 'failed') {
    return <p className="error">Not connected — {state.message}</p>
  }

  return (
    <p className="notice">
      Connected as {state.displayName}
      {!state.emailVisible && (
        <>
          <br />
          <span className="muted">
            This tenant hides email addresses, so users are matched by accountId alone.
          </span>
        </>
      )}
    </p>
  )
}

/**
 * Shows what the tool is wired to and whether it got there.
 *
 * There is still nothing to type. Either the server holds a configured API
 * token, or you sign in to Atlassian and it holds a token of yours that this
 * page never sees - no field here has ever taken a credential.
 */
export default function ConnectionPanel({
  config,
  connections,
  projects,
  onProjectChange,
  auth,
}: Props) {
  const bothSignedIn = auth?.source.signedIn === true && auth.target.signedIn === true

  const tenants: { key: Tenant; label: string; config: ConfigResponse['source'] }[] = [
    { key: 'source', label: 'Source — read only', config: config.source },
    { key: 'target', label: 'Target — written to', config: config.target },
  ]

  return (
    <section className="panel">
      <div className="panel-head">
        <h2>Connection</h2>
      </div>

      {/* The button is in the masthead; what belongs here is the bit that names
          the hosts. One consent grants one site, so the second screen follows
          straight after - saying so stops it reading like something went wrong. */}
      {auth?.available && !bothSignedIn && (
        <p className="muted hint">
          Sign in from the top of the page. Atlassian asks for one site at a time, so
          expect two consent screens — one for {config.source.host}, one for{' '}
          {config.target.host} — and grant whichever it offers first; the order does not
          matter.
        </p>
      )}

      <div className="tenants">
        {tenants.map(({ key, label, config: tenant }) => (
          <div className={`tenant tenant-${key}`} key={key}>
            <h3>
              {label}
              <span className="host">{tenant.host}</span>
            </h3>

            <label>
              Project
              <select
                value={projects[key]}
                onChange={(e) => onProjectChange(key, e.target.value)}
                // One option today. A second would need ProjectKey plumbed
                // through the reader, createmeta and the writer first.
                disabled={tenant.availableProjects.length < 2}
              >
                {tenant.availableProjects.map((project) => (
                  <option key={project} value={project}>
                    {project}
                  </option>
                ))}
              </select>
            </label>

            <p className="muted">Board {tenant.boardId}</p>

            {/* Status already says "Connected as <name>", which answers the
                same question with more in it. */}
            <Status state={connections[key]} />
          </div>
        ))}
      </div>
    </section>
  )
}
