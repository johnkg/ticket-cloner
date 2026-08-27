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

function Row({ row }: { row: MappingRow }) {
  return (
    <tr>
      <td>{row.name}</td>
      <td className="value">{show(row.sourceValue)}</td>
      <td className="value">{show(row.mappedValue)}</td>
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
            <th>Source value</th>
            <th>Will be written as</th>
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
          <a href={plan.sourceUrl} target="_blank" rel="noreferrer">
            {plan.sourceKey}
          </a>
          <span className="sr-only"> will be copied to </span>
          <span aria-hidden="true"> → </span>
          <span className="muted">
            {plan.targetProjectName} as {plan.targetIssueTypeName}
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
