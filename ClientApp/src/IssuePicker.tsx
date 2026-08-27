import { useState } from 'react'
import { api, type IssueListResponse } from './api'

interface Props {
  selected: string[]
  onSelectedChange: (keys: string[]) => void
}

/**
 * Candidate issues from the source project. The Xray test-management types are
 * already excluded by the JQL the server builds, so nothing offered here is a
 * type that could not be copied.
 */
export default function IssuePicker({ selected, onSelectedChange }: Props) {
  const [search, setSearch] = useState('')
  const [page, setPage] = useState<IssueListResponse | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [showJql, setShowJql] = useState(false)

  const load = async (pageToken?: string | null) => {
    setLoading(true)
    setError(null)

    try {
      setPage(await api.issues(search, pageToken))
    } catch (failure) {
      setError((failure as Error).message)
      setPage(null)
    } finally {
      setLoading(false)
    }
  }

  const toggle = (key: string) => {
    onSelectedChange(
      selected.includes(key) ? selected.filter((k) => k !== key) : [...selected, key],
    )
  }

  return (
    <section className="panel">
      <div className="panel-head">
        <h2>1 · Choose tickets</h2>
        {selected.length > 0 && <span className="badge">{selected.length} selected</span>}
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
          placeholder="Issue key (SOURCE_PROJECT-1234) or words from the summary"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
        <button type="submit" disabled={loading}>
          {loading ? 'Loading…' : 'Search'}
        </button>
      </form>

      {error && <p className="error">{error}</p>}

      {page && (
        <>
          <p className="muted">
            {page.issues.length} issue{page.issues.length === 1 ? '' : 's'}
            {' · '}
            <button type="button" className="link" onClick={() => setShowJql(!showJql)}>
              {showJql ? 'hide' : 'show'} the query
            </button>
          </p>

          {showJql && <pre className="jql">{page.jql}</pre>}

          <table>
            <thead>
              <tr>
                <th aria-label="Selected" />
                <th>Key</th>
                <th>Type</th>
                <th>Summary</th>
                <th>Status</th>
                <th>Reporter</th>
              </tr>
            </thead>
            <tbody>
              {page.issues.map((issue) => (
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
