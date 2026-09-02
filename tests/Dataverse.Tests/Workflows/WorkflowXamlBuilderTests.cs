namespace Dataverse.Tests.Workflows;

using Dataverse.Core.Workflows;
using NUnit.Framework;

[TestFixture]
public sealed class WorkflowXamlBuilderTests
{
    private static WorkflowDefinition SimpleUpdate() => new()
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
                        Value = new WorkflowValue { Kind = WorkflowValueKind.Literal, Literal = "Chef" }
                    }
                ]
            }
        ]
    };

    [Test]
    public void Build_EmptyDefinition_ProducesValidSkeleton()
    {
        var result = WorkflowXamlBuilder.Build(new WorkflowDefinition { PrimaryEntity = "lead" });

        Assert.That(result.Xaml, Does.Contain("<mxswa:Workflow />"));
        Assert.That(result.Xaml, Does.Contain("InputEntities"));
        Assert.That(result.Xaml, Does.Contain("CreatedEntities"));
        // A workflow id of Guid.Empty is legal: the platform substitutes the real one on activation.
        Assert.That(result.Xaml, Does.Contain("XrmWorkflow00000000000000000000000000000000"));
        Assert.That(WorkflowDefinitionValidator.ValidateGeneratedXaml(result.Xaml).CanSave, Is.True);
    }

    [Test]
    public void Build_UsesRealWorkflowIdInClassName()
    {
        var id = Guid.Parse("e784a882-c8ef-46b2-b6e4-00cbe9146360");
        var result = WorkflowXamlBuilder.Build(SimpleUpdate(), id);

        Assert.That(result.Xaml, Does.Contain("XrmWorkflowe784a882c8ef46b2b6e400cbe9146360"));
    }

    [Test]
    public void Build_UpdateStep_EmitsTempEntityPatternAndPersist()
    {
        var result = WorkflowXamlBuilder.Build(SimpleUpdate());

        Assert.That(result.Xaml, Does.Contain("primaryEntity#Temp"), "update must go through a temp entity");
        Assert.That(result.Xaml, Does.Contain("mxswa:UpdateEntity"));
        Assert.That(result.Xaml, Does.Contain("mxswa:SetEntityProperty"));
        Assert.That(result.Xaml, Does.Contain("<Persist />"));
        // Literals are always materialised via CreateCrmType, never inlined.
        Assert.That(result.Xaml, Does.Contain("CreateCrmType"));
        Assert.That(WorkflowDefinitionValidator.ValidateGeneratedXaml(result.Xaml).CanSave, Is.True);
    }

    [Test]
    public void Build_StepIdsRunAcrossKindsInPreOrder()
    {
        var definition = new WorkflowDefinition
        {
            PrimaryEntity = "lead",
            Steps =
            [
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.Condition,
                    Conditions = [new WorkflowCondition { Attribute = "lastname", Operator = "NotNull" }],
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
                                    Value = new WorkflowValue { Literal = "x" }
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        var result = WorkflowXamlBuilder.Build(definition);

        // Matches the designer: container, then its branch, then the steps inside.
        Assert.That(result.StepIds, Is.EqualTo(new[] { "ConditionStep1", "ConditionBranchStep2", "UpdateStep3" }));
        Assert.That(result.Xaml, Does.Contain("DisplayName=\"ConditionStep1\""));
        Assert.That(result.Xaml, Does.Contain("DisplayName=\"ConditionBranchStep2\""));
    }

    [Test]
    public void Build_Description_BecomesPartOfDisplayName()
    {
        var definition = SimpleUpdate();
        definition.Steps[0] = definition.Steps[0] with { Description = "Titel setzen" };

        var result = WorkflowXamlBuilder.Build(definition);

        Assert.That(result.Xaml, Does.Contain("DisplayName=\"UpdateStep1: Titel setzen\""));
        // The inner activity keeps the bare step id.
        Assert.That(result.Xaml, Does.Contain("mxswa:UpdateEntity DisplayName=\"UpdateStep1\""));
    }

    [Test]
    public void Build_FieldValueWithFallback_EmitsSelectFirstNonNullWithFallbackLast()
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

        Assert.That(xaml, Does.Contain("SelectFirstNonNull"));
        Assert.That(xaml, Does.Contain("Attribute=\"companyname\""));
        Assert.That(xaml, Does.Contain("Attribute=\"subject\""));
        Assert.That(xaml, Does.Contain("unbekannt"));
        // Result slot is allocated first, sources after — mirrors the designer's numbering.
        Assert.That(xaml, Does.Contain("[UpdateStep1_1]"));
        Assert.That(WorkflowDefinitionValidator.ValidateGeneratedXaml(xaml).CanSave, Is.True);
    }

    [Test]
    public void Build_ConditionWithElse_SetsContainsElseBranchAndLiteralTrueCondition()
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
                    Then = [new WorkflowStep { Kind = WorkflowStepKind.StopWorkflow }],
                    Else = [new WorkflowStep { Kind = WorkflowStepKind.StopWorkflow, Outcome = "cancelled" }]
                }
            ]
        };

        var xaml = WorkflowXamlBuilder.Build(definition).Xaml;

        Assert.That(xaml, Does.Contain("<x:Boolean x:Key=\"ContainsElseBranch\">True</x:Boolean>"));
        Assert.That(xaml, Does.Contain("x:Key=\"Condition\">True</InArgument>"), "else branch uses literal True");
        Assert.That(xaml, Does.Contain("OperationStatus.Canceled"));
        Assert.That(WorkflowDefinitionValidator.ValidateGeneratedXaml(xaml).CanSave, Is.True);
    }

    [Test]
    public void Build_WaitStep_UsesWaitTrueAndNullContainsElseBranch()
    {
        var definition = new WorkflowDefinition
        {
            PrimaryEntity = "lead",
            Steps =
            [
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.Wait,
                    Conditions = [new WorkflowCondition { Attribute = "lastname", Operator = "NotNull" }],
                    Then = [new WorkflowStep { Kind = WorkflowStepKind.StopWorkflow }]
                }
            ]
        };

        var xaml = WorkflowXamlBuilder.Build(definition).Xaml;

        Assert.That(xaml, Does.Contain("x:Key=\"Wait\">True</InArgument>"));
        Assert.That(xaml, Does.Contain("<x:Null x:Key=\"ContainsElseBranch\" />"));
    }

    [Test]
    public void Build_CustomActivity_DeclaresOutputVariableAtWorkflowLevel()
    {
        var aqn = "Sample.CrmPlugins.Workflows.Extract, Sample.CrmPlugins, Version=1.0.0.0, Culture=neutral, PublicKeyToken=6cd3b47345c1c112";
        var definition = new WorkflowDefinition
        {
            PrimaryEntity = "lead",
            Steps =
            [
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.CustomActivity,
                    AssemblyQualifiedName = aqn,
                    Inputs = new Dictionary<string, WorkflowValue>
                    {
                        ["Website"] = new() { Kind = WorkflowValueKind.Field, Fields = ["lead.websiteurl"] }
                    },
                    Outputs = ["Domain"]
                }
            ]
        };

        var xaml = WorkflowXamlBuilder.Build(definition).Xaml;

        Assert.That(xaml, Does.Contain("<mxswa:Workflow.Variables>"));
        Assert.That(xaml, Does.Contain("Name=\"CustomActivityStep1Domain_localParameter\""));
        Assert.That(xaml, Does.Contain("Default=\"[Nothing]\""));
        Assert.That(xaml, Does.Contain(aqn.Replace("&", "&amp;")));
        // Code activity inputs need a real .NET type, hence ConvertCrmXrmTypes + DirectCast.
        Assert.That(xaml, Does.Contain("ConvertCrmXrmTypes"));
        Assert.That(xaml, Does.Contain("DirectCast("));
        Assert.That(xaml, Does.Contain("_converted"));
        Assert.That(WorkflowDefinitionValidator.ValidateGeneratedXaml(xaml).CanSave, Is.True);
    }

    [Test]
    public void Build_ConditionChain_PutsEveryCaseInOneSequenceNamedAfterItsBranch()
    {
        var definition = new WorkflowDefinition
        {
            PrimaryEntity = "invoice",
            Steps =
            [
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.Condition,
                    Description = "Voraussetzungen",
                    Branches =
                    [
                        new WorkflowConditionBranch
                        {
                            Conditions = [new WorkflowCondition { Attribute = "sample_invoicenumber", Operator = "Null" }],
                            Steps = [new WorkflowStep { Kind = WorkflowStepKind.StopWorkflow, Outcome = "cancelled" }]
                        },
                        new WorkflowConditionBranch
                        {
                            LogicalOperator = "Or",
                            Conditions =
                            [
                                new WorkflowCondition { Attribute = "sample_reminderdate", Operator = "NotNull" },
                                new WorkflowCondition { Attribute = "emailaddress", Operator = "Null" }
                            ],
                            Steps = [new WorkflowStep { Kind = WorkflowStepKind.StopWorkflow, Outcome = "cancelled" }]
                        }
                    ],
                    Else =
                    [
                        new WorkflowStep
                        {
                            Kind = WorkflowStepKind.UpdateRecord,
                            Attributes =
                            [
                                new WorkflowAttributeAssignment
                                {
                                    Attribute = "description",
                                    Value = new WorkflowValue { Literal = "ok" }
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        var result = WorkflowXamlBuilder.Build(definition);
        var xaml = result.Xaml;

        // One ConditionSequence carries all cases plus the default one.
        Assert.That(System.Text.RegularExpressions.Regex.Matches(xaml, "ConditionSequence").Count, Is.EqualTo(1));
        Assert.That(System.Text.RegularExpressions.Regex.Matches(xaml, @"\.ConditionBranch,").Count, Is.EqualTo(3));
        Assert.That(xaml, Does.Contain("<x:Boolean x:Key=\"ContainsElseBranch\">True</x:Boolean>"));

        // Helper variables are named after their branch — that is what makes the comparisons
        // assignable back to their case when reading.
        Assert.That(xaml, Does.Contain("Name=\"ConditionBranchStep2_condition\""));
        Assert.That(xaml, Does.Contain("Name=\"ConditionBranchStep4_condition\""));
        Assert.That(xaml, Does.Contain("Name=\"ConditionBranchStep4_1\""));

        // Ids run in document order: step, branch, its contents, next branch, ...
        Assert.That(result.StepIds, Is.EqualTo(new[]
        {
            "ConditionStep1", "ConditionBranchStep2", "StopWorkflowStep3",
            "ConditionBranchStep4", "StopWorkflowStep5",
            "ConditionBranchStep6", "UpdateStep7"
        }));
        Assert.That(WorkflowDefinitionValidator.ValidateGeneratedXaml(xaml).CanSave, Is.True);
    }

    [Test]
    public void Build_CustomActivity_TypesArgumentsFromTheActivityMetadata()
    {
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
                            Literal = "team:11112222-3333-4444-5555-666677778888"
                        }
                    },
                    Outputs = ["isUserInTeam"]
                }
            ]
        };

        var xaml = WorkflowXamlBuilder.Build(definition, null, CustomActivityFixture.Catalog()).Xaml;

        // The output is a CrmBoolean, so variable and OutArgument are x:Boolean — not x:String, and
        // its Default cannot be [Nothing] because a value type has no null.
        Assert.That(xaml, Does.Contain(
            "<Variable x:TypeArguments=\"x:Boolean\" Default=\"False\" " +
            "Name=\"CustomActivityStep1isUserInTeam_localParameter\" />"));
        Assert.That(xaml, Does.Contain(
            "<OutArgument x:TypeArguments=\"x:Boolean\" x:Key=\"isUserInTeam\">" +
            "[CustomActivityStep1isUserInTeam_localParameter]</OutArgument>"));

        // The lookup input keeps the parameter's type on both the argument and the cast.
        Assert.That(xaml, Does.Contain("<InArgument x:TypeArguments=\"mxs:EntityReference\" x:Key=\"Team\">"));
        Assert.That(xaml, Does.Contain("Microsoft.Xrm.Sdk.EntityReference)]"));
        Assert.That(xaml, Does.Not.Contain("x:Key=\"isUserInTeam\">[Nothing]"));
        Assert.That(WorkflowDefinitionValidator.ValidateGeneratedXaml(xaml).CanSave, Is.True);
    }

    [Test]
    public void Build_LookupLiteral_UsesTheUniqueIdentifierMarkerAndAnEmptyLabel()
    {
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
                            Literal = "team:11112222-3333-4444-5555-666677778888"
                        }
                    }
                }
            ]
        };

        var xaml = WorkflowXamlBuilder.Build(definition, null, CustomActivityFixture.Catalog()).Xaml;

        // The designer writes the id with the marker "UniqueIdentifier" — "Key" is not a valid
        // WorkflowPropertyType marker and produces an invalid property bag.
        Assert.That(xaml, Does.Contain(
            "WorkflowPropertyType.Guid, &quot;11112222-3333-4444-5555-666677778888&quot;, " +
            "&quot;UniqueIdentifier&quot;"));
        Assert.That(xaml, Does.Not.Contain("&quot;Key&quot;"));
        // ... and leaves the label of the reference empty, even for a named record.
        Assert.That(xaml, Does.Contain(
            "WorkflowPropertyType.EntityReference, &quot;team&quot;, &quot;&quot;,"));
    }

    [Test]
    public void Build_StepOutputReference_ResolvesToLocalParameterVariable()
    {
        var definition = new WorkflowDefinition
        {
            PrimaryEntity = "lead",
            Steps =
            [
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.CustomActivity,
                    AssemblyQualifiedName = "A.B, A, Version=1.0.0.0, Culture=neutral, PublicKeyToken=abc",
                    Outputs = ["Domain"]
                },
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.UpdateRecord,
                    Attributes =
                    [
                        new WorkflowAttributeAssignment
                        {
                            Attribute = "sample_domain",
                            Value = new WorkflowValue
                            {
                                Kind = WorkflowValueKind.StepOutput,
                                StepOutput = "CustomActivityStep1.Domain"
                            }
                        }
                    ]
                }
            ]
        };

        var xaml = WorkflowXamlBuilder.Build(definition).Xaml;

        Assert.That(xaml, Does.Contain("{ CustomActivityStep1Domain_localParameter }"));
        Assert.That(WorkflowDefinitionValidator.ValidateGeneratedXaml(xaml).CanSave, Is.True,
            "the referenced workflow-level variable must be declared");
    }

    [Test]
    public void Build_MultipleConditions_FoldsWithEvaluateLogicalCondition()
    {
        var definition = new WorkflowDefinition
        {
            PrimaryEntity = "lead",
            Steps =
            [
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.Condition,
                    LogicalOperator = "Or",
                    Conditions =
                    [
                        new WorkflowCondition { Attribute = "websiteurl", Operator = "NotNull" },
                        new WorkflowCondition { Attribute = "emailaddress1", Operator = "NotNull" }
                    ],
                    Then = [new WorkflowStep { Kind = WorkflowStepKind.StopWorkflow }]
                }
            ]
        };

        var xaml = WorkflowXamlBuilder.Build(definition).Xaml;

        Assert.That(xaml, Does.Contain("EvaluateLogicalCondition"));
        Assert.That(xaml, Does.Contain("x:Key=\"LogicalOperator\">Or</InArgument>"));
        Assert.That(WorkflowDefinitionValidator.ValidateGeneratedXaml(xaml).CanSave, Is.True);
    }

    [Test]
    public void Build_UnsupportedKind_Throws()
    {
        var definition = new WorkflowDefinition
        {
            PrimaryEntity = "lead",
            Steps = [new WorkflowStep { Kind = WorkflowStepKind.PerformAction }]
        };

        Assert.That(() => WorkflowXamlBuilder.Build(definition), Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void Build_EscapesXmlInLiterals()
    {
        var definition = SimpleUpdate();
        definition.Steps[0].Attributes![0] = new WorkflowAttributeAssignment
        {
            Attribute = "jobtitle",
            Value = new WorkflowValue { Literal = "A & B <tag> \"q\"" }
        };

        var xaml = WorkflowXamlBuilder.Build(definition).Xaml;

        Assert.That(xaml, Does.Not.Contain("A & B"));
        Assert.That(xaml, Does.Contain("A &amp; B"));
        Assert.That(WorkflowDefinitionValidator.ValidateGeneratedXaml(xaml).CanSave, Is.True,
            "escaped output must still be well-formed XML");
    }
}
