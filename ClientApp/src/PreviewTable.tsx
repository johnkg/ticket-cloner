import { useLayoutEffect, useRef, useState } from 'react'

import type { MappingPlan, MappingRow, MappingStatus } from './api'

interface Props {
  plan: MappingPlan
}

interface Division {
  status: MappingStatus
  title: string
  blurb: string
  /** Blockers and near-misses are read every time; the other two rarely are. */
  openByDefault: boolean
}

/**
 * One division per status, worst first. The order is the point: what needs a
 * person is read before what looked after itself.
 */
const DIVISIONS: Division[] = [
  {
    status: 'MissingRequired',
    title: 'Blocks the copy',
    blurb: 'Required by the target, with nothing to fill it from. Nothing is created while one of these stands.',
    openByDefault: true,
  },
  {
    status: 'Unmappable',
    title: 'Needs attention',
    blurb: 'The field exists on the target but the value cannot be expressed there. The copy is still created; these are finished by hand.',
    openByDefault: true,
  },
  {
    status: 'Dropped',
    title: 'Dropped',
    blurb: 'Empty on the source, not on the target create screen, or not settable on create. Sending one of these would be a 400.',
    openByDefault: false,
  },
  {
    status: 'Mapped',
    title: 'Mapped',
    blurb: 'Written to the target exactly as shown.',
    openByDefault: false,
  },
]

/** Anything that is not a plain string is shown as the JSON that will be sent. */
function show(value: unknown): string {
  if (value === null || value === undefined) {
    return '—'
  }

  return typeof value === 'string' ? value : JSON.stringify(value)
}

/**
 * A field value, clamped to four lines with a +/- in the gutter beside it.
 *
 * A description or a checklist arrives as one long line of JSON that wraps into
 * dozens inside a narrow column, and a ticket with fifty fields becomes a page
 * nobody scrolls through. Whether it actually overflows is measured rather than
 * guessed at, because that depends on the column width, not the text length.
 *
 * The gutter is always rendered, even with nothing in it: values stay aligned
 * down the column, and the text keeps the same width whether or not a control
 * appeared, which would otherwise change the wrapping it was measured against.
 */
function Value({ text }: { text: string }) {
  const [expanded, setExpanded] = useState(false)
  const [overflows, setOverflows] = useState(false)
  const ref = useRef<HTMLDivElement>(null)

  useLayoutEffect(() => {
    const element = ref.current

    // Only measured while clamped: expanded, scrollHeight equals clientHeight
    // and the toggle would conclude there was nothing to show and remove itself.
    if (!element || expanded) {
      return
    }

    const measure = () => setOverflows(element.scrollHeight > element.clientHeight + 1)
    measure()

    // The column is a percentage of the table, so this changes with the window.
    const observer = new ResizeObserver(measure)
    observer.observe(element)

    return () => observer.disconnect()
  }, [text, expanded])

  const toggle = () => setExpanded(!expanded)

  return (
    <div className="value-cell">
      <div className="value-gutter">
        {overflows && (
          <button
            type="button"
            className="value-toggle"
            onClick={toggle}
            aria-expanded={expanded}
            aria-label={expanded ? 'Collapse this value' : 'Show the whole value'}
          >
            {expanded ? '\u2212' : '+'}
          </button>
        )}
      </div>

      <div className="value-body">
        <div ref={ref} className={expanded ? 'value-text' : 'value-text clamped'}>
          {text}
        </div>

        {/* Repeated at the foot of a long value, so collapsing it again does
            not mean scrolling back up to the gutter to find the minus. */}
        {overflows && expanded && (
          <button type="button" className="link" onClick={toggle}>
            show less
          </button>
        )}
      </div>
    </div>
  )
}

function Row({ row }: { row: MappingRow }) {
  return (
    <tr>
      <td>{row.name}</td>
      <td className="value">
        <Value text={show(row.sourceValue)} />
      </td>
      <td className="value">
        <Value text={show(row.mappedValue)} />
      </td>
      <td className="muted">
        {row.reason}
        {row.allowedValues && row.allowedValues.length > 0 && (
          <>
            <br />
            <span className="allowed">Allowed: {row.allowedValues.join(', ')}</span>
          </>
        )}
      </td>
    </tr>
  )
}

/**
 * Rows of one status, under a heading that names it. The heading carries the
 * status, so the per-row pill that used to say it again is gone.
 *
 * Fixed column widths, so the four tables line up with each other and a value
 * can be compared down the page rather than only within its own division.
 */
function Division({ division, rows }: { division: Division; rows: MappingRow[] }) {
  if (rows.length === 0) {
    return null
  }

  return (
    <details
      className={`division division-${division.status.toLowerCase()}`}
      open={division.openByDefault}
    >
      <summary>
        <span className="division-title">{division.title}</span>
        <span className="badge">{rows.length}</span>
      </summary>

      <p className="muted division-blurb">{division.blurb}</p>

      <table>
        <colgroup>
          <col className="col-field" />
          <col className="col-value" />
          <col className="col-value" />
          <col className="col-why" />
        </colgroup>
        <thead>
          <tr>
            <th>Field</th>
            <th className="th-source">Source value</th>
            <th className="th-target">Will be written as</th>
            <th>Why</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <Row key={`${row.name}-${row.sourceFieldId ?? row.targetFieldId}`} row={row} />
          ))}
        </tbody>
      </table>
    </details>
  )
}

/**
 * The whole point of the tool. Rows arrive worst-first from the server and are
 * split by status here, so thirty that mapped cleanly stay folded away behind
 * the two that did not.
 */
export default function PreviewTable({ plan }: Props) {
  return (
    <article className={plan.canCreate ? 'plan' : 'plan blocked'}>
      <header>
        <h3>
          <a className="key-source" href={plan.sourceUrl} target="_blank" rel="noreferrer">
            {plan.sourceKey}
          </a>
          <span className="sr-only"> will be copied to </span>
          <span aria-hidden="true"> → </span>
          <span className="muted">
            <span className="key-target">{plan.targetProjectName}</span> as{' '}
            {plan.targetIssueTypeName}
          </span>
        </h3>

        <p className="muted">{plan.issueTypeReason}</p>

        <p>
          <span className="badge">{plan.mappedCount} mapped</span>
          {plan.needsAttentionCount > 0 && (
            <span className="badge badge-warn">{plan.needsAttentionCount} need attention</span>
          )}
        </p>
      </header>

      {plan.existingCopy && (
        <div className={plan.existingCopy.summaryMatches ? 'notice' : 'warn'}>
          {plan.existingCopy.summaryMatches ? (
            <>
              <strong>Already copied.</strong>{' '}
              <a href={plan.existingCopy.url} target="_blank" rel="noreferrer">
                {plan.existingCopy.key}
              </a>{' '}
              has the same summary and points back at this issue. It starts unticked below — open
              it and check it really is a clone.
            </>
          ) : (
            <>
              <strong>Possibly already copied.</strong>{' '}
              <a href={plan.existingCopy.url} target="_blank" rel="noreferrer">
                {plan.existingCopy.key}
              </a>{' '}
              points back at this issue, but its summary reads “{plan.existingCopy.summary}”. Not
              treated as a copy, because a duplicate has to match on both — so this one stays
              ticked unless you say otherwise.
            </>
          )}
        </div>
      )}

      {plan.epic && (
        <div className="notice">
          {plan.epic.existingCopy ? (
            <>
              Sits under{' '}
              <a href={plan.epic.sourceUrl} target="_blank" rel="noreferrer" className="key-source">
                {plan.epic.sourceKey}
              </a>
              , which is already on the target as{' '}
              <a
                className="key-target"
                href={plan.epic.existingCopy.url}
                target="_blank"
                rel="noreferrer"
              >
                {plan.epic.existingCopy.key}
              </a>
              . The copy will be created under it.
            </>
          ) : (
            <>
              Sits under{' '}
              <a href={plan.epic.sourceUrl} target="_blank" rel="noreferrer" className="key-source">
                {plan.epic.sourceKey}
              </a>{' '}
              — “{plan.epic.summary}” — which has no counterpart on the target yet.{' '}
              <strong>A copy of the epic will be created first</strong>, then this ticket under it.
              It is listed separately below and can be unticked.
            </>
          )}
        </div>
      )}

      {plan.blockers.length > 0 && (
        <div className="error">
          <strong>This ticket will not be copied.</strong>
          <ul>
            {plan.blockers.map((blocker) => (
              <li key={blocker}>{blocker}</li>
            ))}
          </ul>
        </div>
      )}

      {DIVISIONS.map((division) => (
        <Division
          key={division.status}
          division={division}
          rows={plan.rows.filter((row) => row.status === division.status)}
        />
      ))}
    </article>
  )
}
