namespace Dataverse.Tests.Services;

using System.Net;
using System.Text;
using System.Text.Json;
using Dataverse.Core.Auth;
using Dataverse.Core.Clients;
using Dataverse.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using NUnit.Framework;

[TestFixture]
public sealed class ViewServiceTests
{
    private Mock<HttpMessageHandler> _handlerMock = null!;
    private DataverseHttpClient _client = null!;
    private ViewService _svc = null!;

    private const string OrgUrl = "https://test.crm4.dynamics.com";

    [SetUp]
    public void SetUp()
    {
        _handlerMock = new Mock<HttpMessageHandler>();

        var tokenProviderMock = new Mock<ITokenProvider>();
        tokenProviderMock
            .Setup(t => t.GetTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-token");

        _client = new DataverseHttpClient(
            new HttpClient(_handlerMock.Object),
            tokenProviderMock.Object,
            NullLogger<DataverseHttpClient>.Instance);

        _svc = new ViewService(_client, NullLogger<ViewService>.Instance);
    }

    [Test]
    public async Task ListAsync_ReturnsViews_WhenApiRespondsWithResults()
    {
        var viewId = Guid.NewGuid();
        var responseBody = JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new
                {
                    savedqueryid = viewId.ToString(),
                    name = "Active Accounts",
                    querytype = 0,
                    isdefault = true,
                    iscustomizable = new { Value = true }
                }
            }
        });

        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var results = await _svc.ListAsync(OrgUrl, "account", ct: CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Name, Is.EqualTo("Active Accounts"));
        Assert.That(results[0].ViewType, Is.EqualTo("Public"));
        Assert.That(results[0].IsDefault, Is.True);
    }

    [Test]
    public async Task ListAsync_ReturnsEmptyList_WhenNoViewsFound()
    {
        var responseBody = JsonSerializer.Serialize(new { value = Array.Empty<object>() });
        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var results = await _svc.ListAsync(OrgUrl, "nonexistent_entity", ct: CancellationToken.None);

        Assert.That(results, Is.Empty);
    }

    [Test]
    public async Task GetAsync_ReturnsViewDetail_WhenFound()
    {
        var viewId = Guid.NewGuid();
        var fetchXml = "<fetch><entity name=\"account\"/></fetch>";
        var layoutXml = "<grid><row><cell name=\"name\" width=\"200\"/></row></grid>";

        var responseBody = JsonSerializer.Serialize(new
        {
            savedqueryid = viewId.ToString(),
            name = "Meine aktiven Firmen",
            querytype = 0,
            isdefault = false,
            iscustomizable = new { Value = true },
            fetchxml = fetchXml,
            layoutxml = layoutXml,
            description = "Shows active accounts"
        });

        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var detail = await _svc.GetAsync(OrgUrl, viewId, CancellationToken.None);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.Name, Is.EqualTo("Meine aktiven Firmen"));
        Assert.That(detail.ViewType, Is.EqualTo("Public"));
        Assert.That(detail.FetchXml, Is.EqualTo(fetchXml));
        Assert.That(detail.LayoutXml, Is.EqualTo(layoutXml));
        Assert.That(detail.Description, Is.EqualTo("Shows active accounts"));
    }

    [Test]
    public async Task UpdateAsync_SendsPatchRequest_ToCorrectEndpoint()
    {
        var viewId = Guid.NewGuid();
        Uri? capturedUri = null;
        HttpMethod? capturedMethod = null;

        _handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) =>
            {
                capturedUri = req.RequestUri;
                capturedMethod = req.Method;
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NoContent));

        var props = new Dictionary<string, object?> { ["description"] = "Updated description" };

        await _svc.UpdateAsync(OrgUrl, viewId, props, CancellationToken.None);

        Assert.That(capturedMethod, Is.EqualTo(HttpMethod.Patch));
        Assert.That(capturedUri!.ToString(), Does.Contain($"savedqueries({viewId})"));
    }

    [Test]
    public async Task ListAsync_MapsQueryTypesToNames_Correctly()
    {
        var responseBody = JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new { savedqueryid = Guid.NewGuid().ToString(), name = "QuickFind", querytype = 4, isdefault = false, iscustomizable = new { Value = false } },
                new { savedqueryid = Guid.NewGuid().ToString(), name = "Associated", querytype = 2, isdefault = false, iscustomizable = new { Value = false } },
                new { savedqueryid = Guid.NewGuid().ToString(), name = "AdvancedFind", querytype = 1, isdefault = false, iscustomizable = new { Value = false } }
            }
        });

        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var results = await _svc.ListAsync(OrgUrl, "account", ct: CancellationToken.None);

        Assert.That(results[0].ViewType, Is.EqualTo("QuickFind"));
        Assert.That(results[1].ViewType, Is.EqualTo("Associated"));
        Assert.That(results[2].ViewType, Is.EqualTo("AdvancedFind"));
    }

    private void SetupHttpResponse(HttpStatusCode statusCode, string body)
    {
        _handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
