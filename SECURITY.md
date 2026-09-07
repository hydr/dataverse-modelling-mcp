# Security Policy

## Reporting a vulnerability

Please report security issues **privately**, not as a public issue:

- By e-mail to `it@crossvertise.com`.
- Or, if [private vulnerability reporting](https://github.com/hydr/dataverse-modelling-mcp/security/advisories/new)
  is enabled on this repository, as a private security advisory.

Please include what the issue allows an attacker to do, how to reproduce it, and the
version (`.claude-plugin/plugin.json` → `version`, or `scripts/BINARY_VERSION`). You
will get an acknowledgement; there is no paid bounty.

Do not include real credentials, tokens, or customer data in a report — a redacted
reproduction is enough.

## Supported versions

Only the latest release is supported. Fixes go into a new version rather than into
patches for older ones.

## What this tool is, security-wise

Worth knowing before you deploy it, and useful context for judging whether something
is a vulnerability:

- **It authenticates as you.** Authentication is delegated user auth (interactive
  sign-in) — no service accounts, no client secrets in the repo. Everything the server
  does, it does with the permissions of the signed-in user, and Dataverse audits it
  under that user.
- **Tokens are cached in the OS keychain**, not in the repo or in plain files. On
  Windows that is DPAPI-backed. A cached token grants the same access as the user
  until it expires.
- **It writes.** These tools create and modify tables, columns, views, security roles,
  workflows, flows and solutions, and they can delete records. There is no dry-run
  mode and no undo. Point it at a development environment first.
- **It runs locally.** The MCP server is a local process speaking stdio to the client;
  it opens no listening port. The binary is downloaded from this repository's release
  assets — see `docs/GH-RELEASE-SETUP.md`.
- **Telemetry is off unless configured.** `dataverse_submit_feedback` is inert unless
  the operator has configured Application Insights. See the Telemetry section of the
  README.

## Out of scope

- Damage caused by intended functionality — a tool that deletes a column deleting a
  column is not a vulnerability. Excessive permissions granted to the signed-in user
  are a Dataverse configuration matter.
- Vulnerabilities in Dataverse or the Power Platform itself. Report those to Microsoft.
- Findings that require an attacker to already control the machine running the server.
