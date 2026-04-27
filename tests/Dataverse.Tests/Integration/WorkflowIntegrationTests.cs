namespace Dataverse.Tests.Integration;

using Dataverse.Tests.Infrastructure;
using NUnit.Framework;

[TestFixture]
[Category("Integration")]
public sealed class WorkflowIntegrationTests : IntegrationTestBase
{
    private static readonly Guid TestWorkflowId = Guid.Parse("32f36732-b7a9-f011-bbd3-000d3a661d0e");
    private const string TestWorkflowName = "Verbuchungsdatum setzen (Rst)";

    [Test]
    public async Task ListWorkflows_ReturnsResults()
    {
        var workflows = await WorkflowService.ListAsync(OrgUrl);

        Assert.That(workflows.Count, Is.GreaterThan(0),
            "Expected at least one workflow in the environment.");
    }

    [Test]
    public async Task GetWorkflow_ReturnsVerbuchungsdatumSetzen()
    {
        var detail = await WorkflowService.GetAsync(OrgUrl, TestWorkflowId);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.Name, Is.EqualTo(TestWorkflowName));
    }

    [Test]
    public async Task ValidateWorkflow_ReturnsReport()
    {
        var report = await WorkflowService.ValidateAsync(OrgUrl, TestWorkflowId);

        Assert.That(report, Is.Not.Null);
        Assert.That(report.WorkflowId, Is.EqualTo(TestWorkflowId));
    }

    [Test]
    public async Task ListWorkflows_WithNameFilter_ReturnsMatchingResults()
    {
        var all = await WorkflowService.ListAsync(OrgUrl);
        var filtered = all.Where(w => w.Name != null &&
            w.Name.Contains("Verbuchungsdatum", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.That(filtered.Count, Is.GreaterThanOrEqualTo(1),
            "Expected at least one workflow matching 'Verbuchungsdatum'.");
    }

    [Test]
    public async Task ListWorkflows_ContainsKnownWorkflow()
    {
        var workflows = await WorkflowService.ListAsync(OrgUrl);

        Assert.That(workflows.Any(w => w.WorkflowId == TestWorkflowId), Is.True,
            $"Expected to find workflow {TestWorkflowId} in the list.");
    }
}
