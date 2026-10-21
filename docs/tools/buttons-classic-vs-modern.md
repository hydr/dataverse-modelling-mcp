# Putting a button on a command bar

There are two ways to add a button to a table's command bar, and picking the wrong one costs a day.
This page exists so nobody has to rediscover that. Everything below was verified against a live
Dataverse org (9.2.26072) — where it contradicts the product documentation, trust this page.

- **Classic ribbon** — `RibbonDiffXml`. Tools: [`ribbon_get`](#ribbon_get),
  [`ribbon_add_button`](#ribbon_add_button), [`ribbon_remove_button`](#ribbon_remove_button).
- **Modern command** — a row in the `appaction` table. Tools: `command_*`, see
  [modern-commands.md](modern-commands.md).

---

## Which one?

| | Classic ribbon | Modern command |
|---|---|---|
| Visibility on selection | `SelectionCountRule`, **entity-bound** | Power Fx, needs a component library created **in app context** |
| App dependency | none | yes |
| Versionable in the repo | yes, as XML | no, a click artefact |
| Deploy to staging / prod | solution import | click-through, or carry the library along |
| Removal | row-level delete via API (see below); an empty import removes nothing | delete the record |
| Microsoft's direction | legacy | the future |

**The deciding line is the first one.** If the button has to appear or disappear based on what is
selected in a grid, the classic `SelectionCountRule` is entity-bound and self-contained. The modern
equivalent is a Power Fx formula, and that formula has to live inside a canvas component library
which **only the Command Designer opened from an app can create** — open it from a solution and all
you get is "Show". That turns a table-level button into something with an app dependency, which then
has to be dragged through every environment. For an entity-bound rule, classic wins on merit, not on
nostalgia.

**Classic ribbons still render.** An early assumption on this org was that Modern Commanding had
displaced them — that the `appactionmigration` row `msdyn_System` with `ismigrated = True` meant
classic `RibbonDiffXml` was being ignored. That is **false**. Both mechanisms render, side by side,
on the same table.

---

## How the classic ribbon is actually stored

This matters because it determines what you can and cannot do through the API.

`RibbonDiffXml` is not stored as a document. On publish, Dataverse decomposes it into rows hanging off
one `ribboncustomization` record per table:

| XML node | Table | Key column | XML column |
|---|---|---|---|
| `<CustomAction>` | `ribbondiff` | `diffid` | `rdx` |
| `<HideCustomAction>` | `ribbondiff` | `diffid` | `rdx` |
| `<LocLabel>` | `ribbondiff` | `diffid` | `rdx` |
| `<CommandDefinition>` | `ribboncommand` | `command` | `commanddefinition` |
| `<EnableRule>` / `<DisplayRule>` | `ribbonrule` | `ruleid` | `ruledefinition` |
| button ↔ tab placement | `ribbontabtocommandmap` | `controlid` | — |

`ribbondiff` holds more than CustomActions, and the column that tells them apart is `difftype`:

| `difftype` | Name | Section it belongs in |
|---|---|---|
| 0 | `Standard` | `<CustomActions>` — both `<CustomAction>` and `<HideCustomAction>` |
| 1 | `Tab` | `<Templates>` |
| 2 | `LayoutTemplate` | `<Templates>` |
| 3 | `LocalizedLabel` | `<LocLabels>` |

`ribbon_get` returns them pre-sorted: `customActions` (type 0), `locLabels` (type 3) and `otherDiffs`
(types 1 and 2, carried along untouched). See "Tables with Ribbon Workbench history" below for why
that separation is not a nicety.

All four are readable through the Web API. The three that carry XML are also **deletable** — which is
the whole answer to "how do I remove a ribbon button". The parent `ribboncustomization` is not:
`DELETE` on it returns `0x80040800 — The 'Delete' method does not support entities of type
'ribboncustomization'`.

`ribbon_get` reads these rows. That is a deliberate choice over the two obvious alternatives:

- **`RetrieveEntityRibbon` returns the compiled ribbon** — the out-of-the-box ribbon merged with every
  managed and unmanaged customisation, about half a megabyte. It tells you what the client will render;
  it cannot tell you what this org changed. Note that it contains **no `<CustomAction>` elements at
  all**: the diff has already been applied, so a custom button shows up as a plain `<Button>` inlined
  into its group, plus a `<CommandDefinition>` and any rules it references. Searching the compiled
  ribbon for your CustomAction id will always come up empty — search for the **button id** and the
  **caption** instead.
- **A solution export returns nothing useful.** Export a table that was added to the solution *without*
  subcomponents and `<RibbonDiffXml>` comes back as an empty scaffold even when buttons exist. Add it
  *with* subcomponents and you drag in every form, view and column.

> `RetrieveEntityRibbon` is an OData **function**, not an action: `GET` with inline parameters. A `POST`
> answers 404. The response is a zip whose entries include `[Content_Types].xml`.

---

## Adding a button

`ribbon_add_button` does the solution round-trip, because there is no Web API that writes a ribbon:

1. Create a throwaway unmanaged solution.
2. `AddSolutionComponent` for the table with **`DoNotIncludeSubcomponents = true`** — the export is then
   a ~3 KB table shell plus the `RibbonDiffXml` scaffold instead of the entire table.
3. Export unmanaged.
4. In `customizations.xml`: **remove the `<EntityInfo>` block** and replace `<RibbonDiffXml>`.
5. Re-zip with `customizations.xml`, `solution.xml` and `[Content_Types].xml` **at the archive root**.
6. Import with `overwriteUnmanaged = true`.
7. `PublishXml` the table.
8. **Verify** that the `ribbondiff` row exists.
9. Delete the throwaway solution.

Three of those steps are load-bearing in a way that is not obvious.

**Step 4 — strip `<EntityInfo>`.** The export carries the table's complete property block: ownership,
auditing, duplicate detection, mobile visibility, and thirty more flags. Leave it in and every ribbon
import writes all of it back, changing far more than intended. An import carrying only `<Name>` plus
`<RibbonDiffXml>` works fine.

**Step 5 — zip in code, not in the shell.** PowerShell's `Compress-Archive` reads the square brackets
in `[Content_Types].xml` as a wildcard and drops the file without a word. The archive is then not a
valid solution. `ribbon_add_button` uses `System.IO.Compression.ZipArchive`; if you do it by hand, use
`-LiteralPath` or `[System.IO.Compression.ZipFile]`.

**Step 8 — verify, don't trust.** The import job reports
`<entityRibbon … processed="true"><result result="success" />` for a ribbon that was never written.
"The import succeeded" is not evidence that the button exists. The only proof is the `ribbondiff` row,
and it only materialises **after** the publish.

**Import replaces a section — unless the section is empty.** This one is worth reading twice, because
the two halves point in opposite directions and both are real:

- An **empty** `<CustomActions />` means "not specified". Nothing is added, nothing is removed. This is
  why you cannot delete a button by omitting it from an otherwise empty diff.
- A **non-empty** `<CustomActions>` **replaces the entire collection**. Import a diff that mentions only
  button B and button A is gone — silently, of course.

So a naive `ribbon_add_button` that sends just the new button would destroy every other button on the
table. `ribbon_add_button` therefore reads the current diff first (managed nodes excluded — those belong
to their owning solution) and re-sends every existing node alongside the new one, replacing only the
node whose id matches. It then verifies that the pre-existing CustomActions are still there and reports
any that are not in `lostCustomActionIds`.

Two consequences worth knowing. Re-running `ribbon_add_button` with the same `buttonId` updates that
button in place. And removal *could* in principle be done by importing a non-empty collection that omits
the unwanted node — but only while at least one other button remains, since the all-empty case is a
no-op. That is a sharp edge, which is why `ribbon_remove_button` deletes rows instead.

**Re-sending is not enough — each node has to go back into its own section.** `<LocLabels>` behaves
exactly like `<CustomActions>`: empty means "leave alone", non-empty replaces the collection. So the
labels of the other buttons have to travel with the import too, and they have to travel in the right
place. See the next section for what happens when they do not.

### Tables with Ribbon Workbench history

The Ribbon Workbench does not write captions as literal `LabelText` attributes. It writes a `<LocLabel>`
node per localized string and points the button at it with `$LocLabels:<id>;`:

```xml
<LocLabel Id="sample.invoice.Clone.CloneButton.LabelText">
  <Titles><Title languagecode="1031" description="Rechnung kopieren" /></Titles>
</LocLabel>
```

Those nodes land in `ribbondiff` with `difftype = 3`, right next to the CustomActions. Feed one back
into `<CustomActions>` and the import fails — the importer parses every child of that section as a
CustomAction and demands a `Location` attribute, which a LocLabel naturally does not have:

```
Ribbons import: FAILURE: Missing Location Attribute in the Ribbon Customization XML
for CustomAction element with Id=sample.invoice.Clone.CloneButton.LabelText on Entity=invoice
```

The import is atomic, so nothing is lost when this happens — but nothing can be added either, and the
table stays unmanageable through the tool until the sorting is right.

Worth knowing why this went unnoticed for so long: the reference table used throughout this document,
`sample_purchaseorder`, has **zero** LocLabel nodes — all of its captions are literal. Any table someone has
opened in the Ribbon Workbench has them, which is most of the older ones.

`ribbon_remove_button` deletes a button's label rows along with it (`<buttonId>.LabelText`, `.Alt`, and
any other `<buttonId>.<attribute>`). That matters more than tidiness: an orphaned LocLabel row stays in
the table's diff and breaks the *next* `ribbon_add_button` on that table.

**Do not point `solutionUniqueName` at a solution that has grown.** Reusing an existing solution saves
the throwaway round-trip, but the import then carries *everything* that solution contains. One cloud
flow left in `ActiveUnpublished` is enough to fail the whole thing:

```
Error while importing workflow {…} type ModernFlow … : You are attempting to do a published update
of publishable component in an unmodified active context when there exists an unpublished active row
```

That has nothing to do with the ribbon. Leave `solutionUniqueName` unset and let the tool create and
delete its own solution; on an out-of-the-box table such as `invoice` it only needs
`publisherUniqueName` (there is no customization prefix to infer one from).

**The round-trip needs a long HTTP timeout.** `importTimeoutSeconds` (default 900) governs how long the
*import job* may take, but the underlying `HttpClient` used to default to 100 s and cancelled the call
first — `The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing`, on
a table where the same call had succeeded minutes earlier. The clients are now configured with generous
timeouts so the per-operation limits are the ones that actually apply.

**Step 7 needs a retry.** A publish issued immediately after `ImportSolutionAsync` reports terminal
routinely comes back with HTTP 429 / `0x80071151 — "Cannot start the requested operation [Publish]
because there is another [Import] running at this moment"`. The async operation really is finished; the
solution installation behind it is not. Because the round-trip imports and publishes back to back, this
is the normal case, not an edge case — `ribbon_add_button` retries with exponential backoff.

---

## Removing a button

**An empty import cannot remove anything.** Importing `<CustomActions />` empty leaves existing entries
untouched. So does `PublishAllXml` afterwards. So does deleting the solution that imported them — by
then the diff lives in the Active layer, and the solution record has nothing to do with it. This was
reproduced twice, independently. (A *non-empty* collection does replace, per the previous section — but
that only helps while at least one button survives, and it is far too easy to take the wrong ones with
it.)

**Deleting the stored rows does work.** `ribbon_remove_button` deletes the `ribbondiff` row for the
`<CustomAction>`, then the `ribboncommand` row for its `<CommandDefinition>` (only when no surviving
CustomAction still references it) and any `<EnableRule>` rows belonging to the button, then publishes
the table and re-queries to confirm. Verified end-to-end against a live org: before, the compiled
ribbon carried the `<CustomAction>` and its `ribbontabtocommandmap` rows and the button rendered;
after, all of them are gone and it does not.

Two caveats:

- **The `<CommandDefinition>` can linger** in the compiled ribbon even after its `ribboncommand` row is
  deleted and the org is fully published. It is inert — nothing places it on a tab any more — and the
  tool reports it as a warning rather than a failure.
- **Managed diff rows are refused.** A managed `<CustomAction>` can only be suppressed, by importing a
  solution that carries a `<HideCustomAction>` for its location. Deleting the row would leave the org in
  a state the owning solution cannot repair.

---

## Failures that are completely silent

Every one of these returns success from the API, logs nothing, and leaves you with a button that is
missing or wrong. Each cost hours.

**1. An invalid `fontIcon` makes a modern command vanish.** `$clientsvg:Money` is semantically perfect
and does not exist. The command is created, the row is stored, and the button simply never appears. The
only symptom anywhere is the Command Designer flagging "Icon is required" in red. `command_create` now
validates against the known-good set unioned with the icons actually in use in the target environment;
`command_list_icons` prints it.

**2. A caption the client cannot resolve costs you the whole button.** There are two ways to get one,
and the harmless-looking one is the worse:

- A **literal `LabelText`** is stored correctly, reported back correctly by both `ribbon_get` and
  `RetrieveEntityRibbon` — and the Unified Interface draws nothing at all. A command without a caption
  has nothing to render. Verified on `invoice`: the same button appeared the moment its caption moved
  into a `<LocLabel>`, icon and all.
- A **`$LocLabels:` reference whose `<LocLabel>` node is missing** renders the raw token, giving you a
  button captioned `LabelText`.

The working configuration is the one the Ribbon Workbench writes: a `<LocLabel>` node per string plus a
`$LocLabels:<id>` reference on the button. `ribbon_add_button` writes both halves — pass the caption
itself as `label`.

**The `languagecode` on the LocLabel should match the org, though less dramatically than expected.**
`ribbon_add_button` takes the language from **the captions the table already carries** — the same
"compare on the same table" rule that applies to verifying — and only then falls back to the org's base
language. The code it settled on and where it came from are reported as `captionLanguageCode` /
`captionLanguageSource`; `languageCode` overrides it.

> Measured, not assumed: a button whose LocLabels carried `languagecode="1033"` rendered perfectly in an
> org whose base language is 1031 — caption and icon both. So 1033 is resolved as a fallback rather than
> ignored, and a mismatched language is **not** the same class of failure as a literal caption. What
> remains untested is a language the org has not installed at all (1041 and the like); until someone
> measures that, matching the table is simply the choice with no downside.
>
> The reason this is written down at all: the first live run stored 1033 into a 1031 org because the
> org-level query came back empty (the `organization` row is not readable for every caller) and the
> fallback said nothing about it. The eleven existing LocLabels on that very table all said 1031 — the
> table knew the answer the whole time.

> This paragraph used to say the opposite ("use literal `LabelText`"), generalised from a single case
> where the trigger was a *dangling* reference. Literal captions are worse than a wrong caption: the
> button is not there at all.

**3. `ModernImage="$webresource:….svg"` is fine.** This entry previously claimed the whole button
vanishes. It does not — on `invoice`, six buttons carry exactly such a reference and every one of them
renders. The disappearance that produced the rule is failure 2 above. The rejection in
`ribbon_add_button` has been removed. A PNG pair on `Image16by16` / `Image32by32` via `imageWebResource`
remains the route for the classic (non-modern) command bar.

**4. A `Location` that does not exist renders nothing.** Import succeeds, publish succeeds, no button.
`ribbon_add_button` validates the location against the compiled ribbon first; `ribbon_get` with
`includeLocations=true` lists the valid ones.

**5. Custom commands only render in some apps.** `d365default` showed nothing where the custom
model-driven app (`sample_mainapp`) and the Purchase Orders app both showed the button. Check in the wrong app and you
will conclude the mechanism is broken.

**6. `RetrieveEntityRibbon` proves storage, not rendering.** The definition sat there the whole time
while the button was not being drawn. This holds per **attribute**, not just per button: a literal
`LabelText` is reported back verbatim by a button that has no caption as far as the client is concerned.

**7. `origin` on `appaction` is create-only.** `PATCH {"origin":0}` answers 200 and changes nothing.

**8. `uniquename` must start with a customization prefix and an underscore**, else `0x800608ad`.

**9. The `appaction` lookups bind by navigation property**, `ContextEntity` and
`OnClickEventJavaScriptWebResourceId`. The attribute names fail with `0x80048d19`.

**10. `webresource.content` from a GET is the published version.** After an unpublished PATCH you keep
reading the old one.

**11. Deleting a solution does not reset its components.** Removing the throwaway solution does not undo
the ribbon diff it imported.

**12. A grid modern command with `visibilitytype = 0` disappears when rows are selected.** The command
bar switches into its selection context and every command without a visibility rule drops out. This is
the most common "my button vanished" report; `command_create` now refuses it unless you pass
`allowGridWithoutVisibilityRule=true`.

**13. A ribbon import deletes the buttons it does not mention** — provided it mentions at least one.
This is the flip side of failure 4's cousin: the import that adds your new button quietly takes the
existing ones with it. `ribbon_add_button` re-sends the current diff to prevent it, and reports
`lostCustomActionIds` if anything vanishes anyway.

---

## How to check whether it worked

This is the part that cost the most time, and it was a method problem, not a technical one.

The button **was rendering the whole time** — but with an unresolved `$LocLabels:` reference, so its
caption was the literal string `LabelText`. The check was a search for the expected caption
("resolve"), which found nothing, which led to the conclusion that classic
ribbons no longer worked at all on this org. The verification method hid the success.

So:

- **Read the whole command bar.** Do not filter for the label you expect — a mislabelled button is
  precisely the case you would then miss.
- **Compare against a reference object you know works — on the SAME table.** If your reference renders
  and yours does not, the difference is in your definition. If neither renders, you are looking in the
  wrong app.
  This qualifier cost a full round of debugging on its own. A button on `invoice` was compared against
  `sample_purchaseorder`, where a literal caption without an icon works fine. That comparison sent the
  investigation through `Location`, `TemplateAlias`, the id scheme, `Sequence`, publishing and the
  client cache, while the cause sat in the caption the whole time — the five buttons on `invoice` itself
  would have shown it in one look, because every one of them uses `<LocLabel>` nodes.
- **Check in an app where custom commands are known to appear**, not in `d365default`.
- **Separate storage from rendering.** `ribbon_get` (or `RetrieveEntityRibbon`) answers "is it stored".
  Only the browser answers "is it drawn". Never let the first stand in for the second.
- **Search the compiled ribbon for the right marker.** It holds no `<CustomAction>` elements — look for
  `Id="<buttonId>"` and the caption. Asserting on the CustomAction id produces a false negative every
  time, which is its own little version of the trap above.

---

## When the change does not reach the users

A different failure from everything above: the button exists and renders, the change was deployed
and published, and the users still run the **old** command. Nothing reports an error. This was
observed on a production environment, where it took three separate repairs before a repointed button
reached the users.

A ribbon lives in three places, and each can be wrong while the others are right:

| Level | What it is | Read it with | Typical failure | Repair |
|---|---|---|---|---|
| **Stored** | One `ribboncommand` / `ribbondiff` / `ribbonrule` row per node **per solution layer**, keyed by `solutionid`. The row whose solution is `Active` is the unmanaged layer. | `ribbon_diagnose` (layers per command) | An unmanaged layer differs from every managed copy and wins over each deployment. | `ribbon_sync_unmanaged_layer` |
| **Compiled** | `RetrieveEntityRibbon` — the out-of-the-box ribbon with every layer applied. | `ribbon_get_merged`, `ribbon_diagnose` | Not published yet. | `publish_customizations` |
| **Delivered** | `GetClientMetadata` — what an app's client actually receives. | `ribbon_diagnose` | The client keeps receiving an older command than the compiled ribbon. | `ribbon_refresh_client_metadata` |

`ribbon_diagnose` checks all three and names the first one that is wrong. Run it whenever users report
the old behaviour, and after every ribbon change on an environment that receives managed solutions.

### Stored: an unmanaged layer overrides every deployment

Unmanaged always wins over managed. An unmanaged ribbon layer on a target environment usually comes
from editing the ribbon there directly, e.g. with the Ribbon Workbench. From then on every managed
deployment of that command is stored correctly and has no effect.

- **It cannot be removed.** `RemoveActiveCustomizations` refuses `RibbonCustomization` and its parts
  with `0x8004f059 … not supported for components of type RibbonCustomization`; so does the generic
  `RemoveActiveCustomization` action. Removing the **table's** active layer returns `true` and leaves
  the ribbon rows untouched — the ribbon is a component of its own. The Maker Portal offers nothing
  either.
- **It can be overwritten.** `ribbon_sync_unmanaged_layer` writes the copy a solution holds into the
  unmanaged layer: the solution round-trip of `ribbon_add_button`, with `<EntityInfo>`,
  `<CustomControlDefaultConfigs>` and `<EntityMaps>` stripped, so the import touches nothing but the
  ribbon. Nodes that exist only in the unmanaged layer are kept by default, because a non-empty section
  replaces the whole collection. `dryRun` defaults to `true` and returns the plan per node.
- **Afterwards the layer still exists**, now with the right content. The next ribbon change to the same
  command needs the same step after the deployment. `ribbon_scan_unmanaged_layers` lists every table of
  an environment that carries such a layer — on one real target environment, 96 tables had an
  unmanaged `RibbonCustomization` row and five of them unmanaged command definitions.

### Delivered: the client keeps the old command

After the stored layer was fixed, the database and `RetrieveEntityRibbon` were correct, and
`GetClientMetadata` still delivered the old command. A request that bypassed the browser cache
(`fetch(…, { cache: "no-store" })`) got the same answer, so it was the server, not the browser. None of
these changed it:

- `PublishXml` for the table, the application ribbon and the app modules (`<appmodules>`)
- `PublishAllXml` ("Publish all customizations")

What did change it: a change to the **table's own metadata**. One character in one description label,
published, and the client received the current command at the next load. The client metadata version
(the `gcm_<version>_…` key in the browser's Cache Storage) moved at the same moment; the ribbon's own
`VersionNumber` in the payload did not. `ribbon_refresh_client_metadata` does exactly this and then
restores the table:

- **No unmanaged table layer before** → the bump created one; `RemoveActiveCustomizations` removes it.
- **An unmanaged table layer already existed** → the original label is written back, so the
  customizations in that layer stay as they were.

Users reload the app afterwards.

### Reading what the client receives

`GetClientMetadata(ClientMetadataQuery=@q)` takes a complex `MetadataQuery`, passed as a **bare JSON
object** — a quoted string fails with *"Type verification failed. Expected type
'Microsoft.Dynamics.CRM.MetadataQuery'"*.

- `MetadataType: "ribbon"` ignores the table and returns every ribbon context of the app — over 300 MB on
  a grown org, with any `DependencyDepth`. `MetadataNames` and `MetadataId` do not narrow it.
- `MetadataType: "form"` with the id of a main form **that is part of the app** and
  `DependencyDepth: "OnDemandWithoutContext"` returns that form with its command bar, typically
  5–15 MB. A form the app does not contain answers with a few hundred bytes and no ribbon.
- The response wraps the metadata as a JSON **string** in `Metadata`. Inside, `Ribbons[]` has one entry
  per context (`Form`, `HomePageGrid`, `SubGridStandard`, …) with `EntityLogicalName`, `VersionNumber`
  and `Commands.<table>.<commandId>.Actions[].Attributes` (`FunctionName`, `Library`).
- Every delivered command carries `SolutionUniqueName` of the top layer (often `Active`) and the display
  rule `Mscrm.OutlookRenderTypeWeb`. Neither is a sign of a problem.
- Some app/form combinations answer `400 0x80040216 "GetClientMetadata failed"`, observed right after a
  publish. `ribbon_diagnose` then tries the next form and reports the app in `notes`.

In the browser the same payload sits in Cache Storage under `gcm_<version>_<tenant>_<…>`; search an
entry for `"<commandId>":{` and read its `Library`.

---

## Tool reference

### `ribbon_diagnose`

| Parameter | Type | Required | Description |
|---|---|---|---|
| `tableLogicalName` | string | yes | |
| `commandIds` | string | no | Comma-separated command ids; default every stored command of the table |
| `appModuleId` | GUID | no | Only check delivery for this app; default every app containing the table |
| `checkDelivered` | bool | no | Default true. One `GetClientMetadata` call per app |

Read-only. Returns per command the stored layers (solution, managed, action signature), the compiled
signature, the delivered signature per app with its ribbon `VersionNumber`, and a `verdict`: `stored`,
`compiled`, `delivered`, `unused` (no button places the command) or `ok`, with the repair. Grid
commands are checked at the stored and compiled levels; delivery is read for the form command bar.

**Example prompt:** "Users still get the old behaviour of the Send button on invoice — diagnose it"

---

### `ribbon_scan_unmanaged_layers`

No parameters. Read-only. Lists every table of the environment whose ribbon carries unmanaged
commands, split into `commandsOverridingManaged` (a managed deployment cannot change these) and
`unmanagedOnlyCommands`. Run it on a target environment before and after deploying a ribbon change.

---

### `ribbon_sync_unmanaged_layer`

| Parameter | Type | Required | Description |
|---|---|---|---|
| `tableLogicalName` | string | yes | |
| `sourceSolution` | string | yes | Solution whose copy is the intended one — usually the managed solution the deployment shipped |
| `dryRun` | bool | no | **Default true**: return the plan (`add` / `update` / `unchanged` / `keep` / `drop`) and the RibbonDiffXml |
| `dropUnmanagedOnly` | bool | no | Default false: unmanaged-only nodes are kept |
| `publisherUniqueName` | string | no | Publisher of the throwaway solution; defaults to the table's prefix publisher, or the Default solution's publisher for out-of-the-box tables |

With `dryRun=false` it imports, publishes the table and verifies that every node of the source now has
an identical unmanaged copy (`verified`, `stillDifferent`). Imports refused with `0x80071151` while
another solution operation runs — e.g. Microsoft updating its own solutions — are retried.

---

### `ribbon_refresh_client_metadata`

| Parameter | Type | Required | Description |
|---|---|---|---|
| `tableLogicalName` | string | yes | |
| `verify` | bool | no | Default true: run `ribbon_diagnose` afterwards |
| `dryRun` | bool | no | Record the writes instead of sending them |

Reports `hadUnmanagedTableLayer`, how the table was restored and whether the description came back
unchanged.

---

### `ribbon_get`

| Parameter | Type | Required | Description |
|---|---|---|---|
| `tableLogicalName` | string | yes | |
| `includeManaged` | bool | no | Include diff nodes owned by managed solutions (default false) |
| `includeLocations` | bool | no | Also list valid `Location` strings from the compiled ribbon |

Returns the reconstructed `RibbonDiffXml` plus the underlying rows with their ids, so you can see
exactly what `ribbon_remove_button` would delete. The rows come pre-sorted by `difftype`:
`customActions`, `locLabels` and `otherDiffs`.

**Example prompt:** "Show me the classic ribbon customisations on sample_purchaseorder and the locations I
could add a button to"

---

### `ribbon_add_button`

| Parameter | Type | Required | Description |
|---|---|---|---|
| `tableLogicalName` | string | yes | |
| `buttonId` | string | yes | e.g. `sample.sample_purchaseorder.CorrectPrice.Button`; the CustomAction becomes `<buttonId>.CustomAction`, the command `<buttonId>.Command` |
| `location` | string | yes | Full `Location`, e.g. `Mscrm.Form.sample_purchaseorder.MainTab.Save.Controls._children` |
| `label` | string | yes | The caption itself. The tool writes the `<LocLabel>` nodes and the `$LocLabels:` references |
| `webResourceName` | string | yes | JScript web resource holding the handler |
| `functionName` | string | yes | Fully qualified handler |
| `parameters` | string | yes | Classic ribbons **name** their parameters: `PrimaryControl` for a form button, `SelectedControlSelectedItemIds,SelectedControl` for a grid one. Literals: `String:abc`, `Bool:true`, `Int:5` |
| `sequence` | int | no | Default 41 |
| `imageWebResource` | string | no | PNG web resource for `Image16by16`/`Image32by32` — the reliable icon route |
| `modernImage` | string | no | Icon for the modern command bar, e.g. `$webresource:sample_/images/envelope-back-front.svg` |
| `enableRule` | string | no | `OneSelected`, `AtLeastOneSelected`, `SelectionCountRule:<min>[-<max>]`, or a literal `<EnableRule>` fragment. **A `Minimum` above 1 is not enforced** — see below |
| `tooltipTitle`, `tooltipDescription` | string | no | Default to the label; each gets its own `<LocLabel>` |
| `languageCode` | int | no | Language of the caption LocLabels; defaults to the language the table's existing captions use, then the org's base language. Reported back as `captionLanguageCode` |
| `templateAlias` | string | no | Default `o1` |
| `solutionUniqueName` | string | no | Reuse a solution instead of a throwaway one |
| `publisherUniqueName` | string | no | Defaults to the publisher owning the table's prefix |
| `validateLocation` | bool | no | Default true — turning it off re-enables failure 4 |
| `publish` | bool | no | Default true |

**Example prompt:** "Add a grid button 'Resolve price difference' to sample_purchaseorder that calls
Sample.PurchaseOrder.CorrectPrice.onGridButton in sample_purchaseorder_correct_price.js with
SelectedControlSelectedItemIds and SelectedControl, enabled only when exactly one row is selected"

> **`SelectionCountRule` with `Minimum="2"` is not enforced.** The button is enabled with a single row
> selected as well — the platform treats the rule as "at least one selection". This is not a tool defect
> and the rule is still written as asked, but the lower bound has to be checked again in the handler and
> server-side. `Maximum` and the `OneSelected` shortcut are unaffected.

---

### `ribbon_remove_button`

| Parameter | Type | Required | Description |
|---|---|---|---|
| `tableLogicalName` | string | yes | |
| `buttonId` | string | yes | Matches the button id, the CustomAction id, any diff whose XML declares `Id="<buttonId>"`, and the button's `<LocLabel>` rows (`<buttonId>.LabelText`, `.Alt`, …) |
| `removeCommandDefinition` | bool | no | Default true; skipped when another CustomAction still references it |
| `publish` | bool | no | Default true |

Refuses managed rows. Reports the inert-`<CommandDefinition>` residue as a warning.

---

## Modern-command additions

`command_create` now takes `visibilityType` plus, for `visibilityType = 1`,
`visibilityFormulaComponentLibrary` / `visibilityFormulaComponentName` /
`visibilityFormulaFunctionName` — the three fields Power Fx visibility is stored across. Example values
from a live org: library `31eaa81c-b8d7-4f9d-8e4d-900e1c81c301`, component
`838813193347447db39c7ad8e32689c6`, function `Visible`.

`command_list_component_libraries` lists the canvas component libraries (`canvasapp` rows with
`canvasapptype = 1`).

**A component library cannot be created through any API.** `POST /canvasapps` is rejected outright with
`0x80040200 — Attribute 'aadlastpublishedbyid' cannot be NULL`, and getting past that would still leave
you needing a valid `.msapp` document with a component exposing the output property. Libraries are
created by the Command Designer, and only when it is opened **from an app**. This is a hard limitation,
and it is exactly the app dependency that argues for the classic `SelectionCountRule`.

---

## `ribbon_get_merged` vs `ribbon_get`

Two different questions, two different tools.

| | `ribbon_get` | `ribbon_get_merged` |
|---|---|---|
| Source | the stored `ribbondiff` / `ribboncommand` / `ribbonrule` rows | `RetrieveEntityRibbon` |
| Shows | **this environment's own changes** to the ribbon | the out-of-the-box ribbon with every solution's `RibbonDiffXml` applied — what the app actually renders |
| Use it to | see or edit what you control | answer "is anything still hanging off this button or library?" |

The distinction matters when deciding whether something can be removed. A button that arrives from a
managed solution does not appear in the diff at all, so a clean `ribbon_get` is **not** evidence that
nothing uses a library.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `table` | string | Yes | Table logical name |
| `contains` | string | No | Return only matching lines, with line numbers |

The compiled ribbon runs to hundreds of kilobytes (half a megabyte for a stock table is normal), so
use `contains` unless you really want the whole document.

**Example prompt:** "Does the merged ribbon of sample_orderline still reference sample_order_js?"
