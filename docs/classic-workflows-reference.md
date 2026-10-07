# Classic Workflows in Microsoft Dataverse: Architecture and Format Reference

> [!IMPORTANT]
> **This is not an official Microsoft document.** This reference was produced by systematic
> reverse engineering of the classic process designer on a Dataverse environment
> (network capture, XAML diff analysis, Web API queries). It describes **undocumented
> internal interfaces** that Microsoft can change without notice. The SOAP endpoints described
> here are not released for third parties. Use at your own risk.
>
> Basis of the investigation: Dataverse 9.2 (`Microsoft.Crm.Workflow` 9.0.0.0), investigated on 2026-07-29.

## In this article

- [Overview](#overview)
- [Data model](#data-model)
- [Architecture of the process designer](#architecture-of-the-process-designer)
- [The workflow web service](#the-workflow-web-service)
- [The conditionXml condition format](#the-conditionxml-condition-format)
- [Structure of the workflow XAML](#structure-of-the-workflow-xaml)
- [Naming conventions](#naming-conventions)
- [Activity reference](#activity-reference)
- [Value expressions](#value-expressions)
- [Custom workflow activities](#custom-workflow-activities)
- [Activation and compilation](#activation-and-compilation)
- [Programmatic access](#programmatic-access)
- [Error reference](#error-reference)
- [Limitations and notes](#limitations-and-notes)
- [Appendix: Investigation methodology](#appendix-investigation-methodology)

## Overview

Classic workflows (in the user interface, "Processes" of the category *Workflow*) are the
predecessors of modern cloud flows. They are stored in the `workflow` table and carry
their execution logic as **Windows Workflow Foundation 4 (WF4) XAML** in the `xaml` column.

Three statements are central to understanding their programmability:

1. The classic process designer is **rendered server-side**. It does not generate the XAML in the
   browser; instead, for every editing action it calls an internal SOAP service that updates the XAML
   in the database and returns an HTML fragment for display.
2. The designer **saves incrementally**. There is no save operation that transmits a model built
   up in the client. Every single editing step is already persisted.
3. The XAML is therefore **the output of a generator**, not an input. Its semantics depend to a
   considerable extent on **naming conventions** (step IDs, `DisplayName`, variable names).
   Anyone who generates XAML themselves must follow these conventions, otherwise the designer can
   no longer display the workflow.

## Data model

### Table `workflow`

| Column | Type | Description |
|---|---|---|
| `workflowid` | Uniqueidentifier | Primary key. Generated server-side. |
| `name` | String | Display name of the process. |
| `category` | Picklist | `0` = Workflow, `1` = Dialog, `2` = Business rule, `3` = Action, `4` = Business process flow, `5` = Modern flow. |
| `type` | Picklist | `1` = Definition, `2` = internal activation copy, `3` = Template. Queries should filter on `type eq 1`. |
| `primaryentity` | String | Logical name of the primary entity. |
| `xaml` | Memo | WF4 XAML of the execution logic. See [Structure of the workflow XAML](#structure-of-the-workflow-xaml). |
| `clientdata` | Memo | **Always `null` for classic workflows.** See the note below. |
| `statecode` / `statuscode` | State/Status | `0`/`1` = Draft, `1`/`2` = Activated. |
| `mode` | Picklist | `0` = Background (asynchronous), `1` = Real-time (synchronous). |
| `scope` | Picklist | `1` = User, `2` = Business unit, `3` = Parent and child business units, `4` = Organization. |
| `runas` | Picklist | `0` = Owner, `1` = Calling user. |
| `ondemand` | Boolean | Available as an on-demand process. |
| `subprocess` | Boolean | Callable as a child process. |
| `triggeroncreate` / `triggerondelete` | Boolean | Trigger on create/delete. |
| `createstage` / `updatestage` / `deletestage` | Integer | `20` = Before the operation, `40` = After the operation. `0`/`null` = no trigger. |
| `triggeronupdateattributelist` | String | Comma-separated attribute list that restricts the update trigger. |
| `istransacted`, `asyncautodelete`, `syncworkflowlogonfailure`, `rank` | – | Execution behavior. |

> [!NOTE]
> `clientdata` is not used by classic workflows — checked on several workflows
> of different ages, including ones freshly created in the designer. The designer's
> display information lives entirely in the XAML (see
> [Naming conventions](#naming-conventions)). The column is used by *modern* flows.

### Related tables

| Table | Usage |
|---|---|
| `plugintype` | Registered types, including custom workflow activities. The `customworkflowactivityinfo` column contains their parameter metadata. |
| `pluginassembly` | Assemblies with `publickeytoken`, `culture`, `version`. |

## Architecture of the process designer

### Entry points

The classic designer is part of the legacy web client:

| Purpose | URL |
|---|---|
| Process list (classic, **without** command bar) | `/_root/homepage.aspx?etc=4703` |
| Solution explorer (classic, **with** command bar) | `/tools/solution/edit.aspx?id=%7BFD140AAF-4DF4-11DD-BD17-0019B9312238%7D` |
| Process designer | `/sfa/workflow/edit.aspx?appSolutionId={solutionId}&id={workflowId}` |
| Condition editor | `/Condition/Condition.aspx?EntityId={workflowId}&StepId={branchStepId}` |
| Field value editor | `/SFA/Workflow/entityform.aspx?workflowId={id}&entityname={e}&activityname={stepId}&stepId={stepId}&entityFullName={e}&primaryentity={e}&mode=1` |
| Parameter editor for code activities | `/SFA/Workflow/customactivityform.aspx?workflowId={id}&activityname={stepId}&readonlymode=false&customstepcategory=CustomActivity&messageName=` |

> [!TIP]
> The GUID `{FD140AAF-4DF4-11DD-BD17-0019B9312238}` is the default solution and is identical in every
> organization. The modern interface removes the command bar from the process list;
> creating a process requires the solution explorer.
>
> Calling `/sfa/workflow/edit.aspx` without context parameters produces a server error.

### Processing model

Every editing action follows the same pattern:

```
Browser ──SOAP──▶ /AppWebServices/Workflow.asmx
                        │
                        ├─▶ changes workflow.xaml in the database
                        │
                  ◀─HTML─┘  fragment for the designer display
```

The response is **HTML**, not XAML. For example, when adding a condition:

```html
<div id="WorkflowStep0DIV" style="display:block">
  <table class="ms-crm-workflow-outer" id="ConditionStep1" parent="WorkflowStep0"
         stepname="ConditionStep" tabindex="0" onclick="OnWorkflowStepClick(...)">
```

The designer's display model is thus an HTML tree whose nodes are linked via the attributes `id`,
`parent` and `stepname`. When the process is reopened, the server reconstructs this
HTML from the stored XAML.

> [!IMPORTANT]
> The **Save** button in the designer does not transmit any execution logic. It only maintains
> process metadata (name, triggers, scope). If only steps were edited, it produces
> no network call, because the changes have already been saved.

## The workflow web service

**Endpoint:** `POST /AppWebServices/Workflow.asmx`
**Namespace:** `http://schemas.microsoft.com/crm/2009/WebServices`

> [!WARNING]
> **This service cannot be used by API callers.** It requires the WRPC token of the
> legacy web client (anti-forgery protection) and, without it, responds with
> `soap:Fault … INVALID_WRPC_TOKEN`, even with a valid bearer token. The following description
> documents the designer's protocol for understanding only — to write your own workflows,
> use the Web API (see [Programmatic access](#programmatic-access)).

### CreateWorkflow

Creates a process together with its XAML skeleton.

```xml
<soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
  <soap:Body>
    <CreateWorkflow xmlns="http://schemas.microsoft.com/crm/2009/WebServices">
      <workflowName>Beispielprozess</workflowName>
      <primaryEntity>lead</primaryEntity>
      <templateId></templateId>
      <businessProcessType>0</businessProcessType>
      <workflowCategory>0</workflowCategory>
      <isSyncWorkflow>false</isSyncWorkflow>
    </CreateWorkflow>
  </soap:Body>
</soap:Envelope>
```

Response:

```xml
<CreateWorkflowResponse><CreateWorkflowResult>e784a882-c8ef-46b2-b6e4-00cbe9146360</CreateWorkflowResult></CreateWorkflowResponse>
```

After this single call the record exists in full: `category=0`, `type=1`,
`statecode=0`, `statuscode=1` and an XAML skeleton of about 1770 characters.

### AddCheckStep

Inserts a check condition. Creates **two** model nodes: the container
(`ConditionStep<N>`) and a branch (`ConditionBranchStep<N+1>`).

```xml
<AddCheckStep xmlns="http://schemas.microsoft.com/crm/2009/WebServices">
  <parentId>WorkflowStep0</parentId>
  <entityId>{E784A882-C8EF-46B2-B6E4-00CBE9146360}</entityId>
  <descriptionXml></descriptionXml>
</AddCheckStep>
```

### UpdateCondition

Sets the comparison logic of a condition branch.

```xml
<UpdateCondition xmlns="http://schemas.microsoft.com/crm/2009/WebServices">
  <activityId>ConditionBranchStep2</activityId>
  <conditionXml><!-- see below, XML-escaped --></conditionXml>
  <entityId>{E784A882-C8EF-46B2-B6E4-00CBE9146360}</entityId>
  <descriptionXml></descriptionXml>
</UpdateCondition>
```

### Other operations

The designer's menu entries carry stable element IDs following the pattern
`mnu_AddStep_<Type>`; the corresponding service operations follow the same naming
(`AddCheckStep` ↔ `mnu_AddStep_CheckStep`).

| Element ID | Menu entry | Generated step type |
|---|---|---|
| `mnu_AddStep_StageStep` | Stage | `StageStep` |
| `mnu_AddStep_CheckStep` | Check Condition | `ConditionStep` + `ConditionBranchStep` |
| `mnu_AddStep_ElseIfStep` | Conditional Branch | `ConditionBranchStep` |
| `mnu_AddStep_ElseStep` | Default Action | `ConditionBranchStep` |
| `mnu_AddStep_WaitStep` | Wait Condition | `WaitStep` |
| `mnu_AddStep_WaitBranchStep` | Parallel Wait Branch | `WaitBranchStep` |
| `mnu_AddStep_CreateStep` | Create Record | `CreateStep` |
| `mnu_AddStep_UpdateStep` | Update Record | `UpdateStep` |
| `mnu_AddStep_AssignStep` | Assign Record | `AssignStep` |
| `mnu_AddStep_SendEmailStep` | Send Email | `SendEmailStep` |
| `mnu_AddStep_ChildWorkflowStep` | Start Child Workflow | `ChildWorkflowStep` |
| `mnu_AddStep_SDKOperation` | Perform Action | `InvokeSdkMessageStep` |
| `mnu_AddStep_ChangeStatusStep` | Change Status | `SetStateStep` |
| `mnu_AddStep_StopWorkflowStep` | Stop Workflow | `StopWorkflowStep` |
| `CustomActivity<pluginTypeId>` | Code activity (submenu per assembly) | `CustomActivityStep` |

## The conditionXml condition format

Conditions are not transmitted as XAML but in a separate declarative format that
the server translates into XAML.

### Comparison with a static value

```xml
<and>
  <condition>
    <column id="colEntity"      value="lead" />
    <column id="colAttribute"   value="lastname"/>
    <column id="colOperator"    value="eq"/>
    <column id="colStaticValue" value="Mustermann" dataslugs="" />
  </condition>
</and>
```

### Comparison with a field value (data slug)

The field reference is embedded as a `slugbody` structure in the `value` attribute (XML-escaped
there) and marked via `dataslugs`:

```xml
<column id="colStaticValue" dataslugs="0"
        value="<slugbody>
                 <slugelement type=&quot;slug&quot;>
                   <slug type=&quot;dynamic&quot; value=&quot;lead.companyname&quot;/>
                 </slugelement>
               </slugbody>" />
```

`slugbody` can hold multiple `slugelement` children, which makes it possible to represent mixed
expressions of text and field references.

### Comparison operators

| Value | Meaning | Value | Meaning |
|---|---|---|---|
| `eq` | equals | `ne` | does not equal |
| `contains` | contains | `doesnotcontain` | does not contain |
| `beginswith` | begins with | `doesnotbeginwith` | does not begin with |
| `endswith` | ends with | `doesnotendwith` | does not end with |
| `not-null` | contains data | `null` | does not contain data |
| `in` | in | `notin` | not in |
| `gt` | is greater than | `ge` | is greater than or equal to |

## Structure of the workflow XAML

### Document frame

The minimal skeleton generated by the server itself:

```xml
<?xml version="1.0" encoding="utf-16"?>
<Activity x:Class="XrmWorkflow00000000000000000000000000000000"
          xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities" …>
  <x:Members>
    <x:Property Name="InputEntities"   Type="InArgument(scg:IDictionary(x:String, mxs:Entity))" />
    <x:Property Name="CreatedEntities" Type="InArgument(scg:IDictionary(x:String, mxs:Entity))" />
  </x:Members>
  <this:XrmWorkflow000…0.InputEntities>
    <InArgument x:TypeArguments="scg:IDictionary(x:String, mxs:Entity)" />
  </this:XrmWorkflow000…0.InputEntities>
  <this:XrmWorkflow000…0.CreatedEntities>
    <InArgument x:TypeArguments="scg:IDictionary(x:String, mxs:Entity)" />
  </this:XrmWorkflow000…0.CreatedEntities>
  <mva:VisualBasic.Settings>Assembly references and imported namespaces for internal implementation</mva:VisualBasic.Settings>
  <mxswa:Workflow />
</Activity>
```

### Namespace prefixes

| Prefix | Namespace |
|---|---|
| (default) | `http://schemas.microsoft.com/netfx/2009/xaml/activities` |
| `x` | `http://schemas.microsoft.com/winfx/2006/xaml` |
| `this` | `clr-namespace:` |
| `mxs` | `Microsoft.Xrm.Sdk` |
| `mxsq` | `Microsoft.Xrm.Sdk.Query` |
| `mxswa` | `Microsoft.Xrm.Sdk.Workflow.Activities` |
| `mcwa` | `Microsoft.Crm.Workflow.Activities` |
| `mva` | `Microsoft.VisualBasic.Activities` |
| `s`, `scg`, `sco`, `srs` | `System`, `System.Collections.Generic`, `System.Collections.ObjectModel`, `System.Runtime.Serialization` |

> [!WARNING]
> The namespace declarations on the root element are **added on demand**. The prefix
> `mcwa`, for example, only appears once the process contains an SDK message activity.
> A generator must align the declarations with the actual content.

### Context dictionaries

| Expression | Meaning |
|---|---|
| `[InputEntities("primaryEntity")]` | The triggering record. |
| `[InputEntities("primaryEntity").Id]` | Its primary key. |
| `[CreatedEntities("<StepId>_localParameter")]` | The record created by a create step. |
| `[CreatedEntities("<StepId><ParameterName>_entity")]` | The record loaded for a lookup **output** of a code activity (see below). |
| `[InputEntities("related_<lookupAttribute>#<targetEntity>")]` | A directly related record, one level deep. |
| `[CreatedEntities("<name>#Temp")]` | Temporary instance for data changes (see below). |

#### Loading the record for an activity output

The output of a code activity is only a reference. To read its fields, the designer loads
the record directly after the activity — guarded by an `If`, because an empty reference would otherwise
fail at runtime:

```xml
<If Condition="[Microsoft.VisualBasic.IsNothing(CustomActivityStep6InitiatingUser_localParameter)]">
  <If.Then>
    <Assign x:TypeArguments="mxs:Entity" To='[CreatedEntities("CustomActivityStep6InitiatingUser_entity")]' Value="[New Entity()]" />
  </If.Then>
  <If.Else>
    <mxswa:RetrieveEntity Attributes="{x:Null}" Entity='[CreatedEntities("CustomActivityStep6InitiatingUser_entity")]'
                          EntityId="[DirectCast(CustomActivityStep6InitiatingUser_localParameter.Id, System.Guid)]"
                          EntityName="systemuser" ThrowIfNotExists="False" />
  </If.Else>
</If>
```

After that, `GetEntityProperty` activities with `Entity='[CreatedEntities("…_entity")]"` and
`EntityName="systemuser"` read any fields of this record. This is how, for example,
`msdyncrmWorkflowTools.Class.GetInitiatingUser` can be made usable.

> [!WARNING]
> References of this kind depend on the **step ID**. If a workflow is regenerated and
> renumbered in the process, a carried-over key points to a record that does not exist — and
> activation then responds with `0x80040216` without further detail. When restructuring, the key must therefore
> be derived from the *new* step, not from the old one.

### Change pattern with a temporary entity

All data-changing steps follow the same sequence: create a new instance, copy the key,
perform the action, write the result back, set a persistence point.

```xml
<Sequence DisplayName="UpdateStep3">
  <Assign x:TypeArguments="mxs:Entity" To='[CreatedEntities("primaryEntity#Temp")]'    Value='[New Entity("lead")]' />
  <Assign x:TypeArguments="s:Guid"     To='[CreatedEntities("primaryEntity#Temp").Id]' Value='[InputEntities("primaryEntity").Id]' />
  <!-- here: value preparation and SetEntityProperty -->
  <mxswa:UpdateEntity DisplayName="UpdateStep3" Entity='[CreatedEntities("primaryEntity#Temp")]' EntityName="lead" />
  <Assign x:TypeArguments="mxs:Entity" To='[InputEntities("primaryEntity")]' Value='[CreatedEntities("primaryEntity#Temp")]' />
  <Persist />
</Sequence>
```

## Naming conventions

> [!IMPORTANT]
> These conventions are not cosmetic. The designer derives its display model from them.
> Deviations mean the process can no longer be opened (error `0x80045037`).

### Step IDs

Pattern `<Type>Step<N>`. The counter `N` runs **continuously across all step types** of a process
and is not reset per type. Example of a real numbering:

```
ConditionStep1, ConditionBranchStep2, UpdateStep3, CustomActivityStep4, CreateStep5,
AssignStep6, SendEmailStep7, ChildWorkflowStep8, SetStateStep9, InvokeSdkMessageStep10,
StopWorkflowStep11, WaitStep12, WaitBranchStep13
```

### DisplayName

| Case | Value |
|---|---|
| Step without description | `<StepId>`, e.g. `UpdateStep6` |
| Step with description | `<StepId>: <Description>`, e.g. `UpdateStep6: Update Lead.Domain` |
| Inner activity of a step | always just `<StepId>` |
| Branch wrapper (`Composite`) | the **branch** ID, not that of the contained step |
| Helper activities | fixed text: `EvaluateExpression`, `EvaluateCondition`, `EvaluateLogicalCondition`, `ConvertCrmXrmTypes` |

### Variables

| Pattern | Type | Usage |
|---|---|---|
| `<StepId>_<n>` | `x:Object` | Intermediate values. `_1` is the result slot, `_2` … `_n` are the sources in evaluation order. |
| `<StepId>_condition` | `x:Boolean`, `Default="False"` | Result of a condition evaluation. |
| `<StepId>_<n>_converted` | `x:Object` | After type conversion, for arguments of code activities. |
| `<StepId><ParameterName>_localParameter` | Parameter type | Input/output parameter of a code activity. Declared **at workflow level** in `<mxswa:Workflow.Variables>`, not in the sequence. |

For the `Default` of the `_localParameter` variable, the parameter type matters: `[Nothing]` only applies to
reference types. An `x:Boolean` gets `Default="False"`, an `mxs:EntityReference`
`Default="[New EntityReference()]"`; a `[Nothing]` on a value type is a type error.

> [!IMPORTANT]
> **The index in `_<n>_converted` must refer to a declared variable.** The converted
> variable is named after its source — `_1` becomes `_1_converted` —, so it consumes **no**
> new number. A `_3_converted` without a declared `_3` is rejected by activation as
> `InvalidPropertyBag`, even though the XAML is well-formed and every *referenced* variable
> was declared. The check "is every reference declared?" does not catch this: here the
> *unused* base variable is the problem.

**In conditions, the helper variables are named after the branch, not after the step** —
`ConditionBranchStep9_2`, not `ConditionStep1_2`. Only this makes it possible later to determine which
comparison belongs to which branch of an if/else-if chain. All branches of a `ConditionSequence`
declare their variables in **one** shared `Variables` collection.

### Description texts

In addition to the `DisplayName`, the designer creates three variables in the sequence for
language-dependent descriptions:

```xml
<Variable x:TypeArguments="x:String" Default="45a82258-2b01-4f7a-a9f2-9ccb5c941acd" Name="stepLabelLabelId" />
<Variable x:TypeArguments="x:String" Name="stepLabelDescription">
  <Variable.Default><Literal x:TypeArguments="x:String" Value="" /></Variable.Default>
</Variable>
<Variable x:TypeArguments="x:Int32" Default="1031" Name="stepLabelLanguageCode" />
```

> [!NOTE]
> `stepLabelLabelId` is regenerated on **every** write. The value is arbitrary but must
> be present. `stepLabelLanguageCode` is the LCID of the designer language (1031 = German).

## Activity reference

All `AssemblyQualifiedName` values of the platform activities follow the pattern:

```
Microsoft.Crm.Workflow.Activities.<Name>, Microsoft.Crm.Workflow, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35
```

| Step | Activity | Remark |
|---|---|---|
| Check condition | `ConditionSequence` (as `ActivityReference`) | Argument `Wait=False` |
| Wait condition | `ConditionSequence` | Argument `Wait=True`; `ContainsElseBranch` is `x:Null` |
| Condition branch | `ConditionBranch` | Argument `Condition` = variable reference; for the else branch the literal `True` |
| Branch/stage content | `Composite` | Wrapper around a `Sequence` |
| Create record | `mxswa:CreateEntity` | Result in `CreatedEntities("<StepId>_localParameter")` |
| Update record | `mxswa:UpdateEntity` | Temporary pattern |
| Assign record | `mxswa:AssignEntity` | Attribute `Owner` |
| Read field value | `mxswa:GetEntityProperty` | Attributes `Attribute`, `Entity`, `EntityName`, `Value` |
| Set field value | `mxswa:SetEntityProperty` | Same, plus `TargetType` |
| Send email | `mxswa:SendEmail` | Temporary pattern with `New Entity("email")` |
| Start child workflow | `mxswa:StartChildWorkflow` | `WorkflowId`, `InputParameters` (dictionary variable) |
| Change status | `mxswa:SetState` | `State`/`Status` as `OptionSetValue` literals; **without** sequence wrapper |
| Perform action | `mcwa:InvokeSdkMessageActivity` | `SdkMessageId`, `SdkMessageName`, `SdkMessageEntityName` |
| Stop workflow | `TerminateWorkflow` (WF4) | `Exception`, `Reason` |
| Evaluate expression | `EvaluateExpression` | see [Value expressions](#value-expressions) |
| Evaluate condition | `EvaluateCondition` | `ConditionOperator`, `Operand`, `Parameters`, `Result` |
| Logical combination | `EvaluateLogicalCondition` | `LogicalOperator` (`And`/`Or`), `LeftOperand`, `RightOperand` |
| Type conversion | `ConvertCrmXrmTypes` | CRM type → .NET type |
| Code activity | `AssemblyQualifiedName` of the custom assembly | see below |

### Example: check condition

A condition consists of a `ConditionSequence` container with four activities:

```xml
<mxswa:ActivityReference AssemblyQualifiedName="…ConditionSequence, …" DisplayName="ConditionStep1">
  <mxswa:ActivityReference.Arguments>
    <InArgument x:TypeArguments="x:Boolean" x:Key="Wait">False</InArgument>
  </mxswa:ActivityReference.Arguments>
  <mxswa:ActivityReference.Properties>
    <sco:Collection x:TypeArguments="Variable" x:Key="Variables">
      <Variable x:TypeArguments="x:Boolean" Default="False" Name="ConditionBranchStep2_condition" />
      <Variable x:TypeArguments="x:Object" Name="ConditionBranchStep2_1" />
      <Variable x:TypeArguments="x:Object" Name="ConditionBranchStep2_2" />
    </sco:Collection>
    <sco:Collection x:TypeArguments="Activity" x:Key="Activities">
      <!-- 1. read the left-hand side -->
      <mxswa:GetEntityProperty Attribute="lastname" Entity='[InputEntities("primaryEntity")]'
                               EntityName="lead" Value="[ConditionBranchStep2_1]">
        <mxswa:GetEntityProperty.TargetType>
          <InArgument x:TypeArguments="s:Type"><mxswa:ReferenceLiteral x:TypeArguments="s:Type"><x:Null /></mxswa:ReferenceLiteral></InArgument>
        </mxswa:GetEntityProperty.TargetType>
      </mxswa:GetEntityProperty>
      <!-- 2. prepare the right-hand side (literal or a second GetEntityProperty) -->
      <!-- 3. compare -->
      <mxswa:ActivityReference AssemblyQualifiedName="…EvaluateCondition, …" DisplayName="EvaluateCondition">
        <mxswa:ActivityReference.Arguments>
          <InArgument x:TypeArguments="mxsq:ConditionOperator" x:Key="ConditionOperator">Equal</InArgument>
          <InArgument x:TypeArguments="s:Object[]" x:Key="Parameters">[New Object() { ConditionBranchStep2_2 }]</InArgument>
          <InArgument x:TypeArguments="x:Object" x:Key="Operand">[ConditionBranchStep2_1]</InArgument>
          <OutArgument x:TypeArguments="x:Boolean" x:Key="Result">[ConditionBranchStep2_condition]</OutArgument>
        </mxswa:ActivityReference.Arguments>
      </mxswa:ActivityReference>
      <!-- 4. branch -->
      <mxswa:ActivityReference AssemblyQualifiedName="…ConditionBranch, …" DisplayName="ConditionBranchStep2">
        <mxswa:ActivityReference.Arguments>
          <InArgument x:TypeArguments="x:Boolean" x:Key="Condition">[ConditionBranchStep2_condition]</InArgument>
        </mxswa:ActivityReference.Arguments>
        <mxswa:ActivityReference.Properties>
          <x:Null x:Key="Then" /><x:Null x:Key="Else" /><x:Null x:Key="Description" />
        </mxswa:ActivityReference.Properties>
      </mxswa:ActivityReference>
    </sco:Collection>
    <x:Boolean x:Key="ContainsElseBranch">False</x:Boolean>
  </mxswa:ActivityReference.Properties>
</mxswa:ActivityReference>
```

When a branch is filled with steps, a `Composite` wrapper replaces the `x:Null` of the
`Then` or `Else` property. The default action branch is another `ConditionBranch` with
`Condition` = `True`; in addition, `ContainsElseBranch` switches to `True`.

#### Multiple branches: the if/else-if chain

A `ConditionSequence` can carry **N** `ConditionBranch` nodes, each with its own comparisons,
checked in order — the first one that matches wins. This is the pattern for "multiple
preconditions, each with its own abort"; a real workflow in this environment has six branches.

The structure of the activity list is strictly sequential:

```
Comparisons of branch 1 … → ConditionBranch (branch 1)
Comparisons of branch 2 … → ConditionBranch (branch 2)
…
ConditionBranch with Condition="True"   ← the default branch, if present
```

Exactly this order makes it possible, when reading, to assign each comparison to its branch — together
with naming the variables after the branch (see *Naming conventions → Variables*). The numbers of the
branch IDs do **not** have to be ascending: anyone who inserts a branch later in the designer
gets a high number at an early position.

> [!CAUTION]
> A model that only knows "then/else" cannot represent such a chain. Anyone who maps it onto
> such a model anyway loses branches — and additionally merges the comparisons of all branches into a
> single chain, which silently changes the logic.

> [!IMPORTANT]
> **Valueless operators** (`Null`, `NotNull` — in the UI "does not contain data" and "contains
> data") have no comparison value. `Parameters` must then be written as an **explicit null element**,
> not as an `InArgument`:
>
> ```xml
> <InArgument x:TypeArguments="mxsq:ConditionOperator" x:Key="ConditionOperator">NotNull</InArgument>
> <x:Null x:Key="Parameters" />
> <InArgument x:TypeArguments="x:Object" x:Key="Operand">[ConditionBranchStep2_2]</InArgument>
> ```
>
> An empty array (`[New Object() { }]`) is rejected by the platform with `0x80045040`. In practice, this is
> the most common cause of rejected XAML.

### Stage

A stage is a `Composite` wrapper without any special marking:

```xml
<mxswa:ActivityReference AssemblyQualifiedName="…Composite, …"
                         DisplayName="StageStep16: Description of the stage">
  <mxswa:ActivityReference.Properties>
    <sco:Collection x:TypeArguments="Variable" x:Key="Variables" />
    <sco:Collection x:TypeArguments="Activity" x:Key="Activities">
      … steps …
      <Persist />
    </sco:Collection>
  </mxswa:ActivityReference.Properties>
</mxswa:ActivityReference>
```

> [!NOTE]
> Structurally, a stage does **not** differ from a branch wrapper. It is recognized
> solely by the step ID convention `StageStep<N>` in the `DisplayName`.
> If a process contains stages, all steps must lie within stages; when inserting, the designer
> automatically adds a leading stage if necessary.

## Value expressions

Literals are **never** written directly into a target attribute. Every value is prepared into a
variable via a helper activity and referenced from there.

### Static value

`EvaluateExpression` with `ExpressionOperator` = `CreateCrmType`:

```xml
<InArgument x:Key="ExpressionOperator">CreateCrmType</InArgument>
<InArgument x:Key="Parameters">[New Object() { Microsoft.Xrm.Sdk.Workflow.WorkflowPropertyType.String, "Text", "String" }]</InArgument>
<InArgument x:Key="TargetType"><mxswa:ReferenceLiteral x:TypeArguments="s:Type" Value="x:String" /></InArgument>
<OutArgument x:Key="Result">[UpdateStep3_4]</OutArgument>
```

The first parameter is the `WorkflowPropertyType`, the second the value, the third the
**CRM attribute type** — and that is not always the name of the `WorkflowPropertyType`:

| Type | `WorkflowPropertyType` | Marker (3rd parameter) |
|---|---|---|
| Text | `String` | `String` |
| Yes/No | `Boolean` | `Boolean` (the designer also omits it here) |
| Whole number | **`Integer`** (not `Int`) | `Integer` |
| Option set | `OptionSetValue` | **`Picklist`** |
| ID | `Guid` | **`UniqueIdentifier`** |
| Record reference | `EntityReference` | **`Lookup`** (five-part, see below) |

> [!IMPORTANT]
> A wrong marker makes Dataverse reject the entire document on write with `0x80045040`.
> The same goes for `x:DateTime` as a type argument: the XAML 2006 namespace has no DateTime; the type must
> come from `System` — **`s:DateTime`**.

> [!IMPORTANT]
> The **first** parameter must be correct too, and literally so: it is an enum member that is resolved in the
> VB expression. A whole number is called `Integer` there, not `Int` — even though the type is called
> "Int" everywhere else. If a name appears that the enum does not know, the expression cannot be
> translated, and the response is again `0x80045040` with the misleading message "außerhalb der
> Webanwendung erstellt" ("created outside the web application"). So the error does not point at the expression but at the whole document.
>
> This was found on a whole-number input for `msdyncrmWorkflowTools.StringFunctions`.
> Text and Yes/No inputs happened to be named correctly and masked the gap; none of the
> ten designer fixtures contains a scalar input to a code activity.

> [!NOTE]
> Constants for inputs of a code activity need the full chain
> `CreateCrmType` → `ConvertCrmXrmTypes` → `[DirectCast(…)]`. Writing the literal **directly** into the
> `InArgument` — permitted in plain WF4 — is rejected by the platform with `0x80045040`,
> for every type, including text.

#### Clearing a field

An empty value is **not** written as `CreateCrmType` with an empty string. Instead, the designer
declares a variable that it **never assigns** and points the assignment at it — at
runtime this is `Nothing`:

```xml
<Variable x:TypeArguments="x:Object" Name="UpdateStep13_4" />
...
<mxswa:SetEntityProperty Attribute="sample_dunning2" Value="[UpdateStep13_4]" ... />
```

> [!IMPORTANT]
> A `CreateCrmType` with an empty value is rejected with `0x80040216` for a **date** — for text
> it goes through. This inconsistency is the trap: the error only occurs for that one field type,
> long after the pattern was tried out for a different one.
>
> When **reading**, the distinction is just as important: a source variable that nobody fills is an
> intentional clear; one that is filled but whose chain cannot be resolved is a gap
> in the reader and must be reported. Without this separation, either a harmless "clear field" is reported as
> unreadable — or, far worse, a **calculated** value is silently replaced by an empty one
> when writing back.

#### A deadline: date plus duration

A calculated due date is an `Add` over the base date and a duration. The duration is again
a variable with a default value, this time of type `mxsw:XrmTimeSpan` — as a **child element**, not as a
`Default` attribute:

```xml
<Variable x:TypeArguments="mxsw:XrmTimeSpan" Name="UpdateStep15_5">
  <Variable.Default>
    <Literal x:TypeArguments="mxsw:XrmTimeSpan">
      <mxsw:XrmTimeSpan Days="7" Hours="0" Minutes="0" Months="0" Years="0" />
    </Literal>
  </Variable.Default>
</Variable>
```

The chain is then `RetrieveCurrentTime` → `SelectFirstNonNull` → `Add(base, duration)`.

> [!NOTE]
> Unlike text concatenation, this `Add` carries **one** `TargetType`, namely
> `s:DateTime`. When reading, the target type tells you whether an `Add` assembles a string
> or shifts a date.
>
> The namespace `mxsw` (`Microsoft.Xrm.Sdk.Workflow`) is **not** the same as `mxswa`
> (`…Workflow.Activities`) and is only declared in documents that contain a duration.

> [!WARNING]
> **Commas in the value must be encoded as `&#44;`.** The parameter array is split on commas
> before the string literals are evaluated; a comma in the text therefore tears the argument list apart. In
> German sentences this is the normal case — the designer encodes consistently.

#### Sets of values (`In` / `NotIn`)

For a comparison against multiple values, one `CreateCrmType` is generated per value; the
`Parameters` array of the `EvaluateCondition` then lists all result variables:

```xml
<InArgument x:Key="Parameters">[New Object() { ConditionBranchStep2_3, ConditionBranchStep2_4, ConditionBranchStep2_5 }]</InArgument>
```

#### Current time

`ExpressionOperator` = `RetrieveCurrentTime`, **without** parameters and **without** a target type:

```xml
<InArgument x:Key="ExpressionOperator">RetrieveCurrentTime</InArgument>
<InArgument x:Key="Parameters" xml:space="preserve">[New Object() {  }]</InArgument>
<InArgument x:Key="TargetType"><mxswa:ReferenceLiteral x:TypeArguments="s:Type"><x:Null /></mxswa:ReferenceLiteral></InArgument>
```

In a condition, the result goes directly into the `Parameters` array (operators `OnOrAfter`,
`OnOrBefore`, …); as a written value it passes through `SelectFirstNonNull` like any source.

#### Concatenation

`ExpressionOperator` = `Add` joins multiple values into one text — this is how subject lines and
email bodies are built. The target type is **`x:Null`**, because the parts determine it. The result slot is
reserved first, then the parts in order:

```xml
<InArgument x:Key="ExpressionOperator">Add</InArgument>
<InArgument x:Key="Parameters">[New Object() { CreateStep17_6, CreateStep17_7 }]</InArgument>
<InArgument x:Key="TargetType"><mxswa:ReferenceLiteral x:TypeArguments="s:Type"><x:Null /></mxswa:ReferenceLiteral></InArgument>
<OutArgument x:Key="Result">[CreateStep17_5]</OutArgument>
```

Parts may themselves be field references; a real email body consists of a dozen or more.

### Field references with a default value

`EvaluateExpression` with `ExpressionOperator` = `SelectFirstNonNull`. The sources are each read
beforehand with `GetEntityProperty`; the default value is prepared as a literal and appended as the
**last** element of the parameter array:

```xml
<!-- GetEntityProperty companyname → [UpdateStep3_2] -->
<!-- GetEntityProperty subject     → [UpdateStep3_3] -->
<!-- CreateCrmType "unbekannt"     → [UpdateStep3_4]  ("unknown") -->
<InArgument x:Key="ExpressionOperator">SelectFirstNonNull</InArgument>
<InArgument x:Key="Parameters">[New Object() { UpdateStep3_2, UpdateStep3_3, UpdateStep3_4 }]</InArgument>
<OutArgument x:Key="Result">[UpdateStep3_1]</OutArgument>
```

At runtime, the first non-empty value wins.

### Output of a preceding step

```xml
<InArgument x:Key="ExpressionOperator">SelectFirstNonNull</InArgument>
<InArgument x:Key="Parameters">[New Object() { CustomActivityStep3Domain_localParameter }]</InArgument>
<OutArgument x:Key="Result">[UpdateStep6_1]</OutArgument>
```

### Comparison value of a condition

An exception applies here: a field reference on the right-hand side of a comparison replaces the
literal block **entirely with a second `GetEntityProperty`** that writes into the same variable.
Neither `SelectFirstNonNull` nor `ConvertCrmXrmTypes` is used.

### Type conversion

`ConvertCrmXrmTypes` is only required when the value flows into an argument of a **code activity**,
because a real .NET type is expected there:

```xml
<InArgument x:Key="Value">[CustomActivityStep3_1]</InArgument>
<InArgument x:Key="TargetType"><mxswa:ReferenceLiteral x:TypeArguments="s:Type" Value="x:String" /></InArgument>
<OutArgument x:Key="Result">[CustomActivityStep3_1_converted]</OutArgument>
```

It is then used in typed form: `[DirectCast(CustomActivityStep3_1_converted, System.String)]`.

`SetEntityProperty` needs no conversion, because its `Value` accepts a CRM-typed value.

### `TargetType` of `GetEntityProperty`

| Context | Value |
|---|---|
| Condition comparison | `<mxswa:ReferenceLiteral><x:Null /></mxswa:ReferenceLiteral>` |
| Abort message (`TerminateWorkflow.Reason`) | `<mxswa:ReferenceLiteral><x:Null /></mxswa:ReferenceLiteral>` |
| Value preparation for fields/arguments | `<mxswa:ReferenceLiteral Value="x:String" />` (or the target type) |

The middle row was learned the hard way: a field read that names `x:String` as the target type in the
abort message returns **nothing** for anything that is not text — above all a date. The
message thus stays empty, and instead of the sentence Dataverse shows the display name of the step
("Workflow abbrechen", i.e. "Stop workflow"). The subsequent `SelectFirstNonNull` of the same chain also stays
untyped; the following `Add` formats the value.

Established by comparing the same workflow definition in two organizations: the version written by the designer
(target type `null`) outputs the date, the generated one does not — the two
XAML documents differed in exactly these two places and nowhere else.

### Combining multiple conditions

`EvaluateLogicalCondition` combines **pairwise** via `LeftOperand` and `RightOperand`; both may
be the `Result` of another `EvaluateLogicalCondition`. The combination is therefore not a flat
And/Or over a list but an **expression tree** — grouping such as `A And (B Or C)` needs
no additional construct, only a subtree with a different operator.

```xml
<!-- A And (B Or C) -->
<mxswa:ActivityReference AssemblyQualifiedName="…EvaluateLogicalCondition…">
  <InArgument x:Key="LogicalOperator">Or</InArgument>
  <InArgument x:Key="LeftOperand">[ConditionBranchStep7_4]</InArgument>   <!-- B -->
  <InArgument x:Key="RightOperand">[ConditionBranchStep7_6]</InArgument>  <!-- C -->
  <OutArgument x:Key="Result">[ConditionBranchStep7_3]</OutArgument>
</mxswa:ActivityReference>
<mxswa:ActivityReference AssemblyQualifiedName="…EvaluateLogicalCondition…">
  <InArgument x:Key="LogicalOperator">And</InArgument>
  <InArgument x:Key="LeftOperand">[ConditionBranchStep7_1]</InArgument>   <!-- A -->
  <InArgument x:Key="RightOperand">[ConditionBranchStep7_3]</InArgument>  <!-- the group -->
  <OutArgument x:Key="Result">[ConditionBranchStep7_condition]</OutArgument>
</mxswa:ActivityReference>
```

The designer numbers the variables in pre-order (result before the children); this is not
mandatory — what matters is that every variable is declared and that the root node writes to
`<BranchId>_condition`, which `ConditionBranch.Condition` relies on.

Included as a fixture: `condition-group.xaml`, a workflow written by the designer with exactly
one group.

## Custom workflow activities

### Parameter metadata

The input and output parameters of registered code activities are stored as XML in
`plugintype.customworkflowactivityinfo`:

```
GET /api/data/v9.2/plugintypes(<pluginTypeId>)?$select=name,customworkflowactivityinfo,workflowactivitygroupname
```

```xml
<SandboxCustomActivityInfo>
  <CustomActivityInfo>
    <Name>Contoso.Plugins.Workflows.ExtractDomain</Name>
    <GroupName>Contoso.Plugins (1.0.0.0)</GroupName>
    <PublicKeyToken>6cd3b47345c1c112</PublicKeyToken>
    <Culture>neutral</Culture>
    <AssemblyVersion>1.0.0.0</AssemblyVersion>
  </CustomActivityInfo>
  <Inputs>
    <CustomActivityParameterInfo>
      <Name>E-Mail</Name>
      <TypeName>System.String, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</TypeName>
      <Required>false</Required>
      <WorkflowAttributeType>Boolean</WorkflowAttributeType>
      <DependencyPropertyName>Email</DependencyPropertyName>
    </CustomActivityParameterInfo>
  </Inputs>
  <Outputs>…</Outputs>
  <AssemblyQualifiedName>Contoso.Plugins.Workflows.ExtractDomain, Contoso.Plugins, Version=1.0.0.0, Culture=neutral, PublicKeyToken=6cd3b47345c1c112</AssemblyQualifiedName>
</SandboxCustomActivityInfo>
```

> [!IMPORTANT]
> - The `AssemblyQualifiedName` is available **fully assembled** and should be copied, not
>   built yourself. In particular, `PublicKeyToken` is set for signed assemblies.
> - The `x:Key` in the XAML is `DependencyPropertyName`, **not** `Name`. In the example above:
>   `Email`, not `E-Mail`.
> - `WorkflowAttributeType` is unreliable — the example shows `Boolean` for a
>   string parameter. `TypeName` is authoritative.
> - `workflowactivitygroupname` is a **string** (`"Contoso.Plugins (1.0.0.0)"`).

### XAML representation

The activity sits inside a `Composite` wrapper; parameters appear as `InArgument` or
`OutArgument` with `x:Key` = `DependencyPropertyName`:

```xml
<mxswa:ActivityReference AssemblyQualifiedName="…Composite, …" DisplayName="CustomActivityStep3">
  <mxswa:ActivityReference.Properties>
    <sco:Collection x:TypeArguments="Variable" x:Key="Variables" />
    <sco:Collection x:TypeArguments="Activity" x:Key="Activities">
      <mxswa:ActivityReference
          AssemblyQualifiedName="Contoso.Plugins.Workflows.ExtractDomain, Contoso.Plugins, Version=1.0.0.0, Culture=neutral, PublicKeyToken=6cd3b47345c1c112"
          DisplayName="CustomActivityStep3: Extract Domain (CodeActivity)">
        <mxswa:ActivityReference.Arguments>
          <InArgument  x:TypeArguments="x:String" x:Key="Website">[DirectCast(CustomActivityStep3_1_converted, System.String)]</InArgument>
          <InArgument  x:TypeArguments="x:String" x:Key="Email">[DirectCast(CustomActivityStep3_3_converted, System.String)]</InArgument>
          <OutArgument x:TypeArguments="x:String" x:Key="Domain">[CustomActivityStep3Domain_localParameter]</OutArgument>
        </mxswa:ActivityReference.Arguments>
      </mxswa:ActivityReference>
    </sco:Collection>
  </mxswa:ActivityReference.Properties>
</mxswa:ActivityReference>
```

The output variable is declared at workflow level:

```xml
<mxswa:Workflow.Variables>
  <Variable x:TypeArguments="x:String" Default="[Nothing]" Name="CustomActivityStep3Domain_localParameter" />
</mxswa:Workflow.Variables>
```

## Activation and compilation

Activation is performed by changing the state of the record:

```http
PATCH /api/data/v9.2/workflows(<id>)
{ "statecode": 1, "statuscode": 2 }
```

In doing so, the platform compiles the XAML and **replaces the null GUID in the class name with the
actual `workflowid`** — at all occurrences (`x:Class` as well as the `this:` property elements):

```
before activation:  x:Class="XrmWorkflow00000000000000000000000000000000"
after activation:   x:Class="XrmWorkflowe784a882c8ef46b2b6e400cbe9146360"
```

> [!NOTE]
> When writing, the class name does **not** have to contain the process GUID; the null GUID is
> permitted. Different assembly versions in the namespaces are tolerated as well: in one
> organization, processes with `Version=8.0.0.0` and `Version=9.0.0.0` coexist.
>
> Activation does not fully validate the execution logic. Incompletely configured
> steps (such as a create step without required fields) do not prevent activation.

## Programmatic access

### Supported and unsupported operations

| Operation | Route | Result |
|---|---|---|
| Read processes | `GET /api/data/v9.2/workflows` | supported |
| Read XAML | `GET …/workflows(<id>)?$select=xaml` | supported |
| Change metadata | `PATCH …/workflows(<id>)` | supported |
| **Change XAML** | `PATCH …/workflows(<id>)` with `{"xaml": …}` | **supported as long as `statecode=0`** |
| Activate/deactivate | `PATCH` on `statecode`/`statuscode` | supported, trigger required |
| Create process **without** `xaml` | `POST /api/data/v9.2/workflows` | fails with `0x80045040` |
| **Create process with `xaml`** | `POST /api/data/v9.2/workflows` | **supported** |
| Delete process | `DELETE …/workflows(<id>)` | supported, only in draft |

> [!IMPORTANT]
> `0x80045040` ("created outside the web application") refers to **missing or invalid
> XAML**, not to the access route. A `POST` with a valid skeleton in the `xaml` field is
> accepted — verified with a bearer token against Dataverse 9.2. The internal SOAP service is not
> needed for this (and is blocked for API callers anyway).
>
> The same applies to `PATCH`: if XAML is written that the platform does not consider valid,
> the response is also `0x80045040` — even though the record has long existed. The message is
> therefore a general "XAML not accepted" error.

### Editing cycle for XAML

The following cycle is verified: read → change → write back → activate → open in the
designer, with the designer displaying the change correctly and then continuing to work on it
itself.

```
1. GET    …/workflows(<id>)?$select=xaml,statecode
2. if needed PATCH { "statecode": 0, "statuscode": 1 }   // force draft
3. change XAML – follow the naming conventions!
4. PATCH  …/workflows(<id>)  { "xaml": "…" }             // 204 No Content
5. PATCH  …/workflows(<id>)  { "statecode": 1, "statuscode": 2 }
```

> [!TIP]
> Before step 4, always save the unchanged XAML as a restore point.

## Error reference

| Code | Message (abbreviated) | Cause and remedy |
|---|---|---|
| `0x80045040` | „Dieser Workflow kann nicht erstellt, aktualisiert oder veröffentlicht werden, da er außerhalb der Microsoft Dynamics 365-Webanwendung erstellt wurde." ("This workflow cannot be created, updated or published because it was created outside the Microsoft Dynamics 365 web application.") | The XAML is missing or not accepted. When creating, send a valid skeleton along. When changing: check the naming conventions and the detailed rules mentioned below (most common cause: see the `x:Null` rule for valueless operators). |
| `0x80045018` | "Automatic workflow cannot be published if no activation parameters have been specified." | Activation of an automatic process without a trigger. Set `triggeroncreate`/`updatestage`/`triggerondelete` or `ondemand` first. |
| `INVALID_WRPC_TOKEN` | SOAP fault from `Workflow.asmx` | The internal service requires the anti-forgery token of the legacy web client. Use the Web API. |
| `0x80045037` | Designer cannot display the process | Structurally inconsistent XAML: step IDs, `DisplayName` or variable names do not match the conventions. The error is **not** caused by the write itself. |
| `0x80060888` | "Resource not found for the segment 'plugintypeattributes'." | The `plugintypeattributes` table does not exist in current Dataverse versions. Read code activity parameters from `plugintype.customworkflowactivityinfo` instead. |

## Limitations and notes

- **All interfaces in this document except the Web API are internal.** `Workflow.asmx` is
  not versioned and may change.
- Step IDs are assigned uniquely and sequentially across the whole process. When inserting
  steps into existing processes, the counter must be continued.
- The designer also produces **incomplete intermediate states** in the XAML (such as `CreateEntity` with
  an empty `EntityName`). A draft does not have to be executable.
- Changes to activated processes are not possible; deactivate first.
- **`<Persist />` may only appear in background workflows.** A real-time process (`mode=1`) has
  no persistence points; the generated XAML therefore differs depending on the mode, and the mode
  must consequently be fixed *before* the logic is written.
- **Activating creates a second row** (`type=2`, referenced via `parentworkflowid`). Neither
  deactivating nor deleting the definition removes it — so in a test environment
  these copies accumulate.
- When the target entity of a step is changed, the designer discards the configuration of that
  step (confirmation prompt in the browser).

### Notes on automating the user interface

| Element | ID/pattern |
|---|---|
| "Add Step" menu | `mnu_AddStep_<Type>`, code activities `CustomActivity<pluginTypeId>` |
| "Set Properties" button | `<StepId>_button` |
| Entity selection of a step | `<StepId>_entitylstwfc` (triggers a confirmation prompt) |
| Condition row (Advanced Find control) | `EFGRP<id>` + `EFENTITYCTL` / `AFATTRCTL` / `OPFOPCTL` / `VFVALUECTL` |
| Form assistant | `selObjects` (entity), `valueSelector` (field), `wfDynamicExpressionAdd`, `dynamicValueSelector`, `<targetfield>DefaultValueControl`, `wfDynamicExpressionOk` |
| Transferring a field reference | JS function `InsertCustomizedDataSlug(value, text)` |

The four controls of a condition row are loaded in cascade (entity → attribute →
operator → value); each level is only populated after the previous one has been selected. The field list is
type-filtered: in the condition editor only fields that match the data type of the left operand
appear.

## Appendix: Investigation methodology

The information in this document is based on the following procedure:

1. **Network capture** of all designer requests per editing step, including
   complete SOAP bodies.
2. **Diff analysis of the XAML**: after every single designer action, the XAML was read via the
   Web API and compared against the previous state. This isolates the delta of a step type
   exactly.
3. **Cross-check against production processes**: fully configured patterns (code activities with
   parameters, field references) were verified against existing, human-created processes.
4. **Write test**: changed XAML was written back, the process was activated and reopened in the
   designer to demonstrate backward compatibility.

Recommended procedure for further investigations:

```bash
# Read XAML, break it into readable lines, diff against the previous state
sed 's/></>\n</g' step-N.xaml > step-N.pretty
diff step-N-1.pretty step-N.pretty
```

## Addendum: Activation validates the XAML thoroughly

An earlier assumption in this document was that activation barely checks the logic. That only applies to the
*completeness of the configuration* (a step without required details can be activated). The
*structure*, by contrast, is checked closely, and the error names the affected steps:

```
0x80048455 — Dieser Workflow enthaelt Fehler und kann nicht veroeffentlicht werden.
Worklfow Id: <id of the activation copy>
ErrorMap Details: {CustomActivityStep9: InvalidPropertyBag ;
                   ConditionBranchStep8: InvalidEntity, InvalidPropertyBag ;
                   ... ; WorkflowStep0: InvalidEntity, InvalidPropertyBag}
```

(The first line reads: "This workflow contains errors and cannot be published.")

| Flag | Meaning |
|---|---|
| `InvalidEntity` | The step reads from an entity that cannot be resolved in this context — such as a `related_…#…` key whose lookup attribute does not lead to the named target entity. |
| `InvalidPropertyBag` | The `ActivityReference.Properties` of a step do not match the expected shape of the activity. |

> [!TIP]
> The `ErrorMap` is the most important diagnostic tool. It appears **only** on activation, not
> when writing the XAML — so a `PATCH` can succeed even though the process is not
> runnable. Anyone generating XAML should always do a trial activation after writing.
>
> Note also: the workflow ID shown is that of the **activation copy** (`type=2`), not that of the
> edited process.

> [!WARNING]
> **Activation copies cannot be deleted directly.** A `DELETE` on a row with `type=2`
> responds with `0x80045004` ("Cannot delete a workflow activation."). They only disappear when the
> **definition** is deleted — and only before the definition is gone.
>
> If the definition is removed first, the copy remains as an orphan and can no longer be
> removed via the API. Anyone who activates and deletes a lot while experimenting therefore accumulates them: an
> environment can contain hundreds of such rows that do not show up in the UI (queries
> filter on `type eq 1`) but sit in `workflows`. So the order is always: **deactivate, then
> delete the definition** — never delete the definition while an activation exists.
