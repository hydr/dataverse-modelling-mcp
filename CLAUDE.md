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

## Git workflow

- Work on **feature branches**, never directly on `master`. Branch names: `feature/<topic>` for features, `chore/<topic>` for housekeeping.
- Land changes via **pull requests** against `master`. Keep commits content-clean: one logical change per commit, one feature per branch/PR.
- **Commit and push regularly** while working — small, frequent commits on the feature branch rather than one large drop at the end.
- **Before starting work, check the current branch** (`git branch --show-current`) and decide whether to continue there or start a new branch.
- Do not commit local dev artifacts (e.g. `ws-log.json`, screenshots) — they are gitignored.
