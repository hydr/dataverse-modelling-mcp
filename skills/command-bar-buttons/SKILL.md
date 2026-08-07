---
name: command-bar-buttons
description: Add, change or remove a button on a Dataverse / Dynamics 365 table command bar — classic RibbonDiffXml (ribbon_get, ribbon_add_button, ribbon_remove_button) or modern commands in the appaction table (command_*). Covers which mechanism to pick, how the ribbon is really stored, and the failures that return success while the button silently never appears. Use when the user wants a button, ribbon or command bar entry on a table, form or grid, or when a button they added is missing, mislabelled or vanishes on row selection.
version: 1.0
---

# Buttons on a Dataverse command bar

Two mechanisms exist and picking the wrong one costs a day. Full reference, all of it verified
against a live org: `${CLAUDE_PLUGIN_ROOT}/docs/tools/buttons-classic-vs-modern.md`. Read it before
the first write — the summary below is a router, not a substitute.

## Pick the mechanism first

**Does visibility depend on what is selected in a grid?**

- **Yes → classic ribbon.** `SelectionCountRule` is entity-bound and self-contained. Tools:
  `ribbon_get`, `ribbon_add_button`, `ribbon_remove_button`.
- **No → either works.** Modern commands are Microsoft's direction; classic stays versionable as XML.

The modern equivalent of a selection rule is a Power Fx formula, and that formula must live in a
canvas component library. **No API can create one** — only the Command Designer opened *from an app*,
which turns a table-level button into an app dependency that has to be dragged through every
environment. That is the whole argument for classic here, and it is on merit, not nostalgia.

**Classic ribbons still render.** An `appactionmigration` row with `ismigrated = True` does not mean
classic `RibbonDiffXml` is ignored. Both mechanisms render side by side on the same table.

## Before you write

- There is **no Web API that writes a ribbon**. `ribbon_add_button` does a solution round-trip
  internally. Do not hand-roll it — steps 4, 5 and 8 of that round-trip are load-bearing in
  non-obvious ways.
- **A non-empty `<CustomActions>` import replaces the entire collection.** An import naming only your
  new button deletes every other one. `ribbon_add_button` re-sends the existing nodes and reports
  `lostCustomActionIds`; a hand-built import has no such guard.
- An **empty** `<CustomActions />` removes nothing. Deletion goes through `ribbon_remove_button`,
  which deletes the stored `ribbondiff` / `ribboncommand` / `ribbonrule` rows.
- **`ribbondiff` holds more than CustomActions.** Localized captions live there too, as `<LocLabel>`
  nodes with `difftype = 3`, and they belong in `<LocLabels>` — not in `<CustomActions>`, where the
  importer rejects them with *"Missing Location Attribute … for CustomAction element with
  Id=….LabelText"*. `ribbon_get` returns them separately (`customActions` / `locLabels` /
  `otherDiffs`), and `ribbon_remove_button` deletes a button's label rows with it — an orphaned one
  breaks the next add on that table. Any table that has seen the Ribbon Workbench has such rows.

## Failures that return success

Each of these logs nothing and leaves you with a missing or wrong button. Check them first when
something does not appear:

1. **Invalid `fontIcon`** (e.g. `$clientsvg:Money`) — modern command never renders. `command_create`
   validates; `command_list_icons` prints the known-good set.
2. **`ModernImage="$webresource:….svg"`** — the whole classic button disappears, not just the icon.
   Use a PNG pair via `imageWebResource`.
3. **Unresolved `$LocLabels:`** — the button renders with the raw token as its caption. Use literal
   `LabelText`.
4. **A `Location` that does not exist** — nothing renders. `ribbon_get` with `includeLocations=true`
   lists the valid ones.
5. **Grid modern command with `visibilitytype = 0`** — vanishes as soon as rows are selected. The most
   common "my button disappeared" report.
6. **Wrong app.** Custom commands do not render in `d365default`. Check in a real app.

## Verifying — the part that actually costs the time

Separate **stored** from **drawn**. `ribbon_get` answers the first; only a browser answers the second.
Never let one stand in for the other.

- **Read the whole command bar.** Do not filter for the caption you expect — a mislabelled button is
  exactly the case you would then miss. This once produced the conclusion that classic ribbons no
  longer worked at all, when the button had been rendering the whole time as `LabelText`.
- **Compare against a reference button you know works.** If yours is missing and the reference
  renders, the fault is in your definition. If neither renders, you are in the wrong app.
- **Searching the compiled ribbon for your CustomAction id always fails.** `RetrieveEntityRibbon`
  returns the ribbon with the diff already applied — it contains no `<CustomAction>` elements at all.
  Search for the button id and the caption.
