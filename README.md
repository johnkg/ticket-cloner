# TicketCloner

Copies a Jira ticket from one Atlassian **tenant** to another, with a preview
step before anything is written.

Reads an issue from the **SOURCE_PROJECT** project on `source-domain.atlassian.net` and creates
a counterpart in the **TARGET_PROJECT** project on `target-domain.atlassian.net`. Copies are
one-shot snapshots — the copy then lives its own life, and later edits to the
original do not flow through.

Separate from ReleaseTool on purpose: copying tickets has nothing to do with
cutting a release, and the two have unrelated lifecycles.

> **The tenant names are inverted.** The project called **TARGET_PROJECT** is on the
> **your-company** tenant. The project called **YOUR_SOURCE_PROJECT** is on
> the **source-company** tenant. Everything in this codebase says *source* and
> *target*, never "TARGET_PROJECT" or "YOUR_COMPANY", because naming a thing after the project
> resolves it to the wrong site. See [CLAUDE.md](CLAUDE.md).

## Status

| Phase | State |
|---|---|
| 0 — tenant discovery | Both tenants profiled. `phase0-surface.ps1` not yet run |
| 1 — scaffolding | Done |
| 2 — credentials and tenant clients | Done |
| 3 — read side | Done |
| 4 — field mapping | Done. `Mapping:Constants` still needs populating from the surface run |
| 5 — apply | Done. Needs the `TARGET_PROJECT Source Key` field created on the target before a real run |
| 6 — UI | Done. Not yet exercised against a live tenant |

## Requirements

- .NET SDK 10.0.302 or later (pinned in `global.json`)
- Node 20+
- An Atlassian API token. One account currently reaches **both** tenants, so a
  single token serves both — create one at
  <https://id.atlassian.com/manage-profile/security/api-tokens>

## Running it

Two processes. The API serves `/api`; Vite serves the SPA and proxies `/api`
across to it.

```powershell
dotnet watch --project src/TicketCloner.Api
```

```powershell
npm run dev --prefix ClientApp
```

Then open **<http://localhost:5174>** — the Vite port, not the API port. They
only share an origin after publish, when the built SPA lands in `wwwroot`.

Ports are 5002 (API) and 5174 (Vite) rather than the defaults, so ReleaseTool
can run at the same time. `launchSettings.json`, `vite.config.ts` and
`.claude/launch.json` must agree; a mismatch makes every `/api` call 404 with no
obvious cause.

**5001 is not free on the dev machine** — `OriginatorPortalAPI.exe` binds both
5000 and 5001, and ReleaseTool uses 5000. Before changing the port, check:

```powershell
Get-NetTCPConnection -LocalPort 5002 -State Listen
```

Quick check that the proxy is wired up — this must return **404**, not the Vite
page:

```powershell
curl http://localhost:5174/api/does-not-exist
```

## Using it

Four steps down one page:

1. **Credentials** — one pair per tenant. "One account for both tenants" is
   ticked by default because a single Atlassian account currently reaches both
   sites, but the fields stay separate. *Check access* reports who each
   credential authenticates as, and says so when a tenant hides email addresses.
2. **Choose tickets** — search the source project by issue key or summary text.
   The Xray types are already excluded by the query, which the panel will show
   you on request.
3. **Preview** — one plan per ticket. Rows arrive worst-first: fields that block
   the copy, then fields that need attention, then dropped, then mapped. A
   ticket with blockers is marked and will be left out.
4. **Copy** — a confirmation states how many issues will be created, in which
   project on which host, and how many fields could not be mapped. Results list
   every ticket with its per-step outcome.

## Credentials

Nothing is typed into the UI. The tool reads its credentials from configuration
on the server and connects on load, so no token ever reaches the browser.

They go in **`appsettings.json`**, under `Credentials`:

```json
{
  "Credentials": {
    "SourceEmail": "you@example.com",
    "SourceApiToken": "<token>",
    "TargetEmail": "you@example.com",
    "TargetApiToken": "<token>"
  }
}
```

Create tokens at
<https://id.atlassian.com/manage-profile/security/api-tokens>. One account
currently reaches both tenants, so the same pair works in both.

**Restart the API after editing.** Configuration is read at startup.

### Which copy of appsettings.json

There are two, and the difference matters:

| Copy | Fill it in? |
|---|---|
| `src/TicketCloner.Api/appsettings.json` | Only for local development, and never commit it |
| `<publish folder>/appsettings.json` | **Yes** — this is the deployed instance's own config |

`appsettings.json` is **not gitignored** and **is copied into the publish
output**. A token typed into the copy under `src/` therefore travels with the
source *and* with every deployment. Filling in the published copy instead keeps
it out of both, and is the recommended shape.

`src/TicketCloner.Api/appsettings.json` is tracked, so `git add .` picks it up
along with whatever is in it. Check the `Credentials` section is still empty
before every commit that touches it.

### Overrides

Both still work and both beat `appsettings.json`:

- **`appsettings.Local.json`**, loaded last of the files and gitignored. If this
  file exists it **silently overrides** the credentials in `appsettings.json` —
  delete it rather than emptying it, or it will shadow what you set.
- **Environment variables**, which beat everything —
  `Credentials__TargetApiToken`, with two underscores. The right answer for a
  server you would rather not keep a token on disk for.
- **Request headers** beat all configuration, so a scripted caller can act as
  itself: `X-Source-Email`/`X-Source-Token` and `X-Target-Email`/`X-Target-Token`.
  Nothing in the UI sends these.

If a credential is wrong the app says so on load, in a dialog naming the file,
the keys and the restart — rather than failing later on some unrelated call.

## Endpoints

| Endpoint | Credentials | Purpose |
|---|---|---|
| `GET /health` | none | Liveness |
| `GET /api/config` | none | Which sites this instance talks to, and whether it holds a credential for each. Never returns a token |
| `GET /api/source/me` | source | Who the source credential authenticates as |
| `GET /api/target/me` | target | Who the target credential authenticates as |
| `GET /api/source/issues` | source | Candidate issues to copy. `?search=` takes an issue key or free text; `?pageToken=` continues. Returns the JQL it used |
| `GET /api/source/issues/{key}` | source | One issue as a tenant-agnostic DTO: fields keyed by **name**, raw ADF description, all comments, attachment metadata |
| `GET /api/target/issuetypes` | target | Issue types creatable in the target project |
| `GET /api/target/issuetypes/{id}/fields` | target | The create screen. Anything absent from it is a hard 400 if sent |
| `GET /api/preview/{key}` | both | The `MappingPlan` — every field, what it maps to, and why. `?issueType=` overrides the resolved type. Reads both tenants, writes to neither |
| `POST /api/apply` | both | **The only endpoint that writes.** Takes the plans the preview produced and creates their counterparts, one outcome per ticket |

### Before the first real run

Create a text custom field named **`TARGET_PROJECT Source Key`** on the TARGET_PROJECT project and put
it on the create screen. It holds the source issue key and is the duplicate
check — remote links are not JQL-searchable, so without this field a second run
silently duplicates everything. Apply reports it as a failed step rather than
guessing, and the duplicate check is skipped while it is missing.

`/api/config` is deliberately unguarded — the UI reads it before it has any
credentials, to decide what to prompt for.

## Tests

```powershell
dotnet test
```

Integration tests host the real app through `WebApplicationFactory` and swap
only the outbound HTTP handler, so middleware order, the credentials filters and
options validation all run as they do in production.

The suite that matters most is `TenantIsolationTests` — two tenants and two
credentials means the worst realistic bug is a request reaching one site
carrying the other's token. Those tests assert the host and the exact
`Authorization` header on every outbound call.

## Discovery scripts

Read-only, run by hand, not part of the app. See `scripts/`.

| Script | What it does |
|---|---|
| `check-token.ps1` | Whether one credential reaches both tenants, and whether it is the same accountId on each |
| `phase0-profile.ps1` | Profiles one tenant to JSON. Run once per site |
| `phase0-compare.ps1` | Offline diff of two profiles. **Its headline number is inflated** — `/rest/api/3/field` is site-wide and counts fields belonging to unrelated projects |
| `phase0-surface.ps1` | The real mapping surface: fields populated on actual source issues ∩ fields on the target create screen. Not yet run |

The profile JSON holds no credentials and is worth keeping as the
before-picture.

## Project layout

```
src/TicketCloner.Api/        ASP.NET Core 10 minimal API
  Atlassian/                 Tenant clients, credentials, error handling
  Configuration/             Options and validation
  Contracts/                 Request and response DTOs
  Endpoints/                 Route definitions
ClientApp/                   React + TypeScript (Vite), built into wwwroot on publish
tests/TicketCloner.Api.Tests/
scripts/                     Phase 0 discovery scripts
```

## Publishing

```powershell
dotnet publish -c Release
```

The SPA is built into `wwwroot` by the `BuildSpa` target on publish only, so
day-to-day `dotnet build` stays fast. `RestoreSpa` runs `npm ci`, which needs
`ClientApp/package-lock.json` — do not gitignore it.

### What the target machine needs

**Node is not a runtime dependency.** `npm` runs on the machine doing the
publish; what ships is the static output in `wwwroot`.

The .NET runtime is the only real prerequisite, and it can be bundled:

| | Output | Target machine needs |
|---|---|---|
| Default | 1.2 MB | ASP.NET Core 10 runtime installed |
| `-p:PublishProfile=SelfContained` | 107 MB | Nothing |

```powershell
dotnet publish src/TicketCloner.Api -p:PublishProfile=SelfContained -o ./publish
```

**IIS is the exception.** It needs the ASP.NET Core Module, which arrives with
the Hosting Bundle, and no publish setting removes that — the module is the
piece IIS uses to hand requests to the app. Self-contained still helps there:
the app stops caring which runtime version the server has.
