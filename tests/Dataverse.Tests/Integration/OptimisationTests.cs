namespace Dataverse.Tests.Integration;

using Dataverse.Core.Services;
using Dataverse.Core.Workflows;
using Dataverse.Tests.Infrastructure;
using NUnit.Framework;

/// <summary>
/// The conveniences added after working through real workflows: dry run, activation diagnosis and
/// finding workflows by name prefix.
/// </summary>
[TestFixture]
[Category("Integration")]
public sealed class OptimisationTests : IntegrationTestBase
{
    private Guid _workflowId;

    [TearDown]
    public async Task Cleanup()
    {
        if (_workflowId == Guid.Empty)
            return;

        try { await WorkflowService.SetStateAsync(OrgUrl, _workflowId, activate: false); } catch { }
        try { await WorkflowService.DeleteAsync(OrgUrl, _workflowId); } catch { }
        _workflowId = Guid.Empty;
    }

    private static WorkflowDefinition Simple(string literal) => new()
    {
        PrimaryEntity = "salesorder",
        Steps =
        [
            new WorkflowStep
            {
                Kind = WorkflowStepKind.UpdateRecord,
                Description = "Beschreibung setzen",
                Attributes =
                [
                    new WorkflowAttributeAssignment
                    {
                        Attribute = "description",
                        Value = new WorkflowValue { Literal = literal }
                    }
                ]
            }
        ]
    };

    [Test]
    public async Task DryRun_ReportsTheChangeWithoutWriting()
    {
        _workflowId = await WorkflowService.CreateAsync(
            OrgUrl, $"ZZ DryRun {Guid.NewGuid():N}", "salesorder");

        // First write for real, so there is a before-state to compare against.
        var first = await WorkflowAuthoringService.SetDefinitionAsync(OrgUrl, _workflowId, Simple("erst"));
        Assert.That(first.Applied, Is.True);

        var before = (await WorkflowService.GetAsync(OrgUrl, _workflowId))!.Xaml;

        var dry = await WorkflowAuthoringService.SetDefinitionAsync(
            OrgUrl, _workflowId, Simple("dann"), dryRun: true);

        TestContext.Out.WriteLine(dry.Diff);

        Assert.That(dry.Applied, Is.False, "a dry run must not write");
        Assert.That(dry.Diff, Is.Not.Null.And.Contains("updateRecord"));
        Assert.That((await WorkflowService.GetAsync(OrgUrl, _workflowId))!.Xaml, Is.EqualTo(before),
            "the stored XAML must be untouched after a dry run");
    }

    [Test]
    public async Task Diagnose_PointsAtTheStepThatWillNotActivate()
    {
        var diagnoser = new WorkflowActivationDiagnoser(WorkflowService, WorkflowAuthoringService);

        // A definition where one step is fine and one references a record nothing creates.
        var definition = new WorkflowDefinition
        {
            PrimaryEntity = "salesorder",
            Steps =
            [
                Simple("harmlos").Steps[0],
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.UpdateRecord,
                    Description = "Verweis ins Leere",
                    Attributes =
                    [
                        new WorkflowAttributeAssignment
                        {
                            Attribute = "description",
                            Value = new WorkflowValue
                            {
                                Kind = WorkflowValueKind.Field,
                                Fields = ["email.activityid"],
                                FromStep = "CreateStep99"
                            }
                        }
                    ]
                }
            ]
        };

        var result = await diagnoser.DiagnoseAsync(OrgUrl, definition, "salesorder", realtime: false);

        foreach (var attempt in result.Attempts)
            TestContext.Out.WriteLine("  " + attempt);
        TestContext.Out.WriteLine("culprit: " + result.Culprit);

        Assert.That(result.Activated, Is.False, "the broken definition must not activate");
        Assert.That(result.Culprit, Is.Not.Null.And.Contains("Verweis ins Leere"),
            "the diagnosis must name the offending step");
    }

    [Test]
    public async Task FindByNamePrefix_ListsDefinitionsAndActivationCopies()
    {
        var matches = await WorkflowService.FindByNamePrefixAsync(OrgUrl, "ZZ ");

        TestContext.Out.WriteLine($"{matches.Count} leftovers with prefix 'ZZ '");
        foreach (var m in matches.Take(5))
            TestContext.Out.WriteLine($"  {m.Type,-16} {(m.IsActivated ? "activated" : "draft    ")} {m.Name}");

        Assert.That(matches.All(m => m.Name.StartsWith("ZZ ", StringComparison.Ordinal)), Is.True);
    }
}
