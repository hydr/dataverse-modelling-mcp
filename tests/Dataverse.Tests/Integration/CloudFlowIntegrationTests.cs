namespace Dataverse.Tests.Integration;

using Dataverse.Tests.Infrastructure;
using NUnit.Framework;

[TestFixture]
[Category("Integration")]
public sealed class CloudFlowIntegrationTests : IntegrationTestBase
{
    private const string TestFlowId = "b8dde7d1-2316-f400-904b-cae537785fb2";
    private const string TestFlowDisplayName = "Lead Distribution Assistent";

    [Test]
    public async Task ListFlows_ReturnsResults()
    {
        var flows = await CloudFlowService.ListAsync(FlowRegion, EnvironmentId);

        Assert.That(flows.Count, Is.GreaterThan(0),
            "Expected at least one cloud flow in the environment.");
    }

    [Test]
    public async Task GetFlow_ReturnsLeadDistributionFlow()
    {
        var detail = await CloudFlowService.GetAsync(FlowRegion, EnvironmentId, TestFlowId);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.DisplayName, Is.EqualTo(TestFlowDisplayName));
    }

    [Test]
    public async Task GetFlowRuns_ReturnsHistory()
    {
        var runs = await CloudFlowService.GetRunsAsync(FlowRegion, EnvironmentId, TestFlowId);

        Assert.That(runs, Is.Not.Null,
            "Expected a run list (may be empty if the flow has never been triggered).");
    }

    [Test]
    public async Task DescribeFlow_ReturnsNonEmptyDescription()
    {
        var description = await CloudFlowService.DescribeAsync(FlowRegion, EnvironmentId, TestFlowId);

        Assert.That(description.FullDescription, Is.Not.Empty);
        Assert.That(description.FullDescription, Does.Contain(TestFlowDisplayName));
        Assert.That(description.TriggerSummary, Is.Not.Empty);
    }

    [Test]
    public async Task ListFlows_ContainsLeadDistributionFlow()
    {
        var flows = await CloudFlowService.ListAsync(FlowRegion, EnvironmentId);

        Assert.That(flows.Any(f => f.DisplayName == TestFlowDisplayName), Is.True,
            $"Expected to find '{TestFlowDisplayName}' in the flows list.");
    }
}
