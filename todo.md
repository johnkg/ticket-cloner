# TODO

Things parked deliberately, with enough detail to act on without the
conversation that found them.

## Open

### Redeploy with the popup change

Popup confirmed working in development on 17/09/2026. Nothing on IIS has it yet. Publish **from PowerShell, not Git Bash** (MSYS
rewrites `/ticket-cloner/` to `C:/Program Files/Git/ticket-cloner/`):

```powershell
dotnet publish src/TicketCloner.Api -p:PublishProfile=SelfContained -p:AppBase=/ticket-cloner/ -o ./publish
```

then copy `publish\` over the site folder. Everybody is signed out by the
restart - `InMemoryTokenStore`.

### `AllowedHosts` is still `"*"`

Ships wide open in `appsettings.json`. Narrow it to the real host name on the
site, and add IP-restriction guidance to DEPLOYMENT.md - the app has **no
authentication of its own**; anyone who can reach the URL uses it as
themselves, so reachability is the only gate.

### DEPLOYMENT.md does not describe the sub-path deployment

The live site is an application **under Default Web Site** at
`/ticket-cloner/`, not its own site, and the document only covers the latter.
Once sign-in works there, add: the `-p:AppBase=/ticket-cloner/` publish, the
blank-page / MIME symptom of getting it wrong, `OAuth__PostSignInUrl` set to
`/ticket-cloner/`, and the callback URL carrying the prefix.

### Deploying for a second client

**Decided 18/09/2026: one deployment per client, one OAuth app per client.**
No code is tenant-specific - every site, project, board and field name comes
from `appsettings.json`; only comments say source-site and target-site. A second
client is a second IIS application with its own `appsettings.json`:

| Key | Set to |
|---|---|
| `Atlassian:Source:BaseUrl`, `ProjectKey`, `AvailableProjects`, `BoardId`, `ExcludedIssueTypes` | their source |
| `Atlassian:Target:BaseUrl`, `ProjectKey`, `ProjectName`, `AvailableProjects`, `BoardId` | their target; `BoardId` must be a scrum board or the sprint toggle refuses |
| `Atlassian:Target:SourceUrlFieldName` | a text field on their target's create screen - it is the duplicate check |
| `Mapping:Priorities`, `Constants`, `FallbackIssueType` | theirs |
| `OAuth__ClientId` / `ClientSecret` / `CallbackUrl` | **their own 3LO app** |
| publish `-p:AppBase=/their-path/` | baked at build time, one publish per mount point |

Two things outside the code, both from the 3LO docs:

- a 3LO app is *"private by default... only you can install and use it. If you
  want to distribute your app to other users, you must enable sharing"* -
  Distribution tab in the developer console. Users then see a "not reviewed by
  Atlassian" notice on consent; cosmetic, but tell them
- the docs describe one callback URL per app, so a second deployment gets its
  own app rather than a second callback on ours

A different Atlassian login is not a problem: sign-in is per browser session,
and the callback only checks that the granted sites match the two configured
ones. The same person with two accounts uses two browser profiles.

Rejected for now: one instance serving several client pairs, chosen in the UI.
Real code - a pairing id on every request, `TenantOptions` resolved per request
instead of at startup, the tests reworked. Worth it past three or four clients,
not before. Write the per-client steps into DEPLOYMENT.md when the first one
happens.

## Done

### Sprint write shape proven live

**Closed 18/09/2026.** A bare integer to `customfield_70002`, PUT after the
create, landed a real copy in a real target sprint. The "never map Sprint"
rule is untouched - it is about the SOURCE sprint, whose id means nothing on
the target; the target's own sprint is read from its own board.

### Issue-type dropdown opened empty on the first click

**Fixed 18/09/2026, confirmed 18/09/2026.** The options were loaded on the
`<select>`'s `onFocus`, and a click focuses and opens the native picker in one
gesture - the picker rendered before the list arrived and did not redraw. Now
loaded the moment the target connects, with a disabled *Loading…* row if a
click somehow still wins.

### Choose tickets by source sprint

**Built and confirmed 18/09/2026.** *Or a sprint on the source board* lists
board 200's sprints (`GET /api/source/sprints?search=`, paged, name-filtered,
active first then newest) and a chosen one narrows the ticket search with
`sprint = {id}` on top of the text box. The id is used for that and nothing
else. Needed the granular `read:sprint:jira-software` and
`read:board-scope:jira-software` scopes - the classic ones answered
`401 scope does not match` - now in `OAuthOptions.Scopes` and on the app.

### Target sprint dropdown in the Copy step

**Built and confirmed 18/09/2026.** Replaces the *current sprint* checkbox:
*Not added*, *Current sprint — {name}* (resolved at run time), then every
target sprint active-first, newest to oldest, with dates and state because
names repeat. `ApplyRequest.SprintId` wins over `AddToActiveSprint`, is looked
up once per run, and a bad id costs one reported step rather than a 400 per
ticket.

### Header credentials removed

**Done 17/09/2026.** `X-{tenant}-Email` / `X-{tenant}-Token` no longer
authenticate anything; `CredentialsResolver`, the Basic `AtlassianCredentials`
and the dead `Atlassian:{tenant}:ApiBaseUrl` config went with them. Signing in
is the only way to reach a tenant. The test suite seeds a grant into the token
store (`TestApp(signedIn: true)`, `grants:` to narrow it) and the fakes route by
cloud id. `Credential_headers_no_longer_authenticate_anything` holds it shut.

### Prod `invalid_redirect_uri`, API tokens, sprint scope

**Closed 17/09/2026** on confirmation: OAuth signs in on the IIS site, the two
exposed API tokens are revoked, and the console app's scopes are in place.
`OAuth:Scopes` in code still requests only the four classic scopes — if the
*add to active sprint* toggle 403s under OAuth, `read:sprint:jira-software` is
the missing one and goes there.

### Popup sign-in

**Confirmed working 17/09/2026.** Ported from CompanionTool: the sign-in control
opens `/api/auth/start` in a popup, both consent rounds run inside it, and it
hands the outcome back to the opener by `postMessage` and closes. The opener
re-reads auth status and reconnects without reloading, so a sign-in mid-run no
longer loses the preview. Modified clicks and blocked popups fall through to the
plain navigation. Details in CLAUDE.md, "What the UI does with it".

### `TGT Source Key` dropped

**Decided 16/09/2026.** `Atlassian:Target:ProvenanceFieldName` and the write
behind it are gone. `External Issue ID` already holds a JQL-searchable record
of origin and is the duplicate check; a second field for the same fact would
only drift from the first. The test fake keeps a field of that name on the
create screen and `A_field_named_TGT_Source_Key_on_the_target_is_left_alone`
proves nothing writes it.

### Two publish scripts

**Closed 16/09/2026; superseded 18/09/2026** by `Publish-Iis.ps1`, which runs
the same publish and then checks the output (settings files, secret, web.config,
asset paths) before zipping it and printing the server steps. The one-liner is
gone.

### CLAUDE.md was stale in two places

**Fixed 16/09/2026.** *"`Sprint` is a trap"* now records the third source
field, `customfield_70003` (greenhopper), the live 400 it caused and the
`NeverMappedByName` fix. The issue-type section now describes
`ResolveIssueType`'s actual order — configured override, same name,
`FallbackIssueType` (`Task`), else fail — and why `Mapping:IssueTypes` ships
empty.

### Duplicate check on Title + External Issue ID

**Implemented 19/08/2026.** Before this there was no duplicate check running at
all: it hung off a field named `TGT Source Key` that does not exist on the
target, so every re-run created another copy. Two copies of the same source
issue were already sitting in TGT when this was written.

It now matches on two things, both exactly: the target's `External Issue ID`
(`customfield_70008`) must hold the source issue's URL, and the two summaries
must be identical. Jira has no separate "title" field — the summary is it.

JQL only narrows; the decision is made by comparing exactly in code, because
`~` is a tokenised text match rather than equality. Verified against the live
target before building on it:

| Probe | Result |
|---|---|
| `"External Issue ID" ~ "SRC-nnnn"` where the field holds a full URL | matches — the key tokenises whole |
| `~ "SRC-nnn"` (a prefix) | matches nothing, so no over-matching |
| A key with no copy | matches nothing |

A candidate whose External Issue ID agrees but whose title has been reworded is
**not** treated as a duplicate — the rule is that both must match — but it is
reported, because silently making a second copy of a ticket somebody merely
renamed is the wrong kind of quiet.

`TargetIssueWriter.FindExistingCopyAsync` asks for up to 50 candidates rather
than one, so a drifted title cannot hide a later issue that still matches
properly. A bare key in the field counts as well as a URL, so copies made before
the field was populated this way are still recognised.

### Inline images in a copied description

**Settled 18/08/2026 against the live target.**

A copied description lost its images even though the files copied fine. Two
causes, one after the other:

1. Attachments were uploaded *after* the description was written, so there was
   nothing to point the media nodes at. Fixed by reordering: create, upload,
   then rewrite the description a second time.
2. The rewritten node still did not work. A `file` media node needs a **Media
   Services UUID**, and no REST endpoint returns one for an attachment uploaded
   over the API — `GET /rest/api/3/attachment/{id}` returns only `id`, `self`,
   `filename`, `author`, `created`, `size`, `mimeType`, `properties`,
   `content`, `thumbnail`. Sending the attachment id instead is accepted with a
   **204 and then silently discarded**, which is why the copy came back with no
   media node at all.

Resolved by emitting an **external** media node pointing at the target's own
attachment content URL, matched to the source node by file name (`attrs.alt`) —
the only value a media node and an attachment record share. Verified against a live copy: Jira stores the node and
`expand=renderedFields` returns a real `<img>`.


### Sprint was being carried across to the target

**Found 18/08/2026 previewing a real source issue. Confirmed live the same day
as a failed copy, then fixed.**

```
Target tenant returned 400 for POST rest/api/3/issue.
{"errors":{"customfield_70002":"Specify a valid value for Sprint"}}
```

| | Field id | `schema.custom` |
|---|---|---|
| Source (source-site) | `customfield_70003` | `com.pyxis.greenhopper.jira:gh-sprint` |
| Target (target-site) | `customfield_70002` | `com.pyxis.greenhopper.jira:gh-sprint` |

The custom type keys were identical, so `TypesAgree` returned true and the
source object was carried over whole — sprint `8003` on board `200`, both of
which belong to source-site. A sprint id means nothing on the other tenant, so
there was never a correct value to map it to.

Fixed by `NeverMappedByName` in
[`FieldMapper.cs`](src/TicketCloner.Api/Mapping/FieldMapper.cs), keyed by
**name** because the field id differs per tenant — which is exactly why
`NeverSettableOnCreate`, keyed by id, could not express it. Sprint is now
dropped and the row says why. Covered by
`Sprint_is_dropped_whatever_type_the_source_carries` across all three source
shapes, and end-to-end by
`Sprint_is_dropped_even_though_both_tenants_agree_on_its_type`, with both
tenant fakes carrying a greenhopper Sprint so the old fixture gap cannot
reopen.
