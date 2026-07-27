namespace Dataverse.Tests.Integration;

using Dataverse.Core.Models;
using Dataverse.Tests.Infrastructure;
using NUnit.Framework;

[TestFixture]
[Category("Integration")]
public sealed class CommandIntegrationTests : IntegrationTestBase
{
    private const string Table = "sample_purchaseorder";
    private const string JavaScriptWebResource = "sample_purchaseorder_correct_price.js";

    private static readonly Guid TableMetadataId = Guid.Parse("2c974c35-1fad-4409-9d1f-7465003ac518");

    [Test]
    public async Task ListCommands_ForPurchaseOrder_ReturnsResults()
    {
        var commands = await CommandService.ListAsync(OrgUrl, Table);

        Assert.That(commands.Count, Is.GreaterThan(0));
        Assert.That(commands.All(c => c.LocationName != $"Value{c.Location}"),
            "Every location value should map to a known CommandLocation member.");
    }

    [Test]
    public async Task GetTableMetadataId_ResolvesTheContextEntityLookupValue()
    {
        var metadataId = await CommandService.GetTableMetadataIdAsync(OrgUrl, Table);

        Assert.That(metadataId, Is.EqualTo(TableMetadataId));
    }

    [Test]
    public async Task Create_ThenGet_ThenDelete_RoundTrips()
    {
        var webResourceId = await WebResourceService.FindIdByNameAsync(OrgUrl, JavaScriptWebResource);
        Assert.That(webResourceId, Is.Not.Null, $"Expected '{JavaScriptWebResource}' to exist on contoso-dev.");

        var name = $"sample.mcptest.Command.{Guid.NewGuid():N}";
        var parameters = new[]
        {
            new CommandParameter((int)CommandParameterType.SelectedControlSelectedItemIds, null),
            new CommandParameter((int)CommandParameterType.SelectedControl, null)
        };

        var id = await CommandService.CreateAsync(
            OrgUrl,
            Table,
            name,
            buttonLabelText: "MCP Test",
            location: (int)CommandLocation.MainGrid,
            javaScriptWebResourceId: webResourceId!.Value,
            functionName: "Sample.PurchaseOrder.CorrectPrice.onGridButton",
            parameters: parameters,
            tooltipTitle: "MCP Test");

        try
        {
            Assert.That(id, Is.Not.EqualTo(Guid.Empty));

            var detail = await CommandService.GetAsync(OrgUrl, id);
            Assert.That(detail, Is.Not.Null);

            // origin must come out as Default — Migrated rows never render and cannot be patched back.
            Assert.That(detail!.Origin, Is.EqualTo((int)CommandOrigin.Default));
            Assert.That(detail.VisibilityType, Is.EqualTo((int)CommandVisibilityType.None));
            Assert.That(detail.ContextEntityMetadataId, Is.EqualTo(TableMetadataId));
            Assert.That(detail.OnClickEventJavaScriptWebResourceId, Is.EqualTo(webResourceId.Value));
            Assert.That(detail.Parameters.Select(p => p.Type), Is.EqualTo(new[] { 23, 12 }));

            await CommandService.UpdateAsync(
                OrgUrl, id, new Dictionary<string, object?> { ["buttonlabeltext"] = "MCP Test 2" });

            var updated = await CommandService.GetAsync(OrgUrl, id);
            Assert.That(updated!.ButtonLabelText, Is.EqualTo("MCP Test 2"));
        }
        finally
        {
            await CommandService.DeleteAsync(OrgUrl, id);
        }
    }

    [Test]
    public async Task Origin_IsIgnoredOnUpdate()
    {
        var webResourceId = await WebResourceService.FindIdByNameAsync(OrgUrl, JavaScriptWebResource);
        Assert.That(webResourceId, Is.Not.Null);

        var id = await CommandService.CreateAsync(
            OrgUrl,
            Table,
            $"sample.mcptest.Origin.{Guid.NewGuid():N}",
            buttonLabelText: "MCP Origin Test",
            location: (int)CommandLocation.Form,
            javaScriptWebResourceId: webResourceId!.Value,
            functionName: "Sample.PurchaseOrder.CorrectPrice.onFormButton",
            parameters: new[] { new CommandParameter((int)CommandParameterType.PrimaryControl, null) });

        try
        {
            await CommandService.UpdateAsync(
                OrgUrl,
                id,
                new Dictionary<string, object?> { ["origin"] = (int)CommandOrigin.Migrated });

            var detail = await CommandService.GetAsync(OrgUrl, id);

            // Dataverse answers 200 and keeps the stored value — this is why origin has to be right on create.
            Assert.That(detail!.Origin, Is.EqualTo((int)CommandOrigin.Default));
        }
        finally
        {
            await CommandService.DeleteAsync(OrgUrl, id);
        }
    }

    [Test]
    public async Task PublishCustomizations_ForPurchaseOrder_Succeeds()
    {
        var parameterXml = await PublishService.PublishAsync(OrgUrl, entities: new[] { Table });

        Assert.That(parameterXml, Does.Contain($"<entity>{Table}</entity>"));
    }
}
