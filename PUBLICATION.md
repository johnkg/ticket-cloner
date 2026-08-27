# Source publication boundary

The repository uses synthetic tenant hosts, project keys, people, customer names, issue content, and custom field IDs. Configure your own values before use. Empty OAuth values are intentional; provide credentials privately.

## What to share

Share a reviewed source-only export. Include source, tests, package manifests, example configuration, documentation, and generic helper scripts. Exclude Git metadata and history, local settings, live API exports, dependency directories, logs, test output, and compiled or packaged deployments. Old generated artifacts were removed because they may retain previous identifiers or embedded machine paths.

Run `python3 scripts/check-public-source.py` before preparing an export. The check examines first-party files, including ignored local configuration, but excludes generated output and dependencies. It rejects private email domains, personal filesystem paths, unexpected tenant domains, nonempty credentials in appsettings files, and common token formats. It is a practical guard, not a guarantee that every possible name or secret is detected.

## Existing checkout history

The existing local Git history contains private identity metadata and historical company references. Editing working files does not remove those records. Do not publish the existing repository history, its Git directory, reflogs, or a raw folder archive. Start a new repository from the reviewed source-only export and use an appropriate public commit identity. Rewriting the original history is outside this cleanup.

An OAuth client secret was present in local configuration and has been cleared. Regenerate that secret with the provider before using the application again. Clearing a file does not revoke a credential or erase copies outside this checkout.

## Hosting is a separate decision

This cleanup addresses source privacy. It does not certify a running instance for unrestricted public internet access, establish rights to redistribute third-party or employer-owned code, or replace deployment security review. The application accesses Jira data and can write to its configured target; restrict deployment access and review authentication, session handling, authorization, and operational configuration before hosting it.

Third-party package and platform names remain because they identify required dependencies. Their licenses and attribution must remain intact.
