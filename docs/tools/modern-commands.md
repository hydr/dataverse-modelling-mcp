# Modern Command Tools

Modern commands are the Unified Interface successor to classic RibbonDiffXml buttons. They live in the
`appaction` table (entity set `appactions`) and are what the **Command Designer** in the maker portal
writes.

> **Read [buttons-classic-vs-modern.md](buttons-classic-vs-modern.md) before choosing this route.**
> Classic `RibbonDiffXml` is not dead — it renders side by side with modern commands even on an org
> whose `appactionmigration` row `msdyn_System` has `ismigrated = true`. For a button whose visibility
> depends on the grid selection, the classic entity-bound `SelectionCountRule` is usually the better
> tool, because the modern equivalent needs a canvas component library that only the Command Designer
> opened *from an app* can create.

Most of what follows is not in the product documentation; it was verified against the `appaction`
option-set metadata and the migrated system commands of a live org.

## Field semantics

### `location` (`appaction_location`)

| Value | Meaning |
|-------|---------|
| 0 | Form |
| 1 | Main grid (homepage grid) |
| 2 | Sub grid |
| 3 | Associated grid |
| 4 | Quick form |
| 5 | Global header |
| 6 | Dashboard |

### `context` (`appaction_context`)

`0` = All, `1` = Entity. Entity-bound commands use `context = 1` **plus** `contextvalue` = the table's
logical name **plus** the `ContextEntity` lookup.

### `origin` (`appaction_origin`) — the one that decides whether the button appears

| Value | Label | Use |
|-------|-------|-----|
| 0 | Default | Hand-authored commands. **This is what you want.** |
| 1 | Migrated | The modern mirror of a button that still lives in classic ribbon XML. |
| 2 | Enhanced Migrated | Extension of a migrated item. |

On a live org, every one of the 825 `origin = Migrated` rows corresponds to a real ribbon
`CommandDefinition`, while all hand-authored, rendering commands are `origin = Default`. A hand-made
row with `origin = Migrated` has no ribbon definition to bind to.

> **`origin` is create-only.** A `PATCH` that changes it returns HTTP 200 and silently keeps the stored
> value. A command created with the wrong origin must be deleted and recreated — `command_update`
> warns when you try.

### `visibilitytype` (`appaction_visibilitytype`)

| Value | Label | Notes |
|-------|-------|-------|
| 0 | None | **Not the same as "always visible"** — see below |
| 1 | Formula | Power Fx, stored across three fields |
| 2 | Classic Rules | Needs `appactionrule` rows via the `appaction_appactionrule_classicrules` N:N |

> **`None` on a grid is the most common trap on this table.** A grid command with `visibilitytype = 0`
> renders while nothing is selected and **disappears the moment rows are ticked**: the command bar
> switches into its selection context, and commands without a visibility rule fall out of it. If the
> button has to survive a selection it needs `Formula` or `ClassicRules`. `command_create` refuses this
> combination unless you pass `allowGridWithoutVisibilityRule=true`.

**Power Fx visibility** is stored across three fields:

| Field | Example |
|-------|---------|
| `visibilityformulacomponentlibraryid` | lookup to `canvasapp` `31eaa81c-b8d7-4f9d-8e4d-900e1c81c301` |
| `visibilityformulacomponentname` | `838813193347447db39c7ad8e32689c6` |
| `visibilityformulafunctionname` | `Visible` |

The lookup binds through its navigation property, like the other two on this table:
`"VisibilityFormulaComponentLibraryId@odata.bind": "/canvasapps(<id>)"`.

A **canvas component library** is a `canvasapp` row with `canvasapptype = 1`.
`command_list_component_libraries` lists them. **None can be created through an API**: `POST
/canvasapps` fails with `0x80040200 — Attribute 'aadlastpublishedbyid' cannot be NULL`, and a usable
library additionally needs a valid `.msapp` document behind it. Only the Command Designer creates them,
and only when opened **from an app** — opened from a solution it offers just "Show". So Power Fx
visibility always brings an app dependency along. For an entity-bound rule such as "exactly one row
selected", the classic ribbon `SelectionCountRule` does the same job without one — see
[buttons-classic-vs-modern.md](buttons-classic-vs-modern.md).

### `fonticon` — validated, because an invalid value is invisible

An unknown `fonticon` is accepted, stored, and then the command **never renders**. Nothing is logged.
The only symptom is the Command Designer flagging "Icon is required" in red. `$clientsvg:Money` looks
entirely plausible and silently kills the button.

`command_create` therefore rejects values outside the known-good set, which is the statically catalogued
list unioned with the icons actually in use in the target environment (so a richer org stays usable).
`command_list_icons` prints the effective set. The catalogued values are:

`$clientsvg:` — `Accept`, `Add`, `Archive`, `Calendar`, `CreateMajor`, `CreateMinor`, `Delete`, `Edit`,
`EditMail`, `FollowUser`, `ImportToExcel`, `MailLink`, `MergeCase`, `OpenEnrollment`, `Org`,
`PageBlock`, `PageCompleted`, `Phone`, `Pin`, `Refresh`, `RelatedKnowledgeArticle`, `RevertToDraft`,
`RoutingRule`, `Save`, `SaveAndClose`, `Share`, `TranslationNew`, `UpdateRestore`.

Legacy bare names — `Cancel`, `Close`, `Connection`, `DeleteBulk`, `EditDefaultFilter`, `FormDesign`,
`NewMeeting`, `No`, `OpenDelve`, `OpenEmail`, `OpenRecord`, `PublishKnowledgeArticle`,
`QueueItemRelease`, `QueueItemRemove`, `Report`, `Resolve`, `RestoreArticle`, `SendSelected`,
`SetRegarding`, `SharePoint*` (9 values), `TableGroup`, `ViewHierarchy`, `Yes`.

### `onclickeventtype` / `type`

`onclickeventtype`: `0` = None, `1` = Formula, `2` = JavaScript.
`type`: `0` = Standard button, `1` = Dropdown, `2` = Split, `3` = Group.

### `uniquename`

Must start with a publisher customization prefix followed by an underscore, otherwise the create fails
with `0x800608ad — "Export key attribute uniquename for component appaction must start with a valid
customization prefix"`. The generated default is `<prefix>_<name>!<table>!<location>`.

### Lookup binding

The two lookups must be bound through their **navigation property** names:

```json
"ContextEntity@odata.bind": "/entities(<table MetadataId>)",
"OnClickEventJavaScriptWebResourceId@odata.bind": "/webresourceset(<web resource id>)"
```

Using the attribute names `contextentity` / `onclickeventjavascriptwebresourceid` fails with
`0x80048d19 — undeclared property`. Note that `ContextEntity` targets the `entity` table, so the value
is the table's **MetadataId**, not its logical name.

### `onclickeventjavascriptparameters`

A JSON array of `{"type": <int>, "value": <string|null>}`. The integers are the classic ribbon
`CrmParameter` enum carried over by the ribbon-to-modern-command migration. The mapping below was
derived by aligning 470 migrated `appaction` rows position-by-position with the `<CrmParameter>`
children of the matching `CommandDefinition` in the ribbon XML from `RetrieveApplicationRibbon` /
`RetrieveEntityRibbon`; every position was unanimous.

| Value | Name | Meaning |
|-------|------|---------|
| 1 | `PrimaryEntityTypeCode` | Object type code of the form's table |
| 2 | `PrimaryEntityTypeName` | Logical name of the form's table |
| 3 | `PrimaryItemIds` | Array with the form record's id |
| 4 | `FirstPrimaryItemId` | GUID of the form's record |
| 5 | `PrimaryControl` | The form context object |
| 7 | `SelectedEntityTypeCode` | Object type code of the grid's table |
| 8 | `SelectedEntityTypeName` | Logical name of the grid's table |
| 10 | `FirstSelectedItemId` | GUID of the first selected row |
| 12 | `SelectedControl` | The grid control object |
| 18 | `BoolParameter` | Literal boolean; set `value` |
| 20 | `IntParameter` | Literal integer; set `value` |
| 21 | `StringParameter` | Literal string; set `value` |
| 23 | `SelectedControlSelectedItemIds` | GUIDs of the selected rows |
| 24 | `SelectedControlSelectedItemReferences` | Entity references of the selected rows |
| 25 | `SelectedControlAllItemCount` | Count of all rows in the grid |

Other integers appear in system commands but could not be disambiguated; pass them as raw numbers.

Typical form command: `PrimaryControl`.
Typical grid command: `SelectedControlSelectedItemIds,SelectedControl`.

## Tools

### `command_list`

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `tableLogicalName` | string | Yes | Logical name, matched against `contextvalue` |

Includes the managed migrated system commands as well as hand-authored ones.

**Example prompt:** "List the modern commands on sample_purchaseorder"

---

### `command_get`

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `appActionId` | GUID string | Yes | The `appaction` row |

Returns the full definition with resolved enum names and a decoded parameter list.

---

### `command_create`

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `tableLogicalName` | string | Yes | Table the command is bound to |
| `name` | string | Yes | Internal name |
| `buttonLabelText` | string | Yes | Button label |
| `location` | int | Yes | See the location table above |
| `javaScriptWebResourceName` | string | Yes | Name or GUID of the JScript web resource |
| `functionName` | string | Yes | Fully qualified handler, e.g. `Sample.PurchaseOrder.CorrectPrice.onFormButton` |
| `parameters` | string | Yes | Names, JSON array, or `{type,value}` objects |
| `tooltipTitle` | string | No | |
| `tooltipDescription` | string | No | |
| `fontIcon` | string | No | e.g. `$clientsvg:Add` |
| `sequence` | number | No | System commands sit around `1000100200` |
| `uniqueName` | string | No | Must carry a customization prefix |
| `customizationPrefix` | string | No | Defaults to the table's prefix |
| `solutionUniqueName` | string | No | Adds the command as component type **10343** |
| `visibilityType` | int | No | `0` None, `1` Formula, `2` ClassicRules |
| `visibilityFormulaComponentLibrary` | string | No | GUID, unique name or display name; required for `visibilityType = 1` |
| `visibilityFormulaComponentName` | string | No | Required for `visibilityType = 1` |
| `visibilityFormulaFunctionName` | string | No | Required for `visibilityType = 1`, e.g. `Visible` |
| `allowGridWithoutVisibilityRule` | bool | No | Suppress the guard on grid + `visibilityType = 0` |

Always follow with `publish_customizations` for the table.

**Example prompt:** "Add a form command 'EA-ER-Differenz auflösen' to sample_purchaseorder that calls
Sample.PurchaseOrder.CorrectPrice.onFormButton in sample_purchaseorder_correct_price.js with PrimaryControl"

---

### `command_update`

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `appActionId` | GUID string | Yes | |
| `buttonLabelText`, `tooltipTitle`, `tooltipDescription`, `functionName`, `parameters`, `fontIcon`, `sequence`, `hidden` | | No | Convenience fields |
| `visibilityType`, `visibilityFormulaComponentLibrary`, `visibilityFormulaComponentName`, `visibilityFormulaFunctionName` | | No | Attach or change a Power Fx visibility formula |
| `propertiesJson` | JSON | No | Merged on top for anything else |

`origin` cannot be changed — see above. `fontIcon` is validated here too.

---

### `command_delete`

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `appActionId` | GUID string | Yes | |

Publish the table afterwards so the button disappears from the client. Unlike a classic ribbon button —
which survives an import that omits it and has to be deleted row-wise — a modern command really does go
away when its record does.

---

### `command_list_component_libraries`

No parameters. Lists the `canvasapp` rows with `canvasapptype = 1`, i.e. the places a Power Fx
visibility formula can live. Creating one through an API is not possible; see the `visibilitytype`
section above.

---

### `command_list_icons`

No parameters. Returns the statically catalogued `fontIcon` values, the ones actually used in this
environment, and the union of both — which is exactly what `command_create` accepts.
