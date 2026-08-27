import type { ApplyResponse, ApplyStatus, TicketOutcome } from './api'

interface Props {
  results: ApplyResponse
}

const STATUS_LABELS: Record<ApplyStatus, string> = {
  Created: 'Created',
  CreatedWithProblems: 'Created, needs finishing',
  Skipped: 'Skipped',
  Failed: 'Failed',
}

function Outcome({ ticket }: { ticket: TicketOutcome }) {
  return (
    <article className={`outcome outcome-${ticket.status.toLowerCase()}`}>
      <header>
        <h3>
          {ticket.sourceKey}
          {ticket.targetKey && (
            <>
              <span className="sr-only"> was copied to </span>
              <span aria-hidden="true"> → </span>
              <a href={ticket.targetUrl ?? undefined} target="_blank" rel="noreferrer">
                {ticket.targetKey}
              </a>
            </>
          )}
        </h3>
        <span className={`pill pill-${ticket.status.toLowerCase()}`}>
          {STATUS_LABELS[ticket.status]}
        </span>
      </header>

      <p>{ticket.summary}</p>

      {ticket.steps.length > 0 && (
        <ul className="steps">
          {ticket.steps.map((step, index) => (
            <li key={`${step.step}-${index}`} className={step.succeeded ? 'ok' : 'bad'}>
              <strong>{step.step}</strong> {step.detail}
            </li>
          ))}
        </ul>
      )}
    </article>
  )
}

/**
 * Failures are per ticket and never abort the run, so the results are a list
 * rather than a single verdict - somebody has to know exactly which copies need
 * finishing by hand.
 */
export default function ApplyResults({ results }: Props) {
  return (
    <section className="panel">
      <div className="panel-head">
        <h2>Results</h2>
        <p>
          <span className="badge">{results.created} created</span>
          {results.skipped > 0 && <span className="badge">{results.skipped} skipped</span>}
          {results.failed > 0 && <span className="badge badge-error">{results.failed} failed</span>}
        </p>
      </div>

      {results.tickets.map((ticket) => (
        <Outcome key={ticket.sourceKey} ticket={ticket} />
      ))}
    </section>
  )
}
