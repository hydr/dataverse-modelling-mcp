namespace Dataverse.Tests.Integration;

using System.Text;
using Dataverse.Core.Models;
using Dataverse.Tests.Infrastructure;
using NUnit.Framework;

[TestFixture]
[Category("Integration")]
public sealed class WebResourceIntegrationTests : IntegrationTestBase
{
    private const string KnownWebResource = "sample_purchaseorder_correct_price.js";
    private const string ScratchWebResource = "sample_mcptest_scratch.js";

    [Test]
    public async Task ListWebResources_WithXvPrefix_ReturnsResults()
    {
        var resources = await WebResourceService.ListAsync(OrgUrl, "sample_");

        Assert.That(resources.Count, Is.GreaterThan(0));
        Assert.That(resources.All(r => r.Name.StartsWith("sample_", StringComparison.OrdinalIgnoreCase)), Is.True);
    }

    [Test]
    public async Task GetWebResource_ByName_DecodesJavaScriptContent()
    {
        var id = await WebResourceService.FindIdByNameAsync(OrgUrl, KnownWebResource);
        Assert.That(id, Is.Not.Null, $"Expected '{KnownWebResource}' to exist on contoso-dev.");

        var detail = await WebResourceService.GetAsync(OrgUrl, id!.Value);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.WebResourceType, Is.EqualTo((int)WebResourceType.JScript));
        Assert.That(detail.ContentIsText, Is.True);
        Assert.That(detail.Content, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task FindIdByName_ReturnsNull_ForUnknownName()
    {
        var id = await WebResourceService.FindIdByNameAsync(OrgUrl, "sample_this_does_not_exist_12345.js");

        Assert.That(id, Is.Null);
    }

    [Test]
    public async Task Upsert_CreatesThenUpdatesThenPublishes()
    {
        var first = Encoding.UTF8.GetBytes("// created by the MCP integration test\n");
        var (id, created, type) = await WebResourceService.UpsertAsync(
            OrgUrl, ScratchWebResource, first, displayName: "MCP scratch");

        try
        {
            Assert.That(id, Is.Not.EqualTo(Guid.Empty));
            Assert.That(type, Is.EqualTo((int)WebResourceType.JScript));

            var second = Encoding.UTF8.GetBytes("// updated by the MCP integration test\n");
            var (updatedId, updatedCreated, _) = await WebResourceService.UpsertAsync(
                OrgUrl, ScratchWebResource, second);

            Assert.That(updatedId, Is.EqualTo(id));
            Assert.That(updatedCreated, Is.False, "Second upsert must patch the existing row.");

            // A plain GET returns the PUBLISHED content — an unpublished PATCH stays invisible,
            // which is why webresource_upsert publishes by default.
            var beforePublish = await WebResourceService.GetAsync(OrgUrl, id);
            Assert.That(beforePublish!.Content, Does.Contain("created by the MCP integration test"));

            await PublishService.PublishAsync(OrgUrl, webResources: new[] { ScratchWebResource });

            var afterPublish = await WebResourceService.GetAsync(OrgUrl, id);
            Assert.That(afterPublish!.Content, Does.Contain("updated by the MCP integration test"));
        }
        finally
        {
            if (created)
            {
                await WebResourceService.DeleteAsync(OrgUrl, id);
            }
        }
    }
}
