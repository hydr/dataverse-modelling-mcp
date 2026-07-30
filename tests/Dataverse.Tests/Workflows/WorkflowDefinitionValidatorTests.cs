namespace Dataverse.Tests.Workflows;

using Dataverse.Core.Workflows;
using NUnit.Framework;

[TestFixture]
public sealed class WorkflowDefinitionValidatorTests
{
    private static WorkflowValidationResult Validate(params WorkflowStep[] steps) =>
        WorkflowDefinitionValidator.Validate(new WorkflowDefinition
        {
            PrimaryEntity = "lead",
            Steps = [.. steps]
        });

    private static void AssertHasCode(WorkflowValidationResult result, string code)
    {
        Assert.That(result.Issues.Select(i => i.Code), Does.Contain(code),
            "actual: " + string.Join(", ", result.Issues.Select(i => $"{i.Code} @ {i.Path}")));
    }

    [Test]
    public void Validate_Null_ReportsWF001()
    {
        var result = WorkflowDefinitionValidator.Validate(null);

        Assert.That(result.CanSave, Is.False);
        AssertHasCode(result, "WF001");
    }

    [Test]
    public void Validate_MissingPrimaryEntityAndSteps_ReportsBoth()
    {
        var result = WorkflowDefinitionValidator.Validate(new WorkflowDefinition());

        Assert.That(result.CanSave, Is.False);
        AssertHasCode(result, "WF002");
        AssertHasCode(result, "WF003");
    }

    [Test]
    public void Validate_EveryIssueCarriesPathProblemAndFix()
    {
        var result = Validate(new WorkflowStep { Kind = WorkflowStepKind.Condition });

        Assert.That(result.Issues, Is.Not.Empty);
        foreach (var issue in result.Issues)
        {
            Assert.That(issue.Path, Is.Not.Empty, "an issue without a path is not actionable");
            Assert.That(issue.Problem, Is.Not.Empty);
            Assert.That(issue.Fix, Is.Not.Empty, "every issue must say what to do");
            Assert.That(issue.Severity, Is.AnyOf("error", "warning"));
        }
    }

    [Test]
    public void Validate_UnknownStepKind_ReportsWF011()
    {
        var result = Validate(new WorkflowStep { Kind = "teleport" });

        Assert.That(result.CanSave, Is.False);
        AssertHasCode(result, "WF011");
    }

    [Test]
    public void Validate_ReadOnlyStepKind_ReportsWF012()
    {
        var result = Validate(new WorkflowStep { Kind = WorkflowStepKind.SendEmail });

        Assert.That(result.CanSave, Is.False);
        AssertHasCode(result, "WF012");
        Assert.That(result.Issues.First(i => i.Code == "WF012").Fix, Does.Contain("designer"));
    }

    [Test]
    public void Validate_ConditionWithoutBranches_ReportsWF075()
    {
        var result = Validate(new WorkflowStep
        {
            Kind = WorkflowStepKind.Condition,
            Conditions = [new WorkflowCondition { Attribute = "lastname", Operator = "NotNull" }]
        });

        AssertHasCode(result, "WF075");
    }

    [Test]
    public void Validate_OperatorNeedingValueWithoutValue_ReportsWF073()
    {
        var result = Validate(new WorkflowStep
        {
            Kind = WorkflowStepKind.Condition,
            Conditions = [new WorkflowCondition { Attribute = "lastname", Operator = "Equal" }],
            Then = [new WorkflowStep { Kind = WorkflowStepKind.StopWorkflow }]
        });

        AssertHasCode(result, "WF073");
    }

    [Test]
    public void Validate_NullOperatorWithValue_WarnsButAllowsSaving()
    {
        var result = Validate(new WorkflowStep
        {
            Kind = WorkflowStepKind.Condition,
            Conditions =
            [
                new WorkflowCondition
                {
                    Attribute = "lastname",
                    Operator = "Null",
                    Value = new WorkflowValue { Literal = "x" }
                }
            ],
            Then = [new WorkflowStep { Kind = WorkflowStepKind.StopWorkflow }]
        });

        AssertHasCode(result, "WF074");
        Assert.That(result.CanSave, Is.True, "a superfluous value is a warning, not an error");
    }

    [Test]
    public void Validate_UpdateTargetingOtherEntity_ReportsWF021()
    {
        var result = Validate(new WorkflowStep
        {
            Kind = WorkflowStepKind.UpdateRecord,
            Entity = "account",
            Attributes =
            [
                new WorkflowAttributeAssignment
                {
                    Attribute = "name",
                    Value = new WorkflowValue { Literal = "x" }
                }
            ]
        });

        AssertHasCode(result, "WF021");
    }

    [Test]
    public void Validate_CreateWithoutEntity_ReportsWF020()
    {
        var result = Validate(new WorkflowStep
        {
            Kind = WorkflowStepKind.CreateRecord,
            Attributes =
            [
                new WorkflowAttributeAssignment
                {
                    Attribute = "subject",
                    Value = new WorkflowValue { Literal = "x" }
                }
            ]
        });

        AssertHasCode(result, "WF020");
    }

    [Test]
    public void Validate_DuplicateAttributeAssignment_ReportsWF092()
    {
        var result = Validate(new WorkflowStep
        {
            Kind = WorkflowStepKind.UpdateRecord,
            Attributes =
            [
                new WorkflowAttributeAssignment { Attribute = "jobtitle", Value = new WorkflowValue { Literal = "a" } },
                new WorkflowAttributeAssignment { Attribute = "jobtitle", Value = new WorkflowValue { Literal = "b" } }
            ]
        });

        AssertHasCode(result, "WF092");
    }

    [Test]
    public void Validate_BadFieldReference_ReportsWF121()
    {
        var result = Validate(new WorkflowStep
        {
            Kind = WorkflowStepKind.UpdateRecord,
            Attributes =
            [
                new WorkflowAttributeAssignment
                {
                    Attribute = "jobtitle",
                    Value = new WorkflowValue { Kind = WorkflowValueKind.Field, Fields = ["companyname"] }
                }
            ]
        });

        AssertHasCode(result, "WF121");
    }

    [Test]
    public void Validate_FieldOfOtherEntity_ReportsWF122()
    {
        var result = Validate(new WorkflowStep
        {
            Kind = WorkflowStepKind.UpdateRecord,
            Attributes =
            [
                new WorkflowAttributeAssignment
                {
                    Attribute = "jobtitle",
                    Value = new WorkflowValue { Kind = WorkflowValueKind.Field, Fields = ["account.name"] }
                }
            ]
        });

        AssertHasCode(result, "WF122");
    }

    [Test]
    public void Validate_StepOutputWithoutMatchingOutput_ReportsWF132()
    {
        var result = Validate(new WorkflowStep
        {
            Kind = WorkflowStepKind.UpdateRecord,
            Attributes =
            [
                new WorkflowAttributeAssignment
                {
                    Attribute = "jobtitle",
                    Value = new WorkflowValue
                    {
                        Kind = WorkflowValueKind.StepOutput,
                        StepOutput = "CustomActivityStep1.Domain"
                    }
                }
            ]
        });

        AssertHasCode(result, "WF132");
    }

    [Test]
    public void Validate_StepOutputDeclaredEarlier_IsAccepted()
    {
        var result = Validate(
            new WorkflowStep
            {
                Kind = WorkflowStepKind.CustomActivity,
                StepId = "CustomActivityStep1",
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
            });

        Assert.That(result.CanSave, Is.True,
            "actual: " + string.Join(", ", result.Issues.Select(i => $"{i.Code}: {i.Problem}")));
    }

    [Test]
    public void Validate_PublicKeyTokenNull_WarnsWithPointerToCorrectSource()
    {
        var result = Validate(new WorkflowStep
        {
            Kind = WorkflowStepKind.CustomActivity,
            AssemblyQualifiedName = "A.B, A, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"
        });

        AssertHasCode(result, "WF081");
        Assert.That(result.Issues.First(i => i.Code == "WF081").Fix,
            Does.Contain("workflow_get_activity_parameters"));
        Assert.That(result.CanSave, Is.True, "it is a warning — the token may legitimately be absent");
    }

    [Test]
    public void Validate_NestedStage_ReportsWF013()
    {
        var result = Validate(new WorkflowStep
        {
            Kind = WorkflowStepKind.Stage,
            Children =
            [
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.Stage,
                    Children = [new WorkflowStep { Kind = WorkflowStepKind.StopWorkflow }]
                }
            ]
        });

        AssertHasCode(result, "WF013");
    }

    [Test]
    public void Validate_ValidWorkflow_HasNoErrors()
    {
        var result = Validate(new WorkflowStep
        {
            Kind = WorkflowStepKind.Condition,
            Description = "Nur deutsche Leads",
            Conditions =
            [
                new WorkflowCondition
                {
                    Attribute = "address1_country",
                    Operator = "Equal",
                    Value = new WorkflowValue { Literal = "Deutschland" }
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
                            Attribute = "sample_domain",
                            Value = new WorkflowValue
                            {
                                Kind = WorkflowValueKind.Field,
                                Fields = ["lead.websiteurl"],
                                Fallback = "unbekannt"
                            }
                        }
                    ]
                }
            ]
        });

        Assert.That(result.CanSave, Is.True,
            "actual: " + string.Join(", ", result.Issues.Select(i => $"{i.Code}: {i.Problem}")));
        Assert.That(result.ErrorCount, Is.Zero);
    }

    // ---------------------------------------------------------------- code activity parameters
    //
    // These four error classes used to surface only on activation, as 0x80048455. They are checkable
    // beforehand because plugintype.customworkflowactivityinfo carries the real signature.

    private static WorkflowValidationResult ValidateActivity(
        Dictionary<string, WorkflowValue>? inputs, List<string>? outputs = null) =>
        WorkflowDefinitionValidator.Validate(
            new WorkflowDefinition
            {
                PrimaryEntity = "salesorder",
                Steps =
                [
                    new WorkflowStep
                    {
                        Kind = WorkflowStepKind.CustomActivity,
                        AssemblyQualifiedName = CustomActivityFixture.CheckUserInTeam,
                        Inputs = inputs,
                        Outputs = outputs
                    }
                ]
            },
            CustomActivityFixture.Catalog());

    private static WorkflowValue TeamReference() => new()
    {
        DataType = "EntityReference",
        Literal = "team:a0000001-0000-4000-8000-000000000001"
    };

    [Test]
    public void Validate_CustomActivity_MatchingSignature_IsAccepted()
    {
        var result = ValidateActivity(
            new Dictionary<string, WorkflowValue> { ["Team"] = TeamReference() },
            ["isUserInTeam"]);

        Assert.That(result.CanSave, Is.True,
            "actual: " + string.Join(", ", result.Issues.Select(i => $"{i.Code}: {i.Problem}")));
    }

    [Test]
    public void Validate_UnknownInputParameter_ReportsWF085()
    {
        var result = ValidateActivity(new Dictionary<string, WorkflowValue>
        {
            ["Team"] = TeamReference(),
            // The display name instead of the DependencyPropertyName is the classic mistake.
            ["Benutzer"] = TeamReference()
        });

        Assert.That(result.CanSave, Is.False);
        AssertHasCode(result, "WF085");
        Assert.That(result.Issues.First(i => i.Code == "WF085").Fix, Does.Contain("User"),
            "the fix should name the parameters the activity really has");
    }

    [Test]
    public void Validate_DataTypeNotMatchingTheParameter_ReportsWF086()
    {
        var result = ValidateActivity(new Dictionary<string, WorkflowValue>
        {
            // Team is a Microsoft.Crm.Sdk.Lookup, so a string is the wrong argument type.
            ["Team"] = new() { DataType = "String", Literal = "Salesteam" }
        });

        Assert.That(result.CanSave, Is.False);
        AssertHasCode(result, "WF086");
        Assert.That(result.Issues.First(i => i.Code == "WF086").Fix, Does.Contain("EntityReference"));
    }

    [Test]
    public void Validate_DataTypeOmittedOnATypedParameter_ReportsWF086()
    {
        // An omitted dataType silently means String — which is the wrong type here.
        var result = ValidateActivity(new Dictionary<string, WorkflowValue>
        {
            ["Team"] = new() { Literal = "team:a0000001-0000-4000-8000-000000000001" }
        });

        Assert.That(result.CanSave, Is.False);
        AssertHasCode(result, "WF086");
    }

    [Test]
    public void Validate_RequiredInputMissing_ReportsWF087()
    {
        var result = ValidateActivity(inputs: null, outputs: ["isUserInTeam"]);

        Assert.That(result.CanSave, Is.False);
        AssertHasCode(result, "WF087");
        var issue = result.Issues.First(i => i.Code == "WF087");
        Assert.That(issue.Problem, Does.Contain("Team"));
        // 'User' is optional and must not be demanded.
        Assert.That(result.Issues.Count(i => i.Code == "WF087"), Is.EqualTo(1));
    }

    [Test]
    public void Validate_LookupPointingAtTheWrongTable_ReportsWF088()
    {
        var result = ValidateActivity(new Dictionary<string, WorkflowValue>
        {
            ["Team"] = new()
            {
                DataType = "EntityReference",
                Literal = "account:a0000001-0000-4000-8000-000000000001"
            }
        });

        Assert.That(result.CanSave, Is.False);
        AssertHasCode(result, "WF088");
        Assert.That(result.Issues.First(i => i.Code == "WF088").Problem, Does.Contain("team"));
    }

    [Test]
    public void Validate_UnknownOutputParameter_ReportsWF089()
    {
        var result = ValidateActivity(
            new Dictionary<string, WorkflowValue> { ["Team"] = TeamReference() },
            ["IsUserInSalesteam"]);

        Assert.That(result.CanSave, Is.False);
        AssertHasCode(result, "WF089");
        Assert.That(result.Issues.First(i => i.Code == "WF089").Fix, Does.Contain("isUserInTeam"));
    }

    [Test]
    public void Validate_WithoutActivityMetadata_SkipsTheParameterChecks()
    {
        // No catalog: the definition is only checked structurally, so a wrong argument name passes
        // here and shows up on activation instead. This keeps the validator usable offline.
        var result = WorkflowDefinitionValidator.Validate(new WorkflowDefinition
        {
            PrimaryEntity = "salesorder",
            Steps =
            [
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.CustomActivity,
                    AssemblyQualifiedName = CustomActivityFixture.CheckUserInTeam,
                    Inputs = new Dictionary<string, WorkflowValue> { ["Nonsense"] = TeamReference() }
                }
            ]
        });

        Assert.That(result.CanSave, Is.True);
        Assert.That(result.Issues.Select(i => i.Code), Does.Not.Contain("WF085"));
    }

    [Test]
    public void Validate_BareParameterNameAsStepOutput_IsAccepted()
    {
        // The step id is assigned by the builder, so callers cannot qualify the reference. The
        // builder resolves the bare parameter name, and the validator must agree.
        var result = WorkflowDefinitionValidator.Validate(
            new WorkflowDefinition
            {
                PrimaryEntity = "salesorder",
                Steps =
                [
                    new WorkflowStep
                    {
                        Kind = WorkflowStepKind.CustomActivity,
                        AssemblyQualifiedName = CustomActivityFixture.CheckUserInTeam,
                        Inputs = new Dictionary<string, WorkflowValue> { ["Team"] = TeamReference() },
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
                        Then =
                        [
                            new WorkflowStep
                            {
                                Kind = WorkflowStepKind.UpdateRecord,
                                Attributes =
                                [
                                    new WorkflowAttributeAssignment
                                    {
                                        Attribute = "description",
                                        Value = new WorkflowValue { Literal = "x" }
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            CustomActivityFixture.Catalog());

        Assert.That(result.CanSave, Is.True,
            "actual: " + string.Join(", ", result.Issues.Select(i => $"{i.Code}: {i.Problem}")));
        Assert.That(result.Issues.Select(i => i.Code), Does.Not.Contain("WF077"));
    }

    [Test]
    public void ValidateGeneratedXaml_UndeclaredVariable_ReportsWF201()
    {
        // Hand-crafted XAML referencing a variable that is never declared.
        const string xaml = "<Activity xmlns=\"x\"><Sequence><Assign Value=\"[Ghost_1]\" /></Sequence></Activity>";

        var result = WorkflowDefinitionValidator.ValidateGeneratedXaml(xaml);

        Assert.That(result.CanSave, Is.False);
        Assert.That(result.Issues.Select(i => i.Code), Does.Contain("WF201"));
    }

    [Test]
    public void ValidateGeneratedXaml_MalformedXml_ReportsWF200()
    {
        var result = WorkflowDefinitionValidator.ValidateGeneratedXaml("<Activity><unclosed>");

        Assert.That(result.CanSave, Is.False);
        Assert.That(result.Issues.Select(i => i.Code), Does.Contain("WF200"));
    }
}
