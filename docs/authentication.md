# Authentication

## Overview

The server uses **MSAL (Microsoft Authentication Library)** with a `PublicClientApplication`. There are no client secrets. Authentication is delegated — the user authenticates interactively in a browser once, and the resulting token is cached persistently.

## Token acquisition flow

```
GetTokenAsync(scope)
  └─ AcquireTokenSilent (try all cached accounts)
       ├─ Success → return access token
       └─ MsalUiRequiredException
            └─ AcquireTokenInteractive (opens browser)
                 └─ Cache token → return access token
```

## Token cache

Tokens are persisted using `MsalCacheHelper` from `Microsoft.Identity.Client.Extensions.Msal`:

| OS      | Storage |
|---------|---------|
| Windows | DPAPI-encrypted file at `%LOCALAPPDATA%\dataverse-modelling-mcp\token.cache` |
| macOS   | macOS Keychain (service: `dataverse-modelling-mcp`) |
| Linux   | libsecret (collection: `dataverse-modelling-mcp`) with plaintext file fallback |

The cache file is excluded from git via `.gitignore`.

## Required OAuth2 scopes

| API | Scope |
|-----|-------|
| Dataverse Web API | `https://{orgUrl}/.default` (constructed from the configured org URL) |
| Power Automate Flow API | `https://service.flow.microsoft.com/.default` |

## App registration requirements

The Azure AD app must be:
- A **public client** (no secret / certificate required)
- Have redirect URI: `http://localhost`
- Have **delegated** API permissions for Dataverse (`user_impersonation`) and optionally Power Automate

The setup wizard can create this app automatically if the Azure CLI is available.

## Reauthorizing

If the cached token expires or becomes invalid, the server will automatically open a browser for interactive login on the next tool call.

To force reauthorization, delete the token cache file and re-run `dataverse-modelling-mcp setup`.
