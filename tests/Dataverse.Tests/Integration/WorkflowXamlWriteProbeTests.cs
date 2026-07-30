namespace Dataverse.Tests.Integration;

using Dataverse.Core.Workflows;
using Dataverse.Tests.Infrastructure;
using NUnit.Framework;

/// <summary>
/// Narrows down what Dataverse accepts when writing <c>workflow.xaml</c> through the Web API with a
/// bearer token, as opposed to the browser's cookie session. Diagnostic, not a regression suite.
/// </summary>
[TestFixture]
[Category("Integration")]
[Explicit("Diagnostic probe — run deliberately.")]
public sealed class WorkflowXamlWriteProbeTests : IntegrationTestBase
{
    /// <summary>Writes one generated definition per step kind to find which content is rejected.</summary>
    [Test]
    public async Task Probe_WhichStepKindsAreAccepted()
    {
        var cases = new (string Name, WorkflowDefinition Definition)[]
        {
            ("changeStatus", Def(new WorkflowStep { Kind = WorkflowStepKind.ChangeStatus, State = 0, Status = 1 })),
            ("stopWorkflow", Def(new WorkflowStep { Kind = WorkflowStepKind.StopWorkflow })),
            ("update literal", Def(new WorkflowStep
            {
                Kind = WorkflowStepKind.UpdateRecord,
                Attributes = [Assign("jobtitle", new WorkflowValue { Literal = "x" })]
            })),
            ("update field+fallback", Def(new WorkflowStep
            {
                Kind = WorkflowStepKind.UpdateRecord,
                Attributes = [Assign("jobtitle", new WorkflowValue
                {
                    Kind = WorkflowValueKind.Field, Fields = ["lead.companyname"], Fallback = "u"
                })]
            })),
            ("create task", Def(new WorkflowStep
            {
                Kind = WorkflowStepKind.CreateRecord,
                Entity = "task",
                Attributes = [Assign("subject", new WorkflowValue { Literal = "x" })]
            })),
            ("condition -> stopWorkflow", Def(new WorkflowStep
            {
                Kind = WorkflowStepKind.Condition,
                Conditions = [new WorkflowCondition { Attribute = "lastname", Operator = "NotNull" }],
                Then = [new WorkflowStep { Kind = WorkflowStepKind.StopWorkflow }]
            })),
            ("condition -> update", Def(new WorkflowStep
            {
                Kind = WorkflowStepKind.Condition,
                Conditions =
                [
                    new WorkflowCondition
                    {
                        Attribute = "lastname", Operator = "Equal",
                        Value = new WorkflowValue { Literal = "Test" }
                    }
                ],
                Then =
                [
                    new WorkflowStep
                    {
                        Kind = WorkflowStepKind.UpdateRecord,
                        Attributes = [Assign("jobtitle", new WorkflowValue { Literal = "x" })]
                    }
                ]
            })),
            ("condition with else", Def(new WorkflowStep
            {
                Kind = WorkflowStepKind.Condition,
                Conditions = [new WorkflowCondition { Attribute = "lastname", Operator = "NotNull" }],
                Then = [new WorkflowStep { Kind = WorkflowStepKind.StopWorkflow }],
                Else = [new WorkflowStep { Kind = WorkflowStepKind.StopWorkflow }]
            })),
            ("stage", Def(new WorkflowStep
            {
                Kind = WorkflowStepKind.Stage,
                Description = "Phase",
                Children = [new WorkflowStep { Kind = WorkflowStepKind.ChangeStatus, State = 0, Status = 1 }]
            }))
        };

        var id = await WorkflowService.CreateAsync(OrgUrl, $"ZZ Probe Kinds {Guid.NewGuid():N}", "lead");
        TestContext.Out.WriteLine($"created {id}");

        try
        {
            foreach (var (name, definition) in cases)
            {
                var xaml = WorkflowXamlBuilder.Build(definition, id).Xaml;
                try
                {
                    await WorkflowService.UpdateAsync(OrgUrl, id,
                        new Dictionary<string, object?> { ["xaml"] = xaml });
                    TestContext.Out.WriteLine($"RESULT {name}: OK");
                }
                catch (Exception ex)
                {
                    var message = ex.Message.Contains("0x80045040") ? "0x80045040" : ex.Message;
                    TestContext.Out.WriteLine($"RESULT {name}: FAILED -> {message}");
                }
            }
        }
        finally
        {
            try { await WorkflowService.DeleteAsync(OrgUrl, id); }
            catch (Exception ex) { TestContext.Out.WriteLine("cleanup failed: " + ex.Message); }
        }
    }

    private static WorkflowDefinition Def(params WorkflowStep[] steps) =>
        new() { PrimaryEntity = "lead", Steps = [.. steps] };

    private static WorkflowAttributeAssignment Assign(string attribute, WorkflowValue value) =>
        new() { Attribute = attribute, Value = value };

    [Test]
    public async Task Probe_WriteUnchangedXamlBack()
    {
        var id = await WorkflowService.CreateAsync(OrgUrl, $"ZZ Probe {Guid.NewGuid():N}", "lead");
        TestContext.Out.WriteLine($"created {id}");

        try
        {
            var original = await WorkflowService.GetXamlAsync(OrgUrl, id);
            TestContext.Out.WriteLine($"skeleton length: {original?.Length}");

            // Identical content — isolates auth/channel from XAML content.
            try
            {
                await WorkflowService.UpdateAsync(OrgUrl, id,
                    new Dictionary<string, object?> { ["xaml"] = original });
                TestContext.Out.WriteLine("PATCH unchanged xaml: OK");
            }
            catch (Exception ex)
            {
                TestContext.Out.WriteLine("PATCH unchanged xaml: FAILED -> " + ex.Message);
            }

            // Trivial content change: a description added to nothing structural.
            try
            {
                var tweaked = original!.Replace("<mxswa:Workflow />", "<mxswa:Workflow></mxswa:Workflow>");
                await WorkflowService.UpdateAsync(OrgUrl, id,
                    new Dictionary<string, object?> { ["xaml"] = tweaked });
                TestContext.Out.WriteLine("PATCH cosmetically changed xaml: OK");
            }
            catch (Exception ex)
            {
                TestContext.Out.WriteLine("PATCH cosmetically changed xaml: FAILED -> " + ex.Message);
            }

            // Generated logic.
            try
            {
                var generated = WorkflowXamlBuilder.Build(new WorkflowDefinition
                {
                    PrimaryEntity = "lead",
                    Steps =
                    [
                        new WorkflowStep
                        {
                            Kind = WorkflowStepKind.ChangeStatus,
                            State = 0,
                            Status = 1
                        }
                    ]
                }, id).Xaml;

                await WorkflowService.UpdateAsync(OrgUrl, id,
                    new Dictionary<string, object?> { ["xaml"] = generated });
                TestContext.Out.WriteLine("PATCH generated xaml: OK");
            }
            catch (Exception ex)
            {
                TestContext.Out.WriteLine("PATCH generated xaml: FAILED -> " + ex.Message);
            }
        }
        finally
        {
            try { await WorkflowService.DeleteAsync(OrgUrl, id); }
            catch (Exception ex) { TestContext.Out.WriteLine("cleanup failed: " + ex.Message); }
        }
    }
}
