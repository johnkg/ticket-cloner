import { useEffect, useRef } from 'react'
import type { ConfigResponse, Tenant } from './api'
import type { ConnectionState } from './ConnectionPanel'

interface Props {
  config: ConfigResponse
  connections: Record<Tenant, ConnectionState>
  onRetry: () => void
  onClose: () => void
}

const TENANT_LABELS: Record<Tenant, string> = {
  source: 'Source',
  target: 'Target',
}

const KEYS: Record<Tenant, [string, string]> = {
  source: ['SourceEmail', 'SourceApiToken'],
  target: ['TargetEmail', 'TargetApiToken'],
}

/**
 * Shown when the tool cannot reach a tenant. There is nothing to fix on this
 * page - the credentials are in a file on the server - so the dialog's whole
 * job is to say which file, which keys, and that a restart is needed.
 */
export default function CredentialsDialog({ config, connections, onRetry, onClose }: Props) {
  const dialog = useRef<HTMLDialogElement>(null)

  useEffect(() => {
    // showModal rather than the open attribute: it traps focus and takes Escape.
    dialog.current?.showModal()
  }, [])

  const failed = (['source', 'target'] as Tenant[]).filter(
    (tenant) => connections[tenant].status === 'failed',
  )

  const anyCredentialProblem = failed.some((tenant) => {
    const state = connections[tenant]
    return state.status === 'failed' && state.credentialProblem
  })

  return (
    <dialog ref={dialog} className="modal" onCancel={onClose} onClose={onClose}>
      <h2>{anyCredentialProblem ? 'Those credentials were rejected' : 'Could not reach Jira'}</h2>

      <p>
        {failed.length === 2
          ? 'Neither tenant would accept the credentials the server is configured with.'
          : `The ${failed.map((tenant) => TENANT_LABELS[tenant].toLowerCase()).join('')} tenant would not accept the credentials the server is configured with.`}
      </p>

      <ul className="failures">
        {failed.map((tenant) => {
          const state = connections[tenant]
          const [emailKey, tokenKey] = KEYS[tenant]

          return (
            <li key={tenant}>
              <strong>
                {TENANT_LABELS[tenant]} — {config[tenant].host}
              </strong>
              <br />
              {state.status === 'failed' && <span className="muted">{state.message}</span>}
              <br />
              <span className="muted">
                Keys: <code>Credentials:{emailKey}</code> and <code>Credentials:{tokenKey}</code>
              </span>
            </li>
          )
        })}
      </ul>

      <h3>How to fix it</h3>
      <ol>
        <li>
          Open <code>appsettings.json</code> — in the published folder if this is a deployed
          instance, otherwise <code>src/TicketCloner.Api/appsettings.json</code>.
        </li>
        <li>
          Fill in the email and API token for the tenant above. Create a token at{' '}
          <a
            href="https://id.atlassian.com/manage-profile/security/api-tokens"
            target="_blank"
            rel="noreferrer"
          >
            id.atlassian.com
          </a>
          . One account currently reaches both tenants, so the same pair can go in both.
        </li>
        <li>
          <strong>Restart the API.</strong> Configuration is read at startup, so an edit alone
          changes nothing.
        </li>
      </ol>

      <p className="muted">
        <strong>appsettings.json is not gitignored and ships with every publish</strong>, so a
        token typed into the copy under <code>src/</code> travels with the source. Filling in the
        published copy instead keeps it out of both. An environment variable still overrides
        everything if you want one — <code>Credentials__TargetApiToken</code>, two underscores.
      </p>

      <p className="muted">
        If <code>appsettings.Local.json</code> exists it is loaded afterwards and wins, so delete
        it once the credentials live in appsettings.json.
      </p>

      <div className="row">
        <button type="button" className="primary" onClick={onRetry}>
          Try again
        </button>
        <button type="button" onClick={onClose}>
          Close
        </button>
      </div>
    </dialog>
  )
}
