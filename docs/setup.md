# Setup Guide

The short version — three ways to install — is the [Quickstart in the README](../README.md#quickstart).
This page covers the setup wizard and the config file in detail.

## Prerequisites

- .NET 8 SDK (not needed for the Claude Code plugin, which downloads a self-contained binary)
- An Azure AD tenant with a Dataverse / Power Platform environment
- (Optional) Azure CLI for automated app registration

## First-time setup

Run the setup wizard — as a dotnet tool:

```
dotnet tool install -g Dataverse.ModellingMcp.Setup
dataverse-modelling-mcp
```

or from a clone of the repository:

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
| Windows | `%LOCALAPPDATA%\dataverse-modelling-mcp\config.json` |
| macOS   | `~/.dataverse-modelling-mcp/config.json` |
| Linux   | `~/.config/dataverse-modelling-mcp/config.json` |

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

After setup, run the printed command:

```
claude mcp add dataverse-modelling-mcp -- dataverse-modelling-mcp server
```

From a clone of the repository, start the server project instead:

```
claude mcp add dataverse-modelling-mcp -- dotnet run --project /path/to/src/Dataverse.Server
```

## Configuration without the wizard

When no `config.json` exists, the server builds a single environment from environment variables:
`DATAVERSE_URL` and `AZURE_CLIENT_ID` (both required), plus the optional `AZURE_TENANT_ID`,
`DATAVERSE_ENVIRONMENT_ID` (needed for the cloud flow tools) and `POWER_PLATFORM_REGION`
(default `europe`). The Claude Code plugin uses exactly this. An existing `config.json` always wins.

## Updating

Re-run the setup wizard to reconfigure or add environments (`dataverse-modelling-mcp`, or
`dotnet run --project src/Dataverse.Setup` from a clone). To update the dotnet tool itself:

```
dotnet tool update -g Dataverse.ModellingMcp.Setup
```
