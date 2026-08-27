import { useState } from 'react'
import { api, type BoardSprint, type IssueListResponse, type IssueSummary } from './api'
import { sprintDates } from './sprints'

interface Props {
  selected: string[]
  onSelectedChange: (keys: string[]) => void
}

/** Mirrors the server's rule: a search is a key list only if EVERY token is a key. */
const KEY = /^[A-Za-z][A-Za-z0-9]*-\d+$/

function keysIn(search: string): string[] {
  const tokens = search
    .split(/[,;\s]+/)
    .map((token) => token.trim())
    .filter(Boolean)

  if (tokens.length === 0 || !tokens.every((token) => KEY.test(token))) {
    return []
  }

  return [...new Set(tokens.map((token) => token.toUpperCase()))]
}


/**
 * Candidate issues from the source project. The Xray test-management types are
 * already excluded by the JQL the server builds, so nothing offered here is a
 * type that could not be copied.
 *
 * Selection lives in the parent and survives a new search, which is what lets a
 * batch be assembled from several searches rather than one.
 */
export default function IssuePicker({ selected, onSelectedChange }: Props) {
  const [search, setSearch] = useState('')
  const [issues, setIssues] = useState<IssueSummary[]>([])
  const [page, setPage] = useState<IssueListResponse | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [showJql, setShowJql] = useState(false)

  // What the last search asked for by key, so anything the source did not
  // return can be named rather than silently missing from a batch.
  const [asked, setAsked] = useState<string[]>([])

  // Picking by sprint. The sprint narrows the ticket search on top of the
  // text box rather than replacing it, so "everything in sprint 12" and
  // "the portal tickets in sprint 12" are both one search.
  const [sprintSearch, setSprintSearch] = useState('')
  const [sprints, setSprints] = useState<BoardSprint[] | null>(null)
  const [sprint, setSprint] = useState<BoardSprint | null>(null)
  const [findingSprints, setFindingSprints] = useState(false)

  const load = async (pageToken?: string | null, within: BoardSprint | null = sprint) => {
    setLoading(true)
    setError(null)

    try {
      const next = await api.issues(search, pageToken, within?.id)
      setPage(next)

      // A further page ADDS to what is on screen. Replacing it would hide rows
      // that are still selected, which on a batch is how tickets go missing.
      setIssues((current) => (pageToken ? [...current, ...next.issues] : next.issues))

      if (!pageToken) {
        setAsked(keysIn(search))
      }
    } catch (failure) {
      setError((failure as Error).message)
      setPage(null)
      setIssues([])
    } finally {
      setLoading(false)
    }
  }

  const findSprints = async () => {
    setFindingSprints(true)
    setError(null)

    try {
      setSprints((await api.sourceSprints(sprintSearch)).sprints)
    } catch (failure) {
      setError((failure as Error).message)
      setSprints(null)
    } finally {
      setFindingSprints(false)
    }
  }

  // Choosing one closes the list and searches straight away: the sprint IS the
  // search most of the time, and a second click to run it would be a step
  // that only ever has one answer.
  const chooseSprint = (picked: BoardSprint) => {
    setSprint(picked)
    setSprints(null)
    void load(null, picked)
  }

  const clearSprint = () => {
    setSprint(null)
    void load(null, null)
  }

  const toggle = (key: string) => {
    onSelectedChange(
      selected.includes(key) ? selected.filter((k) => k !== key) : [...selected, key],
    )
  }

  const shown = issues.map((issue) => issue.key)
  const allShownSelected = shown.length > 0 && shown.every((key) => selected.includes(key))

  const toggleAllShown = () => {
    onSelectedChange(
      allShownSelected
        ? selected.filter((key) => !shown.includes(key))
        : [...new Set([...selected, ...shown])],
    )
  }

  // Only meaningful once every page has been loaded; until then a key could
  // still be waiting on the next one.
  const missing =
    page?.isLast && asked.length > 0 ? asked.filter((key) => !shown.includes(key)) : []

  return (
    <section className="panel">
      <div className="panel-head">
        <h2><span className="step">1</span>Choose tickets</h2>
        {selected.length > 0 && (
          <span>
            <span className="badge">{selected.length} selected</span>
            <button type="button" className="link" onClick={() => onSelectedChange([])}>
              clear
            </button>
          </span>
        )}
      </div>

      <form
        className="row"
        onSubmit={(e) => {
          e.preventDefault()
          void load()
        }}
      >
        <input
          type="search"
          placeholder="Issue keys (SRC-1234, SRC-1235) or words from the summary"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
        <button type="submit" disabled={loading}>
          {loading ? 'Loading…' : 'Search'}
        </button>
      </form>

      <form
        className="row"
        onSubmit={(e) => {
          e.preventDefault()
          void findSprints()
        }}
      >
        <label>
          Or a sprint on the source board
          <input
            type="search"
            placeholder="Part of the sprint name, or blank for all"
            value={sprintSearch}
            onChange={(e) => setSprintSearch(e.target.value)}
          />
        </label>
        <button type="submit" disabled={findingSprints}>
          {findingSprints ? 'Finding…' : 'Find sprints'}
        </button>

        {sprint && (
          <span className="sprint-chosen">
            <span className="badge">{sprint.name}</span>
            <span className="muted">{sprintDates(sprint)}</span>
            <button type="button" className="link" onClick={clearSprint}>
              clear
            </button>
          </span>
        )}
      </form>

      {sprints && sprints.length === 0 && (
        <p className="muted">No sprint on the board has a name like that.</p>
      )}

      {sprints && sprints.length > 0 && (
        <ul className="sprints">
          {/*
            * Dates and state beside every name, on purpose: a board reuses
            * sprint names, and two "Sprint 1"s a year apart are only told
            * apart by when they ran. The id is what is searched with.
            */}
          {sprints.map((candidate) => (
            <li key={candidate.id}>
              <button type="button" onClick={() => chooseSprint(candidate)}>
                <strong>{candidate.name}</strong>
                <span className="muted">
                  {sprintDates(candidate)} · {candidate.state}
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}

      <p className="muted hint">
        Several keys at once are fine — separate them with commas, spaces or new lines. A chosen
        sprint narrows the search on top of the text box. Selection survives a new search, so a
        batch can be built up over several of them.
      </p>

      {error && <p className="error">{error}</p>}

      {missing.length > 0 && (
        <div className="warn">
          <strong>Not found in the source project:</strong> {missing.join(', ')}
          <br />
          <span className="muted">
            Either the key does not exist, or it is one of the excluded test-management types.
          </span>
        </div>
      )}

      {!page && !loading && !error && (
        <p className="blank">
          <strong>Nothing searched yet</strong>
          Search a key like <code>SRC-1234</code>, several at once, or words from a summary.
        </p>
      )}

      {page && issues.length === 0 && (
        <p className="blank">
          <strong>No issues matched</strong>
          {sprint
            ? 'Nothing copyable in that sprint matches - clear the sprint to search the whole project.'
            : 'Check the key, or widen the search to words from the summary.'}
        </p>
      )}

      {page && issues.length > 0 && (
        <>
          <p className="muted">
            {issues.length} issue{issues.length === 1 ? '' : 's'}
            {' · '}
            <button type="button" className="link" onClick={() => setShowJql(!showJql)}>
              {showJql ? 'hide' : 'show'} the query
            </button>
          </p>

          {showJql && <pre className="jql">{page.jql}</pre>}

          <table>
            <thead>
              <tr>
                <th>
                  <input
                    type="checkbox"
                    checked={allShownSelected}
                    onChange={toggleAllShown}
                    disabled={shown.length === 0}
                    aria-label={allShownSelected ? 'Deselect all shown' : 'Select all shown'}
                  />
                </th>
                <th>Key</th>
                <th>Type</th>
                <th>Summary</th>
                <th>Status</th>
                <th>Reporter</th>
              </tr>
            </thead>
            <tbody>
              {issues.map((issue) => (
                <tr key={issue.key} className={selected.includes(issue.key) ? 'chosen' : undefined}>
                  <td>
                    <input
                      type="checkbox"
                      checked={selected.includes(issue.key)}
                      onChange={() => toggle(issue.key)}
                      aria-label={`Select ${issue.key}`}
                    />
                  </td>
                  <td>
                    <a href={issue.url} target="_blank" rel="noreferrer">
                      {issue.key}
                    </a>
                  </td>
                  <td>{issue.issueType}</td>
                  <td>{issue.summary}</td>
                  <td>{issue.status ?? '—'}</td>
                  <td>{issue.reporter ?? '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>

          {!page.isLast && (
            <button type="button" onClick={() => void load(page.nextPageToken)} disabled={loading}>
              Load more
            </button>
          )}
        </>
      )}
    </section>
  )
}
