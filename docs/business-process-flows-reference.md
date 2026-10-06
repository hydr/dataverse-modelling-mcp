# Business Process Flows — Format Reference

How a business process flow (BPF) is stored, as reverse-engineered against Dataverse 9.2 and the
current process designer (`/Tools/ProcessControl/UnifiedProcessDesigner.aspx`). All ids below are
synthetic.

## Where a process lives

| Row / column | What it holds | Who writes it |
|---|---|---|
| `workflow` (category 4, type 1) | The process | Caller |
| `workflow.xaml` | Stages, steps, branches, relationships, triggered workflows | **Caller** — the only part that has to be authored |
| `workflow.clientdata` | The designer's JSON object model (`WorkflowStep:#Microsoft.Crm.Workflow.ObjectModel`, …) | Platform, from the XAML |
| `workflow.uidata` | What the process bar renders, including a JavaScript function per branching stage and per action step | Platform, from the XAML |
| `processstage` | One row per stage (`processstageid` = stage id, `stagename`, `stagecategory`, `primaryentitytypecode`) | Platform, from the XAML |
| Instance table `<uniquename>` | One row per running instance: `bpf_<table>id` per table, `activestageid`, `traversedpath`, `processid`, `statecode`/`statuscode` | Platform, on first activation |

Writing `xaml` alone through the Web API is enough: `clientdata` is regenerated (the server even fills
in `workflowEntityId`), and the resulting `uidata` is identical to that of a designer save of the same
process apart from the step numbers in its identifiers.

### Columns a create needs

```json
{
  "workflowid": "<chosen client-side, so x:Class can carry it>",
  "name": "…", "uniquename": "sample_leadtoorder", "category": 4, "businessprocesstype": 0,
  "type": 1, "primaryentity": "lead", "scope": 4, "xaml": "…"
}
```

`scope` is the one that is easy to miss: without it the create fails inside
`BpfEntityAttributeValidationStep.ValidateEntityAttributes` with a NullReferenceException, which the
Web API reports as `0x80040216 An unexpected error occurred.` `mode`, `runas`, `iscrmuiworkflow`,
`triggeroncreate`, `processtriggerscope` and `istransacted` are not needed.

### Diagnosing a failed write

The Web API flattens platform errors to `0x80040216`. The SOAP endpoint
(`/XRMServices/2011/Organization.svc/web`, bearer token, `Create`) returns the same failure with the
server stack trace in `ErrorDetails/ApiOriginalExceptionKey`, which names the validation step or the
`UIDataGenerator` method that failed. Every rule in this document was found that way.

## The XAML

```xml
<Activity x:Class="XrmWorkflow<processid without dashes>" …namespaces…>
  <x:Members> InputEntities, CreatedEntities </x:Members>
  …
  <mxswa:Workflow>
    <!-- 1. every cross-table transition -->
    StageRelationshipCollectionComposite
    <!-- 2. one EntityComposite per stage, in display order -->
    EntityComposite "EntityStep3: lead"
      StageComposite "StageStep4: Qualify"
        StepComposite …            (data, action and flow steps)
        ActionComposite …          (workflows triggered on stage enter/exit; process-level ones in the first stage)
        ConditionSequence …        (the stage's branching, if any)
    EntityComposite "EntityStep9: opportunity"
      …
  </mxswa:Workflow>
</Activity>
```

Namespaces used: `mcwb` (`Microsoft.Crm.Workflow.BusinessProcessFlowActivities`), `mcwc`
(`Microsoft.Crm.Workflow.ClientActivities`), `mcwo` (`Microsoft.Crm.Workflow.ObjectModel`, assembly
`Microsoft.Crm`), `mxswa`, `mxs`, `mxsq`, `s`, `scg`, `sco`, `srs`, `x`, `this`, `mva`. The designer
prefixes an `encoding="utf-16"` XML declaration; it is optional.

DisplayNames are numbered across the whole document, in document order: `RelationshipCollectionStep1`,
`RelationshipStep2`, `EntityStep3: lead`, `StageStep4: Qualify`, `StepStep5: …`, `ControlStep6`, ….
The numbers carry no meaning for the platform — `StageLogicalName` in `uidata` is derived from them
(`Step_4`).

### Stage

```xml
<mxswa:ActivityReference AssemblyQualifiedName="Microsoft.Crm.Workflow.Activities.EntityComposite, …" DisplayName="EntityStep3: lead">
  <mxswa:ActivityReference.Properties>
    <sco:Collection x:TypeArguments="Variable" x:Key="Variables" />
    <sco:Collection x:TypeArguments="Activity" x:Key="Activities">
      <mxswa:ActivityReference AssemblyQualifiedName="Microsoft.Crm.Workflow.Activities.StageComposite, …" DisplayName="StageStep4: Qualify">
        <mxswa:ActivityReference.Properties>
          <sco:Collection x:TypeArguments="Variable" x:Key="Variables" />
          <sco:Collection x:TypeArguments="Activity" x:Key="Activities"> …steps… </sco:Collection>
          <sco:Collection x:TypeArguments="mcwo:StepLabel" x:Key="StepLabels">
            <mcwo:StepLabel Description="Qualify" LabelId="11111111-1111-1111-1111-111111110301" LanguageCode="1033" />
          </sco:Collection>
          <x:String x:Key="StageId">11111111-1111-1111-1111-111111110301</x:String>
          <x:String x:Key="StageCategory">0</x:String>            <!-- -1 = none -->
          <x:String x:Key="NextStageId">11111111-1111-1111-1111-111111110302</x:String>   <!-- x:Null on the last stage -->
        </mxswa:ActivityReference.Properties>
      </mxswa:ActivityReference>
    </sco:Collection>
    <x:Null x:Key="RelationshipName" />
    <x:Null x:Key="AttributeName" />
    <x:Boolean x:Key="IsClosedLoop">False</x:Boolean>
  </mxswa:ActivityReference.Properties>
</mxswa:ActivityReference>
```

- The stage id doubles as the label id; step ids likewise. The stage id becomes `processstageid`.
- `NextStageId` is the main path. A stage with a branch still carries one: the designer sets it to
  the stage the branches merge into.
- `StageCategory`: 0 Qualify, 1 Develop, 2 Propose, 3 Close, 4 Identify, 5 Research, 6 Resolve,
  7 Approval, −1 none (`processstage.stagecategory`).

**Older shape** (the processes shipped with Dynamics): one `EntityComposite` per table holding all its
stages, no `NextStageId` (the path is the document order), and the relationship to the next table on
the composite (`RelationshipName`, `AttributeName`). Readers must handle both; writers use the current
one.

### Data step

```xml
<mxswa:ActivityReference AssemblyQualifiedName="Microsoft.Crm.Workflow.Activities.StepComposite, …" DisplayName="StepStep5: Budget">
  <mxswa:ActivityReference.Properties>
    <sco:Collection x:TypeArguments="Variable" x:Key="Variables" />
    <sco:Collection x:TypeArguments="Activity" x:Key="Activities">
      <Sequence DisplayName="ControlStep6">
        <mcwb:Control ClassId="533B9E00-756B-4312-95A0-DC888637AC78" ControlDisplayName="Budget Amount"
                      ControlId="budgetamount" DataFieldName="budgetamount"
                      IsSystemControl="False" IsUnbound="False" SystemStepType="0">
          <mcwb:Control.Parameters>
            <InArgument x:TypeArguments="x:String"><Literal x:TypeArguments="x:String" Value="" /></InArgument>
          </mcwb:Control.Parameters>
        </mcwb:Control>
      </Sequence>
    </sco:Collection>
    <sco:Collection x:TypeArguments="mcwo:StepLabel" x:Key="StepLabels">
      <mcwo:StepLabel Description="Budget" LabelId="11111111-1111-1111-1111-111111110311" LanguageCode="1033" />
    </sco:Collection>
    <x:String x:Key="ProcessStepId">11111111-1111-1111-1111-111111110311</x:String>
    <x:Boolean x:Key="IsProcessRequired">True</x:Boolean>
  </mxswa:ActivityReference.Properties>
</mxswa:ActivityReference>
```

- **`Control.Parameters` is mandatory**, even empty. Without it `UIDataGenerator.ReadControlStep`
  dereferences null and the write fails with `0x80045037 Error generating UiData`. System processes
  carry the parameters as an attribute instead (`Parameters="<parameters><IsDeDupLookup>…"`).
- `ClassId` is the form control class by attribute type (text `4273EDBD…`, memo `E0DECE4B…`, lookup
  `270BD3DB…`, choice `3EF39988…`, two options `67FAC785…`, currency `533B9E00…`, date `5B773807…`,
  whole number `C6D124CA…`). The process bar renders by attribute type regardless.
- `ControlDisplayName` is the attribute's display name; the label is `StepLabel`.

### Action step (button running a workflow or an action)

```xml
StepComposite "StepStep7: <process name>"
  ActionComposite "ActionStep8: Step_8"
    <sco:Collection x:TypeArguments="Activity" x:Key="Activities" />        <!-- input parameters would go here -->
    <x:String x:Key="ActionId">11111111-…-0312</x:String>                   <!-- = ProcessStepId -->
    <x:Int32 x:Key="ActionType">3</x:Int32>                                 <!-- 3 workflow, 0 custom action -->
    <s:Guid x:Key="ProcessId">11111111-…-0101</s:Guid>
    <x:String x:Key="UniqueName"></x:String>                                <!-- action: its SDK message name -->
    <x:Null x:Key="TriggerEvents" />
    <Sequence x:Key="ActionControl" DisplayName="ControlStep9">
      <mcwb:Control ClassId="00ad73da-bd4d-49c6-88a8-2f4f4cad4a20" ControlDisplayName="<label>"
                    ControlId="<UniqueName>_Step_9" IsSystemControl="False" IsUnbound="True" SystemStepType="0">
        <mcwb:Control.DataFieldName> empty string </mcwb:Control.DataFieldName>
        <mcwb:Control.Parameters> empty string </mcwb:Control.Parameters>
      </mcwb:Control>
    </Sequence>
  IsProcessRequired = False                                                  <!-- the designer offers no "required" -->
```

The control id is the unique name followed by `_Step_<control number>` — `_Step_9` for a workflow,
`sample_NotifyOwner_Step_9` for an action. An action's `UniqueName` is the name of its `sdkmessage`
(`workflow._sdkmessageid_value`), not `workflow.uniquename`, which lacks the publisher prefix.

### Flow step

```xml
StepComposite "StepStep20: <flow name>"
  FlowComposite "FlowStep21: Step_21"
    <s:Guid x:Key="WorkflowId">11111111-…-0103</s:Guid>                     <!-- the flow's workflow row, category 5 -->
    <s:Guid x:Key="ActionId">11111111-…-0313</s:Guid>                       <!-- = ProcessStepId -->
    <x:String x:Key="UniqueName">FlowStep_<WorkflowId N>_<ActionId N></x:String>
    <Sequence x:Key="FlowControl" DisplayName="ControlStep22">
      <mcwb:Control ClassId="00ad73da-…" ControlId="Step_22" IsUnbound="True" …/>
    </Sequence>
  IsProcessRequired = True | False
```

### Triggered workflow (stage or process event)

An `ActionComposite` directly in a stage, without a control:

```xml
ActionComposite "ActionStep10: Step_10"
  <x:String x:Key="ActionId">11111111-…-0401</x:String>
  <x:Int32 x:Key="ActionType">3</x:Int32>
  <s:Guid x:Key="ProcessId">11111111-…-0104</s:Guid>
  <x:String x:Key="UniqueName"><the workflow's display name></x:String>
  <x:Array x:Key="TriggerEvents" Type="mcwo:ProcessTriggerData">
    <mcwo:ProcessTriggerData Event="STAGEEXIT" FilterId="<stage id>" PipelineStageId="20" />
  </x:Array>
  <x:Null x:Key="ActionControl" />
```

| Designer trigger | `Event` | `FilterId` | `PipelineStageId` |
|---|---|---|---|
| Stage entry | `STAGEENTER` | stage id | 40 |
| Stage exit | `STAGEEXIT` | stage id | 20 |
| Process applied | `PROCESSAPPLIED` | process id, upper case | 40 |
| Process reactivated | `PROCESSSTATUSCHANGE` | 1 | 40 |
| Process finished | `PROCESSSTATUSCHANGE` | 2 | 40 |
| Process abandoned | `PROCESSSTATUSCHANGE` | 3 | 40 |

Process-level ("global") workflows are stored in the **first** stage. The filter ids 1–3 are the
instance's `statuscode` values.

### Branching

The same encoding as a classic-workflow condition (`ConditionSequence` → `GetEntityProperty` →
`EvaluateExpression CreateCrmType` → `EvaluateCondition` [→ `EvaluateLogicalCondition`] →
`ConditionBranch`), placed last in the stage. Each case's `Then` holds exactly one

```xml
<Sequence DisplayName="SetNextStageStep71">
  <mcwc:SetNextStage ParentStageId="<this stage>" StageId="<target stage>" />
</Sequence>
```

The default case is a `ConditionBranch` with `Condition="True"`; `ContainsElseBranch` is `True` then.
A case's `Description` (`x:String`) is its name in the designer. Comparisons read
`InputEntities("primaryEntity")` with `EntityName` = the stage's table. The designer only offers the
stage's data-step fields, and refuses to save a condition without a "no" branch — the platform accepts
both. The condition runs client-side: `uidata` holds a generated `function bpf_<stage>(eventContext)`
that calls `Xrm.Page.ui.process.reflow(…)`.

A lookup literal keeps its display label (`"opportunity", "Big deal", <id var>, "Lookup"`); the label
is shown in the designer and in `uidata`.

### Cross-table transition

```xml
<mxswa:ActivityReference AssemblyQualifiedName="Microsoft.Crm.Workflow.BusinessProcessFlowActivities.StageRelationshipCollectionComposite, …" DisplayName="RelationshipCollectionStep1">
  …<sco:Collection x:TypeArguments="Activity" x:Key="Activities">
    <Sequence DisplayName="RelationshipStep2">
      <mcwb:StageRelationship AttributeName="originatingleadid" RelationshipName="opportunity_originating_lead"
                              SourceStageId="<last lead stage>" TargetStageId="<first opportunity stage>" />
    </Sequence>
  </sco:Collection>…
```

The relationship is a 1:N from the source table to the target table; `AttributeName` is the lookup on
the target table. The collection is written even when empty.

## Lifecycle

| Action | Behaviour |
|---|---|
| Create | Draft; `processstage` rows and `uidata` exist immediately. No instance table yet. |
| First activation | Synchronous, about two minutes: creates the instance table (`<uniquename>`, org-owned) with its form, the `bpf_<table>id` lookups, `activestageid`, `traversedpath`, `bpf_duration`. |
| Write while active | Allowed. 10–60 s. A new table in the process gets its `bpf_<table>id` column. |
| Active process | Applied to every new record of its table for users with access, lowest `processorder` first. |
| Deactivate | Quick. The instance table stays. |
| Delete | Refused while active (`0x8004500f Cannot delete an active workflow definition`). Takes 2–3 minutes; the instance table is removed asynchronously afterwards. |
| Solution export | Only together with the instance table (`0x80060376` otherwise) — so only after one activation. |

## Instances

Rows of the instance table (Web API, entity set of `<uniquename>`):

- Start: `POST` with `bpf_<table>id@odata.bind` and `activestageid@odata.bind`; `traversedpath` when
  starting beyond the first stage.
- Move: `PATCH` `activestageid@odata.bind` and `traversedpath` (comma-separated stage ids ending with
  the active one). A move onto another table's stage also needs `bpf_<table>id@odata.bind`; without it
  the platform answers `0x80040216 Participating entity record of stage: <id> is not valid`.
- Status: `statecode`/`statuscode` 0/1 active, 1/2 finished, 1/3 aborted.
- `RetrieveProcessInstances(EntityLogicalName, EntityId)` returns all instances on a record across
  processes as `businessprocessflowinstance` rows (`processstageid` = active stage), newest first.
