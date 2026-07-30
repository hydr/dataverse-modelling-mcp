namespace Dataverse.Tests.Integration;

using System.Text.RegularExpressions;
using Dataverse.Core.Workflows;
using Dataverse.Tests.Infrastructure;
using NUnit.Framework;

/// <summary>
/// Writes one generated feature per workflow and tries to ACTIVATE it. Activation is where Dataverse
/// validates the structure (0x80048455 with an ErrorMap), so this is the only way to learn which
/// constructs are actually accepted. Findings feed back into WorkflowDefinitionValidator.
/// </summary>
[TestFixture]
[Category("Integration")]
[Explicit("Diagnostic probe — creates and deletes workflows in the target environment.")]
public sealed class WorkflowActivationProbeTests : IntegrationTestBase
{
    private const string SalesTeamId = "a0000001-0000-4000-8000-000000000001";

    private const string CheckUserInTeam =
        "msdyncrmWorkflowTools.CheckUserInTeam, msdyncrmWorkflowTools, Version=1.0.62.1, " +
        "Culture=neutral, PublicKeyToken=416e876b9bee261e";

    private const string GetRecordId =
        "msdyncrmWorkflowTools.GetRecordID, msdyncrmWorkflowTools, Version=1.0.62.1, " +
        "Culture=neutral, PublicKeyToken=416e876b9bee261e";

    private static WorkflowStep Update(string attribute, WorkflowValue value, string? description = null) => new()
    {
        Kind = WorkflowStepKind.UpdateRecord,
        Description = description,
        Attributes = [new WorkflowAttributeAssignment { Attribute = attribute, Value = value }]
    };

    /// <summary>Feature matrix. Each case is written to its own workflow and activated.</summary>
    private static IEnumerable<(string Name, string Entity, WorkflowDefinition Definition)> Cases()
    {
        // Baseline — known to activate.
        yield return ("A baseline: condition + update", "salesorder", new WorkflowDefinition
        {
            PrimaryEntity = "salesorder",
            Steps =
            [
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.Condition,
                    Conditions = [new WorkflowCondition { Attribute = "sample_projektbeteiligter1", Operator = "Null" }],
                    Then = [Update("description", new WorkflowValue { Literal = "x" })]
                }
            ]
        });

        // Nested conditions.
        yield return ("B nested conditions", "salesorder", new WorkflowDefinition
        {
            PrimaryEntity = "salesorder",
            Steps =
            [
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.Condition,
                    Conditions = [new WorkflowCondition { Attribute = "sample_projektbeteiligter1", Operator = "Null" }],
                    Then =
                    [
                        new WorkflowStep
                        {
                            Kind = WorkflowStepKind.Condition,
                            Conditions = [new WorkflowCondition { Attribute = "opportunityid", Operator = "NotNull" }],
                            Then = [Update("description", new WorkflowValue { Literal = "y" })]
                        }
                    ]
                }
            ]
        });

        // Related read in a condition.
        yield return ("C related read in condition", "salesorder", new WorkflowDefinition
        {
            PrimaryEntity = "salesorder",
            Steps =
            [
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.Condition,
                    Conditions =
                    [
                        new WorkflowCondition
                        {
                            Entity = "opportunity", Via = "opportunityid",
                            Attribute = "sample_salesma", Operator = "NotNull"
                        }
                    ],
                    Then = [Update("description", new WorkflowValue { Literal = "z" })]
                }
            ]
        });

        // Related read as a field value (EntityReference).
        yield return ("D related read as value", "salesorder", new WorkflowDefinition
        {
            PrimaryEntity = "salesorder",
            Steps =
            [
                Update("sample_projektbeteiligter1", new WorkflowValue
                {
                    Kind = WorkflowValueKind.Field,
                    DataType = "EntityReference",
                    Fields = ["opportunity.sample_salesma"],
                    Via = "opportunityid"
                })
            ]
        });

        // Two conditions folded with a logical operator.
        yield return ("E two conditions with And", "salesorder", new WorkflowDefinition
        {
            PrimaryEntity = "salesorder",
            Steps =
            [
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.Condition,
                    LogicalOperator = "And",
                    Conditions =
                    [
                        new WorkflowCondition { Attribute = "opportunityid", Operator = "NotNull" },
                        new WorkflowCondition { Attribute = "accountid", Operator = "NotNull" }
                    ],
                    Then = [Update("description", new WorkflowValue { Literal = "e" })]
                }
            ]
        });

        // Custom activity with plain string parameters — the simplest possible signature.
        yield return ("F custom activity, string in and out", "salesorder", new WorkflowDefinition
        {
            PrimaryEntity = "salesorder",
            Steps =
            [
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.CustomActivity,
                    AssemblyQualifiedName = GetRecordId,
                    Inputs = new Dictionary<string, WorkflowValue>
                    {
                        ["RecordURL"] = new() { DataType = "String", Literal = "https://example.invalid" }
                    },
                    Outputs = ["RecordID"]
                }
            ]
        });

        // Custom activity with a fixed lookup literal.
        yield return ("G custom activity + lookup literal", "salesorder", new WorkflowDefinition
        {
            PrimaryEntity = "salesorder",
            Steps =
            [
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.CustomActivity,
                    AssemblyQualifiedName = CheckUserInTeam,
                    Inputs = new Dictionary<string, WorkflowValue>
                    {
                        ["Team"] = new()
                        {
                            Kind = WorkflowValueKind.Literal,
                            DataType = "EntityReference",
                            Literal = $"team:{SalesTeamId}"
                        }
                    },
                    Outputs = ["isUserInTeam"]
                }
            ]
        });

        // Condition on the activity output.
        yield return ("H condition on step output", "salesorder", new WorkflowDefinition
        {
            PrimaryEntity = "salesorder",
            Steps =
            [
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.CustomActivity,
                    AssemblyQualifiedName = CheckUserInTeam,
                    Inputs = new Dictionary<string, WorkflowValue>
                    {
                        ["Team"] = new()
                        {
                            Kind = WorkflowValueKind.Literal,
                            DataType = "EntityReference",
                            Literal = $"team:{SalesTeamId}"
                        }
                    },
                    Outputs = ["isUserInTeam"]
                },
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.Condition,
                    Conditions =
                    [
                        new WorkflowCondition
                        {
                            StepOutput = "isUserInTeam",
                            Operator = "Equal",
                            Value = new WorkflowValue { DataType = "Boolean", Literal = "true" }
                        }
                    ],
                    Then = [Update("description", new WorkflowValue { Literal = "h" })]
                }
            ]
        });

        // Same custom activity, but nested inside a condition branch (as the designer produces it).
        yield return ("I custom activity inside branch", "salesorder", new WorkflowDefinition
        {
            PrimaryEntity = "salesorder",
            Steps =
            [
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.Condition,
                    Conditions = [new WorkflowCondition { Attribute = "accountid", Operator = "NotNull" }],
                    Then =
                    [
                        new WorkflowStep
                        {
                            Kind = WorkflowStepKind.CustomActivity,
                            AssemblyQualifiedName = CheckUserInTeam,
                            Inputs = new Dictionary<string, WorkflowValue>
                            {
                                ["Team"] = new()
                                {
                                    Kind = WorkflowValueKind.Literal,
                                    DataType = "EntityReference",
                                    Literal = $"team:{SalesTeamId}"
                                }
                            },
                            Outputs = ["isUserInTeam"]
                        }
                    ]
                }
            ]
        });

        // Every parameter of the activity supplied, exactly as the designer writes it. If this
        // activates while G does not, an incomplete property bag is what activation rejects.
        yield return ("K custom activity, all parameters set", "salesorder", new WorkflowDefinition
        {
            PrimaryEntity = "salesorder",
            Steps =
            [
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.CustomActivity,
                    AssemblyQualifiedName = CheckUserInTeam,
                    Inputs = new Dictionary<string, WorkflowValue>
                    {
                        ["Team"] = new()
                        {
                            Kind = WorkflowValueKind.Literal,
                            DataType = "EntityReference",
                            Literal = $"team:{SalesTeamId}"
                        },
                        ["User"] = new()
                        {
                            Kind = WorkflowValueKind.Field,
                            DataType = "EntityReference",
                            Fields = ["account.ownerid"],
                            Via = "accountid"
                        }
                    },
                    Outputs = ["isUserInTeam"]
                }
            ]
        });

        // Stage on root level (also a Composite) — is that accepted?
        yield return ("J stage on root", "salesorder", new WorkflowDefinition
        {
            PrimaryEntity = "salesorder",
            Steps =
            [
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.Stage,
                    Description = "Phase 1",
                    Children = [Update("description", new WorkflowValue { Literal = "j" })]
                }
            ]
        });
    }

    [Test]
    public async Task Probe_WhichConstructsActivate()
    {
        foreach (var (name, entity, definition) in Cases())
        {
            Guid id = Guid.Empty;
            try
            {
                id = await WorkflowService.CreateAsync(OrgUrl, $"ZZ Probe {Guid.NewGuid():N}", entity);

                var save = await WorkflowAuthoringService.SetDefinitionAsync(OrgUrl, id, definition);
                if (!save.Applied)
                {
                    TestContext.Out.WriteLine($"RESULT {name}: NOT WRITTEN -> " +
                        string.Join(" | ", save.Validation.Issues.Select(i => $"{i.Code} {i.Path}")));
                    continue;
                }

                await WorkflowService.UpdateAsync(OrgUrl, id, new Dictionary<string, object?>
                {
                    ["triggeroncreate"] = true,
                    ["createstage"] = 40
                });

                await WorkflowService.SetStateAsync(OrgUrl, id, activate: true);
                TestContext.Out.WriteLine($"RESULT {name}: ACTIVATED");
            }
            catch (Exception ex)
            {
                TestContext.Out.WriteLine($"RESULT {name}: FAILED -> {Compact(ex.Message)}");
            }
            finally
            {
                if (id != Guid.Empty)
                {
                    try { await WorkflowService.SetStateAsync(OrgUrl, id, activate: false); } catch { }
                    try { await WorkflowService.DeleteAsync(OrgUrl, id); } catch { }
                }
            }
        }
    }

    /// <summary>Keeps the error code and the ErrorMap, drops the rest.</summary>
    private static string Compact(string message)
    {
        var code = Regex.Match(message, @"0x[0-9a-fA-F]{8}").Value;
        var map = Regex.Match(message, @"ErrorMap Details: \{([^}]*)\}");
        return map.Success ? $"{code} {map.Groups[1].Value}" : $"{code} {message[..Math.Min(160, message.Length)]}";
    }

    /// <summary>Leaves a workflow with the failing custom-activity XAML in place for inspection.</summary>
    [Test]
    public async Task Probe_LeaveCustomActivityWorkflowForInspection()
    {
        var id = await WorkflowService.CreateAsync(OrgUrl, "ZZ Inspect CustomActivity", "salesorder",
            "Bleibt absichtlich stehen: CustomActivity-XAML, das die Aktivierung ablehnt");

        var definition = new WorkflowDefinition
        {
            PrimaryEntity = "salesorder",
            Steps =
            [
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.CustomActivity,
                    Description = "Ist der Firmenbesitzer im Salesteam",
                    AssemblyQualifiedName = CheckUserInTeam,
                    Inputs = new Dictionary<string, WorkflowValue>
                    {
                        ["Team"] = new()
                        {
                            Kind = WorkflowValueKind.Literal,
                            DataType = "EntityReference",
                            Literal = $"team:{SalesTeamId}"
                        }
                    },
                    Outputs = ["isUserInTeam"]
                }
            ]
        };

        var save = await WorkflowAuthoringService.SetDefinitionAsync(OrgUrl, id, definition);
        TestContext.Out.WriteLine($"INSPECT applied={save.Applied} id={id}");
        TestContext.Out.WriteLine($"INSPECT url=https://contoso-dev.crm4.dynamics.com/sfa/workflow/edit.aspx?appSolutionId=%7bFD140AAF-4DF4-11DD-BD17-0019B9312238%7d&id={id}");
    }
}
