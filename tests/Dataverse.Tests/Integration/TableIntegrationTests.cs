namespace Dataverse.Tests.Integration;

using Dataverse.Tests.Infrastructure;
using NUnit.Framework;

[TestFixture]
[Category("Integration")]
public sealed class TableIntegrationTests : IntegrationTestBase
{
    private const string TestTableLogicalName = "sample_mcptest";

    [Test]
    public async Task ListTables_ReturnsXvMcpTest()
    {
        var tables = await TableService.ListAsync(OrgUrl);

        Assert.That(tables.Any(t => t.LogicalName == TestTableLogicalName), Is.True,
            $"Expected table '{TestTableLogicalName}' in the list.");
    }

    [Test]
    public async Task ListTables_ContainsAccountTable()
    {
        var tables = await TableService.ListAsync(OrgUrl);

        Assert.That(tables.Any(t => t.LogicalName == "account"), Is.True,
            "Expected standard 'account' table to be present.");
    }

    [Test]
    public async Task GetTable_ReturnsXvMcpTestDetail()
    {
        var detail = await TableService.GetAsync(OrgUrl, TestTableLogicalName);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.LogicalName, Is.EqualTo(TestTableLogicalName));
        Assert.That(detail.IsCustomEntity, Is.True);
        Assert.That(detail.Attributes.Any(a => a.LogicalName == "sample_name"), Is.True,
            "Expected 'sample_name' column in sample_mcptest.");
    }

    [Test]
    public async Task GetTable_ReturnsNull_ForNonExistentTable()
    {
        try
        {
            var detail = await TableService.GetAsync(OrgUrl, "nonexistent_table_xyz_fake");
            Assert.That(detail, Is.Null, "Expected null for a non-existent table.");
        }
        catch (HttpRequestException ex) when ((int?)ex.StatusCode == 404)
        {
            Assert.Pass("Service threw 404 HttpRequestException for non-existent table, which is acceptable.");
        }
    }

    [Test]
    public async Task GetTable_Account_HasExpectedColumns()
    {
        var detail = await TableService.GetAsync(OrgUrl, "account");

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.Attributes.Any(a => a.LogicalName == "name"), Is.True,
            "Expected 'name' column on account table.");
        Assert.That(detail.Attributes.Any(a => a.LogicalName == "accountid"), Is.True,
            "Expected 'accountid' column on account table.");
    }

    [Test]
    public async Task AddColumn_ToXvMcpTest_ThenVerify()
    {
        var schemaName = "sample_integrationtest_score";
        // Metadata API requires full @odata.type annotations on all nested label/managed-property objects
        var columnDef = new Dictionary<string, object?>
        {
            ["@odata.type"] = "Microsoft.Dynamics.CRM.DecimalAttributeMetadata",
            ["SchemaName"] = schemaName,
            ["DisplayName"] = new Dictionary<string, object?>
            {
                ["@odata.type"] = "Microsoft.Dynamics.CRM.Label",
                ["LocalizedLabels"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["@odata.type"] = "Microsoft.Dynamics.CRM.LocalizedLabel",
                        ["Label"] = "Integration Test Score",
                        ["LanguageCode"] = 1033
                    }
                }
            },
            ["RequiredLevel"] = new Dictionary<string, object?>
            {
                ["@odata.type"] = "Microsoft.Dynamics.CRM.AttributeRequiredLevelManagedProperty",
                ["Value"] = "None",
                ["CanBeChanged"] = true,
                ["ManagedPropertyLogicalName"] = "canmodifyrequirementlevelsettings"
            },
            ["Precision"] = 2,
            ["MinValue"] = 0.0,
            ["MaxValue"] = 100.0
        };

        try
        {
            await TableService.AddColumnAsync(OrgUrl, TestTableLogicalName, columnDef);
        }
        catch (HttpRequestException ex)
        {
            if (ex.Message.Contains("DuplicateAttributeSchemaName") || ex.Message.Contains("already exists"))
                Assert.Ignore("Column already exists from a previous test run; skipping creation.");
            throw;
        }

        var detail = await TableService.GetAsync(OrgUrl, TestTableLogicalName);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.Attributes.Any(a => a.LogicalName == schemaName.ToLowerInvariant()), Is.True,
            $"Expected newly added column '{schemaName}' to appear in table attributes.");
    }
}
