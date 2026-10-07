# Related records and the Workflow Tools

Two topics that take classic workflows beyond their apparent limits. A supplement to `SKILL.md`.

## 1. Reading fields of related records

Classic workflows can read fields of **directly related** records — one level deep, through lookup
fields of the primary record. Access goes through a second key in the `InputEntities` dictionary:

```
InputEntities("related_<lookupAttribute>#<targetEntity>")
```

Example — on a `salesorder`, read the field `sample_salesrep` of the related `opportunity`
(lookup field leading there: `opportunityid`):

```xml
<!-- First read the lookup field of the primary record (makes the relationship available) -->
<mxswa:GetEntityProperty Attribute="opportunityid" Entity='[InputEntities("primaryEntity")]'
                         EntityName="salesorder" Value="[UpdateStep3_2]"> … </mxswa:GetEntityProperty>

<!-- Then the field on the related entity -->
<mxswa:GetEntityProperty Attribute="sample_salesrep"
                         Entity='[InputEntities("related_opportunityid#opportunity")]'
                         EntityName="opportunity" Value="[UpdateStep3_3]"> … </mxswa:GetEntityProperty>
```

Rules of thumb:

- The key is made up of the **lookup attribute of the primary record** and the **logical name of the
  target entity**, separated by `#`, with the prefix `related_`.
- `EntityName` is the **target entity**, not the primary entity.
- One level only. Deeper paths need a child workflow on the target entity or
  `msdyncrmWorkflowTools.QueryValues` (see below).
- The platform fills these keys itself; you do not have to "load" the relationship.

In this server's definition model such references are written as `"<entity>.<attribute>"`, i.e.
`"opportunity.sample_salesrep"` — the builder generates the `related_…` key from it, provided it knows
the lookup attribute (specified via `via`).

## 2. msdyncrmWorkflowTools — what classic workflows can do after all

The assembly **msdyncrmWorkflowTools** (version 1.0.62.1, `PublicKeyToken=416e876b9bee261e`) is
installed in this environment and provides **87 code activities**. They close exactly the gaps where
classic workflows otherwise fail.

> [!TIP]
> Look here before reaching for a cloud flow. Much of what "cannot be done with classic workflows"
> can be done with these activities after all — synchronously and inside the transaction.
>
> Always fetch the parameters and the correct `AssemblyQualifiedName` via
> `workflow_get_activity_parameters`; never guess them.

### Relationships and memberships (cannot be checked with built-in steps)

| Activity | Purpose |
|---|---|
| `CheckUserInTeam` | Is a user a member of a team? In: `Team` (lookup team), `User` (lookup systemuser) · Out: `isUserInTeam` (Boolean) |
| `Class.IsMemberOfTeam` | the same, alternative implementation |
| `CheckUserInRole` | Does a user have a specific security role? |
| `Class.IsMemberOfMarketingList` | Membership in a marketing list |
| `CheckAssociateEntity` | Does an N:N association exist? |
| `AssociateEntity` / `DisassociateEntity` | Create/remove an N:N association |
| `AddUserToTeam` / `RemoveUserFromTeam` | Change team membership |
| `AddRoleToUser` / `RemoveRoleFromUser` / `AddRoleToTeam` / `RemoveRoleFromTeam` | Assign roles |

### Querying and aggregating data

| Activity | Purpose |
|---|---|
| `QueryValues` | **The general-purpose building block:** run FetchXML and return values (7 in / 2 out). This reaches relationships of any depth, filters and sort orders. |
| `CountChildEntityRecords` | Count child records |
| `ConcatenateFromQuery` | Concatenate field values of several records into one text |
| `RollupFunctions` | Aggregates (5 outputs) |
| `CalculateRollupField` | Recalculate a rollup field immediately |
| `GetRecordID` | Id of the record as text |

### Changing records

| Activity | Purpose |
|---|---|
| `UpdateChildRecords` | Update child records in one go |
| `CloneRecord` / `CloneChildren` | Copy records including their children |
| `Class.DeleteRecord` | Delete (classic workflows cannot do this) |
| `SetState` | Set the status, even where the standard step does not work |
| `SetLookupFieldFromRecordUrl` | Set a lookup from a record URL |
| `Class.SetProcess` / `SetProcessStage` | Control the business process flow and its stage |

### Option sets, text, numbers, dates

| Activity | Purpose |
|---|---|
| `GetMultiSelectOptionSet` / `SetMultiSelectOptionSet` / `MapMultiSelectOptionSet` | Multi-select fields (unreachable with built-in steps) |
| `GetOptionSetValue` / `InsertOptionValue` / `DeleteOptionValue` | Read option values and maintain the metadata |
| `StringFunctions` | 12 inputs / 11 outputs — substrings, replace, search, length … |
| `NumericFunctions` / `DateFunctions` | Arithmetic (4 and 11 outputs respectively, including weekday and differences) |
| `CalculateAgregateDate` | Date aggregates |
| `CurrencyConvert` | Currency conversion |
| `JsonParser` / `EntityJsonSerializer` | Read and write JSON |
| `EncryptText` / `TranslateText` | Encrypt, translate |

### Communication, sharing, miscellaneous

| Activity | Purpose |
|---|---|
| `Class.EmailToTeam`, `Class.SendEmailToUsersInRole`, `Class.SendEmailFromTemplateToUsersInRole` | E-mails to teams and roles |
| `Class.EntityAttachmentToEmail`, `Class.SalesLiteratureToEmail` | Add attachments |
| `ShareRecordWithUser` / `ShareRecordWithTeam` (+ Unshare) | Set sharing |
| `ShareSecuredField` | Share field security |
| `Class.ExecuteWorkflowByID`, `ExecuteWorkflowForRecordsinQuery` | Start workflows dynamically or for query results |
| `Class.GetInitiatingUser` | Determine the initiating user |
| `RetrieveUserBUDefaultTeam` | Default team of the business unit |
| `GetAppRecordUrl`, `GetAppModuleID`, `EntityMobileDeepLink` | Links into the app |
| `GeoCodeAddress` | Geocode an address |
| `OrgDBSettingsRetrieve` / `OrgDBSettingsUpdate` | Organization settings |
| `Class.PickFromQueue`, `QueueItemCount`, `ApplyRoutingRule`, `Class.ResolveCase`, `QualifyLead`, `WinQuote`, `CreateQuoteFromOpportunity`, `CalculatePrice` | Case and sales automation |

### Complete list

87 activities, retrievable with:

```
GET /api/data/v9.2/plugintypes?$select=name,customworkflowactivityinfo
    &$filter=workflowactivitygroupname ne null and contains(assemblyname,'msdyncrmWorkflowTools')
```

or via `workflow_list_activities` with `nameFilter: "msdyncrmWorkflowTools"`.

## 3. Checking a code activity's outputs in conditions

An activity such as `CheckUserInTeam` delivers its result into a variable
`<StepId><Parameter>_localParameter`. To branch on it, this variable is used as the `Operand` in
`EvaluateCondition` — instead of a `GetEntityProperty` result:

```xml
<InArgument x:TypeArguments="mxsq:ConditionOperator" x:Key="ConditionOperator">Equal</InArgument>
<InArgument x:TypeArguments="s:Object[]" x:Key="Parameters">[New Object() { ConditionStepN_2 }]</InArgument>
<InArgument x:TypeArguments="x:Object" x:Key="Operand">[CustomActivityStepMisUserInTeam_localParameter]</InArgument>
```

In the definition model: `conditions[].stepOutput` instead of `conditions[].attribute`. The **bare
parameter name** (`"isUserInTeam"`) is enough there — the step id is only assigned by the builder, so
it cannot be known when writing. The qualified form `"CustomActivityStep4.isUserInTeam"` is accepted
as well; when reading back, the server returns that form.

The variable's type is the **parameter type**, not `x:String` — for `CheckUserInTeam`, therefore,
`x:Boolean` with `Default="False"`. The builder reads it from the metadata; a hard-coded `x:String`
makes activation fail with `InvalidPropertyBag`.

## 4. Reading fields of the record an activity returns

Activities such as `Class.GetInitiatingUser`, `RetrieveUserBUDefaultTeam` or `Class.PickFromQueue`
return a **reference**, not a record. To get at its fields, the record is loaded — in the definition
model, `fromStepOutput` on the value is all it takes:

```json
{ "kind": "field", "dataType": "String",
  "fields": ["systemuser.internalemailaddress"],
  "fromStepOutput": "InitiatingUser" }
```

The server generates the load itself (an `If` around `RetrieveEntity`, like the designer) and stores
the record under `CreatedEntities("<StepId><Parameter>_entity")`. So you need **no** second activity
and no child workflow to use, for example, the name, phone and e-mail of the executing user.

The same field works in conditions — typically as a guard beforehand:

```json
{ "fromStepOutput": "InitiatingUser", "entity": "systemuser",
  "attribute": "internalemailaddress", "operator": "Null" }
```

For a record that the workflow itself **creates** (`createRecord`), the counterpart is called
`fromStep` — with the entity as the value, e.g. `"fromStep": "email"`, to pass the `activityid` of the
e-mail just created to an activity such as `Class.SendEmail`.

## 5. CRM types of the Workflow Tools parameters

The activities use CRM-specific parameter types, not the .NET types:

| `TypeName` in the metadata XML | Meaning | `dataType` in the model |
|---|---|---|
| `Microsoft.Crm.Sdk.Lookup` | Record reference; valid target entities are listed in `EntityNames` | `EntityReference` |
| `Microsoft.Crm.Sdk.CrmBoolean` | Yes/No | `Boolean` |
| `Microsoft.Crm.Sdk.CrmDateTime` | Date/time | `DateTime` |
| `Microsoft.Crm.Sdk.CrmDecimal` / `CrmFloat` / `CrmMoney` | Numbers | `Decimal` / `Double` / `Money` |
| `Microsoft.Crm.Sdk.Picklist` | Option set | `OptionSetValue` |
| `System.String` | Text | `String` |

`EntityNames` of a lookup parameter names the allowed target entities (e.g. `team`, `systemuser`) — a
lookup to a different entity is rejected at runtime. For fixed references (`"team:<guid>"`) validation
checks this in advance (`WF088`); if the value comes from a field, it cannot know.

You do not have to make this mapping yourself: `workflow_get_activity_parameters` returns the matching
`dataType` for every parameter, and a wrong one is reported as `WF086` before writing.
