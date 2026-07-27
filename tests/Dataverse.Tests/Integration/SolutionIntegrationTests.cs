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

    // MetadataId of the 'account' table — a stock table carrying many managed layers.
    private const string AccountMetadataId = "70816501-edb9-4740-a16c-6a5efbc05d84";
    // MetadataId of 'sample_purchaseorder' — a custom table, unmanaged only (single layer).
    private const string PurchaseOrderMetadataId = "2c974c35-1fad-4409-9d1f-7465003ac518";

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

    [Test]
    public async Task CheckLayers_ForAccountEntity_ReturnsStackedManagedLayers()
    {
        var info = await SolutionService.CheckLayersAsync(OrgUrl, Guid.Parse(AccountMetadataId), 1);

        TestContext.Out.WriteLine(
            $"{info.ComponentName} ({info.ComponentTypeName}): {info.LayerCount} layer(s), " +
            $"top = {info.TopLayerSolutionName}");
        foreach (var l in info.Layers)
        {
            TestContext.Out.WriteLine($"  {l.Order,3}  {l.SolutionName,-32} {l.PublisherName}");
        }

        Assert.That(info.ComponentTypeName, Is.EqualTo("Entity"));
        Assert.That(info.ComponentName, Is.EqualTo("Account"));
        Assert.That(info.LayerCount, Is.GreaterThan(1), "account is expected to carry several managed layers.");
        Assert.That(info.Layers.Select(l => l.SolutionName), Does.Contain("System"));
        Assert.That(info.Layers.Select(l => l.Order), Is.Ordered, "layers must come back bottom-up.");
        Assert.That(info.Layers[^1].IsTopLayer, Is.True);
        Assert.That(info.TopLayerSolutionName, Is.EqualTo(info.Layers[^1].SolutionName));
    }

    [Test]
    public async Task CheckLayers_DiffersPerComponent()
    {
        var account = await SolutionService.CheckLayersAsync(OrgUrl, Guid.Parse(AccountMetadataId), 1);
        var purchaseOrder = await SolutionService.CheckLayersAsync(OrgUrl, Guid.Parse(PurchaseOrderMetadataId), 1);

        TestContext.Out.WriteLine($"account        : {account.LayerCount} layer(s)");
        TestContext.Out.WriteLine($"sample_purchaseorder: {purchaseOrder.LayerCount} layer(s)");

        Assert.That(purchaseOrder.ComponentName, Is.EqualTo("sample_purchaseorder"));
        Assert.That(account.LayerCount, Is.Not.EqualTo(purchaseOrder.LayerCount),
            "Two different components must not produce the same layer list.");
    }

    [Test]
    public void CheckLayers_Throws_ForUnknownComponentType()
    {
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await SolutionService.CheckLayersAsync(OrgUrl, Guid.Parse(AccountMetadataId), 999999));

        Assert.That(ex!.Message, Does.Contain("999999"));
    }
}
