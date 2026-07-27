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
public sealed class PublishServiceTests
{
    private Mock<HttpMessageHandler> _handlerMock = null!;
    private PublishService _svc = null!;

    private const string OrgUrl = "https://test.crm4.dynamics.com";

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

        _svc = new PublishService(
            client,
            new WebResourceService(client, NullLogger<WebResourceService>.Instance),
            NullLogger<PublishService>.Instance);
    }

    [Test]
    public void BuildParameterXml_EmitsEntitiesAndWebResources()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");

        var xml = PublishService.BuildParameterXml(new[] { "account", "sample_purchaseorder" }, new[] { id });

        Assert.That(xml, Is.EqualTo(
            "<importexportxml>" +
            "<entities><entity>account</entity><entity>sample_purchaseorder</entity></entities>" +
            "<webresources><webresource>11111111-2222-3333-4444-555555555555</webresource></webresources>" +
            "</importexportxml>"));
    }

    [Test]
    public void BuildParameterXml_OmitsEmptySections()
    {
        var xml = PublishService.BuildParameterXml(new[] { "account" }, Array.Empty<Guid>());

        Assert.That(xml, Does.Not.Contain("webresources"));
        Assert.That(xml, Does.Contain("<entity>account</entity>"));
    }

    [Test]
    public async Task PublishAsync_PostsPublishXml_WithAssembledParameterXml()
    {
        Uri? capturedUri = null;
        string? capturedBody = null;
        SetupSingleResponse(HttpStatusCode.NoContent, null, req =>
        {
            capturedUri = req.RequestUri;
            capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
        });

        var parameterXml = await _svc.PublishAsync(
            OrgUrl, entities: new[] { "sample_purchaseorder" }, ct: CancellationToken.None);

        Assert.That(capturedUri!.ToString(), Does.EndWith("api/data/v9.2/PublishXml"));
        Assert.That(parameterXml, Does.Contain("<entity>sample_purchaseorder</entity>"));

        using var body = JsonDocument.Parse(capturedBody!);
        // The action parameter is PascalCase — camelCasing it breaks the call.
        Assert.That(body.RootElement.GetProperty("ParameterXml").GetString(), Is.EqualTo(parameterXml));
    }

    [Test]
    public async Task PublishAsync_UsesPublishAllXml_WhenAllIsTrue()
    {
        Uri? capturedUri = null;
        SetupSingleResponse(HttpStatusCode.NoContent, null, req => capturedUri = req.RequestUri);

        var parameterXml = await _svc.PublishAsync(OrgUrl, all: true, ct: CancellationToken.None);

        Assert.That(capturedUri!.ToString(), Does.EndWith("api/data/v9.2/PublishAllXml"));
        Assert.That(parameterXml, Is.Null);
    }

    [Test]
    public async Task PublishAsync_ResolvesWebResourceNamesToIds()
    {
        var wrId = Guid.NewGuid();
        var call = 0;
        string? publishBody = null;

        _handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) =>
            {
                if (req.RequestUri!.ToString().EndsWith("PublishXml", StringComparison.Ordinal))
                {
                    publishBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                }
            })
            .ReturnsAsync(() =>
            {
                call++;
                if (call == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            JsonSerializer.Serialize(new { value = new[] { new { webresourceid = wrId.ToString() } } }),
                            Encoding.UTF8,
                            "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NoContent);
            });

        await _svc.PublishAsync(OrgUrl, webResources: new[] { "sample_script.js" }, ct: CancellationToken.None);

        Assert.That(publishBody, Does.Contain(wrId.ToString()));
    }

    [Test]
    public void PublishAsync_Throws_WhenNothingWasRequested()
    {
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await _svc.PublishAsync(OrgUrl, ct: CancellationToken.None));
    }

    private void SetupSingleResponse(HttpStatusCode statusCode, string? body, Action<HttpRequestMessage> capture)
    {
        _handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capture(req))
            .ReturnsAsync(() => new HttpResponseMessage(statusCode)
            {
                Content = body is null
                    ? new StringContent(string.Empty)
                    : new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
