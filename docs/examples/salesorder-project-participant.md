# Example: deriving the project participant on a sales order

A worked example of three builder capabilities that only appear together in a non-trivial workflow:
reading fields across a lookup, branching on an activity output, and writing an `EntityReference`
literal.

The requirement:

1. On **creation of a sales order**, check whether `sample_projectparticipant1` is already filled.
2. If not, check whether an opportunity is linked and `sample_salesrep` is filled there.
3. If so, take that value.
4. Otherwise, check whether `customerid` (customer/account) is filled.
5. If so, determine the owner of that account (`account.ownerid`).
6. If that owner is a member of the sales team, write them into `sample_projectparticipant1`.

## Fields involved

| Purpose | Field | Type |
|---|---|---|
| Target | `salesorder.sample_projectparticipant1` | Lookup |
| Opportunity | `salesorder.opportunityid` | Lookup → opportunity |
| Participant there | `opportunity.sample_salesrep` | Lookup |
| Customer/account | `salesorder.customerid` | Customer (account **or** contact) |
| Account (pure account) | `salesorder.accountid` | Lookup → account |
| Owner of the account | `account.ownerid` | Owner |
| Team | sales team | `11112222-3333-4444-5555-666677778888` |

> [!NOTE]
> `customerid` is a **Customer** field and can point at `account` *or* `contact`. For step 5,
> `accountid` is the more reliable entry point, because the `related_…` access needs a fixed target
> entity. Alternatively, branch on `customeridtype`.

## The definition

```json
{
  "primaryEntity": "salesorder",
  "steps": [
    {
      "kind": "condition",
      "description": "Participant still empty",
      "conditions": [
        { "attribute": "sample_projectparticipant1", "operator": "Null" }
      ],
      "then": [
        {
          "kind": "condition",
          "description": "Opportunity with a participant exists",
          "conditions": [
            { "attribute": "opportunityid", "operator": "NotNull" },
            { "entity": "opportunity", "via": "opportunityid",
              "attribute": "sample_salesrep", "operator": "NotNull" }
          ],
          "logicalOperator": "And",
          "then": [
            {
              "kind": "updateRecord",
              "description": "Take the participant from the opportunity",
              "attributes": [
                { "attribute": "sample_projectparticipant1",
                  "value": { "kind": "field", "dataType": "EntityReference",
                             "fields": ["opportunity.sample_salesrep"], "via": "opportunityid" } }
              ]
            }
          ],
          "else": [
            {
              "kind": "condition",
              "description": "Account present on the order",
              "conditions": [
                { "attribute": "accountid", "operator": "NotNull" }
              ],
              "then": [
                {
                  "kind": "customActivity",
                  "description": "Is the account owner on the sales team",
                  "assemblyQualifiedName": "msdyncrmWorkflowTools.CheckUserInTeam, msdyncrmWorkflowTools, Version=1.0.62.1, Culture=neutral, PublicKeyToken=416e876b9bee261e",
                  "inputs": {
                    "Team": { "kind": "literal", "dataType": "EntityReference",
                              "literal": "team:11112222-3333-4444-5555-666677778888" },
                    "User": { "kind": "field", "dataType": "EntityReference",
                              "fields": ["account.ownerid"], "via": "accountid" }
                  },
                  "outputs": ["isUserInTeam"]
                },
                {
                  "kind": "condition",
                  "description": "Owner is on the sales team",
                  "conditions": [
                    { "stepOutput": "isUserInTeam",
                      "operator": "Equal",
                      "value": { "kind": "literal", "dataType": "Boolean", "literal": "true" } }
                  ],
                  "then": [
                    {
                      "kind": "updateRecord",
                      "description": "Set the account owner as the participant",
                      "attributes": [
                        { "attribute": "sample_projectparticipant1",
                          "value": { "kind": "field", "dataType": "EntityReference",
                                     "fields": ["account.ownerid"], "via": "accountid" } }
                      ]
                    }
                  ]
                }
              ]
            }
          ]
        }
      ]
    }
  ]
}
```

Then:

```
workflow_update(id, {"triggeroncreate": true, "createstage": 40})
workflow_set_state(id, activate=true)
```

## The three builder capabilities this needs

| # | Capability | How it works |
|---|---|---|
| 1 | reading fields of related records | `via` (the lookup attribute) on `WorkflowCondition` and `WorkflowValue`; the builder emits `Entity="[InputEntities("related_<via>#<entity>")]"` with `EntityName="<entity>"`. `WF079`/`WF122` require `via` as soon as the entity is not the primary entity. |
| 2 | a condition on an activity output | `WorkflowCondition.StepOutput` as an alternative to `Attribute`; the `_localParameter` variable becomes the `Operand`, without `GetEntityProperty`. |
| 3 | `EntityReference` literals | written as `"<entity>:<guid>"`; two `CreateCrmType` steps (Guid with marker `UniqueIdentifier`, then the reference with an empty label and marker `Lookup`). |

On top of that came what actually blocked activation: argument types read from
`plugintype.customworkflowactivityinfo` rather than hard-coded `x:String`, and the naming scheme of
the converted helper variables — see [`custom-activity-xaml-reference.md`](custom-activity-xaml-reference.md).

## Reading it back

`workflow_get_definition` reconstructs the **inputs** of the `customActivity` step as well: the fixed
team comes back as `"team:<guid>"`, the account owner as a field reference with `via`. The workflow
therefore stays editable through `workflow_set_definition`.

This is evidenced against real designer XAML, not only against XAML the builder produced itself:
`DesignerXamlReadingTests` reads the fixture `designer-custom-activity.xaml` — a step configured by
hand in the designer — and rebuilds the same XAML from it. `FixtureSurveyTests` extends that to all
committed fixtures, including a 130 KB workflow with a six-case branch chain, so the read → change →
write path is covered for grown workflows too.
