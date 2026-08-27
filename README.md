# TicketCloner

Copies a Jira ticket from one Atlassian **tenant** to another, with a preview
step before anything is written.

Reads an issue from the **SRC** project on `source.example.invalid` and creates
a counterpart in the **TGT** project on `target.example.invalid`. Copies are
one-shot snapshots — the copy then lives its own life, and later edits to the
original do not flow through.

All tenants, project keys, people, and sample issue content in this checkout are placeholders. The reserved `source.example.invalid` and `target.example.invalid` hosts cannot identify a real Jira site. Configure your own tenants and boards locally before connecting.

See [PUBLICATION.md](PUBLICATION.md) for the source publication boundary and checks. Source publication does not establish that a running instance is safe for public internet access.

## Status

| Phase | State |
|---|---|
| 0 — tenant discovery | Both tenants profiled. `phase0-surface.ps1` not yet run |
| 1 — scaffolding | Done |
| 2 — credentials and tenant clients | Done |
| 3 — read side | Done |
| 4 — field mapping | Done. `Mapping:Constants` still needs populating from the surface run |
| 5 — apply | Done |
| 6 — UI | Done. Not yet exercised against a live tenant |

## Requirements

- .NET SDK 10.0.302 or later (pinned in `global.json`)
- Node 20+
- An Atlassian OAuth 2.0 (3LO) app registered with an **account-level** grant
  — see "Signing in" below. There is no API-token path; without an app, nobody
  can reach either tenant.

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

Development uses ports 5002 (API) and 5174 (Vite). Keep `launchSettings.json`, `vite.config.ts`, and `.claude/launch.json` aligned. Check for a local port conflict before starting:

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

1. **Sign in** — one button, top right. It covers both tenants; see
   [Signing in](#signing-in). The Connection panel then reports who you are on
   each site, and says so when a tenant hides email addresses.
2. **Choose tickets** — search the source project by issue key or summary text.
   **Several keys at once** are fine: paste them separated by commas, spaces or
   new lines and they become one `key IN (...)` query. One non-key among them
   makes the whole thing a summary search instead, rather than quietly copying
   the subset it recognised. Any key the source did not return is named, so a
   batch cannot come up short without saying so. Selection survives a new
   search, so a batch can be assembled over several of them. The Xray types are
   already excluded by the query, which the panel will show you on request.
3. **Preview** — one plan per ticket, rows split by status: fields that block
   the copy, then those needing attention, then dropped, then mapped. A ticket
   with blockers is marked and cannot be copied.

   Preview also says whether the ticket **is already on the target**, naming the
   existing issue and linking to it, so it can be opened and checked before
   anything is decided.
4. **Copy** — every previewed ticket is listed with a tick box, the type it
   would be created as, and any copy already on the target. **Anything already
   copied starts unticked**, so re-copying it is a deliberate act. The
   confirmation states how many issues will be created, where, how many of them
   already have a copy, and how many fields could not be mapped. Results list
   every ticket with its per-step outcome.

### Epics

A ticket sitting under an Epic is copied under the matching Epic on the target.

The preview names the Epic the source ticket belongs to and says whether a
counterpart already exists. If one does, the copy is parented under it. If not,
the Epic is listed separately in the copy step, ticked, and cloned first — using
the same mapping rules and provenance as any other ticket — with the copy then
created underneath it.

- Only an **Epic** parent counts. A sub-task's parent is a different
  relationship and is left alone.
- An Epic already on the target is matched on `External Issue ID` first, then on
  an identical summary. That fallback is what recognises an Epic somebody
  created on TGT by hand, which carries no `External Issue ID` and would
  otherwise be cloned a second time.
- Several tickets sharing one Epic create it **once** per run.
- Untick an Epic and the tickets under it are still copied, just without a
  parent. If creating the Epic fails the same applies, and the result says so —
  one awkward parent does not cost you the copy.
- `parent` is set on the create itself; Jira fills in `Epic Link` from it.

## Signing in

Nothing is typed into the UI, and **the tool stores no Atlassian API token**.
There is no `Credentials` section in `appsettings.json` any more; a token there
travelled with the source and with every deployment, and made every issue the
tool created read as the work of one shared account.

Press **Sign in with Atlassian**, top right.

Atlassian consents to **one site at a time**, so expect **two consent screens**
— one for `source.example.invalid`, one for `target.example.invalid`. Grant
whichever it offers first; whatever you consent to is filed under the tenant it
matches, and the app asks for the other one straight afterwards. Pick the same
site twice and it stops asking and names what is still missing.

Consent for a site that is neither of ours is refused before anything is stored.

### Registering the app

Developer console → **Create** → OAuth 2.0 integration, with an
**account-level** grant so one sign-in can reach both sites.

- **Authorization → OAuth 2.0 (3LO)** → Callback URL. Dev:
  `http://localhost:5002/api/auth/callback`. Production:
  `https://<prod-host>/api/auth/callback` — HTTPS is required off localhost, and
  it must match the authorize request exactly.
- **Permissions → Jira API** → classic `read:jira-work`, `write:jira-work`,
  `read:jira-user`, **and** granular `read:sprint:jira-software`,
  `read:board-scope:jira-software` — the classic scopes do not cover sprints,
  and a scope requested but not enabled here fails every sign-in with
  `invalid_scope`. (`offline_access` goes in the authorize URL, not here.)

Then put the client id and secret where the server can read them:

| Where | When |
|---|---|
| `appsettings.Local.json` | Development. Gitignored **and** excluded from publish, and loaded last so it beats everything |
| `dotnet user-secrets set "OAuth:ClientSecret" "<secret>" --project src/TicketCloner.Api` | Development, if you would rather keep it out of the repo folder entirely. Loads **only** in Development |
| `OAuth__ClientId` / `OAuth__ClientSecret` / `OAuth__CallbackUrl` environment variables | Production |

**Never `src/TicketCloner.Api/appsettings.json`** — it is not gitignored and it
is copied into the publish output.

**Restart the API after editing.** Configuration is read at startup, so an edit
alone changes nothing. `GET /api/auth/status` reporting `"available": true` is
how you know all three values arrived; until then the sign-in button stays
hidden.

There is no other way in. The tool stores no API token and, since 17/09/2026,
takes none on request headers either — a caller without a browser session gets
a 401.

## Endpoints

| Endpoint | Credentials | Purpose |
|---|---|---|
| `GET /health` | none | Liveness |
| `GET /api/config` | none | Which sites this instance talks to. Says nothing about credentials |
| `GET /api/source/me` | source | Who the signed-in grant is on the source site |
| `GET /api/target/me` | target | Who the signed-in grant is on the target site |
| `GET /api/source/issues` | source | Candidate issues to copy. `?search=` takes an issue key or free text; `?sprint=` narrows to a source sprint id; `?pageToken=` continues. Returns the JQL it used |
| `GET /api/source/sprints` | source | The source board's sprints, active first then newest to oldest. `?search=` matches part of a name. Ids only ever go back into `?sprint=` above |
| `GET /api/target/sprints` | target | The target board's sprints, same order. Ids only ever go into `POST /api/apply`'s `sprintId` |
| `GET /api/target/active-sprint` | target | The target board's single current sprint, or a reason there is none |
| `GET /api/source/issues/{key}` | source | One issue as a tenant-agnostic DTO: fields keyed by **name**, raw ADF description, all comments, attachment metadata |
| `GET /api/target/issuetypes` | target | Issue types creatable in the target project |
| `GET /api/target/issuetypes/{id}/fields` | target | The create screen. Anything absent from it is a hard 400 if sent |
| `GET /api/preview/{key}` | both | The `MappingPlan` — every field, what it maps to, and why. `?issueType=` overrides the resolved type. Reads both tenants, writes to neither |
| `POST /api/apply` | both | **The only endpoint that writes.** Takes the plans the preview produced and creates their counterparts, one outcome per ticket |

### The duplicate check

Apply refuses to copy an issue that is already here. A candidate counts as a
duplicate when **both** agree:

- **`External Issue ID`** — a text field on TGT holding the source issue's URL,
  written on every copy. Configurable as `Atlassian:Target:SourceUrlFieldName`.
- **The summary**, compared exactly — Jira has no separate "title" field; the
  summary is it.

JQL narrows the search and the comparison is then made in code, because `~` is
a tokenised text match rather than equality. A candidate whose External Issue ID
agrees but whose summary has been reworded is reported rather than skipped — the
rule needs both — so a renamed copy is visible instead of silently duplicated.

Remote links would be the natural place to record origin, but they are not
JQL-searchable, which is why a real field does the work.

`/api/config` is deliberately unguarded — the UI reads it before it has any
credentials, to decide what to prompt for.

## Tests

```powershell
dotnet test
```

Integration tests host the real app through `WebApplicationFactory` and swap
only the outbound HTTP handler, so middleware order, the credentials filters and
options validation all run as they do in production.

The suite that matters most is `TenantIsolationTests` — two tenants behind one
signed-in grant means the worst realistic bug is a request for one site going
to the other's cloud id. Under OAuth every call goes to the same host, so those
tests assert the cloud id in the path of every outbound call, and that a grant
reaching only one site cannot call the other.

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

**Deploying to a server? Read [DEPLOYMENT.md](DEPLOYMENT.md).** It covers IIS
versus a Windows Service, and the three IIS app-pool defaults that otherwise
sign every user out on a timer.


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
dotnet publish src/TicketCloner.Api -p:PublishProfile=SelfContained -p:AppBase=/ticket-cloner/ -o ./publish
```

`Publish-Iis.ps1` runs exactly that and then checks the output before calling
it ready - see DEPLOYMENT.md.

**IIS is the exception.** It needs the ASP.NET Core Module, which arrives with
the Hosting Bundle, and no publish setting removes that — the module is the
piece IIS uses to hand requests to the app. Self-contained still helps there:
the app stops caring which runtime version the server has.

### HTTPS

**The site needs an HTTPS binding and a certificate.** Outside Development the
app redirects plain http to https and sends HSTS. The session cookie is what
reaches a tenant, and it is marked `Secure` only when the request is https —
on plain http it would cross the network in clear.

`Https:Port` in `appsettings.json` is the port the redirect points at. It
defaults to `443`; change it if the binding is on anything else.

Leaving it unset does **not** turn the redirect off. The middleware then hunts
for a port among the server's own bindings and, finding none, logs a warning and
serves the request unredirected — so an unset port on a site with no HTTPS
binding looks exactly like a working deployment.

Development is exempt: `http://localhost:5002` has no certificate behind it.
