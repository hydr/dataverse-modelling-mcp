# Reference: a code activity with lookup inputs (as the designer writes it)

Template taken from a workflow whose single step was configured by hand in the designer:
`msdyncrmWorkflowTools.CheckUserInTeam` with `Team` set to a fixed team and `User` set to the owner of
the related account. The committed copy is the fixture
`tests/Dataverse.Tests/Workflows/Fixtures/designer-custom-activity.xaml`.

## Target shape

```xml
<!-- Workflow level: type and default follow the PARAMETER TYPE -->
<mxswa:Workflow.Variables>
  <Variable x:TypeArguments="x:Boolean" Default="False"
            Name="CustomActivityStep1isUserInTeam_localParameter" />
</mxswa:Workflow.Variables>

<mxswa:ActivityReference AssemblyQualifiedName="…Activities.Composite, …"
                        DisplayName="CustomActivityStep1: Is the account owner on the sales team?">
  <mxswa:ActivityReference.Properties>
    <!-- Helper variables live INSIDE the composite -->
    <sco:Collection x:TypeArguments="Variable" x:Key="Variables">
      <Variable x:TypeArguments="x:Object" Name="CustomActivityStep1_1" />
      <Variable x:TypeArguments="x:Object" Name="CustomActivityStep1_2" />
      <Variable x:TypeArguments="x:Object" Name="CustomActivityStep1_1_converted" />
      <Variable x:TypeArguments="x:Object" Name="CustomActivityStep1_3" />
      <Variable x:TypeArguments="x:Object" Name="CustomActivityStep1_4" />
      <Variable x:TypeArguments="x:Object" Name="CustomActivityStep1_3_converted" />
    </sco:Collection>
    <sco:Collection x:TypeArguments="Activity" x:Key="Activities">

      <!-- (1) Fixed lookup: Guid with marker "UniqueIdentifier", TargetType mxs:EntityReference -->
      … EvaluateExpression: CreateCrmType,
        Parameters = [New Object() { …WorkflowPropertyType.Guid, "11112222-3333-4444-5555-666677778888", "UniqueIdentifier" }],
        TargetType = mxs:EntityReference, Result = [CustomActivityStep1_2]

      <!-- (2) the reference built from it: the label is EMPTY -->
      … EvaluateExpression: CreateCrmType,
        Parameters = [New Object() { …WorkflowPropertyType.EntityReference, "team", "", CustomActivityStep1_2, "Lookup" }],
        TargetType = mxs:EntityReference, Result = [CustomActivityStep1_1]

      <!-- (3) convert to the .NET type for the argument -->
      … ConvertCrmXrmTypes: Value=[CustomActivityStep1_1], TargetType=mxs:EntityReference
        → [CustomActivityStep1_1_converted]

      <!-- (4) read the related field; TargetType is the TARGET type, not null -->
      <mxswa:GetEntityProperty Attribute="ownerid"
          Entity='[InputEntities("related_sample_customeraccount#account")]' EntityName="account"
          Value="[CustomActivityStep1_4]">
        <mxswa:GetEntityProperty.TargetType>
          <InArgument x:TypeArguments="s:Type">
            <mxswa:ReferenceLiteral x:TypeArguments="s:Type" Value="mxs:EntityReference" />
          </InArgument>
        </mxswa:GetEntityProperty.TargetType>
      </mxswa:GetEntityProperty>

      <!-- (5) SelectFirstNonNull + Convert as usual → [CustomActivityStep1_3_converted] -->

      <!-- (6) the activity itself: argument types = parameter types -->
      <mxswa:ActivityReference
          AssemblyQualifiedName="msdyncrmWorkflowTools.CheckUserInTeam, msdyncrmWorkflowTools, Version=1.0.62.1, Culture=neutral, PublicKeyToken=416e876b9bee261e"
          DisplayName="CustomActivityStep1: Is the account owner on the sales team?">
        <mxswa:ActivityReference.Arguments>
          <InArgument  x:TypeArguments="mxs:EntityReference" x:Key="Team">[DirectCast(CustomActivityStep1_1_converted, Microsoft.Xrm.Sdk.EntityReference)]</InArgument>
          <InArgument  x:TypeArguments="mxs:EntityReference" x:Key="User">[DirectCast(CustomActivityStep1_3_converted, Microsoft.Xrm.Sdk.EntityReference)]</InArgument>
          <OutArgument x:TypeArguments="x:Boolean" x:Key="isUserInTeam">[CustomActivityStep1isUserInTeam_localParameter]</OutArgument>
        </mxswa:ActivityReference.Arguments>
      </mxswa:ActivityReference>
    </sco:Collection>
  </mxswa:ActivityReference.Properties>
</mxswa:ActivityReference>
```

## Builder deviations that were fixed

All five are implemented; the feature matrix in `WorkflowActivationProbeTests` activates the code
activity with a lookup literal, with every parameter, and inside a branch.

| # | Should be | Was | Effect |
|---|---|---|---|
| 1 | output variable typed with the **parameter type** (`x:Boolean`, `Default="False"`) | hard-coded `x:String`, `Default="[Nothing]"` | type conflict |
| 2 | `OutArgument x:TypeArguments` = **parameter type** | hard-coded `x:String` | type conflict |
| 3 | Guid literal marker `"UniqueIdentifier"` | `"Key"` | invalid property bag |
| 4 | EntityReference label **empty** | display name | harmless, but different |
| 5 | `GetEntityProperty.TargetType` = target type (`mxs:EntityReference`) | `x:Null` on conditions, target type on values | already correct for lookup values |

What was right all along: helper variables belong **inside** the composite's `Variables` collection.

## The actual cause: how helper variables are named

Even with the correct types, `InvalidPropertyBag` persisted while the same XAML from the designer
activated. The only remaining difference was the **name of the converted variable**:

```
Designer:      _1, _2, _1_converted        ← _1 exists for _1_converted
Builder (old): _1, _2, _3_converted        ← there is no _3
```

`Convert` had consumed a fresh index instead of appending to the source variable. Activation
reconstructs the step from exactly these names — a `_3_converted` without a declared `_3` is a broken
property bag to it. Fitting that: the designer reserves the **result** slot first and the source
second (`_1` = reference, `_2` = Guid), not the other way round.

> [!IMPORTANT]
> This is the same mechanism as `0x80045037`: names are structure here. A self-check that only asks
> "is every referenced variable declared?" does not catch it — the problem was the *unused* base
> variable, not a missing reference.

## Other places where the marker or the type name decides

The same class of error — the XAML looks plausible and is rejected with `0x80045040` on write:

| Construct | Wrong | Right |
|---|---|---|
| `CreateCrmType` for an option set | marker `"OptionSetValue"` | marker **`"Picklist"`** |
| `CreateCrmType` for a Guid | marker `"Guid"` | marker **`"UniqueIdentifier"`** |
| `CreateCrmType` for a reference | marker `"EntityReference"` | marker **`"Lookup"`** (five parts) |
| date type argument | `x:DateTime` | **`s:DateTime`** — the XAML 2006 namespace has no DateTime |
| `RetrieveCurrentTime` | `TargetType` = target type | **`x:Null`**, parameter `[New Object() {  }]` with `xml:space="preserve"` |
| `Add` (concatenation) | `TargetType` = target type | **`x:Null`** — the parts decide the type |

So the marker is the **CRM attribute type**, not the name of the `WorkflowPropertyType`. Evidenced by
`ValueKindWriteProbeTests`, which writes each value form on its own.

Two more things surfaced only when activating a whole workflow — both `0x80040216`:

| Construct | Wrong | Right |
|---|---|---|
| comma inside a constant | `"1,2,3"` | **`"1&#44;2&#44;3"`** — the parameter array is split on commas before string literals are read |
| persistence point in a real-time workflow | `<Persist />` | **omit it** — only background workflows may persist |
| `fromStep` reference after reading back | the old step id | resolve via the **entity** — renumbering invalidates the id, and the reference then points at a record that is never created |

The last one was the most stubborn: the XAML was well-formed, the self-check content, the validation
silent. It was found by bisecting — writing subsets of a large workflow and activating each one until
only the three code activities carrying the `fromStep` reference were left.

## Validation as implemented

The parameter type is read from `plugintype.customworkflowactivityinfo` (`WorkflowActivityCatalog`)
and checked against the model before the PATCH:

| Check | Code |
|---|---|
| Does the parameter name (`DependencyPropertyName`) exist? | `WF085` (input), `WF089` (output) |
| Does `dataType` match the parameter's `TypeName`? | `WF086` |
| Is every input marked `Required=true` supplied? | `WF087` |
| Is a lookup's target entity listed in `EntityNames`? | `WF088` |

Without reachable metadata (offline use, unknown activity) these four checks are skipped — activation
then reports the error, as it did before.
