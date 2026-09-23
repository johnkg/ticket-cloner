// ---- Tenants ---------------------------------------------------------------

/**
 * Source and target, never "TGT" and "target-site". The project named TGT lives
 * on the target-site tenant and the project named Source Project lives
 * on the source-site tenant, so naming anything after the project resolves it to
 * the wrong site.
 */
export type Tenant = 'source' | 'target'

/**
 * Which sites this instance is wired to. Carries nothing about credentials -
 * the server stores no API token, and whether you can reach a tenant is what
 * the sign-in status answers.
 */
export interface TenantConfig {
  host: string
  projectKey: string

  /** What the project dropdown offers. One entry today. */
  availableProjects: string[]
  boardId: number
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

// ---- Sign-in ---------------------------------------------------------------

export interface AuthTenantStatus {
  /** Lowercased on the server: 'source' or 'target'. */
  tenant: string
  signedIn: boolean

  /** Which Atlassian site the grant reaches. Null when signed out. */
  cloudId: string | null
  expiresAt: string | null
}

export interface AuthStatusResponse {
  /**
   * False when no 3LO app is registered on this instance, which is a normal
   * state - the tool runs on configured API tokens without one. Everything
   * sign-in related stays hidden when this is false.
   */
  available: boolean
  source: AuthTenantStatus
  target: AuthTenantStatus
}

/**
 * Where this app is mounted, without a trailing slash.
 *
 * Every path below is written from the app's own root, and this is what turns
 * it into a path from the SERVER's root. They are the same thing only when the
 * app is its own site; mounted as an application inside another site it is not,
 * and an unprefixed "/api/config" leaves the app entirely.
 *
 * Vite substitutes BASE_URL at build time from `base` in vite.config.ts, and it
 * always ends with a slash - hence the trim, so callers can keep writing paths
 * that start with one.
 */
const mountedAt = import.meta.env.BASE_URL.replace(/\/$/, '')

/**
 * A plain navigation, not a fetch: signing in has to leave this origin, and the
 * browser cannot follow a cross-origin redirect made from script.
 *
 * One URL for both tenants. The app holds an account-level grant, so a single
 * token can reach both sites and the server files whatever is consented to
 * under whichever tenant it turns out to be.
 */
export const authStartUrl = () => `${mountedAt}/api/auth/start`

/** The target board's current sprint. Ids belong to the target tenant. */
export interface ActiveSprint {
  id: number
  name: string
  goal: string | null
  startDate: string | null
  endDate: string | null
}

/**
 * Null `sprint` means there is no single active one - none, or several running
 * in parallel. `reason` says which, in a sentence worth showing.
 */
export interface ActiveSprintResult {
  sprint: ActiveSprint | null
  reason: string | null
}

export interface IdentityResponse {
  host: string
  accountId: string
  displayName: string
  emailAddress: string | null

  /** source-site hides email addresses; target-site does not. */
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

/**
 * One sprint on a board, from either tenant. A sprint id belongs to the tenant
 * that issued it: a source sprint only ever goes back into the source search,
 * a target sprint only ever into ApplyRequest.sprintId. The two never meet.
 */
export interface BoardSprint {
  id: number
  name: string
  /** closed, active or future, as Jira reports it. */
  state: string
  startDate: string | null
  endDate: string | null
}

export interface SprintListResponse {
  sprints: BoardSprint[]
  board: number
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

export interface ExistingCopy {
  key: string

  /** Clickable, so a copy can be opened and checked before it is trusted. */
  url: string
  summary: string

  /**
   * False means its External Issue ID points at this source issue but the
   * summary has since been reworded, so it is NOT treated as a copy.
   */
  summaryMatches: boolean
}

export interface EpicPlan {
  sourceKey: string
  sourceUrl: string
  summary: string

  /** The TGT epic that already corresponds to this one, if there is one. */
  existingCopy: ExistingCopy | null

  /** True when nothing on the target matches, so one has to be created. */
  willCreate: boolean
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

  /**
   * A copy already on the target, found while previewing. Advisory: apply runs
   * the same search again rather than trusting what came back through here.
   */
  existingCopy: ExistingCopy | null

  /** Set when the source issue sits under an epic. */
  epic: EpicPlan | null

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

  /** Put each copy in the target board's current sprint, whichever that is at run time. */
  addToActiveSprint: boolean

  /** Or in this target sprint, chosen from the board's list. Wins over the flag above. */
  sprintId?: number | null

  /**
   * Source epic keys approved for creation. An epic missing from the target is
   * only created when its key is here, so a ticket nobody picked is never
   * written just because something else points at it.
   */
  createEpicsFor: string[]
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
  // Prefixed here rather than at each call site: there are ten of them, and one
  // missed would fail only when the app is mounted under a sub-path.
  const response = await fetch(mountedAt + path, {
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

  issues: (search: string, pageToken?: string | null, sprintId?: number | null) => {
    const query = new URLSearchParams()

    if (search.trim()) {
      query.set('search', search.trim())
    }

    if (pageToken) {
      query.set('pageToken', pageToken)
    }

    if (sprintId) {
      query.set('sprint', String(sprintId))
    }

    const suffix = query.toString() ? `?${query}` : ''
    return request<IssueListResponse>(`/api/source/issues${suffix}`)
  },

  /** The source board's sprints whose name contains the search; all of them when empty. */
  sourceSprints: (search: string) => {
    const query = new URLSearchParams()

    if (search.trim()) {
      query.set('search', search.trim())
    }

    const suffix = query.toString() ? `?${query}` : ''
    return request<SprintListResponse>(`/api/source/sprints${suffix}`)
  },

  targetIssueTypes: () => request<TargetIssueType[]>('/api/target/issuetypes'),

  /** One answer for the whole run, so it is read once and named before applying. */
  activeSprint: () => request<ActiveSprintResult>('/api/target/active-sprint'),

  /** Every sprint on the target board: the active one first, then newest to oldest. */
  targetSprints: () => request<SprintListResponse>('/api/target/sprints'),

  /** Reads both tenants and writes to neither. */
  preview: (key: string, issueType?: string) => {
    const suffix = issueType ? `?issueType=${encodeURIComponent(issueType)}` : ''
    return request<MappingPlan>(`/api/preview/${encodeURIComponent(key)}${suffix}`)
  },

  /** The only call that writes. It carries the plans the preview produced. */
  apply: (body: ApplyRequest) => request<ApplyResponse>('/api/apply', body),

  /** Readable without credentials, like config - the UI asks before it has any. */
  auth: () => request<AuthStatusResponse>('/api/auth/status'),

  /**
   * POST, so a prefetch or a stray link cannot sign somebody out. All or
   * nothing: there is one grant, and signing out of one tenant alone would mean
   * holding a token and pretending not to.
   */
  signOut: () => request<void>('/api/auth/signout', {}),
}
