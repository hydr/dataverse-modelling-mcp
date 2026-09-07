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
/// Covers "can this web resource go?".
/// </summary>
/// <remarks>
/// The risk here is a confident "nothing found" that skipped the places where a reference actually
/// lives, so the tests care as much about what the answer admits to not having searched.
/// </remarks>
[TestFixture]
public sealed class WebResourceUsageServiceTests
{
    private const string OrgUrl = "https://test.crm4.dynamics.com";
    private static readonly Guid WebResourceId = Guid.Parse("beb1af8b-1c05-f011-bae3-7c1e52873506");
    private static readonly Guid FormId = Guid.Parse("2c488b41-1405-f011-bae3-7c1e52873506");

    private Mock<HttpMessageHandler> _handlerMock = null!;
    private WebResourceUsageService _svc = null!;
    private List<string> _urls = null!;

    [SetUp]
    public void SetUp()
    {
        _handlerMock = new Mock<HttpMessageHandler>();
        _urls = [];

        var tokenProviderMock = new Mock<ITokenProvider>();
        tokenProviderMock
            .Setup(t => t.GetTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-token");

        var client = new DataverseHttpClient(
            new HttpClient(_handlerMock.Object),
            tokenProviderMock.Object,
            NullLogger<DataverseHttpClient>.Instance);

        _svc = new WebResourceUsageService(
            client,
            new ComponentDependencyService(client, NullLogger<ComponentDependencyService>.Instance),
            new RibbonService(
                client,
                new SolutionService(client, NullLogger<SolutionService>.Instance),
                new PublishService(
                    client,
                    new WebResourceService(client, NullLogger<WebResourceService>.Instance),
                    NullLogger<PublishService>.Instance),
                NullLogger<RibbonService>.Instance),
            NullLogger<WebResourceUsageService>.Instance);
    }

    private void Route()
    {
        _handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                var url = req.RequestUri!.ToString();
                _urls.Add(url);

                string body;
                if (url.Contains("webresourceset?", StringComparison.Ordinal))
                {
                    body = JsonSerializer.Serialize(new
                    {
                        value = new[]
                        {
                            new { webresourceid = WebResourceId.ToString(), name = "xv_purchaseinvoice_js" }
                        }
                    });
                }
                else if (url.Contains("systemforms?", StringComparison.Ordinal))
                {
                    body = JsonSerializer.Serialize(new
                    {
                        value = new[]
                        {
                            new
                            {
                                formid = FormId.ToString(),
                                name = "Informationen",
                                objecttypecode = "xv_purchaseinvoice",
                                type = 2
                            }
                        }
                    });
                }
                else
                {
                    body = "{\"value\":[]}";
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            });
    }

    [Test]
    public async Task FindsAFormReference_AndNamesTheTableItSitsOn()
    {
        Route();

        var report = await _svc.FindUsagesAsync(OrgUrl, "xv_purchaseinvoice_js");

        Assert.Multiple(() =>
        {
            Assert.That(report.IsUsed, Is.True);
            Assert.That(report.Forms, Has.Count.EqualTo(1));
            Assert.That(report.Forms[0].Name, Is.EqualTo("Informationen"));
            Assert.That(report.Forms[0].Context, Is.EqualTo("xv_purchaseinvoice"));
            Assert.That(report.Summary, Does.Contain("1 form(s)"));
        });
    }

    /// <summary>
    /// Modern commands point at a web resource through real lookups, so that search must be exact
    /// rather than a text match that could hit an unrelated name.
    /// </summary>
    [Test]
    public async Task SearchesModernCommands_ByLookupNotByText()
    {
        Route();

        await _svc.FindUsagesAsync(OrgUrl, "xv_purchaseinvoice_js");

        var call = _urls.Single(u => u.Contains("appactions", StringComparison.Ordinal));
        Assert.Multiple(() =>
        {
            Assert.That(call, Does.Contain($"_onclickeventjavascriptwebresourceid_value eq {WebResourceId:D}"));
            Assert.That(call, Does.Contain($"_iconwebresourceid_value eq {WebResourceId:D}"));
            Assert.That(call, Does.Not.Contain("contains("));
        });
    }

    /// <summary>
    /// The stored ribbon diff misses buttons that come from managed solutions, so an answer that did
    /// not look at any merged ribbon has to say so.
    /// </summary>
    [Test]
    public async Task AdmitsThatMergedRibbonsWereNotSearched_WhenNoTableWasGiven()
    {
        Route();

        var report = await _svc.FindUsagesAsync(OrgUrl, "xv_purchaseinvoice_js");

        Assert.That(
            report.NotSearched.Any(n => n.Contains("merged", StringComparison.OrdinalIgnoreCase)),
            Is.True);
    }

    [Test]
    public async Task DropsThatCaveat_WhenTablesWereGiven()
    {
        Route();

        var report = await _svc.FindUsagesAsync(OrgUrl, "xv_purchaseinvoice_js", ["xv_purchaseinvoice"]);

        Assert.That(
            report.NotSearched.Any(n => n.Contains("merged", StringComparison.OrdinalIgnoreCase)),
            Is.False);
    }

    /// <summary>Base64 content cannot be text-searched server-side, and the answer must not pretend it was.</summary>
    [Test]
    public async Task AlwaysAdmitsThatOtherWebResourcesContentWasNotSearched()
    {
        Route();

        var report = await _svc.FindUsagesAsync(OrgUrl, "xv_purchaseinvoice_js", ["xv_purchaseinvoice"]);

        Assert.That(
            report.NotSearched.Any(n => n.Contains("base64", StringComparison.OrdinalIgnoreCase)),
            Is.True);
    }

    [Test]
    public void Throws_WhenTheWebResourceDoesNotExist()
    {
        _handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"value\":[]}", Encoding.UTF8, "application/json")
            });

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _svc.FindUsagesAsync(OrgUrl, "nosuchresource"));

        Assert.That(ex!.Message, Does.Contain("nosuchresource"));
    }

    // ---------------------------------------------------------------- summary

    private static ComponentDependencyReport NoDeps() =>
        new(WebResourceId, 61, "WebResource", "x", true, 0, "…", []);

    [Test]
    public void Summary_SendsYouToNotSearched_WhenNothingWasFound()
    {
        var summary = WebResourceUsageService.BuildSummary("x", [], [], [], [], [], NoDeps());

        Assert.Multiple(() =>
        {
            Assert.That(summary, Does.Contain("No usage"));
            Assert.That(summary, Does.Contain("notSearched"),
                "A bare \"nothing found\" invites the wrong conclusion.");
        });
    }

    [Test]
    public void Summary_ListsEverySourceThatMatched()
    {
        var one = new List<WebResourceUsage> { new("Form", Guid.NewGuid(), "f", null) };

        var summary = WebResourceUsageService.BuildSummary("x", one, one, one, one, one, NoDeps());

        Assert.Multiple(() =>
        {
            Assert.That(summary, Does.Contain("1 form(s)"));
            Assert.That(summary, Does.Contain("1 ribbon diff(s)"));
            Assert.That(summary, Does.Contain("1 site map(s)"));
            Assert.That(summary, Does.Contain("1 modern command(s)"));
            Assert.That(summary, Does.Contain("1 merged ribbon(s)"));
        });
    }
}
