# Binary delivery via GitHub Releases

The plugin does **not** ship a precompiled MCP server binary in the Git repo.
Instead, the matching self-contained binary is downloaded from a GitHub release —
on `SessionStart` **and** every time the MCP server starts.

## Moving parts

| File | Role |
|---|---|
| `.mcp.json` | Starts `scripts/launch` — no extension, see [One command for every OS](#one-command-for-every-os) |
| `scripts/launch` / `launch.cmd` | Per-OS entry point: shell script on macOS/Linux, batch file on Windows |
| `scripts/run-server.ps1` / `.sh` | Launcher: updates the binary, starts it, passes stdio through |
| `scripts/binary-common.ps1` / `.sh` | Download/resolution logic shared by hook and launcher |
| `hooks/hooks.json` | Registers the `SessionStart` hook (PowerShell **and** bash; the PowerShell entry ends in `; exit 0`, valid in both shells, so it stays quiet where PowerShell is missing) |
| `scripts/ensure-binary.ps1` | SessionStart hook (Windows), thin wrapper around `binary-common.ps1` |
| `scripts/ensure-binary.sh` | Same for git-bash, macOS and Linux |
| `scripts/BINARY_VERSION` | Expected binary version — **must match the release tag** |
| `.github/workflows/release.yml` | Builds, smoke-tests and publishes the binaries as release assets |
| `.github/scripts/smoke_mcp.py` | Starts a binary and checks `initialize` + `tools/list` |

## Updating without restarting the session

Because the launcher runs on **every** server start, after a release a
**reconnect in the `/mcp` menu** is enough — the server process restarts, the launcher pulls the
new version and starts it. Previously the binary could only be updated through the `SessionStart`
hook, i.e. it was only usable in the next session.

The hook stays in place (it pre-warms the download). There are no duplicate downloads:
both paths use the same function, which returns immediately when the expected
version is already installed.

## Layout on disk (versioned)

```
<plugin-data>/bin/<version>/DataverseMcp[.exe]   <- is executed
<plugin-data>/bin/DataverseMcp[.exe]             <- legacy path, best-effort copy
<plugin-data>/bin/.version                       <- legacy marker
```

`.exe` on Windows; on macOS and Linux the binary has no extension.

Versioned directories, because Windows does **not let you overwrite a running `.exe`**:
with parallel Claude sessions, or a reconnect while the old process is still
shutting down, an in-place update would fail. A new version lands in
a new directory; old directories are cleaned up best-effort
(locked ones are skipped and retried on the next run).

The legacy copy at `bin/DataverseMcp.exe` is kept so that installations
with an older `.mcp.json` (which points directly at this path) still start.

## Naming convention

- **Release assets:** `DataverseMcp-win-x64.exe`, `DataverseMcp-linux-x64`,
  `DataverseMcp-osx-arm64`, `DataverseMcp-osx-x64` (RID suffix).
- **Runtime name on disk:** `DataverseMcp.exe` on Windows, `DataverseMcp` elsewhere. The
  launcher picks the asset for its platform (`uname -s`/`uname -m` in `binary-common.sh`) and
  normalises the name when it stores the file.

## One command for every OS

`.mcp.json` has no platform-specific entries, so it names one command for every OS:
`${CLAUDE_PLUGIN_ROOT}/scripts/launch` — **without an extension**.

- **macOS/Linux** execute the file `launch` itself, a POSIX shell script (mode 100755 in git,
  LF line endings via `.gitattributes`), which hands over to `run-server.sh`.
- **Windows:** Claude Code resolves an extensionless command via `PATHEXT` and finds
  `launch.cmd` next to it — even though `launch` exists too. `launch.cmd` starts
  `run-server.ps1`. Verified with Claude Code on Windows 11 (October 2026).

Do not rename either file or add an extension to the command in `.mcp.json`.

The macOS binaries are built on a macOS runner and ad-hoc signed (`codesign --sign -`):
Apple Silicon kills an unsigned binary on start. Files downloaded with `curl` carry no
quarantine attribute, so Gatekeeper does not get involved.

## stdout discipline (important)

The launcher shares stdout with the MCP stdio stream. That is why the launcher
and the shared logic write **only to stderr**; the binary is started without redirection
and inherits stdin/stdout/stderr unchanged. A single line on stdout
(e.g. a `Write-Host`) would break the MCP connection.

If GitHub is unreachable, the launcher starts the already installed binary and
only warns on stderr — offline operation remains possible. The hook, by contrast,
still fails loudly.

## Version coupling (important)

The hook downloads from `releases/download/v${BINARY_VERSION}/DataverseMcp-<rid>[.exe]`.
For that to work, the following must hold:

```
scripts/BINARY_VERSION  ==  release tag (without 'v')
                        ==  Version in Dataverse.Setup.csproj
                        ==  version in .claude-plugin/plugin.json
```

The release workflow enforces this with the guard step "Verify all versions match
the tag": if any of the three files does not match the tag, the build aborts and names
the mismatching file. The guard prevents two things — the earlier
`0.0.0` bug, where no asset existed at all, and drift of the
plugin version, which used to have its own track (0.18.1 versus binary 1.16.1),
so the plugin version did not tell you which binary would be installed.

## Authentication

The repo is public, so the normal case needs **no** sign-in. The
hook downloads in this order:

1. **Anonymously** via `https://github.com/<repo>/releases/download/v<version>/<asset>`.
   This is the standard path: most users have neither `gh` nor a token, and
   requiring either would keep them from starting the server.
2. **`gh` CLI**, if installed and authenticated (`gh auth status`) — applies
   when a **private** repo is configured through the plugin option `release_repo`.
3. **REST API with a token** from `GITHUB_TOKEN` or `GH_TOKEN` (Bearer). Resolves the
   asset ID via `releases/tags/v<version>` and downloads it with
   `Accept: application/octet-stream`.

A private repo answers path 1 with 404; the hook then falls through to 2 and 3.
If everything fails, the hook aborts **loudly** (no silent `exit 0`) and
names the missing release tag.

## Release process (binary)

1. Feature branch, changes, PR against `master`.
2. Bump the version — in **all three** files to **exactly the same** value, which is also the tag:
   - `src/Dataverse.Setup/Dataverse.Setup.csproj` → `<Version>`
   - `scripts/BINARY_VERSION`
   - `.claude-plugin/plugin.json` → `version`

   The workflow step "Verify all versions match the tag" compares all three with the tag and
   aborts on any mismatch, naming the mismatching file. The plugin version used to run
   on its own track — "plugin 0.18.1" then did not tell you which binary was inside.
3. Merge the PR.
4. `git tag v<Version> && git push origin v<Version>` → the release workflow
   - packs the NuGet tool `Dataverse.Setup`,
   - builds the server self-contained/single-file for `win-x64`, `linux-x64`, `osx-arm64`
     and `osx-x64`, and starts each non-Windows binary once on its own platform (smoke test),
   - attaches the four binaries and the `.nupkg` to the GitHub release,
   - then pushes the `.nupkg` to nuget.org (see below).

## Publishing the NuGet tool (Trusted Publishing)

The workflow publishes `Dataverse.ModellingMcp.Setup` to nuget.org with
[Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing): the job
exchanges a GitHub OIDC token for an API key that is valid for one hour. There is **no
`NUGET_API_KEY` secret**, and none should be added.

One-time setup, in the public repository only (releases are built nowhere else):

1. On nuget.org: user menu → **Trusted Publishing** → add a policy with
   - Repository Owner: `hydr`
   - Repository: `dataverse-modelling-mcp`
   - Workflow File: `release.yml` (file name only)
   - Environment: leave empty
2. In GitHub: Settings → Secrets and variables → Actions → **Variables** → add `NUGET_USER` with
   the nuget.org **profile name** (not the e-mail address).

Without `NUGET_USER` both NuGet steps are skipped and the release is still created. The push
runs after the GitHub release on purpose, so a NuGet problem never withholds the binary the
launcher downloads. `--skip-duplicate` makes re-running a release job harmless.

## Local workaround (without a release)

As long as no release exists, the binary can be built and placed manually:

```bash
dotnet publish src/Dataverse.Server -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish
cp ./publish/Dataverse.Server.exe \
  "$HOME/.claude/plugins/data/dataverse-modelling-mcp-hydr/bin/DataverseMcp.exe"
# optional, so the hook does not download again:
printf '%s' "<version>" > \
  "$HOME/.claude/plugins/data/dataverse-modelling-mcp-hydr/bin/.version"
```

## Testing pipeline changes

A PR that touches `release.yml`, `.github/scripts/`, `scripts/` or the server project file runs
the `binaries` job of the release workflow — all four builds plus the smoke tests. The `release`
job only runs for `v*` tags, so a PR publishes nothing.
