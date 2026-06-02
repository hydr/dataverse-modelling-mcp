namespace Dataverse.Tests.Workflows;

using System.Xml.Linq;
using Dataverse.Core.Workflows;
using NUnit.Framework;

[TestFixture]
public sealed class RollupFormulaTests
{
    [Test]
    public void Build_SumRollup_MatchesRealStructure()
    {
        // Mirrors opportunity.sample_revenue_orders: SUM of salesorder.totallineitemamount
        // over the opportunity_sales_orders (1:N) relationship.
        var def = new RollupDefinition(
            ChildEntity: "salesorder",
            RelationshipSchemaName: "opportunity_sales_orders",
            LookupAttribute: "opportunityid",
            Aggregate: RollupAggregate.Sum,
            AggregateAttribute: "totallineitemamount");

        var xaml = RollupFormulaBuilder.Build(def);

        Assert.DoesNotThrow(() => XDocument.Parse(xaml));
        Assert.That(xaml, Does.Contain("XrmWorkflow00000000000000000000000000000000"));
        Assert.That(xaml, Does.Contain("DisplayName=\"RollupRuleStep1\""));
        Assert.That(xaml, Does.Contain("DisplayName=\"Source\""));
        Assert.That(xaml, Does.Contain("DisplayName=\"Target\""));
        Assert.That(xaml, Does.Contain("DisplayName=\"Aggregate\""));
        // Relationship link encoding.
        Assert.That(xaml, Does.Contain("relatedlinked_opportunity_sales_orders#opportunityid#salesorder#Temp"));
        Assert.That(xaml, Does.Contain("salesorder.opportunityid.opportunity_sales_orders"));
        // Aggregate reads the numeric child attribute and sums it.
        Assert.That(xaml, Does.Contain("Attribute=\"totallineitemamount\""));
        Assert.That(xaml, Does.Contain(">Sum</InArgument>"));
    }

    [Test]
    public void Build_CountRollup_UsesChildPrimaryKey()
    {
        // Mirrors account.sample_number_opportunities: COUNT of related opportunities (no filter).
        var def = new RollupDefinition(
            ChildEntity: "opportunity",
            RelationshipSchemaName: "sample_account_opportunity_AdvertiserAccount",
            LookupAttribute: "sample_advertiseraccount",
            Aggregate: RollupAggregate.Count);

        var xaml = RollupFormulaBuilder.Build(def);

        Assert.DoesNotThrow(() => XDocument.Parse(xaml));
        Assert.That(xaml, Does.Contain("relatedlinked_sample_account_opportunity_AdvertiserAccount#sample_advertiseraccount#opportunity#Temp"));
        // Count aggregates the child primary key, derived as '{child}id'.
        Assert.That(xaml, Does.Contain("Attribute=\"opportunityid\""));
        Assert.That(xaml, Does.Contain(">Count</InArgument>"));
    }

    [Test]
    public void Build_SumWithoutAggregateAttribute_Throws()
    {
        var def = new RollupDefinition("salesorder", "opportunity_sales_orders", "opportunityid", RollupAggregate.Sum);
        Assert.Throws<ArgumentException>(() => RollupFormulaBuilder.Build(def));
    }
}
