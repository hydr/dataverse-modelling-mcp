namespace Dataverse.Tests.Services;

using System.Net;
using System.Text;
using System.Text.Json;
using Dataverse.Core.Auth;
using Dataverse.Core.Clients;
using Dataverse.Core.Models;
using Dataverse.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using NUnit.Framework;

/// <summary>
/// Covers "which solution carries this change?".
/// </summary>
/// <remarks>
/// The question came up three times in one session and was answered wrongly twice, because it hangs
/// on <c>rootcomponentbehavior</c> per solution and the same table sits in several solutions with
/// different values.
/// </remarks>
[TestFixture]
public sealed class EntitySolutionMapServiceTests
{
    private const string OrgUrl = "https://test.crm4.dynamics.com";
    private static readonly Guid TableId = Guid.Parse("4c0e7f31-a9de-45ea-b983-363b946f18c5");
    private static readonly Guid RowInFormsSolution = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RowInPurchaseSolution = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid FormsSolutionId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid PurchaseSolutionId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid ColumnId = Guid.Parse("06145688-ca2a-f011-8c4d-002248a42c4e");

    private Mock<HttpMessageHandler> _handlerMock = null!;
    private EntitySolutionMapService _svc = null!;

    [SetUp]
    public void SetUp()
    {
        _handlerMock = new Mock<HttpMessageHandler>();

        var tokenProviderMock = new Mock<ITokenProvider>();
        tokenProviderMock
            .Setup(t => t.GetTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-token");

        var client = new DataverseHttpClient(
            new HttpClient(_handlerMock.Object),
            tokenProviderMock.Object,
            NullLogger<DataverseHttpClient>.Instance);

        _svc = new EntitySolutionMapService(client, NullLogger<EntitySolutionMapService>.Instance);
    }

    private void Route(Func<string, string> bodyFactory)
    {
        _handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(bodyFactory(req.RequestUri!.ToString()), Encoding.UTF8, "application/json")
            });
    }

    /// <summary>Two solutions holding the same table with different behaviours — the real case.</summary>
    private void RouteTwoSolutions() => Route(url =>
    {
        if (url.Contains("EntityDefinitions(LogicalName='invoice')", StringComparison.Ordinal))
            return JsonSerializer.Serialize(new { MetadataId = TableId.ToString() });

        if (url.Contains("componenttype eq 1", StringComparison.Ordinal))
            return JsonSerializer.Serialize(new
            {
                value = new[]
                {
                    new
                    {
                        solutioncomponentid = RowInFormsSolution.ToString(),
                        rootcomponentbehavior = 0,
                        _solutionid_value = FormsSolutionId.ToString()
                    },
                    new
                    {
                        solutioncomponentid = RowInPurchaseSolution.ToString(),
                        rootcomponentbehavior = 1,
                        _solutionid_value = PurchaseSolutionId.ToString()
                    }
                }
            });

        if (url.Contains($"solutions({FormsSolutionId:D})", StringComparison.Ordinal))
            return JsonSerializer.Serialize(new { uniquename = "CrossvertiseForms", ismanaged = false });

        if (url.Contains($"solutions({PurchaseSolutionId:D})", StringComparison.Ordinal))
            return JsonSerializer.Serialize(new { uniquename = "CrossvertisePurchaseOrders", ismanaged = false });

        // Only the behaviour-1 solution holds a subcomponent explicitly.
        if (url.Contains($"rootsolutioncomponentid eq {RowInPurchaseSolution:D}", StringComparison.Ordinal))
            return JsonSerializer.Serialize(new
            {
                value = new[]
                {
                    new
                    {
                        solutioncomponentid = Guid.NewGuid().ToString(),
                        objectid = ColumnId.ToString(),
                        componenttype = 2
                    }
                }
            });

        return "{\"value\":[]}";
    });

    [Test]
    public async Task ReportsEachSolution_WithItsBehaviourAsCodeAndLabel()
    {
        RouteTwoSolutions();

        var map = await _svc.GetAsync(OrgUrl, "invoice", resolveComponentNames: false);

        Assert.That(map.SolutionCount, Is.EqualTo(2));

        var forms = map.Solutions.Single(s => s.SolutionUniqueName == "CrossvertiseForms");
        var purchase = map.Solutions.Single(s => s.SolutionUniqueName == "CrossvertisePurchaseOrders");

        Assert.Multiple(() =>
        {
            Assert.That(forms.RootComponentBehavior, Is.EqualTo(0));
            Assert.That(forms.RootComponentBehaviorName, Is.EqualTo("IncludeSubcomponents"));
            Assert.That(purchase.RootComponentBehavior, Is.EqualTo(1));
            Assert.That(purchase.RootComponentBehaviorName, Is.EqualTo("DoNotIncludeSubcomponents"));
        });
    }

    /// <summary>
    /// Subcomponents are found through the platform's own back-reference, so no guessing about which
    /// column belongs to which table is involved.
    /// </summary>
    [Test]
    public async Task FindsSubcomponents_ThroughTheRootBackReference()
    {
        RouteTwoSolutions();

        var map = await _svc.GetAsync(OrgUrl, "invoice", resolveComponentNames: false);

        var forms = map.Solutions.Single(s => s.SolutionUniqueName == "CrossvertiseForms");
        var purchase = map.Solutions.Single(s => s.SolutionUniqueName == "CrossvertisePurchaseOrders");

        Assert.Multiple(() =>
        {
            Assert.That(forms.SubcomponentCount, Is.Zero, "behaviour 0 covers its subcomponents");
            Assert.That(purchase.SubcomponentCount, Is.EqualTo(1));
            Assert.That(purchase.Subcomponents[0].ComponentId, Is.EqualTo(ColumnId));
            Assert.That(purchase.Subcomponents[0].ComponentTypeName, Is.EqualTo("Attribute"));
        });
    }

    /// <summary>A root row also carries its own id, and it is not a subcomponent of itself.</summary>
    [Test]
    public async Task IgnoresTheRootRowsSelfReference()
    {
        Route(url =>
        {
            if (url.Contains("EntityDefinitions(LogicalName='invoice')", StringComparison.Ordinal))
                return JsonSerializer.Serialize(new { MetadataId = TableId.ToString() });

            if (url.Contains("componenttype eq 1", StringComparison.Ordinal))
                return JsonSerializer.Serialize(new
                {
                    value = new[]
                    {
                        new
                        {
                            solutioncomponentid = RowInPurchaseSolution.ToString(),
                            rootcomponentbehavior = 1,
                            _solutionid_value = PurchaseSolutionId.ToString()
                        }
                    }
                });

            if (url.Contains("rootsolutioncomponentid eq", StringComparison.Ordinal))
                return JsonSerializer.Serialize(new
                {
                    value = new[]
                    {
                        new
                        {
                            solutioncomponentid = RowInPurchaseSolution.ToString(),
                            objectid = TableId.ToString(),
                            componenttype = 1
                        },
                        new
                        {
                            solutioncomponentid = Guid.NewGuid().ToString(),
                            objectid = ColumnId.ToString(),
                            componenttype = 2
                        }
                    }
                });

            if (url.Contains("solutions(", StringComparison.Ordinal))
                return JsonSerializer.Serialize(new { uniquename = "S", ismanaged = false });

            return "{\"value\":[]}";
        });

        var map = await _svc.GetAsync(OrgUrl, "invoice", resolveComponentNames: false);

        Assert.That(map.Solutions[0].SubcomponentCount, Is.EqualTo(1));
        Assert.That(map.Solutions[0].Subcomponents[0].ComponentType, Is.EqualTo(2));
    }

    [Test]
    public void Throws_WhenTheTableDoesNotExist()
    {
        Route(_ => "{}");

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _svc.GetAsync(OrgUrl, "nosuchtable"));

        Assert.That(ex!.Message, Does.Contain("nosuchtable"));
    }

    // ---------------------------------------------------------------- summary

    private static EntitySolutionMembership Membership(string name, int? behavior, int subcomponents = 0) =>
        new(name, Guid.NewGuid(), false, behavior, SolutionService.MapRootComponentBehavior(behavior),
            Guid.NewGuid(), subcomponents, []);

    /// <summary>
    /// The summary has to state the consequence, because the codes are what nobody remembers.
    /// </summary>
    [Test]
    public void Summary_NamesTheSolutionThatCarriesSubcomponentsAutomatically()
    {
        var summary = EntitySolutionMapService.BuildSummary("invoice",
            [Membership("CrossvertiseForms", 0), Membership("CrossvertisePurchaseOrders", 1, 3)]);

        Assert.Multiple(() =>
        {
            Assert.That(summary, Does.Contain("travel automatically with CrossvertiseForms"));
            Assert.That(summary, Does.Contain("added explicitly in CrossvertisePurchaseOrders"));
            Assert.That(summary, Does.Contain("3 held explicitly"));
        });
    }

    [Test]
    public void Summary_SaysSoWhenNoSolutionPicksUpSubcomponents()
    {
        var summary = EntitySolutionMapService.BuildSummary("invoice",
            [Membership("CrossvertiseForms", 2), Membership("CrossvertiseInvoicing", 1)]);

        Assert.That(summary, Does.Contain("No solution holds 'invoice' with rootcomponentbehavior 0"));
    }

    [Test]
    public void Summary_SaysSoWhenTheTableIsInNoSolution()
    {
        Assert.That(
            EntitySolutionMapService.BuildSummary("invoice", []),
            Does.Contain("not a component of any solution"));
    }
}
