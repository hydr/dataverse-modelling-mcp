namespace Dataverse.Tests.Integration;

using Dataverse.Core.Workflows;
using Dataverse.Tests.Infrastructure;
using NUnit.Framework;

/// <summary>
/// End-to-end verification against a live environment: create a workflow, write generated logic,
/// activate it (which makes the platform compile the XAML), read it back, and clean up.
/// </summary>
[TestFixture]
[Category("Integration")]
public sealed class WorkflowAuthoringIntegrationTests : IntegrationTestBase
{
    private readonly List<Guid> _created = [];

    [TearDown]
    public async Task RemoveCreatedWorkflows()
    {
        foreach (var id in _created)
        {
            try
            {
                // Deactivate first — active workflows cannot be deleted.
                await WorkflowService.SetStateAsync(OrgUrl, id, activate: false);
            }
            catch
            {
                // already a draft
            }

            try
            {
                await WorkflowService.DeleteAsync(OrgUrl, id);
            }
            catch (Exception ex)
            {
                TestContext.Out.WriteLine($"Could not delete workflow {id}: {ex.Message}");
            }
        }

        _created.Clear();
    }

    private static WorkflowDefinition SampleDefinition() => new()
    {
        PrimaryEntity = "lead",
        Steps =
        [
            new WorkflowStep
            {
                Kind = WorkflowStepKind.Condition,
                Description = "Nachname gefuellt",
                Conditions = [new WorkflowCondition { Attribute = "lastname", Operator = "NotNull" }],
                Then =
                [
                    new WorkflowStep
                    {
                        Kind = WorkflowStepKind.UpdateRecord,
                        Description = "Position setzen",
                        Attributes =
                        [
                            new WorkflowAttributeAssignment
                            {
                                Attribute = "jobtitle",
                                Value = new WorkflowValue
                                {
                                    Kind = WorkflowValueKind.Field,
                                    Fields = ["lead.companyname"],
                                    Fallback = "unbekannt"
                                }
                            }
                        ]
                    }
                ],
                Else =
                [
                    new WorkflowStep
                    {
                        Kind = WorkflowStepKind.StopWorkflow,
                        Description = "Abbruch",
                        Reason = new WorkflowValue { Literal = "Nachname fehlt" }
                    }
                ]
            }
        ]
    };

    [Test]
    public async Task Create_WritesDraftWithValidSkeleton()
    {
        var id = await WorkflowService.CreateAsync(
            OrgUrl, $"ZZ IT Skeleton {Guid.NewGuid():N}", "lead", "created by integration test");
        _created.Add(id);

        Assert.That(id, Is.Not.EqualTo(Guid.Empty));

        var detail = await WorkflowService.GetAsync(OrgUrl, id);
        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.StateCode, Is.Zero, "a new workflow must be a draft");
        Assert.That(detail.PrimaryEntity, Is.EqualTo("lead"));
        Assert.That(detail.Xaml, Is.Not.Null.And.Contains("mxswa:Workflow"));
    }

    [Test]
    public async Task SetDefinition_ThenActivate_IsAcceptedAndCompiledByThePlatform()
    {
        var id = await WorkflowService.CreateAsync(
            OrgUrl, $"ZZ IT Authoring {Guid.NewGuid():N}", "lead");
        _created.Add(id);

        var save = await WorkflowAuthoringService.SetDefinitionAsync(OrgUrl, id, SampleDefinition());

        Assert.That(save.Applied, Is.True,
            "issues: " + string.Join("; ", save.Validation.Issues.Select(i => $"{i.Code} {i.Path}: {i.Problem}")));
        Assert.That(save.StepIds, Is.EqualTo(new[] { "ConditionStep1", "ConditionBranchStep2", "UpdateStep3", "ConditionBranchStep4", "StopWorkflowStep5" }));

        // An automatic workflow needs a trigger before it can be activated (0x80045018).
        await WorkflowService.UpdateAsync(OrgUrl, id, new Dictionary<string, object?>
        {
            ["triggeroncreate"] = true,
            ["createstage"] = 40
        });

        // Activation makes Dataverse compile the XAML — the real proof that it is valid.
        await WorkflowService.SetStateAsync(OrgUrl, id, activate: true);

        var detail = await WorkflowService.GetAsync(OrgUrl, id);
        Assert.That(detail!.StateCode, Is.EqualTo(1), "workflow should be activated");
        // On activation the platform substitutes the real workflow id into the class name.
        Assert.That(detail.Xaml, Does.Contain($"XrmWorkflow{id:N}"));
    }

    [Test]
    public async Task SetDefinition_ThenGetDefinition_RoundtripsThroughDataverse()
    {
        var id = await WorkflowService.CreateAsync(
            OrgUrl, $"ZZ IT Roundtrip {Guid.NewGuid():N}", "lead");
        _created.Add(id);

        await WorkflowAuthoringService.SetDefinitionAsync(OrgUrl, id, SampleDefinition());

        var (_, parsed) = await WorkflowAuthoringService.GetDefinitionAsync(OrgUrl, id);

        Assert.That(parsed.FullyUnderstood, Is.True,
            "unrecognised: " + string.Join("; ", parsed.Unrecognised));
        Assert.That(parsed.Definition.Steps, Has.Count.EqualTo(1));

        var condition = parsed.Definition.Steps[0];
        Assert.That(condition.Kind, Is.EqualTo(WorkflowStepKind.Condition));
        Assert.That(condition.Description, Is.EqualTo("Nachname gefuellt"));
        Assert.That(condition.Then, Has.Count.EqualTo(1));
        Assert.That(condition.Then![0].Attributes![0].Attribute, Is.EqualTo("jobtitle"));
        Assert.That(condition.Else, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task SetDefinition_WithUnknownAttribute_IsRefusedBeforeWriting()
    {
        var id = await WorkflowService.CreateAsync(
            OrgUrl, $"ZZ IT Guard {Guid.NewGuid():N}", "lead");
        _created.Add(id);

        var before = await WorkflowService.GetXamlAsync(OrgUrl, id);

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
                            Attribute = "definitely_not_a_field",
                            Value = new WorkflowValue { Literal = "x" }
                        }
                    ]
                }
            ]
        };

        var save = await WorkflowAuthoringService.SetDefinitionAsync(OrgUrl, id, definition);

        Assert.That(save.Applied, Is.False, "an unknown attribute must not be written");
        Assert.That(save.Validation.Issues.Select(i => i.Code), Does.Contain("WF301"));

        var after = await WorkflowService.GetXamlAsync(OrgUrl, id);
        Assert.That(after, Is.EqualTo(before), "the workflow must be untouched after a refused write");
    }

    [Test]
    public async Task SetDefinition_OnActiveWorkflow_IsRefusedUnlessReactivateRequested()
    {
        var id = await WorkflowService.CreateAsync(
            OrgUrl, $"ZZ IT Active {Guid.NewGuid():N}", "lead");
        _created.Add(id);

        await WorkflowAuthoringService.SetDefinitionAsync(OrgUrl, id, SampleDefinition());
        await WorkflowService.UpdateAsync(OrgUrl, id, new Dictionary<string, object?>
        {
            ["triggeroncreate"] = true,
            ["createstage"] = 40
        });
        await WorkflowService.SetStateAsync(OrgUrl, id, activate: true);

        var refused = await WorkflowAuthoringService.SetDefinitionAsync(OrgUrl, id, SampleDefinition());
        Assert.That(refused.Applied, Is.False);
        Assert.That(refused.Validation.Issues.Select(i => i.Code), Does.Contain("WF210"));

        var allowed = await WorkflowAuthoringService.SetDefinitionAsync(
            OrgUrl, id, SampleDefinition(), reactivate: true);
        Assert.That(allowed.Applied, Is.True,
            "issues: " + string.Join("; ", allowed.Validation.Issues.Select(i => $"{i.Code}: {i.Problem}")));

        var detail = await WorkflowService.GetAsync(OrgUrl, id);
        Assert.That(detail!.StateCode, Is.EqualTo(1), "should be re-activated afterwards");
    }
}
