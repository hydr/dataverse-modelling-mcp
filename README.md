# dataverse-modelling-mcp

[![NuGet](https://img.shields.io/nuget/v/Dataverse.ModellingMcp.Setup?label=dotnet%20tool)](https://www.nuget.org/packages/Dataverse.ModellingMcp.Setup)
[![Release](https://img.shields.io/github/v/release/hydr/dataverse-modelling-mcp)](https://github.com/hydr/dataverse-modelling-mcp/releases/latest)
[![CI](https://github.com/hydr/dataverse-modelling-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/hydr/dataverse-modelling-mcp/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A local [Model Context Protocol](https://modelcontextprotocol.io/) server that lets AI clients (Claude Code, GitHub Copilot, …) **model** Dataverse and the Power Platform — not just read and write records, but build the schema, the views, forms and command bar, the security roles, the processes and the solutions around them.

113 tools across Tables & Columns, Views, Forms, Security Roles, Solutions/ALM, Solution Pipelines, Classic Workflows, Business Process Flows, Cloud Flows, Modern Commands, Classic Ribbons, Web Resources, Environment Variables and Analysis — all via delegated user auth (no secrets, no service accounts), with a [read-only mode, a production guard and dry runs](#security--safety).

```powershell
dotnet tool install -g Dataverse.ModellingMcp.Setup   # then: dataverse-modelling-mcp
```

→ [Quickstart](#quickstart) · [Security & safety](#security--safety) · [Why not Microsoft's Dataverse MCP server?](#why-not-microsofts-dataverse-mcp-server)

---

## Tools

| Group | Tools |
|-------|-------|
| **Classic Workflows** | `workflow_list`, `workflow_get`, `workflow_export_xaml`, `workflow_create`, `workflow_update`, `workflow_set_state`, `workflow_delete`, `workflow_assign`, `workflow_list_activities`, `workflow_get_activity_parameters`, `workflow_explain`, `workflow_get_definition`, `workflow_validate_definition`, `workflow_set_definition`, `workflow_diagnose_activation`, `workflow_restore_xaml`, `workflow_validate` |
| **Business Process Flows** | `bpf_list`, `bpf_get_definition`, `bpf_validate_definition`, `bpf_find_relationships`, `bpf_create`, `bpf_set_definition`, `bpf_update`, `bpf_set_state`, `bpf_delete`, `bpf_set_order`, `bpf_grant_access`, `bpf_export_xaml`, `bpf_restore_xaml`, `bpf_instance_list`, `bpf_instance_start`, `bpf_instance_move`, `bpf_instance_set_status` |
| **Cloud Flows** | `flow_list`, `flow_get`, `flow_create`, `flow_set_state`, `flow_get_runs`, `flow_list_versions`, `flow_get_version`, `flow_publish`, `flow_save_draft`, `flow_restore_version`, `flow_get_clientdata`, `flow_patch_action_input`, `flow_save_draft_and_publish`, `fetchxml_validate`, `flow_trigger_run`, `flow_wait_for_run`, `flow_get_run_actions`, `flow_get_action_outputs`, `flow_describe` |
| **Tables & Columns** | `table_list`, `table_get`, `table_create`, `table_update`, `column_add`, `column_update`, `column_delete` |
| **Records** | `record_upsert` |
| **Views** | `view_list`, `view_get`, `view_create`, `view_update`, `view_add_column`, `view_set_sort` |
| **Forms** | `form_list`, `form_get`, `form_add_control`, `form_replace_control`, `form_remove_tab` |
| **Security Roles** | `role_list`, `role_get`, `role_create`, `role_update` |
| **Environment Variables** | `envvar_list`, `envvar_get`, `envvar_set` |
| **Solutions / ALM** | `solution_list`, `solution_get`, `solution_create`, `solution_export`, `solution_import`, `solution_add_component`, `solution_remove_component`, `solution_check_layers`, `solution_remove_active_layer`, `solution_uninstall` |
| **Solution Pipelines** (read-only) | `pipeline_list`, `pipeline_stages`, `pipeline_environments`, `pipeline_run_status` |
| **Web Resources** | `webresource_list`, `webresource_get`, `webresource_usages`, `webresource_upsert` |
| **Publishing** | `publish_customizations` |
| **Modern Commands** | `command_list`, `command_get`, `command_create`, `command_update`, `command_delete`, `command_list_component_libraries`, `command_list_icons` |
| **Classic Ribbons** | `ribbon_get`, `ribbon_get_merged`, `ribbon_add_button`, `ribbon_remove_button` |
| **Analysis** | `component_dependencies`, `entity_solution_map` |
| **Auth** | `auth_relogin` |

Plus `dataverse_submit_feedback` — an **optional** telemetry tool that is inert unless the server operator has configured Application Insights. See [Telemetry](#telemetry) below.

> Adding a button to a command bar? Read
> **[Classic ribbon vs modern command](docs/tools/buttons-classic-vs-modern.md)** first. It covers which
> mechanism to pick, how a ribbon is really stored (and therefore how to remove a button, which a
> solution import cannot do), and the dozen ways both mechanisms fail completely silently.

---

## Quickstart

You need two things, whichever way you install:

- A **Dataverse / Power Platform environment** — preferably a dev or trial environment, not production.
- An **Azure AD app registration** that is a public client with redirect URI `http://localhost` and
  the delegated Dataverse permission `user_impersonation` (details in
  [docs/authentication.md](docs/authentication.md#app-registration-requirements)). The setup wizard
  in option B creates one for you if the [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli)
  is installed.

### A. Claude Code plugin (Windows, macOS, Linux)

Inside Claude Code:

```
/plugin marketplace add hydr/dataverse-modelling-mcp
/plugin install dataverse-modelling-mcp@dataverse-modelling
```

Claude Code asks for the environment URL and the app's client ID. On the first server start the
plugin downloads the server binary from the latest GitHub release — no .NET SDK, no clone. The first
tool call opens a browser for sign-in. The plugin also brings the [skills](#skills).

Binaries are published for Windows x64, macOS (Apple Silicon and Intel) and Linux x64.

### B. dotnet tool (Windows, macOS, Linux — any MCP client)

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet tool install -g Dataverse.ModellingMcp.Setup
dataverse-modelling-mcp            # setup wizard: app registration, sign-in, environment
claude mcp add dataverse-modelling-mcp -- dataverse-modelling-mcp server
```

The wizard:

1. Optionally creates or locates an Azure AD app registration via the Azure CLI
2. Opens a browser for interactive sign-in (delegated auth — no secret required)
3. Asks for the environment URL, environment ID and region
4. Writes `config.json` and caches the token in the OS keychain / DPAPI
5. Prints the `claude mcp add` command

Any other MCP client (GitHub Copilot, Cursor, …) starts the server with the command
`dataverse-modelling-mcp server` over stdio.

### C. From source

```powershell
git clone https://github.com/hydr/dataverse-modelling-mcp
cd dataverse-modelling-mcp
dotnet run --project src/Dataverse.Setup
claude mcp add dataverse-modelling-mcp -- dotnet run --project /path/to/src/Dataverse.Server
```

### Configuration

The server reads `config.json`, written by the setup wizard:

| OS | Path |
|----|------|
| Windows | `%LOCALAPPDATA%\dataverse-modelling-mcp\config.json` |
| macOS | `~/.dataverse-modelling-mcp/config.json` |
| Linux | `~/.config/dataverse-modelling-mcp/config.json` |

Without a `config.json` it falls back to environment variables — this is how the plugin passes its
settings. The two safety switches at the bottom also apply on top of a `config.json`:

| Variable | Required | Meaning |
|---|---|---|
| `DATAVERSE_URL` | yes | Environment URL, e.g. `https://contoso.crm4.dynamics.com` |
| `AZURE_CLIENT_ID` | yes | Client ID of the app registration |
| `AZURE_TENANT_ID` | no | Tenant ID; default: the tenant the account signs in to |
| `DATAVERSE_ENVIRONMENT_ID` | no | Power Platform environment GUID — needed only for the cloud flow tools |
| `POWER_PLATFORM_REGION` | no | Region for the cloud flow tools, default `europe` |
| `DATAVERSE_READ_ONLY` | no | `true` exposes only read tools — see [Security & safety](#security--safety) |
| `DATAVERSE_ALLOW_PRODUCTION_WRITES` | no | `true` lifts the production guard |

---

## Security & safety

"An AI changes my Dataverse schema?" — yes, and this is what keeps that in bounds:

| Concern | Answer |
|---|---|
| **Whose rights?** | Yours. The server signs in **as you** (delegated OAuth, MSAL) and Dataverse enforces your security roles on every call. No client secret, no service account, no application user. |
| **Production?** | **Write tools refuse to run against production** — organizations of type Customer/Secondary and the tenant's Default environment, as Dataverse reports them. Model in a dev or sandbox environment and promote with a solution or pipeline. Opt out explicitly with `DATAVERSE_ALLOW_PRODUCTION_WRITES=true` (plugin: *Allow writes to production*). |
| **Just looking?** | **Read-only mode** (`DATAVERSE_READ_ONLY=true`, plugin: *Read-only mode*, or `"readOnly": true` in `config.json`) removes all 56 write tools — the client never even sees them. 57 read tools remain. |
| **What exactly would it send?** | **Dry run** on the schema tools (`table_*`, `column_*`, `view_*`, `role_*` writes): pass `dryRun: true` and nothing is changed — the result lists every request the call would send, with URL, headers and the exact body. Reads still run, so an update shows the real merged definition. Dry runs are allowed on production, so you can plan a change there and apply it elsewhere. `workflow_set_definition`, `bpf_set_definition` and `solution_uninstall` have their own validate-and-report dry run (the last one by default). |
| **What will this call do?** | Every tool carries MCP annotations — `readOnlyHint`, `destructiveHint`, `idempotentHint` — so the client can tell reading from changing from deleting, and ask before the latter. In Claude Code every tool call needs your approval unless you allow it. |
| **Undo?** | Before overwriting, export: `workflow_export_xaml` / `workflow_restore_xaml`, `bpf_export_xaml` / `bpf_restore_xaml`, `flow_list_versions` / `flow_restore_version`, `solution_export`. `solution_check_layers` shows what sits on top of a component. |
| **Where does data go?** | Only to your Dataverse and Power Platform endpoints. Tokens stay in the OS keychain / DPAPI. Telemetry is off unless the operator sets it up — see [Telemetry](#telemetry). |

Two honest limits: the production guard checks the environment type on the first write and lets
the call through (with a warning on stderr) if Dataverse does not reveal the type. And delegated
rights cut both ways — in an environment where you are System Administrator, so is the AI. A
dedicated dev environment is still the best guard.

---

## Why not Microsoft's Dataverse MCP server?

Microsoft ships an official [Dataverse MCP server](https://learn.microsoft.com/power-apps/maker/data-platform/data-platform-mcp).
It is a good product — for a different job. It is built around **data**: search, SQL queries,
record CRUD, files. This server is built around **modelling**: everything a maker or developer
does in the solution explorer. The two work side by side.

| | Microsoft Dataverse MCP server | dataverse-modelling-mcp |
|---|---|---|
| **Built for** | Working with data from agents | Building and changing the model |
| **Records & queries** | ✅ `search_data`, `read_query` (SQL), record create/update/delete, file up/download | ➖ only `record_upsert` |
| **Tables** | ✅ create, update, delete table | ✅ create, update; columns via `column_add` / `_update` / `_delete` |
| **Views, forms, command bar** | — | ✅ views, form controls, modern commands, classic ribbons |
| **Security roles** | — | ✅ create, update, privileges |
| **Solutions & ALM** | —¹ | ✅ create, add/remove components, export/import, layers, pipelines (read) |
| **Classic workflows, BPFs, cloud flows** | — | ✅ with XAML validation, activation diagnosis, flow versions and run inspection |
| **Runs** | Hosted by Microsoft in every environment; npm stdio proxy for non-Microsoft clients | Locally on your machine (Claude Code plugin or dotnet tool) |
| **Setup** | A Power Platform admin enables every non-Copilot-Studio client per environment; tenant consent for the proxy app | Your own app registration (public client); no admin-center switch |
| **Cost** | Copilot Credits when used by agents outside Copilot Studio (since 15 Dec 2025; exemptions for Dynamics 365 data with D365 Premium or M365 Copilot licences) | Free and open source (MIT); normal Dataverse API limits apply |
| **Support** | Microsoft, generally available | Community, best effort |
| **Safety rails** | Delegated user rights; delete tools ask for explicit approval | Delegated user rights; annotations on every tool; read-only mode; production guard; dry run |

¹ Microsoft's separate [Dataverse skills plugin](https://github.com/microsoft/Dataverse-skills) covers solution
handling through the PAC CLI.

**Rule of thumb:** use Microsoft's server when an agent should work *with* the data in Dataverse
— in production, supported by Microsoft. Use this one when an agent should *build* in a dev
environment: tables, columns, views, roles, processes, solutions. Then promote the solution like
any other.

*Comparison as of October 2026, based on Microsoft Learn ([tools and billing](https://learn.microsoft.com/power-apps/maker/data-platform/data-platform-mcp),
[non-Microsoft clients](https://learn.microsoft.com/power-apps/maker/data-platform/data-platform-mcp-other-clients),
[configuration](https://learn.microsoft.com/power-apps/maker/data-platform/data-platform-mcp-disable)). Microsoft's tool set has
changed before — check the links for the current state, and open an issue if this table is out of date.*

---

## Authentication

Tokens are acquired via **MSAL `PublicClientApplication`** (silent first, browser fallback) and persisted platform-natively:

| OS | Storage |
|----|---------|
| Windows | DPAPI-encrypted file (`%LOCALAPPDATA%\dataverse-modelling-mcp\token.cache`) |
| macOS | Keychain (service `dataverse-modelling-mcp`) |
| Linux | libsecret with plaintext fallback |

No client secrets are ever stored. See [docs/authentication.md](docs/authentication.md) for full details.

---

## Architecture

```
AI Client (Claude Code / Copilot)
          │ MCP stdio
  Dataverse.Server          — thin MCP tool wrappers
          │
  Dataverse.Core            — services, HTTP clients, auth, models
          │ HTTP
  Dataverse Web API v9.2
  Power Automate Flow API
  BAP API
```

| Project | Role |
|---------|------|
| `Dataverse.Core` | Domain logic, HTTP clients, MSAL token management, models |
| `Dataverse.Server` | MCP server host; tool classes that delegate to Core |
| `Dataverse.Setup` | Interactive setup wizard |
| `Dataverse.Tests` | NUnit unit tests + integration tests |

See [docs/architecture.md](docs/architecture.md) for the full diagram and design principles.

---

## Development

```powershell
# Build
dotnet build

# Unit tests
dotnet test

# Integration tests (requires a configured environment)
$env:DATAVERSE_INTEGRATION_TESTS = "true"
dotnet test

# Run the MCP server locally
dotnet run --project src/Dataverse.Server
```

Integration tests connect to the environment configured in `config.json` and expect a `sample_mcptest` table, a `DV MCP Test Role` security role, and a `DV MCP Test` solution to exist.

---

## Updating to a new release (no session restart needed)

When installed as a Claude Code plugin, the server binary is not started directly —
`.mcp.json` launches `scripts/launch` (the shell script on macOS/Linux, `launch.cmd` → `run-server.ps1`
on Windows), which installs the binary version pinned in
`scripts/BINARY_VERSION` (skipping the download when it is already there) and then execs it,
passing stdio straight through.

So after a new release, getting the new tools takes:

1. Update the plugin so `scripts/BINARY_VERSION` points at the new version (`/plugin` → update,
   or however the marketplace entry is refreshed).
2. Open the **`/mcp`** menu, pick `dataverse-modelling`, and choose **Reconnect**.

That restarts the server process, which re-runs the launcher and picks up the new binary — no
new Claude Code session required. The `SessionStart` hook (`scripts/ensure-binary.*`) still
pre-warms the download; because both paths share `scripts/binary-common.*` and short-circuit
when the expected version is already installed, nothing is downloaded twice.

Binaries live in versioned directories (`<plugin-data>/bin/<version>/DataverseMcp[.exe]`) so an
update never has to overwrite a running `.exe` — which Windows would refuse while another
session still has it open.

---

## Docs

- [Setup guide](docs/setup.md)
- [Architecture](docs/architecture.md)
- [Authentication](docs/authentication.md)
- [Tool reference](docs/tools/)
- [Analysis — dependencies and solution membership](docs/tools/analysis.md)
- [Forms — structure, code components, FormXML pitfalls](docs/tools/forms.md)
- [Command bar buttons — classic ribbon vs modern command](docs/tools/buttons-classic-vs-modern.md)
- [Modern commands (`appaction`) — field semantics and gotchas](docs/tools/modern-commands.md)
- [Contributing](CONTRIBUTING.md) · [Security policy](SECURITY.md)

---

## Skills

Skills load automatically when a conversation touches their subject — they exist so the hard-won
parts of the docs above surface without anyone having to know they exist.

| Skill | Loads when |
|---|---|
| [`classic-workflows`](skills/classic-workflows/SKILL.md) | reading, building or debugging a classic workflow / its XAML |
| [`business-process-flows`](skills/business-process-flows/SKILL.md) | creating, changing or running a business process flow (process bar, stages, BPF instances) |
| [`cloud-flows`](skills/cloud-flows/SKILL.md) | working with solution-aware Power Automate flows |
| [`solution-pipelines`](skills/solution-pipelines/SKILL.md) | promoting a solution dev → staging → prod |
| [`modeling-patterns`](skills/modeling-patterns/SKILL.md) | designing tables, columns or relationships |
| [`command-bar-buttons`](skills/command-bar-buttons/SKILL.md) | adding, changing or debugging a button on a table's command bar |
| [`form-authoring`](skills/form-authoring/SKILL.md) | seeing or changing what sits on a model-driven form, placing a code component |

They ship in **`skills/`**, which travels with the plugin and is therefore active in every repo the
plugin is installed in — as opposed to `.claude/skills/`, which would scope them to *this* repository,
the one place the Dataverse advice is least needed.

---

## Telemetry

The server exposes one optional telemetry tool, `dataverse_submit_feedback`, which an AI client may
call to send session feedback (free text, a category, an optional severity and an optional session summary)
as a single Application Insights event.

**It is off by default and off for anyone who just clones this repo.** Telemetry is transmitted only
when the process is started with the standard `APPLICATIONINSIGHTS_CONNECTION_STRING` environment
variable pointing at an Application Insights resource. When that variable is unset — the default —
the underlying client is a no-op: calling the tool logs locally and sends nothing over the network.
No connection string is bundled with the server.

If you run the server yourself and do **not** want this endpoint at all, leave
`APPLICATIONINSIGHTS_CONNECTION_STRING` unset (nothing is sent) or remove
`src/Dataverse.Server/Tools/FeedbackTool.cs` and rebuild. What is sent, when configured, is exactly
the fields above plus constant/derived metadata (schema version, `server` tag, server version, transport `stdio`,
the fixed caller `local-stdio`, and the MCP client name and version from the `initialize` handshake) — no user names,
auth tokens, org URLs, or record data.

---

## CI

GitHub Actions runs `dotnet build` and `dotnet test` (unit tests only) on every pull request. See [`.github/workflows/ci.yml`](.github/workflows/ci.yml).

---

## License

[MIT](LICENSE) © Crossvertise GmbH.
