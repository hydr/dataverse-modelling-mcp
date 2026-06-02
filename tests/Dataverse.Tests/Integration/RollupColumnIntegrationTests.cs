namespace Dataverse.Tests.Integration;

using Dataverse.Core.Workflows;
using Dataverse.Tests.Infrastructure;
using NUnit.Framework;

/// <summary>
/// Verifies that a rollup column (SourceType=2) with a generated RollupRule FormulaDefinition is
/// accepted by Dataverse. Opt-in (DATAVERSE_INTEGRATION_TESTS=true). Creates a COUNT rollup on
/// account over its related opportunities, then deletes the column in teardown.
/// </summary>
[TestFixture]
[Category("Integration")]
public sealed class RollupColumnIntegrationTests : IntegrationTestBase
{
    private const string ParentEntity = "account";
    private const string SchemaName = "sample_mcprolluptest";

    [TearDown]
    public async Task TearDown()
    {
        try { await TableService.DeleteColumnAsync(OrgUrl, ParentEntity, SchemaName.ToLowerInvariant()); }
        catch { /* best-effort cleanup */ }
    }

    [Test]
    public async Task CreateCountRollup_OverRelatedOpportunities()
    {
        // opportunity.sample_advertiseraccount -> account, relationship sample_account_opportunity_AdvertiserAccount.
        var rollup = new RollupDefinition(
            ChildEntity: "opportunity",
            RelationshipSchemaName: "sample_account_opportunity_AdvertiserAccount",
            LookupAttribute: "sample_advertiseraccount",
            Aggregate: RollupAggregate.Count);

        await TableService.AddRollupColumnAsync(
            OrgUrl, ParentEntity, SchemaName, "MCP Rollup Test", RollupNumericType.Integer, rollup);

        var detail = await TableService.GetAsync(OrgUrl, ParentEntity);
        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.Attributes.Any(a => a.LogicalName == SchemaName.ToLowerInvariant()), Is.True,
            "Rollup column was not found after creation.");
    }
}
