# TicketCloner — Project Context

Copies a Jira ticket from one Atlassian **tenant** to another. Deliberately a
separate project from ReleaseTool: copying tickets has nothing to do with
cutting a release, and bolting it on as a fourth tab would have coupled two
unrelated lifecycles.

Everything under "What Phase 0 established" was read from the two live sites on
15/08/2026 and is fact. Everything under "Not settled yet" is explicitly not.

## Goal

Read an issue from the **SOURCE_PROJECT** project on `source-domain.atlassian.net`, and create a
counterpart in the **TARGET_PROJECT** project on `target-domain.atlassian.net`, with a preview
step before anything is written.

Copies are **one-shot snapshots**. The copy then lives its own life; edits to the
original do not flow through. If two-way sync is ever wanted, this is the wrong
tool — see "Considered and rejected".

## The tenant map — READ THIS FIRST

|  | Tenant | Board | Project |
|---|---|---|---|
| **Source** | `source-domain.atlassian.net` | SOURCE_PROJECT_ID | **SOURCE_PROJECT** "YOUR_SOURCE_PROJECT", classic |
| **Target** | `target-domain.atlassian.net` | TARGET_PROJECT_ID | **TARGET_PROJECT** "YOUR_TARGET_PROJECT", classic |

The cloud ID of each site is deliberately not recorded here. Nothing in the tool
needs one — every call goes through the tenant's own hostname — and this file is
public. Read one from `GET /_edge/tenant_info` on the site itself if you ever do.

**GOTCHA — the names are inverted.** The project called **TARGET_PROJECT** is on the
**your-company** tenant. The project called **YOUR_SOURCE_PROJECT** is on the
**source-company** tenant. Read that twice.

Consequences, and they are not stylistic:

- Config sections are `Atlassian:Source` and `Atlassian:Target`. **Never** name
  anything `TARGET_PROJECT` or `YOUR_COMPANY` — a reader will resolve the name to the wrong
  tenant, and the failure mode is writing test-management tickets into the live
  release project.
- Same for log messages, variable names, test fixture names and UI labels. Say
  "source tenant" and show the host, not the word TARGET_PROJECT.
- Two named `HttpClient`s, `"source"` and `"target"`, each with its own
  `BaseAddress` and its own credential. One client with a swappable base address
  is the shape that eventually writes to the wrong site.

The target project **TARGET_PROJECT is the same project ReleaseTool writes release pages
for**. The two tools now touch it from opposite ends. A bad run here is visible
in a release.

## What Phase 0 established

Captured by `phase0-profile.ps1` on both tenants, compared by
`phase0-compare.ps1`. Both profiles are read-only; the JSON holds no credentials
and is worth keeping as the before-picture.

### Credentials — not a blocker

**One Atlassian account reaches both tenants.** A single ordinary account
authenticated against `source-company` and `your-company` alike, so one API token serves
both. No second account, no admin request.

That also means **`accountId` is shared**: accountIds belong to the Atlassian
account, not the tenant, so anyone present on both sites is the same accountId
in both, with no email lookup needed.

Build the credential layer for **two** tenants anyway. One shared credential is
just the case where both slots hold the same value, and retrofitting the split
later means touching every service.

### Permissions on the target — all granted

`CREATE_ISSUES`, `EDIT_ISSUES`, `ADD_COMMENTS`, `CREATE_ATTACHMENTS`,
`LINK_ISSUES`, `TRANSITION_ISSUES`, `DELETE_ISSUES`, `SET_ISSUE_SECURITY` and
**`MODIFY_REPORTER`** are all true on TARGET_PROJECT.

`MODIFY_REPORTER` being granted means the original reporter **can** be preserved
— but only when that person exists as a your-company user. See "User mapping".

Only `BROWSE_PROJECTS` is needed on the source. The tool never writes to
source-company, and nothing in the design should make it possible to.

### Emails are hidden on the source

`source-company` returned an email address for **0 of 25** sampled users;
`your-company` returned 23 of 25. So users cannot be matched by email in the
direction that matters.

Partly mitigated by the shared-accountId finding above: a person on both sites
maps by accountId alone. The manual table is only needed for people who exist on
one side. Nobody has measured how big that set is yet.

### Issue types — 10 of 15 have no counterpart, but most should never be copied

Six are Xray test-management types that have no business in a development
project: **Test, Test Set, Test Plan, Test Execution, Sub Test Execution,
Precondition**. Exclude them at the source query, not at the mapping stage — a
type that is never read cannot be mis-mapped.

The real gap is four types needing a rule onto Task/Story/Bug:
**Improvement, New Feature, Production Issue, Config Change**.

Shared by both: Epic, Bug, Story, Task, Sub-task.

### `Sprint` is a trap

The source has **two** fields named `Sprint`, one a `textfield` and one a
`select`, and **neither** is the greenhopper sprint field the target uses
(`com.pyxis.greenhopper.jira:gh-sprint`). Never auto-map it. A target sprint id
is meaningless coming from another tenant anyway.

Other name-matched type clashes worth knowing: `Impact` (polaris rating vs
select), `End Date` (datetime vs datepicker — silently truncates), `Category`
(cascadingselect vs jwm-category), `Location`, `Workaround` (textfield vs
textarea, safe to widen). The rest are Checklist/Polaris/CMDB app fields nobody
would copy.

### Do NOT trust `phase0-compare.ps1`'s headline number

It reported "565 decisions". That is wrong, and the script's own build-vs-buy
warning fired on it. `GET /rest/api/3/field` is **site-wide**: of 615 source
fields, 328 are scoped to unrelated projects on the source-company tenant — risk
registers, project portfolios, timesheets. None appear on an SOURCE_PROJECT issue.

The real surface is *fields populated on real SOURCE_PROJECT issues* ∩ *fields on the TARGET_PROJECT
create screen*. `phase0-surface.ps1` computes exactly that. Expect a number in
the tens.

## Not settled yet

- **The real mapping surface.** `phase0-surface.ps1` has not been run. Run it
  for `Bug` and again for `Story` — their create screens can differ.
- **Required target fields with no source value and no default.** These make a
  create fail outright and need configured constants. The surface script lists
  them; nothing should be built against a guess.
- **How many SOURCE_PROJECT reporters exist on your-company.** Decides whether the user
  mapping table is a footnote or a feature.
- Volume and trigger: one ticket at a time, or batches.

## Stack decision

Mirrors ReleaseTool, because the plumbing is proven and the deployment story is
already solved — not because the domains are related.

- Backend: ASP.NET Core 10 minimal API (`src/TicketCloner.Api`), `net10.0`,
  SDK pinned in `global.json`
- Frontend: React + TypeScript via Vite (`ClientApp/`), built into `wwwroot`
  on publish, served same-origin
- Tests: xunit + `WebApplicationFactory` (`tests/TicketCloner.Api.Tests`)
- Auth: Atlassian API tokens
- **Ports 5002 (API) / 5174 (Vite)**, not 5000/5173. `launchSettings.json`,
  `vite.config.ts` and `.claude/launch.json` must all agree — a mismatch makes
  every `/api` call 404 with no obvious cause, which ReleaseTool hit once
  already.
  GOTCHA: 5001 was the first choice and is **not free** on the dev machine —
  `OriginatorPortalAPI.exe` (the op-api project) binds **both 5000 and 5001**,
  and ReleaseTool uses 5000 too. Check with
  `Get-NetTCPConnection -LocalPort <port> -State Listen` before picking a port.
- The `.claude/launch.json` entries are named `ticketcloner-api` and
  `ticketcloner-clientapp`, not `api` and `clientapp`. ReleaseTool's config uses
  the plain names, and a bare `api` resolves to whichever repo the session is
  rooted in — which is how a run here tried to bind ReleaseTool's port.
- `NuGet.config` with `<clear />` and nuget.org only. The machine-level
  `LWTLibraries` feed 401s without credentials.
- Serilog.AspNetCore is the only API package. `AddHttpClient` is in the shared
  framework — do **not** add `Microsoft.Extensions.Http`.

## Credentials

Same precedence as ReleaseTool: request headers first, configuration second.

```
X-Source-Email / X-Source-Token
X-Target-Email / X-Target-Token
```

Headers win so a caller acts as themselves — the created issue's history stays
honest on an instance that holds a credential of its own.

```
dotnet user-secrets set "Credentials:TargetApiToken" "<token>" --project src/TicketCloner.Api
```

GOTCHA: user-secrets load **only in Development**. A published IIS site sees
nothing however correct the secrets file is; use environment variables
(`Credentials__TargetApiToken`, double underscore) or a vault. Keys are listed
**empty** in `appsettings.json` so they are discoverable; filling them in there
commits a live credential.

`StoredCredentialsOptions` is **not** `ValidateOnStart`-validated — empty is a
legitimate state. The tenant options are.

`GET /api/config` reports `configured: true/false` per tenant plus the emails,
never a token, and sits outside the credentials-guarded group because the UI
reads it before it has any.

## Pipeline

1. **Select** — read candidate issues from the source board/project. Exclude the
   six Xray types in the JQL.
   GOTCHA: `POST /rest/api/3/search/jql` pages by an **opaque
   `nextPageToken`** and reports no `total`. The old `startAt`/`total` contract
   on `/rest/api/3/search` is gone, so anything written against an older example
   silently returns one page and looks complete.
2. **Read** — `GET /rest/api/3/issue/{key}` on the source, with comments and
   attachment metadata.
   GOTCHA: pass **`expand=names`** or every custom field is an opaque
   `customfield_NNNNN` and nothing can be mapped, since mapping is by name.
   Comments come from `/issue/{key}/comment`, which *does* still page by
   `startAt`/`total` — the two endpoints disagree, and both loops are needed.
3. **Target metadata** — `GET /rest/api/3/issue/createmeta/{TARGET_PROJECT}/issuetypes`,
   then `/issuetypes/{id}` for the fields.
   GOTCHA: **paginated, default page 50.** A project this size is silently
   truncated without the `startAt` loop, which understates the create screen and
   makes the mapper drop fields that were actually available.
   GOTCHA: **the two createmeta endpoints use different collection keys, and
   neither is `values`.** Verified against the live TARGET_PROJECT project on 18/08/2026
   by `scripts/probe-createmeta.ps1`:

   | Endpoint | Collection key |
   |---|---|
   | `…/createmeta/{project}/issuetypes` | `issueTypes` |
   | `…/createmeta/{project}/issuetypes/{id}` | `fields` |

   Most Jira paginated endpoints return `values`, and reading that here yields
   an **empty array with no error** — which downstream reads as "the project has
   no issue types" or "the create screen is empty", and produces a plan that
   looks tidy and is completely wrong. `ReadAllPagesAsync` therefore throws when
   the expected key is absent rather than returning nothing.

   This shipped as a real bug: a `New Feature` → `Story` rule failed with "the
   target has no such issue type" when `Story` (id 10001) was creatable all
   along. The test fake had the same wrong key, so the whole suite passed.
   **Build target-side fixtures from a captured real response**, not from
   assumption.
4. **Map** — produce a `MappingPlan` (see below). This is the preview.
5. **Create** — `POST /rest/api/3/issue` on the target.
6. **Extras**, in order: provenance field, remote link, comments, attachments,
   transitions.

Preview and apply are **separate endpoints**, and apply takes the plan back from
the client, so the two cannot disagree. Same pattern as ReleaseTool's
resolve→apply.

## Field mapping

The engine takes a **source-issue DTO** and knows nothing about which tenant
produced it. That is what lets the whole thing be developed and tested
your-company→your-company before touching source-company.

Rules:

1. **Map by field name, never by id.** `customfield_EXAMPLE_ID` on the source is a
   different field on the target. Build `sourceName → targetFieldId` from the
   target's createmeta.
2. **Match on name AND type.** A name match with a differing `schema.custom` is
   the dangerous case — it looks mappable and will either 400 or write the wrong
   shape of value. Treat it as unmapped and report it.
3. **A field not on the target's create screen is a hard 400.** Anything
   unmapped is dropped and reported, never sent hopefully.
4. **Value mapping is separate from field mapping.** Issue type, priority and
   every single-select carry per-tenant ids. Match on name against
   `allowedValues`; no match fails the row with the list of what is allowed.
5. **Never settable on create:** `status`, `resolution`, `created`, `updated`,
   `creator`, `votes`, `watches`, `attachment`, `comment`, `issuelinks`.
6. **Priority:** source `None` → target `Medium`. Target-only `Trivial` is
   unreachable and that is fine.
7. **Status:** not settable, and copies should land in the target's initial
   status regardless. Do not build an 18-rule status map; build one rule and a
   post-create transition if a specific landing status is ever wanted.
8. **Resolution:** only matters if closed tickets are copied. Defer.

`MappingPlan` per field: source value, target field, mapped value, and a status
of `Mapped` / `Dropped (not on create screen)` / `Unmappable (no matching
value)`. That object is the preview table.

## User mapping

Order of resolution for reporter, assignee and mentions:

1. **accountId as-is.** Shared across tenants for anyone with an account on
   both. Verify the account exists on the target before using it — a stale id
   writes a dead link.
2. **Configured mapping table**, source accountId → target accountId, for people
   who exist on one side only.
3. **Fall back to the running account**, with the original person's display name
   preserved in the provenance field and a line at the top of the description.

Email matching is **not** an option in the source direction — source-company hides
email addresses.

Expect the fallback to be a normal path, not an error case.

## Provenance and idempotency

Cross-tenant issue links do not exist. `POST /rest/api/3/issueLink` is same-site
only, so the `Cloned by` link cannot be written.

- **Remote link** — `POST /rest/api/3/issue/{key}/remotelink` with the source
  URL. Good for a human, useless for a machine: **remote links are not
  JQL-searchable**.
- **Provenance custom field** — a text field on TARGET_PROJECT named per
  `Atlassian:Target:ProvenanceFieldName` (default `TARGET_PROJECT Source Key`), holding the
  source key. This *is* JQL-searchable, and is the duplicate check:
  `"TARGET_PROJECT Source Key" ~ "SOURCE_PROJECT-1234"` before creating anything.

We control the your-company tenant, so adding this field is ours to do. Do it
before the first real run — without it, a second run silently duplicates
everything.

## ADF rewriting

Descriptions and comments are ADF, and a tree written for one tenant renders
broken on the other. Before create:

- `mention` → plain text (or a re-resolved accountId when the mapping has one).
  An unmapped mention renders as a dead link.
- `inlineCard` / `blockCard` → an absolute URL to the **source** site. A
  smartlink to `SOURCE_PROJECT-1234` means nothing on your-company.
- `media` nodes reference attachments by id in the source tenant; they must be
  rewritten to the re-uploaded attachment or stripped.

ReleaseTool's `Adf/AdfText.cs` flattens ADF and is worth lifting as a starting
point, but this needs a *rewriter*, not a flattener.

## Comments and attachments

- Comments: one `POST /issue/{key}/comment` each. **They will all be authored by
  the running token** — Jira has no impersonation on this route. Prefix each body
  with the original author and timestamp or the history is a lie.
- Attachments: download `GET /rest/api/3/attachment/content/{id}` from the
  source, re-upload multipart to `POST /issue/{key}/attachments` on the target
  with header `X-Atlassian-Token: no-check`. Watch the target's size cap.

## Failure policy

**Failures are per ticket and never abort the batch.** Tickets are independent —
unlike ReleaseTool's cherry-pick run, where every operation moves the branch head
and one conflict strands everything after it. One refusal here must not sink the
rest.

Report per ticket, with the reason, so a person knows exactly what to finish by
hand.

Jira Cloud returns **429 with `Retry-After`** under bulk load. ReleaseTool never
needed backoff; this does.

## Considered and rejected

- **Jira Automation's Clone action** — same-site only. Cross-tenant it can only
  fire an outgoing webhook, which means something has to be listening, which is
  this tool. The "maybe you don't need to build it" escape hatch does not exist
  here.
- **Exalate / Unito / Backbone Issue Sync** — these solve cross-tenant sync
  properly, including the mapping tables. Right answer *if the requirement ever
  becomes two-way sync*. Wrong answer for one-shot copies under our own control,
  and they need a third party holding both tenants' credentials.

## Discovery scripts

Read-only, run by hand, not part of the app. Keep them with the repo.

- `phase0-profile.ps1` — profiles one tenant to JSON. Run once per site.
- `phase0-compare.ps1` — offline diff of two profiles. **Its headline number is
  inflated; see above.**
- `phase0-surface.ps1` — the real mapping surface. Not yet run.

## Test target

Develop and test **your-company → your-company**, into a throwaway sandbox project,
before pointing anything at source-company. That exercises createmeta, field and value
mapping, create, transitions, attachments, comments, preview and apply — every
hard part — with no cross-tenant risk.

Swapping the source reader to source-company is then one client and a config change.

Integration tests host the real app via `WebApplicationFactory` and swap only the
outbound HTTP handler, so middleware order, the credentials filters and options
validation are all exercised. Needs **two** fake tenants, and at least one test
proving a source-tenant call never carries the target's token.

Gotchas inherited from ReleaseTool's test suite, all still true:

- Serilog's request logging writes through the **static** `Log.Logger`, which
  every hosted app overwrites at startup — hence
  `[assembly: CollectionBehavior(DisableTestParallelization = true)]`.
- Serilog.Settings.Configuration finds sinks by scanning the entry assembly's
  dependencies, which is not this app under `dotnet test`. Name them in
  `Serilog:Using` in `appsettings.json`.
- Clear the `Credentials` section in the test host. `appsettings.json` is copied
  into the test output, so a credential configured there makes every
  "no credentials → 401" test pass through as authenticated.

## Program.cs orderings that matter

```csharp
app.UseSerilogRequestLogging();   // BEFORE UseExceptionHandler, or every handled
app.UseExceptionHandler();        // 404/409 is logged as a 500
...
app.Map("/api/{**path}", () => Results.NotFound());  // BEFORE the fallback, or a
app.MapFallbackToFile("index.html");                 // mistyped /api path answers
                                                     // 200 with the SPA shell
```
