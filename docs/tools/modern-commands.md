# Modern Command Tools

Modern commands are the Unified Interface successor to classic RibbonDiffXml buttons. They live in the
`appaction` table (entity set `appactions`) and are what the **Command Designer** in the maker portal
writes. Several tables no longer render RibbonDiffXml at all, so on those a command bar button *has to*
be an `appaction` row.

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

`0` = None, `1` = Formula, `2` = Classic Rules. Classic Rules requires `appactionrule` rows associated
through the `appaction_appactionrule_classicrules` N:N relationship; without them there is nothing to
evaluate. Use `None` for always-visible buttons.

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

Always follow with `publish_customizations` for the table.

**Example prompt:** "Add a form command 'EA-ER-Differenz auflösen' to sample_purchaseorder that calls
Sample.PurchaseOrder.CorrectPrice.onFormButton in sample_purchaseorder_correct_price.js with PrimaryControl"

---

### `command_update`

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `appActionId` | GUID string | Yes | |
| `buttonLabelText`, `tooltipTitle`, `tooltipDescription`, `functionName`, `parameters`, `fontIcon`, `sequence`, `hidden` | | No | Convenience fields |
| `propertiesJson` | JSON | No | Merged on top for anything else |

`origin` cannot be changed — see above.

---

### `command_delete`

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `appActionId` | GUID string | Yes | |

Publish the table afterwards so the button disappears from the client.
