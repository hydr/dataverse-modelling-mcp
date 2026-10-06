---
name: business-process-flows
description: Create, read, change and run Business Process Flows (Dataverse "Geschäftsprozessflüsse", process bar on top of model-driven forms) via the dataverse-modelling-mcp server — without the designer. Covers the JSON definition (stages, data/action/flow steps, branches, cross-table stages, triggered workflows), validation codes and their fixes, activation, ordering, security roles, and moving running instances between stages. Use when the user mentions business process flow, BPF, Geschäftsprozessfluss, process stages, stage gates, the process bar, or wants to guide users through a sales/service process.
version: 1.0
---

# Business Process Flows on Dataverse

A business process flow (BPF) is a `workflow` row with `category = 4`. Its XAML describes stages, not
actions. **You never write that XAML.** You describe the process as a JSON definition; the server
generates the XAML in exactly the shape the designer writes, and the platform derives everything
else from it on save — `clientdata`, `uidata` (what the process bar renders and where branch
conditions are evaluated) and the `processstage` rows.

Format reference: `docs/business-process-flows-reference.md` in this repository.

## Tool map

| Intent | Tool |
|---|---|
| Which processes exist on a table, in which order? | `bpf_list` |
| Get a process as editable JSON | `bpf_get_definition` |
| Check a definition before writing | `bpf_validate_definition` |
| Create a process (optionally activate it) | `bpf_create` |
| Rewrite a process — also while it is active | `bpf_set_definition` |
| Rename / describe | `bpf_update` |
| Activate / deactivate | `bpf_set_state` |
| Delete | `bpf_delete` |
| Which process new records get | `bpf_set_order` |
| Let a security role use the process | `bpf_grant_access` |
| Raw XAML for backup | `bpf_export_xaml` |
| Undo a change | `bpf_restore_xaml` |
| Instances on a record | `bpf_instance_list` |
| Start / switch a record's process | `bpf_instance_start` |
| Move an instance to another stage | `bpf_instance_move` |
| Finish, abandon, reactivate an instance | `bpf_instance_set_status` |

## Standard workflows

### Create a process

```
bpf_validate_definition(definitionFile) → fix issues
→ bpf_create(name, definitionFile, uniqueName | solutionUniqueName, activate=true)
→ bpf_grant_access(processId, roleIds)
→ bpf_set_order(primaryEntity, processIds)   (only if the table has several processes)
```

- **The first activation takes about two minutes**: it creates the table that stores the instances
  (logical name = `uniqueName`, e.g. `sample_leadtoorder`) with a lookup per table of the process
  (`bpf_<table>id`), `activestageid` and `traversedpath`.
- **An active process applies itself to every new record of its table** for users who have access
  to it — lowest process order first. Do not leave test processes activated in a shared environment.
- Until `bpf_grant_access` runs, only System Administrator and System Customizer see the process.

### Change a process

```
bpf_get_definition(processId) → edit the JSON → bpf_set_definition(processId, …, dryRun=true)
→ read diff and issues → bpf_set_definition(processId, …, backupFile=…)
```

- **An activated process can be rewritten in place** (unlike a classic workflow): no deactivation.
  A table that is new in the process gets its lookup column during the write.
- **Keep `stageId` and `stepId`.** Running instances point at the stage id (`activestageid`); a new
  id for the same stage leaves them on a stage that no longer exists (warning BPF060). Stages and
  steps you leave without an id are matched to the existing ones — stages by name and table, data
  steps by attribute, action/flow steps by process, triggers by workflow and event — so a definition
  written from scratch keeps the ids too.
- Only write back when `fullyUnderstood` is true. Task-flow pages, the link controls of the shipped
  system processes, closed loops and action steps with input parameters are not authored here; the
  parser lists them in `unrecognised`.

### Work with running instances

```
bpf_instance_list(entity, recordId)            → instance ids, active stage, status
bpf_instance_move(processId, instanceId, stageId [, recordId])
bpf_instance_set_status(processId, instanceId, "finished" | "aborted" | "active")
bpf_instance_start(processId, recordId [, stageId])   → also switches the record to that process
```

Moves go back to any passed stage, or forward to the stage that follows the active one (its `next`
or a branch target) — one stage at a time, like the form. Branch conditions are **not** evaluated
server-side: moving to a branch target is the caller's decision. A move onto a stage of another table
needs `recordId` — the record of that table the process continues on.

## The definition

```json
{
  "primaryEntity": "lead",
  "languageCode": 1033,
  "workflows": [ { "workflowId": "<guid>", "on": "finished" } ],
  "stages": [
    {
      "name": "Qualify",
      "category": "Qualify",
      "steps": [
        { "attribute": "parentaccountid", "label": "Existing account?" },
        { "attribute": "budgetamount", "required": true },
        { "kind": "action", "processId": "<guid of an on-demand workflow or custom action>", "label": "Score lead" }
      ],
      "workflows": [ { "workflowId": "<guid>", "on": "stageExit" } ],
      "branch": {
        "branches": [ {
          "description": "Large deal",
          "conditions": [ { "attribute": "budgetamount", "operator": "GreaterEqual",
                            "value": { "kind": "literal", "dataType": "Money", "literal": "50000" } } ],
          "next": "Executive review"
        } ],
        "else": "Develop"
      }
    },
    { "name": "Executive review", "steps": [ { "attribute": "description" } ], "next": "Develop" },
    {
      "name": "Develop",
      "entity": "opportunity",
      "relationship": { "name": "opportunity_originating_lead" },
      "steps": [ { "attribute": "customerneed" },
                 { "kind": "flow", "processId": "<guid of an instant flow>", "required": true } ]
    }
  ]
}
```

### Definition

| Field | Meaning |
|---|---|
| `primaryEntity` | Table the process starts on. The first stage is on it. Fixed after creation (BPF008). |
| `stages` | Stages in display order. The path runs top to bottom unless `next` or a branch says otherwise. |
| `languageCode` | Language of all labels. Defaults to the organisation's base language. |
| `workflows` | Process-level ("global") workflows: `on` = `applied`, `reactivated`, `finished`, `abandoned`. |

### Stage

| Field | Meaning |
|---|---|
| `stageId` | GUID. Keep it on existing stages; assigned when absent. |
| `key` | Optional handle for references. A stage can also be referred to by its name or id (precedence: key, id, name). |
| `name` | Label in the process bar. |
| `entity` | Table of the stage; defaults to the table of the stage before. |
| `category` | `Qualify`, `Develop`, `Propose`, `Close`, `Identify`, `Research`, `Resolve`, `Approval` (or the number). Reporting only. |
| `steps` | Steps in display order (max 30). |
| `next` | Stage that follows on the main path (key, name or id). Default: the next one in the list; the last stage ends the path. `"end"` ends it early. |
| `branch` | Condition evaluated when the user leaves the stage — see below. |
| `relationship` | How the process reaches this stage from a stage on another table. Required for the first stage on a new table. |
| `workflows` | Workflows run on `stageEnter` / `stageExit`. |

### Step

| Field | Meaning |
|---|---|
| `kind` | `field` (default, the designer's "data step"), `action` (button running an on-demand workflow or a custom process action), `flow` (button running an instant cloud flow). |
| `stepId` | GUID. Keep it; assigned when absent. |
| `label` | Text shown. Defaults to the attribute's display name / the process name. |
| `attribute` | `field`: column of the stage's table. |
| `required` | `field`: the stage cannot be left empty. `flow`: the flow must have run. Not for `action` (BPF027). |
| `processId` | `action`: workflow (category 0, on-demand) or custom action (category 3) on the stage's table. `flow`: the flow's workflow id (category 5). |
| `classId` | `field`: form control class; derived from the attribute type. Only read back to keep an existing step unchanged. |
| `parameters` | `field`: raw control parameters (duplicate detection of system lookups). Read back and written unchanged; not to be authored. |
| `systemControl` | `field`: marks a system control. Read back and written unchanged. |

### Branch

`branch.branches` is an if / else-if chain; `branch.else` the stage taken when no case holds.

| Field | Meaning |
|---|---|
| `branches` | Cases, tested in order. |
| `else` | Stage when no case holds. Optional for the platform — but the designer refuses to save a condition without it (BPF026). |
| `conditions` | Comparisons, in the shape of the classic-workflow definitions: `attribute`, `operator`, `value` (`literal` or `field`), bracketed groups via `conditions` + `groupOperator`. |
| `logicalOperator` | `And` (default) or `Or`. |
| `next` | Stage taken when the case holds. |
| `description` | Name of the case in the designer. |

Rules the designer enforces and the validator checks:

- **A condition can only compare the stage's own data steps** (BPF024) — the designer offers no
  other fields and rejects them on save. Add a data step for the field, or move the condition.
- Operators: `Equal`, `NotEqual`, `GreaterThan`, `GreaterEqual`, `LessThan`, `LessEqual`,
  `Contains`, `DoesNotContain`, `BeginsWith`, `DoesNotBeginWith`, `EndsWith`, `DoesNotEndWith`,
  `Null`, `NotNull` (BPF019). No relative dates.
- Conditions are evaluated in the browser, from `uidata`, when the user clicks "Next stage".

### Relationship

| Field | Meaning |
|---|---|
| `name` | Schema name of a 1:N relationship from the previous table to this stage's table, e.g. `opportunity_originating_lead`. |
| `attribute` | The lookup on this stage's table (e.g. `originatingleadid`). Filled in from metadata when omitted. |
| `fromStage` | The stage the process comes from. Default: the one stage leading here, otherwise the stage listed before. |

### Triggered workflow

| Field | Meaning |
|---|---|
| `workflowId` | An activated, on-demand classic workflow on the stage's table (stage level) or the primary table (process level). |
| `on` | Stage level: `stageEnter`, `stageExit`. Process level: `applied`, `reactivated`, `finished`, `abandoned`. |
| `triggerId` | GUID of the trigger inside the process. Keep it; assigned when absent. |

## Validation codes

`BPF0xx` are model checks, `BPF3xx` checks against live metadata. Every finding names a path and a fix.

| Code | Meaning | Fix |
|---|---|---|
| BPF000 | Definition missing or not valid JSON | Pass the definition; check the JSON |
| BPF001 | No `primaryEntity` | Set it |
| BPF002 | No stages | Add at least one |
| BPF003 | Stage without name | Set `name` |
| BPF004 | First stage not on the primary table | Remove its `entity` or change `primaryEntity` |
| BPF005 | Key or id used twice | Make them unique |
| BPF006 | Id is not a GUID, or a step id equals a stage id | Remove it to have one assigned |
| BPF007 | Unknown stage category | Use a listed category or leave it out |
| BPF008 | `primaryEntity` differs from the existing process | Keep it; create a new process instead |
| BPF009 | `uniqueName` invalid or taken | `<prefix>_<name>`, lowercase, unused |
| BPF010 | `next` names no stage | Use key, name, id or `"end"` |
| BPF011 | Stage leads to itself | Point `next` elsewhere |
| BPF012 | Branch target names no stage | Fix the reference |
| BPF013 | Branching without cases, or a case without comparisons | Add them, or remove `branch` |
| BPF014 | Case without `next` | Set it |
| BPF015 | Case leads back to its own stage | Point it elsewhere |
| BPF016 | Unknown logical operator | `And` / `Or` |
| BPF017 | Comparison without attribute | Set `attribute` |
| BPF018 | Comparison reads another table (`entity`, `via`, …) | Compare the stage's own columns |
| BPF019 | Operator not available, value missing/ignored, or value kind other than literal/field | See the operator list |
| BPF020 | Unknown step kind | `field`, `action`, `flow` |
| BPF021 | Data step without attribute | Set `attribute` |
| BPF023 | Action/flow step or trigger without a valid process id | Set `processId` / `workflowId` |
| BPF024 | Condition on a field that is no data step of the stage | Add the data step, or compare another field |
| BPF025 | Trigger event not allowed at this level | Stage: `stageEnter`/`stageExit`; process: `applied`/`reactivated`/`finished`/`abandoned` |
| BPF026 | Condition without `else` (warning) | Add an `else` stage if the designer must still open it |
| BPF027 | Flag ignored on this step kind (warning) | Remove it |
| BPF030 | More than 30 stages on one table | Split the process |
| BPF031 | More than 30 steps in a stage | Move steps to another stage |
| BPF032 | More than 5 tables | Split the process |
| BPF040 | Stage changes table without `relationship` | Add the 1:N relationship |
| BPF041 | Relationship without name | Set `name` |
| BPF042 | `fromStage` names no stage | Fix or remove it |
| BPF043 | Relationship on a stage entered from the same table (warning) | Remove it or set `fromStage` |
| BPF050 | Stage unreachable (warning) | Point `next` or a branch at it, or remove it |
| BPF051 | The main path loops | End the path; use `"end"` |
| BPF060 | A removed stage still has active instances (warning) | Move the instances first, or keep the stage |
| BPF061 | Current process has parts this server does not understand (warning) | They are dropped on write — check `unrecognised` |
| BPF300 | Table does not exist | Check the logical name |
| BPF301 | Column does not exist | See the suggestions / `describe_table` |
| BPF302 | Column type cannot be shown in a data step | Pick another column |
| BPF303 | Relationship does not exist or is not 1:N | Use a 1:N relationship |
| BPF304 | Relationship does not connect the two tables, or `attribute` mismatches | Use the relationship from the previous table to this one |
| BPF305 | Table not enabled for business process flows | `table_update` with `IsBusinessProcessEnabled = true` — irreversible |
| BPF307 | Referenced workflow/action/flow does not exist | Check the id |
| BPF308 | Referenced process of the wrong kind (or not on-demand) | See the step kind's requirement |
| BPF309 | Referenced process not activated | Activate it |
| BPF310 | Referenced workflow/action runs on another table | Use one on the stage's (or primary) table |

## Platform facts worth knowing

- **Create needs `scope = 4`.** Without it the platform's own validation throws a
  NullReferenceException, surfaced as `0x80040216 An unexpected error occurred`. `bpf_create` sets it.
- **Errors come in two layers**: the Web API returns the generic `0x80040216`; the SOAP endpoint
  carries the server stack trace (`BpfEntityAttributeValidationStep`, `UIDataGenerator.ReadControlStep`)
  — useful when something new fails.
- **Delete**: an activated process cannot be deleted (`0x8004500f`); deactivate first. Deleting takes
  two to three minutes; the instance table disappears asynchronously afterwards.
- **Export in a solution**: a process exports only together with its instance table — so a process
  that was never activated cannot be exported (`0x80060376`). Activate it once, then add both
  (`solution_add_component`: the workflow as type 29, the table as type 1).
- **Actions** are stored by their SDK message name (`sample_NotifyOwner`), not by the workflow's
  unique name; the server resolves it.
- The designer saves through an internal endpoint (`ProcessControl.asmx`), not the Web API. Writing
  the `xaml` column through the Web API produces the same `uidata` as a designer save — verified by
  comparing both.
