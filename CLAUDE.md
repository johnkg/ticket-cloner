# TicketCloner project guidance

TicketCloner copies Jira issues between a source tenant and a target tenant after a preview. Copies are independent snapshots, not synchronized records.

## Public example configuration

Checked-in configuration uses reserved example hosts, synthetic project keys (`SRC` and `TGT`), example board IDs, and fictional fixtures. These values are not evidence about any live tenant. Configure actual tenant settings locally. Keep credentials, API exports, logs, deployment packages, and personal machine settings out of source publication. Read `PUBLICATION.md` when preparing a public source release.

Use `Source` and `Target` consistently in configuration, UI, logs, and services. A project name must not determine which tenant receives a request. The source client must remain read-only.

## Architecture

- `src/TicketCloner.Api` contains the ASP.NET Core API. `Program.cs` registers configuration, clients, services, middleware, and endpoints.
- `ClientApp` contains the React and TypeScript interface. Vite proxies development API calls; publishing builds static assets for the API to serve.
- `tests/TicketCloner.Api.Tests` exercises the real application with fake outbound HTTP responses.
- `AtlassianClient` separates source and target traffic. OAuth REST calls use the granted cloud ID through the Atlassian API host. User-facing links use the configured site URI.
- OAuth sessions and tokens are server-side. Keep secrets out of logs, responses, committed configuration, and build artifacts.

Package manifests and `global.json` define dependencies and toolchain versions. `README.md` explains local setup; `DEPLOYMENT.md` covers hosting. Historical notes in `todo.md` describe implementation decisions, not instructions to connect to a live tenant.

## Mapping and copy behavior

1. Read source issues, including paged comments, attachments, and structured descriptions.
2. Read target metadata and build a preview. Match fields and allowed values against target metadata; tenant-local numeric IDs are not portable.
3. Resolve issue types by explicit mapping, then exact name, then the configured fallback. Surface unresolved required fields as blockers.
4. Exclude configured test-management issue types from source searches. Never copy source sprint objects directly to a target sprint field.
5. Detect an existing copy by its source URL and summary. The configured source URL field is also the link back to the original.
6. Apply each ticket independently. A failure for one ticket must not stop the remaining batch.
7. Perform optional follow-up operations with separate outcomes. A failed attachment, comment, link, or sprint assignment must not falsely report that issue creation failed.

User mapping uses account IDs and target membership. Email visibility varies; do not assume email lookup is available. If a user cannot be mapped, preserve readable attribution without assigning an unrelated account.

Structured description rewriting must preserve text, replace mapped mentions, retain readable unmapped mentions, resolve relative source links, and replace uploaded attachment references with target site links.

Sprint names can repeat. Select sprints by IDs retrieved from the relevant target board. Epics are containers and are not assigned to sprints. The current-sprint option must refuse ambiguous results rather than guess.

## Verification

Run `dotnet test TicketCloner.slnx`, `npm run build --prefix ClientApp`, and `npm run lint --prefix ClientApp` after changes affecting both layers. Report blocked or failing checks accurately. Fake HTTP tests do not prove real Jira authorization, browser interaction, or production deployment.

Before publication, run `python3 scripts/check-public-source.py`. Distribute reviewed source without `.git`, local settings, raw API responses, dependencies, logs, or old build output. Build fresh deployment artifacts only after supplying appropriate private configuration.
