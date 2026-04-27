# Setup Guide

## Prerequisites

- .NET 8 SDK
- An Azure AD tenant with a Dataverse / Power Platform environment
- (Optional) Azure CLI for automated app registration

## First-time setup

Run the setup wizard:

```
dotnet run --project src/Dataverse.Setup
```

The wizard will:

1. Check if the Azure CLI is installed and offer to create the app registration automatically.
2. Ask for your Azure AD Application (client) ID and optional tenant ID.
3. Open a browser to acquire an OAuth2 token (delegated, no secret required).
4. Ask for your Dataverse org URL, environment ID, and Power Automate region.
5. Write the config file and cache the token.
6. Print the `claude mcp add` command to register the server.

## Config file location

| OS      | Path |
|---------|------|
| Windows | `%LOCALAPPDATA%\dataverse-mcp\config.json` |
| macOS   | `~/.dataverse-mcp/config.json` |
| Linux   | `~/.config/dataverse-mcp/config.json` |

## Config structure

```json
{
  "auth": {
    "clientId": "your-aad-app-client-id",
    "tenantId": "your-tenant-id-or-null"
  },
  "activeEnvironment": "prod",
  "environments": {
    "prod": {
      "orgUrl": "https://yourorg.crm4.dynamics.com",
      "environmentId": "your-environment-guid",
      "region": "europe"
    }
  }
}
```

## Registering with Claude Code

After setup, run the printed command, for example:

```
claude mcp add dataverse-mcp -- dotnet run --project /path/to/src/Dataverse.Server
```

## Updating

Re-run the setup wizard to reconfigure or add environments:

```
dotnet run --project src/Dataverse.Setup
```
