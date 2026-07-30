namespace Dataverse.Tests.Workflows;

using Dataverse.Core.Workflows;
using NUnit.Framework;

[TestFixture]
public sealed class WorkflowXamlParserTests
{
    /// <summary>
    /// Real XAML produced by the Dataverse designer (workflow "Anruf wieder öffnen", trimmed of
    /// namespace noise but structurally identical). A status-change step sits directly under
    /// mxswa:Workflow without a Sequence wrapper.
    /// </summary>
    private const string DesignerSetStateXaml = """
        <?xml version="1.0" encoding="utf-16"?>
        <Activity x:Class="XrmWorkflowcda9b0fa18d844418e780ee6b9ef5087"
                  xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                  xmlns:mxs="clr-namespace:Microsoft.Xrm.Sdk;assembly=Microsoft.Xrm.Sdk"
                  xmlns:mxswa="clr-namespace:Microsoft.Xrm.Sdk.Workflow.Activities;assembly=Microsoft.Xrm.Sdk.Workflow"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <mxswa:Workflow>
            <mxswa:SetState DisplayName="SetStateStep1" Entity="[InputEntities(&quot;primaryEntity&quot;)]"
                            EntityId="[InputEntities(&quot;primaryEntity&quot;).Id]" EntityName="phonecall">
              <mxswa:SetState.State>
                <InArgument x:TypeArguments="mxs:OptionSetValue">
                  <mxswa:ReferenceLiteral x:TypeArguments="mxs:OptionSetValue">
                    <mxs:OptionSetValue ExtensionData="{x:Null}" Value="0" />
                  </mxswa:ReferenceLiteral>
                </InArgument>
              </mxswa:SetState.State>
              <mxswa:SetState.Status>
                <InArgument x:TypeArguments="mxs:OptionSetValue">
                  <mxswa:ReferenceLiteral x:TypeArguments="mxs:OptionSetValue">
                    <mxs:OptionSetValue ExtensionData="{x:Null}" Value="1" />
                  </mxswa:ReferenceLiteral>
                </InArgument>
              </mxswa:SetState.Status>
            </mxswa:SetState>
          </mxswa:Workflow>
        </Activity>
        """;

    [Test]
    public void Parse_EmptyXaml_IsUnderstoodAsEmptyDraft()
    {
        var result = WorkflowXamlParser.Parse(null, "lead");

        Assert.That(result.FullyUnderstood, Is.True);
        Assert.That(result.Definition.Steps, Is.Empty);
        Assert.That(result.Notes, Is.Not.Empty);
    }

    [Test]
    public void Parse_MalformedXaml_IsReportedNotThrown()
    {
        var result = WorkflowXamlParser.Parse("<Activity><oops>", "lead");

        Assert.That(result.FullyUnderstood, Is.False);
        Assert.That(result.Unrecognised, Is.Not.Empty);
    }

    [Test]
    public void Parse_DesignerSetStateWorkflow_IsFullyUnderstood()
    {
        var result = WorkflowXamlParser.Parse(DesignerSetStateXaml, "phonecall");

        Assert.That(result.FullyUnderstood, Is.True,
            "unrecognised: " + string.Join(", ", result.Unrecognised));
        Assert.That(result.Definition.Steps, Has.Count.EqualTo(1));

        var step = result.Definition.Steps[0];
        Assert.That(step.Kind, Is.EqualTo(WorkflowStepKind.ChangeStatus));
        Assert.That(step.StepId, Is.EqualTo("SetStateStep1"));
        Assert.That(step.Entity, Is.EqualTo("phonecall"));
        Assert.That(step.State, Is.EqualTo(0));
        Assert.That(step.Status, Is.EqualTo(1));
    }

    [Test]
    public void SplitDisplayName_SeparatesStepIdFromDescription()
    {
        Assert.That(WorkflowXamlParser.SplitDisplayName("UpdateStep6: Update Lead.Domain"),
            Is.EqualTo(("UpdateStep6", "Update Lead.Domain")));
        Assert.That(WorkflowXamlParser.SplitDisplayName("UpdateStep6"),
            Is.EqualTo(("UpdateStep6", (string?)null)));
    }

    [Test]
    public void ExtractCreateCrmTypeLiteral_ReadsTheConstant()
    {
        const string parameters =
            "[New Object() { Microsoft.Xrm.Sdk.Workflow.WorkflowPropertyType.String, \"RE-Test Position\", \"String\" }]";

        Assert.That(WorkflowXamlParser.ExtractCreateCrmTypeLiteral(parameters), Is.EqualTo("RE-Test Position"));
    }

    [Test]
    public void VariablesIn_ReadsTheParameterArray()
    {
        const string expression = "[New Object() { UpdateStep3_2, UpdateStep3_3, UpdateStep3_4 }]";

        Assert.That(WorkflowXamlParser.VariablesIn(expression),
            Is.EqualTo(new[] { "UpdateStep3_2", "UpdateStep3_3", "UpdateStep3_4" }));
    }

    // ---------------------------------------------------------------- roundtrips

    [Test]
    public void Roundtrip_UpdateWithLiteral_PreservesAttributeAndValue()
    {
        var definition = new WorkflowDefinition
        {
            PrimaryEntity = "lead",
            Steps =
            [
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.UpdateRecord,
                    Description = "Titel setzen",
                    Attributes =
                    [
                        new WorkflowAttributeAssignment
                        {
                            Attribute = "jobtitle",
                            Value = new WorkflowValue { Literal = "Chef" }
                        }
                    ]
                }
            ]
        };

        var xaml = WorkflowXamlBuilder.Build(definition).Xaml;
        var parsed = WorkflowXamlParser.Parse(xaml, "lead");

        Assert.That(parsed.FullyUnderstood, Is.True,
            "unrecognised: " + string.Join(", ", parsed.Unrecognised));
        Assert.That(parsed.Definition.Steps, Has.Count.EqualTo(1));

        var step = parsed.Definition.Steps[0];
        Assert.That(step.Kind, Is.EqualTo(WorkflowStepKind.UpdateRecord));
        Assert.That(step.StepId, Is.EqualTo("UpdateStep1"));
        Assert.That(step.Description, Is.EqualTo("Titel setzen"));
        Assert.That(step.Attributes, Has.Count.EqualTo(1));
        Assert.That(step.Attributes![0].Attribute, Is.EqualTo("jobtitle"));
        Assert.That(step.Attributes[0].Value.Literal, Is.EqualTo("Chef"));
    }

    [Test]
    public void Roundtrip_RelatedReadAndStepOutputCondition_SurviveTheReading()
    {
        // Both were invisible to the parser before: a related read came back without its 'via', and a
        // condition on an activity output came back as an attribute named "?". Reading a workflow and
        // writing it again would have silently changed its logic.
        var definition = new WorkflowDefinition
        {
            PrimaryEntity = "salesorder",
            Steps =
            [
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.CustomActivity,
                    AssemblyQualifiedName = CustomActivityFixture.CheckUserInTeam,
                    Inputs = new Dictionary<string, WorkflowValue>
                    {
                        ["Team"] = new()
                        {
                            DataType = "EntityReference",
                            Literal = "team:a0000001-0000-4000-8000-000000000001"
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
                        },
                        new WorkflowCondition
                        {
                            Entity = "opportunity", Via = "opportunityid",
                            Attribute = "sample_salesma", Operator = "NotNull"
                        }
                    ],
                    Then =
                    [
                        new WorkflowStep
                        {
                            Kind = WorkflowStepKind.UpdateRecord,
                            Attributes =
                            [
                                new WorkflowAttributeAssignment
                                {
                                    Attribute = "sample_projektbeteiligter1",
                                    Value = new WorkflowValue
                                    {
                                        Kind = WorkflowValueKind.Field,
                                        DataType = "EntityReference",
                                        Fields = ["opportunity.sample_salesma"],
                                        Via = "opportunityid"
                                    }
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        var xaml = WorkflowXamlBuilder.Build(definition, null, CustomActivityFixture.Catalog()).Xaml;
        var parsed = WorkflowXamlParser.Parse(xaml, "salesorder");

        var condition = parsed.Definition.Steps[1];
        Assert.That(condition.Conditions![0].StepOutput, Is.EqualTo("CustomActivityStep1.isUserInTeam"));
        Assert.That(condition.Conditions[1].Via, Is.EqualTo("opportunityid"));
        Assert.That(condition.Conditions[1].Entity, Is.EqualTo("opportunity"));

        var value = condition.Then![0].Attributes![0].Value;
        Assert.That(value.Fields, Is.EqualTo(new[] { "opportunity.sample_salesma" }));
        Assert.That(value.Via, Is.EqualTo("opportunityid"));

        // The inputs of the code activity are NOT reconstructed, and the reading says so — otherwise
        // a rewrite would turn "[DirectCast(…)]" into a string literal.
        Assert.That(parsed.FullyUnderstood, Is.False);
        Assert.That(string.Join(" ", parsed.Unrecognised), Does.Contain("Team"));

        // Belt and braces: even if that warning were ignored, validation refuses the rewrite.
        var again = WorkflowDefinitionValidator.Validate(
            parsed.Definition with { PrimaryEntity = "salesorder" }, CustomActivityFixture.Catalog());
        Assert.That(again.CanSave, Is.False);
        Assert.That(again.Issues.Select(i => i.Code), Does.Contain("WF088"));
    }

    [Test]
    public void Roundtrip_UpdateWithFieldsAndFallback_PreservesFieldList()
    {
        var definition = new WorkflowDefinition
        {
            PrimaryEntity = "lead",
            Steps =
            [
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.UpdateRecord,
                    Attributes =
                    [
                        new WorkflowAttributeAssignment
                        {
                            Attribute = "jobtitle",
                            Value = new WorkflowValue
                            {
                                Kind = WorkflowValueKind.Field,
                                Fields = ["lead.companyname", "lead.subject"],
                                Fallback = "unbekannt"
                            }
                        }
                    ]
                }
            ]
        };

        var xaml = WorkflowXamlBuilder.Build(definition).Xaml;
        var parsed = WorkflowXamlParser.Parse(xaml, "lead");

        var value = parsed.Definition.Steps[0].Attributes![0].Value;
        Assert.That(value.Kind, Is.EqualTo(WorkflowValueKind.Field));
        Assert.That(value.Fields, Is.EqualTo(new[] { "lead.companyname", "lead.subject" }));
        Assert.That(value.Fallback, Is.EqualTo("unbekannt"));
    }

    [Test]
    public void Roundtrip_ConditionWithThenAndElse_PreservesStructure()
    {
        var definition = new WorkflowDefinition
        {
            PrimaryEntity = "lead",
            Steps =
            [
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.Condition,
                    Conditions =
                    [
                        new WorkflowCondition
                        {
                            Attribute = "lastname",
                            Operator = "Equal",
                            Value = new WorkflowValue { Literal = "Test" }
                        }
                    ],
                    Then =
                    [
                        new WorkflowStep
                        {
                            Kind = WorkflowStepKind.UpdateRecord,
                            Attributes =
                            [
                                new WorkflowAttributeAssignment
                                {
                                    Attribute = "jobtitle",
                                    Value = new WorkflowValue { Literal = "ja" }
                                }
                            ]
                        }
                    ],
                    Else = [new WorkflowStep { Kind = WorkflowStepKind.StopWorkflow }]
                }
            ]
        };

        var xaml = WorkflowXamlBuilder.Build(definition).Xaml;
        var parsed = WorkflowXamlParser.Parse(xaml, "lead");

        Assert.That(parsed.FullyUnderstood, Is.True,
            "unrecognised: " + string.Join(", ", parsed.Unrecognised));

        var condition = parsed.Definition.Steps[0];
        Assert.That(condition.Kind, Is.EqualTo(WorkflowStepKind.Condition));
        Assert.That(condition.Conditions, Has.Count.EqualTo(1));
        Assert.That(condition.Conditions![0].Attribute, Is.EqualTo("lastname"));
        Assert.That(condition.Conditions[0].Operator, Is.EqualTo("Equal"));
        Assert.That(condition.Conditions[0].Value!.Literal, Is.EqualTo("Test"));
        Assert.That(condition.Then, Has.Count.EqualTo(1));
        Assert.That(condition.Then![0].Kind, Is.EqualTo(WorkflowStepKind.UpdateRecord));
        Assert.That(condition.Else, Has.Count.EqualTo(1));
        Assert.That(condition.Else![0].Kind, Is.EqualTo(WorkflowStepKind.StopWorkflow));
    }

    [Test]
    public void Roundtrip_ConditionComparingTwoFields_KeepsRightHandSideAsField()
    {
        var definition = new WorkflowDefinition
        {
            PrimaryEntity = "lead",
            Steps =
            [
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.Condition,
                    Conditions =
                    [
                        new WorkflowCondition
                        {
                            Attribute = "lastname",
                            Operator = "Equal",
                            Value = new WorkflowValue
                            {
                                Kind = WorkflowValueKind.Field,
                                Fields = ["lead.companyname"]
                            }
                        }
                    ],
                    Then = [new WorkflowStep { Kind = WorkflowStepKind.StopWorkflow }]
                }
            ]
        };

        var xaml = WorkflowXamlBuilder.Build(definition).Xaml;
        var parsed = WorkflowXamlParser.Parse(xaml, "lead");

        var value = parsed.Definition.Steps[0].Conditions![0].Value;
        Assert.That(value, Is.Not.Null);
        Assert.That(value!.Kind, Is.EqualTo(WorkflowValueKind.Field));
        Assert.That(value.Fields, Does.Contain("lead.companyname"));
    }

    [Test]
    public void Roundtrip_CreateRecord_PreservesTargetEntity()
    {
        var definition = new WorkflowDefinition
        {
            PrimaryEntity = "lead",
            Steps =
            [
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.CreateRecord,
                    Entity = "task",
                    Attributes =
                    [
                        new WorkflowAttributeAssignment
                        {
                            Attribute = "subject",
                            Value = new WorkflowValue { Literal = "Nachfassen" }
                        }
                    ]
                }
            ]
        };

        var xaml = WorkflowXamlBuilder.Build(definition).Xaml;
        var parsed = WorkflowXamlParser.Parse(xaml, "lead");

        var step = parsed.Definition.Steps[0];
        Assert.That(step.Kind, Is.EqualTo(WorkflowStepKind.CreateRecord));
        Assert.That(step.Entity, Is.EqualTo("task"));
        Assert.That(step.Attributes![0].Attribute, Is.EqualTo("subject"));
        Assert.That(step.Attributes[0].Value.Literal, Is.EqualTo("Nachfassen"));
    }

    [Test]
    public void Roundtrip_CustomActivity_PreservesAssemblyAndOutputs()
    {
        const string aqn = "Sample.CrmPlugins.Workflows.Extract, Sample.CrmPlugins, Version=1.0.0.0, Culture=neutral, PublicKeyToken=6cd3b47345c1c112";
        var definition = new WorkflowDefinition
        {
            PrimaryEntity = "lead",
            Steps =
            [
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.CustomActivity,
                    AssemblyQualifiedName = aqn,
                    Outputs = ["Domain"]
                }
            ]
        };

        var xaml = WorkflowXamlBuilder.Build(definition).Xaml;
        var parsed = WorkflowXamlParser.Parse(xaml, "lead");

        var step = parsed.Definition.Steps[0];
        Assert.That(step.Kind, Is.EqualTo(WorkflowStepKind.CustomActivity));
        Assert.That(step.AssemblyQualifiedName, Is.EqualTo(aqn));
        Assert.That(step.Outputs, Does.Contain("Domain"));
    }

    [Test]
    public void Roundtrip_Stage_PreservesChildren()
    {
        var definition = new WorkflowDefinition
        {
            PrimaryEntity = "lead",
            Steps =
            [
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.Stage,
                    Description = "Phase 1",
                    Children =
                    [
                        new WorkflowStep
                        {
                            Kind = WorkflowStepKind.UpdateRecord,
                            Attributes =
                            [
                                new WorkflowAttributeAssignment
                                {
                                    Attribute = "jobtitle",
                                    Value = new WorkflowValue { Literal = "x" }
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        var xaml = WorkflowXamlBuilder.Build(definition).Xaml;
        var parsed = WorkflowXamlParser.Parse(xaml, "lead");

        var stage = parsed.Definition.Steps[0];
        Assert.That(stage.Kind, Is.EqualTo(WorkflowStepKind.Stage));
        Assert.That(stage.Description, Is.EqualTo("Phase 1"));
        Assert.That(stage.Children, Has.Count.EqualTo(1));
        Assert.That(stage.Children![0].Kind, Is.EqualTo(WorkflowStepKind.UpdateRecord));
    }
}
