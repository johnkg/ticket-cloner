// ---- Tenants ---------------------------------------------------------------

/**
 * Source and target, never "TARGET_PROJECT" and "YOUR_COMPANY". The project named TARGET_PROJECT lives
 * on the your-company tenant and the project named YOUR_SOURCE_PROJECT lives
 * on the source-company tenant, so naming anything after the project resolves it to
 * the wrong site.
 */
export type Tenant = 'source' | 'target'

/**
 * What this instance holds. Credentials live in configuration on the server -
 * appsettings.Local.json - and are never entered here, so nothing on this page
 * has ever seen a token.
 */
export interface TenantConfig {
  host: string
  projectKey: string

  /** What the project dropdown offers. One entry today. */
  availableProjects: string[]
  boardId: number
  configured: boolean
  email: string
}

export interface ConfigResponse {
  source: TenantConfig
  target: TenantConfig

  /**
   * What a source type with no same-named counterpart becomes. Named by the
   * server so the dropdown states the rule actually configured rather than a
   * sentence that drifts.
   */
  fallbackIssueType: string
}

export interface IdentityResponse {
  host: string
  accountId: string
  displayName: string
  emailAddress: string | null

  /** source-company hides email addresses; your-company does not. */
  emailVisible: boolean
}

// ---- Source issues ---------------------------------------------------------

export interface IssueSummary {
  key: string
  url: string
  issueType: string
  summary: string
  status: string | null
  priority: string | null
  reporter: string | null
  assignee: string | null
  updated: string | null
}

export interface IssueListResponse {
  issues: IssueSummary[]

  /** Opaque. Jira's bounded search pages by token and reports no total. */
  nextPageToken: string | null
  isLast: boolean

  /** Echoed back so the exclusions being applied are visible, not implied. */
  jql: string
}

// ---- Mapping ---------------------------------------------------------------

export type MappingStatus = 'Mapped' | 'Dropped' | 'Unmappable' | 'MissingRequired'

export interface MappingRow {
  name: string
  sourceFieldId: string | null
  sourceValue: unknown
  targetFieldId: string | null
  mappedValue: unknown
  status: MappingStatus
  reason: string
  allowedValues: string[] | null
}

export interface MappingPlan {
  sourceKey: string
  sourceUrl: string
  targetProjectKey: string
  targetProjectName: string
  targetIssueTypeId: string
  targetIssueTypeName: string
  issueTypeReason: string
  rows: MappingRow[]

  /** Non-empty means nothing will be created for this ticket. */
  blockers: string[]
  canCreate: boolean
  mappedCount: number
  needsAttentionCount: number
}

export interface TargetIssueType {
  id: string
  name: string
  subtask: boolean
}

// ---- Apply -----------------------------------------------------------------

export type ApplyStatus = 'Created' | 'Skipped' | 'Failed' | 'CreatedWithProblems'

export interface StepOutcome {
  step: string
  succeeded: boolean
  detail: string
}

export interface TicketOutcome {
  sourceKey: string
  targetKey: string | null
  targetUrl: string | null
  status: ApplyStatus
  summary: string
  steps: StepOutcome[]
}

export interface ApplyResponse {
  tickets: TicketOutcome[]
  created: number
  skipped: number
  failed: number
}

export interface ApplyRequest {
  plans: MappingPlan[]
  includeComments: boolean
  includeAttachments: boolean
  skipDuplicates: boolean
}

// ---- Transport -------------------------------------------------------------

/** Shape of the ProblemDetails the API returns on failure. */
interface Problem {
  title?: string
  detail?: string
  status?: number
}

/** Carries the HTTP status so a 401 can be told apart from a real failure. */
export class ApiError extends Error {
  // Declared rather than a constructor parameter property: the latter is
  // TypeScript-only syntax that cannot simply be erased, which this project's
  // erasableSyntaxOnly setting forbids.
  readonly status: number

  constructor(message: string, status: number) {
    super(message)
    this.name = 'ApiError'
    this.status = status
  }

  /** The credentials on the server are missing or wrong. */
  get isCredentialProblem(): boolean {
    return this.status === 401 || this.status === 403
  }
}

async function request<T>(path: string, body?: unknown): Promise<T> {
  const response = await fetch(path, {
    method: body === undefined ? 'GET' : 'POST',
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })

  const text = await response.text()

  if (!response.ok) {
    throw new ApiError(describe(response.status, text), response.status)
  }

  return text ? (JSON.parse(text) as T) : (undefined as T)
}

/** The API's problem responses carry the actionable message, so prefer them. */
function describe(status: number, body: string): string {
  try {
    const problem = JSON.parse(body) as Problem

    if (problem.title) {
      return problem.detail ? `${problem.title} — ${problem.detail}` : problem.title
    }
  } catch {
    // Not JSON - fall through to the status.
  }

  return `Request failed with status ${status}.`
}

export const api = {
  /** Readable without credentials - it is what tells the UI which sites it talks to. */
  config: () => request<ConfigResponse>('/api/config'),

  whoAmI: (tenant: Tenant) => request<IdentityResponse>(`/api/${tenant}/me`),

  issues: (search: string, pageToken?: string | null) => {
    const query = new URLSearchParams()

    if (search.trim()) {
      query.set('search', search.trim())
    }

    if (pageToken) {
      query.set('pageToken', pageToken)
    }

    const suffix = query.toString() ? `?${query}` : ''
    return request<IssueListResponse>(`/api/source/issues${suffix}`)
  },

  targetIssueTypes: () => request<TargetIssueType[]>('/api/target/issuetypes'),

  /** Reads both tenants and writes to neither. */
  preview: (key: string, issueType?: string) => {
    const suffix = issueType ? `?issueType=${encodeURIComponent(issueType)}` : ''
    return request<MappingPlan>(`/api/preview/${encodeURIComponent(key)}${suffix}`)
  },

  /** The only call that writes. It carries the plans the preview produced. */
  apply: (body: ApplyRequest) => request<ApplyResponse>('/api/apply', body),
}
