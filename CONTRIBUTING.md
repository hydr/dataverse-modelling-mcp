# Contributing

Thanks for your interest. This is a working tool, and contributions that make it
handle real Dataverse environments better are welcome.

## Before you start

Open an issue first for anything larger than a bug fix. Dataverse has a lot of
undocumented behaviour, and there is a good chance the thing you are about to work
around is already understood — `docs/classic-workflows-reference.md` and
`skills/classic-workflows/SKILL.md` collect what has been learned the hard way.

## Development

```bash
dotnet build
dotnet test                      # unit tests, no environment needed
dotnet run --project src/Dataverse.Server   # run the MCP server locally
```

Requirements: .NET 8 SDK. Windows for the packaged plugin (see the platform note in
`docs/GH-RELEASE-SETUP.md`); the server and tests build anywhere .NET 8 runs.

### Tests

`dotnet test` runs the unit tests. Integration tests are marked
`[Category("Integration")]` and are skipped without a configured environment — they
talk to a real Dataverse org and write records, so they never run in CI.

Tests marked `[Explicit]` are maintenance tasks, not checks. `DesignerXamlFixtureTests`
in particular **overwrites the committed fixtures** from whatever environment it is
pointed at. Do not run it unless that is what you want, and adjust the workflow IDs in
it to your own org first.

### The fixtures are evidence, not samples

`tests/Dataverse.Tests/Workflows/Fixtures/*.xaml` is real designer output. Builder and
parser tests would otherwise only prove they agree with each other; these files are the
only evidence that the parser reads what Dataverse itself writes. When you change one,
keep the XML structure intact — the point of the file is its shape, not its text.

## Pull requests

- Branch off `master`. Naming: `feature/<topic>`, `fix/<topic>`, `chore/<topic>`.
- One topic per branch, one logical step per commit.
- Explain **why** in the commit message, not just what. A diff shows what changed;
  it cannot show which Dataverse error code sent you down that path.
- CI runs `dotnet build` and `dotnet test` on every PR. Both must pass.

## Documentation belongs with the fix

An insight that lives only in the code does not exist for the next contributor. When
you change something, carry the documentation along:

| What changed | Where it goes |
|---|---|
| A new validation code | the code table in `skills/classic-workflows/SKILL.md` |
| A new field in the definition model | `SKILL.md` |
| A new tool | the tool map in `SKILL.md` **and** `docs/tools/<area>.md` |
| An insight about the XAML format | `docs/classic-workflows-reference.md` |

`DocumentationCoverageTests` enforces the first three and fails when something is
missing. The fourth is on you.

## Releases

Maintainers only, and the version is coupled across three files — see the release
process in `docs/GH-RELEASE-SETUP.md`.

## License

By contributing you agree that your contribution is licensed under the MIT License,
the same terms as the rest of the project.
