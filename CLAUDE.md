# dataverse-modelling-mcp

## Build
dotnet build

## Test
dotnet test

## Run (local MCP server)
dotnet run --project src/Dataverse.Server

## Setup (first time or after update)
dotnet run --project src/Dataverse.Setup

## Pack (dotnet tool)
dotnet pack src/Dataverse.Setup -c Release

## Language

**Everything in this repository is written in English**: commit messages, PR titles and bodies,
branch names, code, comments, tool descriptions, error messages, docs and skills — also when the
conversation with the user is in another language.

Two deliberate exceptions:

- **Search keywords in skill descriptions** — German terms such as "Geschäftsprozessfluss" stay in a
  skill's `description`, so that the skill is also picked for a German request.
- **Test data that reproduces real content** — e.g. the German e-mail texts in the workflow
  fixtures, or strings that test umlaut handling.

`LanguageTests` fails the build when German text shows up anywhere else; its allowlist names the
exceptions. Older commit messages are partly German; they stay, because rewriting the history would
detach the release tags the launcher downloads from.

## Two repositories

The project lives in two repositories, and both trees are meant to be identical:

- **`hydr/dataverse-modelling-mcp`** (public) — the publication. Releases, with the binary and
  NuGet asset, are built here.
- **`crossvertise/dataverse-modelling-mcp`** (private) — the internal working repository. It carries
  the unsanitised commit history and the PR discussions with internal references; that is why it
  stays private.

**Releases are built in the public repository only.** `scripts/BINARY_VERSION` and the default of
the `release_repo` option point to the public repository in **both** repositories, so the launcher
always downloads the binary from there. A tag in the private repository would create a second,
unused release of the same version — so do not tag there.

## No company internals in the public repository

The public repository is deliberately anonymised (`#26`, `#35`). **No identifier from a real
environment may go into it** — not in docs, code comments or tool descriptions, nor in tests,
commit messages or PR texts. This has happened before (v1.19.0 carried 77 `xv_*` and 46
`Crossvertise*` occurrences, cleaned up in 1.20.0), and a release cannot be taken back afterwards.

| Instead of | Use |
|---|---|
| A real org's publisher prefix, e.g. `xv_` | `sample_` |
| Real solution names | `Contoso…` (e.g. `ContosoForms`, `ContosoOrders`) |
| Real tables and columns | `sample_widget`, `sample_score`, … |
| Real namespaces of code components | `Contoso.DocumentViewer` |
| GUIDs from an environment | recognisably synthetic ones (`11111111-1111-1111-1111-111111110001`) |
| Real `versionnumber` values, dependency counts, solution landscapes | synthetic values or no number at all |

Standard Dataverse tables (`account`, `contact`, `invoice`, `salesorder`, `product`) are harmless
and stay. So does the **publisher identity** of the repository in `LICENSE`, `SECURITY.md`,
`README.md`, `.claude-plugin/plugin.json` and `Dataverse.Setup.csproj` — that one is intended.

Check before committing:

```bash
git grep -I -i -E "xv_|crossvertise|xvdev|xvstaging" -- docs src tests skills README.md
```

Hits outside the identity files named above are an error. The same goes for `git log -p` on your own
commits, because commit messages can no longer be corrected once a tag has been set.

For live tests against a real environment this means: **anonymise results before they go into a
file.** The test environment itself may of course be real — only its identifiers do not belong in
the repository. One exception is the pre-existing integration test `SolutionIntegrationTests.cs`,
which needs a real MetadataId as its fixture.

## Skills

Skills belong in **`skills/`** — this directory ships with the plugin and is therefore active in
every repository the plugin is installed in. **Not** in `.claude/skills/`: that restricts them to this
repository, i.e. to precisely the one environment where Dataverse knowledge is needed least.

Skills are plain Markdown files with no tie to the binary — changing them needs no new binary. For
clients to pick them up, though, the plugin version has to go up, and it is coupled to binary and
tool (see [Release process](#release-process-for-every-change)). So: bump all three version files,
merge, tag. The release then rebuilds a functionally identical binary — the price of keeping the
version number unambiguous.

## Docs travel with the fix

A finding that only exists in the code does not exist for the next agent. So with every change, also
update:

| What changes | Where |
|---|---|
| New validation code | Code table in `skills/classic-workflows/SKILL.md` |
| New field in the definition model | `SKILL.md` |
| New tool | Tool map in `SKILL.md` **and** `docs/tools/<area>.md` |
| Finding about the XAML format | `docs/classic-workflows-reference.md` |
| BPF: new code, new model field, new tool | `skills/business-process-flows/SKILL.md` (tools also `docs/tools/business-process-flows.md`) |
| BPF: finding about the XAML format | `docs/business-process-flows-reference.md` |

`DocumentationCoverageTests` checks the first three (and the BPF row) automatically and fails when
something is missing — only the fourth point relies on discipline.

## Git conventions

- **Feature branches**, never directly on `master`. Naming: `feature/<topic>` for features,
  `chore/<topic>` for housekeeping, `fix/<topic>` for bug fixes.
- Changes land via **pull requests** against `master`. One logical step per commit, one topic per
  branch/PR.
- **Commit and push regularly** — small commits while working instead of one big drop at the end.
- **Check the current branch before starting** (`git branch --show-current`) and decide whether to
  continue there or open a new branch.
- Do not commit local dev artefacts (`ws-log.json`, screenshots) — they are gitignored.

## Release process (for every change)

For changes to the MCP server always — steps 1 to 3 in **both** repositories, step 4 only in the
public one (see [Two repositories](#two-repositories)):
1. Create a feature branch and open a PR (against `master`)
2. Raise the version (minor for new features, patch for bug fixes) — in **all three** files to
   **exactly the same** value, which is also the release tag:
   - `src/Dataverse.Setup/Dataverse.Setup.csproj` → `<Version>`
   - `scripts/BINARY_VERSION` (hook and launcher download the binary from `releases/download/v<BINARY_VERSION>/…`)
   - `.claude-plugin/plugin.json` → `version`

   The release workflow compares all three with the tag and aborts on any mismatch. Keep
   `<Version>` in `src/Dataverse.Server/Dataverse.Server.csproj` in step too: the feedback event
   reports it as `serverVersion`.
3. Merge the PR
4. Set a git tag **in the public repository** (`git tag v<Version> && git push origin v<Version>`) → triggers the release workflow (NuGet publish + GitHub release including the server binary asset)

## Using a new version in a running session

No session restart needed: `.mcp.json` does not start the binary directly but the launcher
`scripts/run-server.ps1`, which on every server start brings the binary to the version pinned in
`scripts/BINARY_VERSION` and then starts it (stdio passed through unchanged).

So after a release: update the plugin (so that `BINARY_VERSION` is right), then **reconnect** the
server `dataverse-modelling` in the **`/mcp` menu** — done.

Details on binary delivery: see `docs/GH-RELEASE-SETUP.md`.
