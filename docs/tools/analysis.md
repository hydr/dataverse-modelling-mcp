# Analysis Tools

Read-only questions worth answering **before** changing or deleting something.

## `component_dependencies`

Lists everything that depends on a component. Wraps `RetrieveDependenciesForDelete` and resolves the
GUIDs and type codes it returns into names.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `objectId` | GUID string | Yes | The component's own `objectid` — for a table or column its `MetadataId`, the same id `solution_get` reports as `componentId` |
| `componentType` | int | Yes | Component type code (1=Entity, 2=Attribute, 26=SavedQuery, 29=Workflow, 60=SystemForm, 61=WebResource, 66=CustomControl, 91=PluginAssembly) |

**Example prompt:** "What depends on the xv_score column of xv_mcptest?"

`canDelete: true` means nothing in the environment requires the component. That plain statement is
the point of the tool: the raw function answers with an empty collection, which reads exactly like a
call that silently failed.

Calling the function by hand is easy to get wrong in a way that hides the cause. It is a function
with parameters, so it needs OData parameter aliases:

```
GET /api/data/v9.2/RetrieveDependenciesForDelete(ObjectId=@p1,ComponentType=@p2)?@p1=<guid>&@p2=2
```

Inlining the values instead returns an HTML `Runtime Error` page rather than a JSON error.

A column can only be named through its owning table, and the dependency response carries that parent
— so both the dependents and the component under examination come back with real names:

```
"componentName": "xv_mcptest.xv_name",
"summary": "9 component(s) depend on Attribute xv_mcptest.xv_name and would break if it were
            deleted: 3× SystemForm, 6× SavedQuery"
```

---

## `entity_solution_map`

Shows which solutions contain a table, each one's `rootcomponentbehavior`, and the subcomponents it
holds explicitly.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `logicalName` | string | Yes | Table logical name (e.g. `salesorder`) |
| `resolveComponentNames` | bool | No | Resolve subcomponent GUIDs to names (default `true`) |

**Example prompt:** "Which solution carries changes to the invoice table?"

This answers the recurring "which solution carries this change?". The answer hangs entirely on
`rootcomponentbehavior`, and the same table routinely sits in a dozen solutions with different
values:

| Value | Label | Consequence for subcomponents |
|---|---|---|
| 0 | Include Subcomponents | Forms and columns travel with the table automatically, and get **no** membership row of their own |
| 1 | Do not include subcomponents | Must be added explicitly |
| 2 | Include As Shell Only | Must be added explicitly |

That is why `solution_add_component` for a column can be a no-op in one solution and necessary in
the next — see [solution_add_component](solutions.md#solution_add_component).

Subcomponents are found through their `rootsolutioncomponentid`, which points back at the table's own
membership row. That is the platform's own record of what belongs to what, so no matching on type
and owning entity is involved.

The `summary` states the consequence rather than the codes:

> Forms and columns of 'invoice' travel automatically with Accounting, Crossvertise, Default
> (rootcomponentbehavior 0). Subcomponents must be added explicitly in CrossvertiseInvoicing
> (behaviour 1, 19 held explicitly), CrossvertiseForms (behaviour 2, 1 held explicitly), …
