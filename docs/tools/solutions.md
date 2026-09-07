# Solution Tools

Solution tools manage ALM (Application Lifecycle Management) using the **Dataverse Web API** solution endpoints and standard Dataverse actions.

## Tools

### `solution_list`

Lists all visible solutions in the environment.

**Example prompt:** "List all solutions in the environment"

---

### `solution_get`

Gets a solution by unique name, including its complete component list.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `uniqueName` | string | Yes | Solution unique name |
| `resolveComponentNames` | bool | No | Resolve component GUIDs to names (default `true`). Costs one bulk lookup per component type — pass `false` when only the ids matter. |

**Example prompt:** "Show me all components in the MySolution solution"

Per component:

| Field | Meaning |
|---|---|
| `componentId` | The component's own `objectid` — for a table or column its `MetadataId`. **This** is the id `solution_add_component` and `solution_remove_component` take. |
| `componentType` / `componentTypeName` | Numeric code and its label. Codes ≥ 1000 are environment-specific and are resolved at runtime from `solutioncomponentdefinitions`. |
| `name` | Resolved name, where a source exists for that type (tables, columns, views, workflows, forms, web resources, code components, plug-in types and assemblies, SDK steps, roles, field security profiles, service endpoints, environment variable definitions). `null` when the type has no name source or the lookup was denied — name resolution never fails the read. |
| `rootComponentBehavior` / `rootComponentBehaviorName` | `0` = include subcomponents, `1` = do not include subcomponents, `2` = include as shell only. `null` on rows that are not roots. See [rootcomponentbehavior](#rootcomponentbehavior). |
| `rootComponentId` | The root this row hangs off (`rootsolutioncomponentid`). |
| `solutionComponentId` | Primary key of the membership row. Diagnostic only — passing it where a `componentId` is expected fails with `0x8004f021 Cannot find solution component`. |

The component list is read as its own paged query against `solutioncomponents`, not as an `$expand`
on the solution: an expanded collection carries no `@odata.nextLink`, so it can offer no proof of
being complete.

---

### `solution_create`

Creates a new unmanaged solution.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `uniqueName` | string | Yes | Unique name (no spaces) |
| `displayName` | string | Yes | Friendly display name |
| `publisherUniqueName` | string | Yes | Publisher unique name |
| `version` | string | Yes | Version string (e.g. `1.0.0.0`) |

**Example prompt:** "Create an unmanaged solution called 'My Feature' with publisher 'mycompany'"

---

### `solution_export`

Exports a solution as a base64-encoded zip.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `uniqueName` | string | Yes | Solution unique name |
| `managed` | bool | No | `true` for managed export (default: `false`) |

**Example prompt:** "Export the MySolution solution as an unmanaged zip"

---

### `solution_import`

Imports a solution asynchronously (`ImportSolutionAsync` plus ImportJob polling).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `filePath` | string | One of the two | Local path to the zip. Preferred — takes precedence over `zipBase64`. |
| `zipBase64` | string | One of the two | Base64-encoded solution zip |
| `overwriteUnmanaged` | bool | No | Whether to overwrite unmanaged customizations (default: `false`) |
| `timeoutSeconds` | int | No | Max wait for the import to finish (default `600`) |

**Example prompt:** "Import C:\temp\MySolution.zip and overwrite unmanaged customizations"

#### Code components (PCF) are checked afterwards

A reported success is not proof that a code component was updated. **A solution import only applies
a component whose `ControlManifest` version is higher than the stored one** — and it reports success
either way, right down to `result="success"` for that component in the import job. The old code keeps
running:

> Update the component version (minor or patch) in the component manifest file (for example, 1.0.0 to
> 1.0.1). Every update in the component needs a component version bump to be reflected on the
> Microsoft Dataverse server.
>
> — [Common issues and workarounds](https://learn.microsoft.com/power-apps/developer/component-framework/issues-and-workarounds)

`pac pcf push` appears to work in the same situation because it deliberately "bypasses the code
component versioning requirements".

Every control in the zip is therefore compared against what the environment stores after the import:

- `customControlVersions[]` — per control: `manifestName`, `storedName`, `manifestVersion`,
  `storedVersion`, `matches`. `matches` is `null` when the comparison could not be made at all (the
  control is not in the environment yet, or the manifest has no version) — unknown is not reported as
  a problem.
- `customControlWarnings[]` — only confirmed mismatches, with the likely cause named: a stored
  version that is *higher* is the version rule above; a stored version that is *behind* points at an
  unmanaged active layer instead (check with `solution_check_layers`, `componentType` 66, and
  re-import with `overwriteUnmanaged=true`).

Note that `publish_customizations` cannot help here: `PublishXml` has no element for code
components. Its documented `<importexportxml>` children are exactly `entities`, `ribbons`,
`dashboards`, `optionsets`, `sitemaps` and `webresources`.

---

### `solution_add_component`

Adds a component to a solution, then verifies that a membership row was really created.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `solutionUniqueName` | string | Yes | Target solution |
| `componentId` | GUID string | Yes | The component's own `objectid` — for a table or column its `MetadataId`, **not** the `solutioncomponentid` of a membership row |
| `componentType` | int | Yes | Component type code (1=Entity, 2=Attribute, 26=SavedQuery, 29=Workflow, 60=SystemForm, 61=WebResource, 66=CustomControl, 91=PluginAssembly) |

**Example prompt:** "Add workflow abc123 (type 29) to the MySolution solution"

`AddSolutionComponent` reports success even when it changes nothing, so the result says what actually
happened:

- `explicitMembership: true` plus `solutionComponentId` — a row was created.
- `explicitMembership: false` — no row exists. That is the normal outcome when the component is
  covered by a table held with `rootcomponentbehavior 0`: subcomponents travel with such a table and
  never get a row of their own. `note` names the covering table where it can be pinned down (for a
  column it is determined exactly, by asking each candidate table whether it owns that `MetadataId`),
  `coveringRootComponents` lists the candidates, and `success` stays `true` — being covered is a fine
  outcome, not a failure.
- `explicitMembership: false` with `success: false` — nothing could have covered the component, so
  the add really did nothing. Verify `componentId` and `componentType`.

---

### `solution_remove_component`

Removes a component from a solution.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `solutionUniqueName` | string | Yes | Target solution |
| `componentId` | GUID string | Yes | The component's own `objectid` — for a table or column its `MetadataId`, as returned by `solution_get` |
| `componentType` | int | Yes | Component type code |

**Example prompt:** "Remove the account entity (type 1) from MySolution"

The Web API action does **not** take a `ComponentId` the way the SDK message does. Its parameters are
`SolutionComponent` (an entity reference), `ComponentType` and `SolutionUniqueName` — sending
`ComponentId` fails with
`0x80048d19 … The parameter 'ComponentId' … is not a valid parameter for the operation`.

Which GUID belongs inside that reference is documented contradictorily: the SDK property reference
calls it "the primary key for the SolutionComponent entity", while the official ALM sample passes an
`EntityMetadata.MetadataId`. Empirically the component's own `objectid` is what works; the membership
row's `solutioncomponentid` fails with `0x8004f021 Cannot find solution component`. Hence the
`componentId` parameter above.

The membership is checked before and after the call, so the two confusing cases come back as a
sentence rather than a platform error code:

- not a member at all — the action is not even called, and the note names the id mix-up as the likely
  cause;
- covered by a table held with `rootcomponentbehavior 0` — a covered subcomponent cannot be removed
  on its own; change the table's behaviour or remove the table.

---

### rootcomponentbehavior

Which solution carries a change to a form or a column hangs entirely on the owning table's
`rootcomponentbehavior` in that solution — `solution_get` reports it per root component.

| Value | Label | Consequence for subcomponents |
|---|---|---|
| 0 | Include Subcomponents | Forms and columns travel with the table automatically, and get no membership row of their own |
| 1 | Do not include subcomponents | Must be added explicitly |
| 2 | Include As Shell Only | Must be added explicitly |

The same table routinely sits in several solutions with different behaviours, which is why "add the
column to the solution" can be a no-op in one and necessary in the next.
[`entity_solution_map`](analysis.md#entity_solution_map) answers that question for a whole table in
one call.

---

### `solution_uninstall`

Uninstalls a solution by deleting it. For a managed solution this removes its components too, and
**there is no undo**.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `uniqueName` | string | Yes | Solution unique name |
| `dryRun` | bool | No | `true` (default) reports what would block the uninstall and changes nothing; `false` actually uninstalls |

**Example prompt:** "What would block uninstalling the CrossvertiseControls solution?"

The dry run checks every root component with
[`component_dependencies`](analysis.md#component_dependencies) and reports the ones something else
requires:

> Dry run: 1 of 5 checked root component(s) of 'DV_MCP_Test' are required by something else and
> would block the uninstall — Entity invoice (193 dependent(s)). Nothing was changed.

A clean dry run is **evidence, not proof**: only root components are checked (subcomponents ride
along with theirs), and only the first 100 of them. `checkTruncated` says when the cap was hit, and
the summary admits it.

---

### `solution_check_layers`

Checks the solution layers for a component.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `componentId` | GUID string | Yes | Component GUID |
| `componentType` | int | Yes | Component type code |

**Example prompt:** "Show me the solution layers for component abc123"

---

### `solution_remove_active_layer`

Removes the active customization layer for a component.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `componentId` | GUID string | Yes | Component GUID |
| `componentType` | int | Yes | Component type code |

**Example prompt:** "Remove the active layer for component abc123"

---

## Solution Pipelines (read-only)

Power Platform Pipeline **deployment** is not exposed as a tool — a headless MCP cannot mint the
Power-Apps-Maker token the pipeline backend requires, so committing a deploy is a Maker-UI (or PAC
CLI) operation. Use the file-based `solution_export` + `solution_import` path instead. The read-only
pipeline tools below remain available for discovery and monitoring. See the
[`solution-pipelines`](../../skills/solution-pipelines/SKILL.md) skill for the full background.

### `pipeline_list`

Lists all Power Platform Pipelines visible on a Pipeline-Host environment.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `pipelineHostOrgUrl` | string | Yes | Org URL of the Pipeline-Host environment |

### `pipeline_stages`

Lists the stages of a pipeline, including each stage's target deployment environment.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `pipelineHostOrgUrl` | string | Yes | Org URL of the Pipeline-Host environment |
| `pipelineId` | string | Yes | Pipeline GUID |

### `pipeline_environments`

Lists the deployment-environment mappings on a Pipeline-Host (Power-Platform env GUID → pipeline-internal mapping row).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `pipelineHostOrgUrl` | string | Yes | Org URL of the Pipeline-Host environment |

### `pipeline_run_status`

Gets the status of a deployment stage run (status, operation, validation results, error message).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `pipelineHostOrgUrl` | string | Yes | Org URL of the Pipeline-Host environment |
| `stageRunId` | string | Yes | Deployment stage-run GUID |

**Example prompt:** "List the pipelines on the host org and show the status of run abc123"
