# dataverse-modelling-mcp

A local [Model Context Protocol](https://modelcontextprotocol.io/) server that gives AI clients (Claude Code, GitHub Copilot, etc.) first-class access to Dataverse and Power Platform.

Covers 38 tools across Classic Workflows, Cloud Flows, Tables & Columns, Views, Security Roles, Environment Variables, and Solutions/ALM — all via delegated user auth (no secrets, no service accounts).

---

## Tools

| Group | Tools |
|-------|-------|
| **Classic Workflows** | `workflow_list`, `workflow_get`, `workflow_create`, `workflow_update`, `workflow_set_state`, `workflow_assign`, `workflow_validate` |
| **Cloud Flows** | `flow_list`, `flow_get`, `flow_create`, `flow_update`, `flow_set_state`, `flow_get_runs`, `flow_describe` |
| **Tables & Columns** | `table_list`, `table_get`, `table_create`, `table_update`, `column_add`, `column_update` |
| **Views** | `view_list`, `view_get`, `view_create`, `view_update`, `view_add_column`, `view_set_sort` |
| **Security Roles** | `role_list`, `role_get`, `role_create`, `role_update` |
| **Environment Variables** | `envvar_list`, `envvar_get`, `envvar_set` |
| **Solutions / ALM** | `solution_list`, `solution_get`, `solution_create`, `solution_export`, `solution_import`, `solution_add_component`, `solution_remove_component`, `solution_check_layers`, `solution_remove_active_layer`, `solution_deploy_pipeline` |

---

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- An Azure AD tenant with a Dataverse / Power Platform environment
- (Optional) [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) for automated app registration

---

## Setup

Clone the repo and run the setup wizard:

```powershell
git clone https://github.com/hydr/dataverse-modelling-mcp
cd dataverse-modelling-mcp
dotnet run --project src/Dataverse.Setup
```

The wizard will:

1. Optionally create or locate an Azure AD app registration via Azure CLI
2. Open a browser for interactive login (delegated auth — no secret required)
3. List available Dataverse environments and let you choose one
4. Write `config.json` and cache the token to the OS keychain / DPAPI
5. Print the `claude mcp add` command to register the server with Claude Code

### Config file location

| OS | Path |
|----|------|
| Windows | `%LOCALAPPDATA%\dataverse-modelling-mcp\config.json` |
| macOS | `~/.dataverse-modelling-mcp/config.json` |
| Linux | `~/.config/dataverse-modelling-mcp/config.json` |

### Register with Claude Code

After setup, run the printed command, for example:

```
claude mcp add dataverse-modelling-mcp -- dotnet run --project /path/to/src/Dataverse.Server
```

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

## Docs

- [Setup guide](docs/setup.md)
- [Architecture](docs/architecture.md)
- [Authentication](docs/authentication.md)
- [Tool reference](docs/tools/)

---

## CI

GitHub Actions runs `dotnet build` and `dotnet test` (unit tests only) on every pull request. See [`.github/workflows/ci.yml`](.github/workflows/ci.yml).
