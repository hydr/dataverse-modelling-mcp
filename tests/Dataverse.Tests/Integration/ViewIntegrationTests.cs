namespace Dataverse.Tests.Integration;

using Dataverse.Tests.Infrastructure;
using NUnit.Framework;

[TestFixture]
[Category("Integration")]
public sealed class ViewIntegrationTests : IntegrationTestBase
{
    private static readonly Guid KnownViewId = Guid.Parse("00000000-0000-0000-00aa-000010001001");
    private const string KnownViewName = "Meine aktiven Firmen";

    [Test]
    public async Task ListViews_ForAccount_ReturnsResults()
    {
        var views = await ViewService.ListAsync(OrgUrl, "account");

        Assert.That(views.Count, Is.GreaterThan(0),
            "Expected at least one view for the account entity.");
    }

    [Test]
    public async Task GetView_ReturnsMeineAktivenFirmen()
    {
        var detail = await ViewService.GetAsync(OrgUrl, KnownViewId);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.Name, Is.EqualTo(KnownViewName));
    }

    [Test]
    public async Task ListViews_ContainsPublicViews()
    {
        var views = await ViewService.ListAsync(OrgUrl, "account");

        Assert.That(views.Any(v => v.ViewType == "Public"), Is.True,
            "Expected at least one public (querytype=0) view for account.");
    }

    [Test]
    public async Task GetView_ReturnsNull_ForNonExistentView()
    {
        var nonExistentId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

        try
        {
            var detail = await ViewService.GetAsync(OrgUrl, nonExistentId);
            Assert.That(detail, Is.Null, "Expected null for a non-existent view.");
        }
        catch (HttpRequestException ex) when ((int?)ex.StatusCode == 404)
        {
            Assert.Pass("Service threw 404 HttpRequestException for non-existent view, which is acceptable.");
        }
    }
}
