---
name: cloud-flows
description: Work with Power Automate solution-aware Cloud Flows on Dataverse — list/get/create/update flows, manage version history, publish drafts, restore previous versions, and activate/deactivate at runtime. Use when the user asks to inspect, modify, publish, save-draft, restore, activate, or deactivate Power Automate cloud flows in a Dataverse environment via the dataverse-mcp server. Captures Microsoft-undocumented Maker-UI endpoints reverse-engineered from network traces.
version: 1.0
---

# Cloud Flows on Dataverse

Expert reference for managing **solution-aware** Power Automate cloud flows via the dataverse-mcp server.

## Hard prerequisites

- **Only solution-aware flows have version history.** Flows in the default solution get no `componentversion` rows at all. If a flow is missing from version-history queries, check that it belongs to a non-default solution (`solutioncomponents` filtered on `objectid eq <flowid>`).
- **Environment write policy:** `contoso-dev` is the safe write target. `xv` (production) and `contoso-staging` need explicit user confirmation before any mutating call.
- **The `componentversion` virtual entity refuses direct Create.** The Microsoft Learn doc lists Create as supported, but every POST/PATCH against `/componentversions` or the elastic backing table `/componentversionnrddatasourceset` fails with either "SDK Message 'Create' is not implemented" or "Primary Key cannot be an empty Guid". The bound actions documented below are the only working write path.

## Tool map

| User intent | MCP tool | Underlying call |
|---|---|---|
| List flows | `flow_list` | Power Automate Flow API |
| Inspect single flow | `flow_get`, `flow_describe` | Power Automate Flow API |
| Create / update definition | `flow_create`, `flow_update` | Power Automate Flow API |
| List version history | `flow_list_versions` | Dataverse `GET /workflows({id})/componentversions` |
| Inspect single version | `flow_get_version` | Filtered list (direct GET-by-key is blocked) |
| **Save a draft (Op=1 Update version)** | `flow_save_draft` | `PATCH /workflows({id})` + header `mscrm.AsUnpublished: true` |
| **Publish current draft (Op=2 Publish version)** | `flow_publish` | `POST /PublishComponent?ActivateFlowOnPublish=<bool>` |
| Activate/deactivate at runtime (no snapshot) | `flow_set_state` | Power Automate `/start` or `/stop` |
| **Restore a prior published version (Op=3 Restore)** | `flow_restore_version` | `POST /workflows({id})/Microsoft.Dynamics.CRM.RestoreComponentVersion` |
| Get run history | `flow_get_runs` | Power Automate Flow API |

## Conceptual model

The `workflow` row carries the **live** flow. Alongside it, the virtual `componentversion` view exposes snapshots of every meaningful change. Each snapshot is one row with an `Operation` discriminator:

| Operation | Label | Triggered by |
|---|---|---|
| 0 | Create | Initial flow creation |
| 1 | Update | `flow_save_draft` (PATCH with `mscrm.AsUnpublished: true`) |
| 2 | Publish | `flow_publish` (POST `PublishComponent`) |
| 3 | Restore | `flow_restore_version` |
| 4 | Solution Import | Solution-import pipeline |

**Restore semantics:** `RestoreComponentVersion` only acts on **Publish-typed (Op=2)** versions. Calls against Create/Update IDs return HTTP 204 but produce no observable change.

**Publish vs. Activate:** these are independent. `flow_publish` snapshots the draft as the new published version but leaves the runtime state unchanged. `ActivateFlowOnPublish=true` combines both. `flow_set_state` toggles runtime alone without touching version history.

## Reverse-engineered endpoints (Maker UI parity)

These came from Playwright network traces of `make.powerautomate.com` and are now wrapped by the MCP tools. Documented here for the rare case the wrapper needs extending — most work belongs in the service layer, not in tool callers.

### Restore (bound action on workflow)
```http
POST /api/data/v9.2/workflows({flowId})/Microsoft.Dynamics.CRM.RestoreComponentVersion
Content-Type: application/json

{"RestoringVersionId": "componentversions(<componentversionId>)"}
```
- Parameter name is **PascalCase**. A C# anonymous object `new { RestoringVersionId = ... }` gets serialized as `restoringVersionId` by the shared `JsonSerializerOptions` (CamelCase policy) and rejected. Use a `Dictionary<string, object?>` to keep the key verbatim.
- Returns 204 even when the snapshot wasn't actually applied — verify by re-listing versions and checking for an Op=3 row pointing at the source.

### Publish (unbound action)
```http
POST /api/data/v9.2/PublishComponent?ActivateFlowOnPublish=false
Content-Type: application/json

{"Target": "/workflows({flowId})"}
```
- `ActivateFlowOnPublish=true` flips `statecode` to Activated in the same call.

### Save draft (direct PATCH with special header)
```http
PATCH /api/data/v9.2/workflows({flowId})
Content-Type: application/json
mscrm.AsUnpublished: true
If-Match: *

{"clientdata": "<stringified-logic-apps-json>", "name": "..."}
```
- Without `mscrm.AsUnpublished`, the PATCH may try to validate/publish immediately and fail.
- A second header `mscrm.SkipComponentVersioning: true` is what the Maker UI uses in the publish-batch's first PATCH to avoid double-versioning; do **not** add it to plain Save-draft calls — it would suppress the Update version row entirely.

### List versions (used by the panel)
```http
GET /api/data/v9.2/workflows({flowId})/componentversions
    ?$orderby=createdon desc
    &$expand=createdby($select=fullname)
    &$filter=_component_value+eq+{flowId}
    &$select=createdon,componentversionid,operation,createdby
```
- Direct query against `/componentversions` without the workflow nav-property prefix fails with "RetrieveMultiple on componentversions must be done through navigation property or related entity query."

### Retrieve a version's payload (snapshot)
```http
GET /api/data/v9.2/workflows({flowId})/Microsoft.Dynamics.CRM.RetrieveComponentVersionPayload(ComponentVersionId=<id>)
```
- Not currently wrapped — the snapshot content sits in the elastic table's `payload` File attribute as a ZIP archive containing `Workflows/<name>.json`. The bound action above is the supported retrieval path used by the Maker UI's "View version" feature.

## Pitfalls

- **`clientdata` is a stringified JSON**, not nested JSON. When constructing it manually, the inner `properties.definition` block must use Logic Apps keys `$schema`, `$connections`, `$authentication` — verbatim with dollar signs. Don't try to coerce them via `string.Replace`; build the JSON literal directly (raw string interpolation in C# works well).
- **Manual trigger schema:** the Request trigger's `inputs.schema` must be a JSON-schema object (`{"type":"object","properties":{},"required":[]}`), not an empty `{}` — empty `{}` is treated as an invalid `ManualActionInput` member.
- **Direct OData PATCH on `clientdata` bypasses versioning.** It modifies the live flow without creating a `componentversion` row. To get a proper Update snapshot, go through `flow_save_draft`.
- **Composite key lookups are blocked:** `GET /componentversions({id})` and `GET /workflows({wfid})/componentversions({verId})` both fail with "Get with NavigationKey is allowed only on Metadata Entities." Resolve a single version by listing and filtering client-side.
- **`RestoreComponentVersion` is async.** After calling it, give Dataverse 1–2 seconds before re-listing versions to assert the Op=3 row exists.

## Typical workflows

### "Roll a flow back to yesterday's published version"
1. `flow_list_versions(flowId)` → identify the target row where `Operation == 2` and `CreatedOn` matches.
2. `flow_restore_version(flowId, versionId)` — produces a new Op=3 row and a fresh Draft on the workflow.
3. (Optional) `flow_publish(flowId, activateFlow=true)` to re-publish-and-activate immediately.

### "Apply a small edit safely without breaking the active flow"
1. `flow_get(flowId)` → extract current `clientdata`.
2. Edit the definition locally.
3. `flow_save_draft(flowId, newClientData)` — creates Op=1 Update version, runtime unaffected.
4. Inspect & test, then `flow_publish(flowId)` when ready.

### "Disable a noisy flow temporarily"
- `flow_set_state(flowId, enable=false)` — stops runs without changing version history. Re-enable later with `enable=true`.

### "Audit who changed what"
- `flow_list_versions(flowId)` returns each row with `CreatedByName`, `Operation`, `CreatedOn`. Map operation 1/2/3 to Update/Publish/Restore.

## Related memories

- See [[feedback_environment_write_policy]] for the contoso-dev vs xv/contoso-staging write rules.
