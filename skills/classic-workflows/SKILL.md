---
name: classic-workflows
description: Read, explain, edit and create Classic Workflows (Dataverse "Prozesse" of category Workflow) via the dataverse-modelling-mcp server. Covers the declarative definition model, the JSON shape for steps and values, validation codes and their fixes, custom code activities, and the architecture behind the format (server-rendered designer, incremental saves, naming conventions). Use when the user asks about classic workflows, background/real-time workflows, workflow XAML, or wants to understand or change an existing process.
version: 1.0
---

# Classic Workflows on Dataverse

Classic Workflows store their logic as WF4 XAML in `workflow.xaml`. **You never write that XAML by
hand.** You describe the logic as a JSON definition; the server generates conventional XAML from it.

Full format reference: `docs/classic-workflows-reference.md` in this repository.

**Related records and the 87 activities of msdyncrmWorkflowTools: see `workflow-tools.md` next to
this file.** Read it before concluding that something "cannot be done with classic workflows" —
team membership, N:N checks, multi-select fields, FetchXML queries and deletes are all available
through that assembly.

## Why the definition model exists

The classic designer is **server-rendered**: every edit is a SOAP call that rewrites the XAML
server-side and returns HTML for display. The designer rebuilds its view from the XAML alone
(`clientdata` is always `null`), and it does so by relying on **naming conventions** — step ids,
`DisplayName` values and variable names. Hand-written XAML that breaks a convention produces
`0x80045037` and the process can no longer be opened.

`workflow_set_definition` owns all naming, so that class of error cannot occur. Everything else is
caught by validation before anything is written.

## Tool map

| Intent | Tool |
|---|---|
| What does this workflow do? | `workflow_explain` |
| Get the logic as editable JSON | `workflow_get_definition` |
| Check a definition before writing | `workflow_validate_definition` |
| Write logic | `workflow_set_definition` |
| Create a new (empty) workflow | `workflow_create` |
| Triggers, mode, scope, subprocess | `workflow_update` |
| Activate / deactivate | `workflow_set_state` |
| Raw XAML for backup or diffing | `workflow_export_xaml` |
| Undo a change | `workflow_restore_xaml` |
| Available code activities | `workflow_list_activities` |
| Parameters of a code activity | `workflow_get_activity_parameters` |

## Standard workflows

### Explaining an existing workflow

```
workflow_list (filter) → workflow_explain(workflowId)
```

Read the tail of the report. A **"Not understood"** section means the XAML contains constructs the
parser cannot map. The explanation is then incomplete — and you must not rewrite that workflow with
`workflow_set_definition`, because the unmapped parts would be dropped.

### Changing an existing workflow

```
1. workflow_export_xaml            → keep as restore point
2. workflow_get_definition         → check "fullyUnderstood": true
3. modify the JSON
4. workflow_validate_definition    → fix all errors
5. workflow_set_definition         → writes only when clean
6. workflow_set_state activate=true
```

If `fullyUnderstood` is false, stop and tell the user which parts are unsupported. Offer to make the
change in the designer instead.

### Creating a new workflow

```
1. workflow_create(name, primaryEntity)        → draft with empty skeleton
2. workflow_set_definition(id, definition)     → the logic
3. workflow_update(id, {"triggeroncreate": true, "createstage": 40})   → trigger (required!)
4. workflow_set_state(id, activate=true)
```

**Step 3 is not optional.** Dataverse refuses to activate an automatic workflow that has no trigger
(`0x80045018`). Pick one:

| Intent | `workflow_update` payload |
|---|---|
| On create | `{"triggeroncreate": true, "createstage": 40}` |
| On change of specific fields | `{"updatestage": 40, "triggeronupdateattributelist": "field1,field2"}` |
| On delete (e.g. to prevent it) | `{"triggerondelete": true, "deletestage": 20}` |
| Started manually | `{"ondemand": true}` |
| Called by another workflow | `{"subprocess": true}` |

Stage `20` = pre-operation (inside the transaction, can block the operation), `40` = post-operation.
Blocking an operation additionally requires a real-time workflow (`{"mode": 1}`) and a
`stopWorkflow` step with `outcome: "cancelled"`.

## The definition shape

```json
{
  "primaryEntity": "lead",
  "steps": [
    {
      "kind": "condition",
      "description": "Only for German leads",
      "conditions": [
        { "attribute": "address1_country", "operator": "Equal",
          "value": { "kind": "literal", "literal": "Deutschland" } }
      ],
      "then": [
        {
          "kind": "updateRecord",
          "description": "Fill the domain",
          "attributes": [
            { "attribute": "sample_domain",
              "value": { "kind": "field", "fields": ["lead.websiteurl", "lead.emailaddress1"],
                         "fallback": "unbekannt" } }
          ]
        }
      ],
      "else": [
        { "kind": "stopWorkflow", "outcome": "succeeded",
          "reason": { "kind": "literal", "literal": "Not a German lead" } }
      ]
    }
  ]
}
```

### Step kinds

| `kind` | Purpose | Required fields |
|---|---|---|
| `condition` | If/then/otherwise | `conditions`, and `then` and/or `else` |
| `wait` | Wait until a condition holds (background workflows only) | like `condition` |
| `stage` | Grouping. If one step is in a stage, all must be | `children` |
| `createRecord` | Create a record of any table | `entity`, `attributes` |
| `updateRecord` | Update the triggering record | `attributes` |
| `assignRecord` | Change owner | `ownerId`, optional `ownerType` |
| `changeStatus` | Set statecode/statuscode | `state` and/or `status` |
| `customActivity` | Run a code activity | `assemblyQualifiedName`, optional `inputs`/`outputs` |
| `startChildWorkflow` | Start another workflow | `childWorkflowId` |
| `stopWorkflow` | End the workflow | optional `outcome`, `reason` |

`sendEmail` and `performAction` are **read-only**: they are reported by `workflow_explain` but
cannot be generated. Configure them in the designer.

### Value kinds

| `kind` | Meaning | Example |
|---|---|---|
| `literal` | Constant | `{"kind":"literal","literal":"Aktiv","dataType":"String"}` |
| `field` | One or more fields, first non-empty wins, optional fallback | `{"kind":"field","fields":["lead.websiteurl","lead.emailaddress1"],"fallback":"unbekannt"}` |
| `stepOutput` | Output of an earlier code activity | `{"kind":"stepOutput","stepOutput":"CustomActivityStep4.Domain"}` |

`dataType` defaults to `String`. Others: `Integer`, `Boolean`, `DateTime`, `Decimal`, `Double`,
`Money`, `OptionSetValue`, `EntityReference`, `Guid`. Set it whenever the target field is not text —
for a plain field a wrong type produces a runtime failure; on a code activity's input it is caught as
`WF086`.

An `EntityReference` literal is written as `"<entity>:<guid>"`, e.g.
`"team:a0000001-0000-4000-8000-000000000001"`.

Field references are always `entity.attribute` with logical names. Fields of the primary record need
nothing extra; fields of a **directly linked** record need `via` with the lookup attribute leading
there — `{"kind":"field","fields":["opportunity.sample_salesma"],"via":"opportunityid"}`. One level only;
deeper paths need a child workflow or `msdyncrmWorkflowTools.QueryValues` (see `workflow-tools.md`).
The same applies to `conditions[]`, which take `entity` + `via` + `attribute`.

### Code activities

```
workflow_list_activities(nameFilter) → pick the pluginTypeId
workflow_get_activity_parameters(pluginTypeId)
```

Take `assemblyQualifiedName` **verbatim** from the result — it carries the assembly's real
`PublicKeyToken`, and a wrong one makes the workflow fail to load. Use each parameter's
`dependencyPropertyName` as the key in `inputs`/`outputs`, not its display name (`Email`, not
`E-Mail`). Each parameter reports the `dataType` to use, and a lookup reports its allowed
`entityNames`.

You do not have to get the types right by inspection: the server reads the activity's real signature
before writing and reports a wrong name, a wrong `dataType`, a missing required input or a lookup on
the wrong table as `WF085`–`WF089`. Only what it cannot know statically — the target of a lookup
taken from a field — still surfaces at runtime.

Outputs of a step are referencable by later steps as `<stepId>.<ParameterName>` **or by the bare
parameter name**, which is usually what you want: step ids are assigned by the builder, so you cannot
know them when writing the definition. They come back in `stepIds` and are assigned in document
order, so a step can only use outputs of steps declared before it.

Reading back is asymmetric: `workflow_get_definition` cannot reconstruct the *inputs* of a code
activity (it reports them as raw expressions and sets `fullyUnderstood: false`). Such a workflow must
be changed in the designer, not rewritten.

## Validation

`workflow_validate_definition` and `workflow_set_definition` run the same three gates:

1. **Model** — completeness and consistency of the definition.
2. **Metadata** — every table and attribute must exist in the environment, and the arguments of a
   code activity must match its real signature (both live lookups).
3. **Generated XAML** — well-formed, all variables declared, step ids unique.

Nothing is written unless all three pass. Each issue carries `code`, `path`, `problem` and `fix`.

### Issue codes

| Code | Meaning |
|---|---|
| `WF000` | `definitionJson` is not valid JSON |
| `WF002` / `WF003` | primary entity or steps missing |
| `WF010`–`WF012` | step kind missing, unknown, or not generatable |
| `WF013` / `WF014` | stage nested, or empty |
| `WF020` / `WF021` | create without entity; update targeting a different table |
| `WF030`–`WF032` | assign step: owner missing or malformed |
| `WF040` | change-status without state/status |
| `WF050` / `WF051` | child workflow id missing or malformed |
| `WF070`–`WF076` | condition: no comparisons, missing attribute/operator/value, no branches |
| `WF077` / `WF078` | condition on a step output: no parameter named, or no earlier step declares it |
| `WF080`–`WF084` | code activity: AssemblyQualifiedName missing/malformed, `PublicKeyToken=null`, empty parameter name |
| `WF085`–`WF089` | code activity checked against its real signature: unknown input (`WF085`), `dataType` not matching the parameter (`WF086`), required input missing (`WF087`), lookup pointing at a table the parameter does not accept (`WF088`), unknown output (`WF089`) |
| `WF090`–`WF092` | attribute assignments: none, unnamed, duplicated |
| `WF100`–`WF102` | value missing, unknown kind, unknown data type |
| `WF120`–`WF122` | field value: no fields, bad reference format, non-primary entity |
| `WF130`–`WF132` | stepOutput: missing, malformed, or no matching earlier output |
| `WF200`–`WF202` | generated XAML failed self-check (**internal defect — report it**) |
| `WF210` | workflow is activated; deactivate or pass `reactivate=true` |
| `WF300` / `WF301` | table or attribute does not exist (includes name suggestions) |

## Rules and pitfalls

- **Active workflows cannot be changed.** Either deactivate first or pass `reactivate=true`. If
  re-activation fails after writing, the response says so and the workflow stays a draft — never
  assume it is active again.
- **Keep the backup.** `workflow_set_definition` returns the previous XAML in `backup`. On any
  surprise, `workflow_restore_xaml` puts it back.
- **Don't create workflows with `record_upsert`.** A create without valid `xaml` is rejected with
  `0x80045040`. `workflow_create` supplies a valid skeleton, so use it.
- **`0x80045040` on a write means "XAML not accepted"**, not "wrong access path" — even though the
  message talks about being "created outside of the web application". If you ever see it from
  `workflow_set_definition`, that is a builder defect worth reporting; the tool normally catches
  such cases beforehand.
- **Activation does not validate the logic.** A step with missing configuration activates happily
  and fails at runtime. Validation in this server is stricter than Dataverse on purpose.
- **`updateRecord` writes to the triggering record only.** It uses a temporary entity internally and
  writes the result back.
- **`wait` steps require background mode** (`mode=0`). In a real-time workflow they will not work.
- **Environment write policy:** `contoso-dev` is the safe write target. Confirm with the user before
  writing to `xv` (production).

## Choosing between classic workflow and cloud flow

Classic workflows are legacy but still the right tool when: the logic must run **synchronously**
inside the transaction (real-time, pre-operation), it must **prevent an operation** by throwing, or
it calls an existing **custom code activity**. For anything else prefer a cloud flow — see the
`cloud-flows` skill.
