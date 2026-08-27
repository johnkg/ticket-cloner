import type { ConfigResponse, Tenant } from './api'

export type ConnectionState =
  | { status: 'checking' }
  | { status: 'connected'; displayName: string; accountId: string; emailVisible: boolean }
  | { status: 'failed'; message: string; credentialProblem: boolean }

interface Props {
  config: ConfigResponse
  connections: Record<Tenant, ConnectionState>
  projects: Record<Tenant, string>
  onProjectChange: (tenant: Tenant, project: string) => void
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
 * Shows what the tool is wired to and whether it got there. There is nothing to
 * type: credentials live in configuration on the server, so no token ever
 * reaches this page.
 */
export default function ConnectionPanel({ config, connections, projects, onProjectChange }: Props) {
  const tenants: { key: Tenant; label: string; config: ConfigResponse['source'] }[] = [
    { key: 'source', label: 'Source — read only', config: config.source },
    { key: 'target', label: 'Target — written to', config: config.target },
  ]

  return (
    <section className="panel">
      <div className="panel-head">
        <h2>Connection</h2>
      </div>

      <div className="tenants">
        {tenants.map(({ key, label, config: tenant }) => (
          <div className="tenant" key={key}>
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

            <Status state={connections[key]} />
          </div>
        ))}
      </div>
    </section>
  )
}
