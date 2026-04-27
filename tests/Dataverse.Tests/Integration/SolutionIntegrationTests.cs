namespace Dataverse.Tests.Integration;

using Dataverse.Tests.Infrastructure;
using NUnit.Framework;

[TestFixture]
[Category("Integration")]
public sealed class SolutionIntegrationTests : IntegrationTestBase
{
    private const string TestSolutionUniqueName = "DV_MCP_Test";
    private const string KnownSolutionUniqueName = "CrossvertiseSchema";
    private const string EntityComponentId = "a0e66081-6242-f111-bec6-7c1e528730f7";

    [Test]
    public async Task ListSolutions_ReturnsAtLeastOne()
    {
        var solutions = await SolutionService.ListAsync(OrgUrl);

        Assert.That(solutions, Is.Not.Empty);
    }

    [Test]
    public async Task ListSolutions_ContainsDvMcpTestSolution()
    {
        var solutions = await SolutionService.ListAsync(OrgUrl);

        Assert.That(solutions.Any(s => s.UniqueName == TestSolutionUniqueName), Is.True,
            $"Expected to find solution '{TestSolutionUniqueName}' in the list.");
    }

    [Test]
    public async Task GetSolution_ReturnsDvMcpTestSolution()
    {
        var detail = await SolutionService.GetAsync(OrgUrl, TestSolutionUniqueName);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.UniqueName, Is.EqualTo(TestSolutionUniqueName));
        Assert.That(detail.FriendlyName, Is.Not.Empty);
        Assert.That(detail.Version, Is.Not.Empty);
        Assert.That(detail.Components.Count, Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public async Task GetSolution_ReturnsNull_ForNonExistentSolution()
    {
        var detail = await SolutionService.GetAsync(OrgUrl, "NonExistentSolution_XXXX_Fake");

        Assert.That(detail, Is.Null);
    }

    [Test]
    public async Task ListSolutions_ContainsKnownSolution_CrossvertiseSchema()
    {
        var solutions = await SolutionService.ListAsync(OrgUrl);

        Assert.That(solutions.Any(s => s.UniqueName == KnownSolutionUniqueName), Is.True,
            $"Expected to find '{KnownSolutionUniqueName}' in list of solutions.");
    }

    [Test]
    public async Task GetSolution_HasExpectedEntityComponent()
    {
        var detail = await SolutionService.GetAsync(OrgUrl, TestSolutionUniqueName);

        Assert.That(detail, Is.Not.Null);
        var componentId = Guid.Parse(EntityComponentId);
        Assert.That(detail!.Components.Any(c => c.ComponentId == componentId), Is.True,
            $"Expected component {EntityComponentId} in solution components.");
    }
}
