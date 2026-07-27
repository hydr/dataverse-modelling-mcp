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

[TestFixture]
public sealed class WebResourceServiceTests
{
    private Mock<HttpMessageHandler> _handlerMock = null!;
    private WebResourceService _svc = null!;

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

        _svc = new WebResourceService(client, NullLogger<WebResourceService>.Instance);
    }

    [Test]
    public async Task ListAsync_UsesWebResourceSetEntitySet_AndMapsTypeNames()
    {
        var id = Guid.NewGuid();
        Uri? capturedUri = null;

        SetupCapturingResponse(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(new
            {
                value = new[]
                {
                    new
                    {
                        webresourceid = id.ToString(),
                        name = "sample_script.js",
                        displayname = "Script",
                        webresourcetype = 3,
                        ismanaged = false
                    }
                }
            }),
            req => capturedUri = req.RequestUri);

        var results = await _svc.ListAsync(OrgUrl, "sample_", CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].WebResourceTypeName, Is.EqualTo("JScript"));
        Assert.That(results[0].WebResourceId, Is.EqualTo(id));
        // The entity set is webresourceset — 'webresources' returns 0x80060888.
        Assert.That(capturedUri!.ToString(), Does.Contain("webresourceset"));
        Assert.That(Uri.UnescapeDataString(capturedUri.ToString()), Does.Contain("startswith(name,'sample_')"));
    }

    [Test]
    public async Task GetAsync_DecodesTextContent()
    {
        var id = Guid.NewGuid();
        const string source = "var Xv = Xv || {};";

        SetupResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            webresourceid = id.ToString(),
            name = "sample_script.js",
            webresourcetype = 3,
            content = Convert.ToBase64String(Encoding.UTF8.GetBytes(source))
        }));

        var detail = await _svc.GetAsync(OrgUrl, id, CancellationToken.None);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.ContentIsText, Is.True);
        Assert.That(detail.Content, Is.EqualTo(source));
        Assert.That(detail.ContentBytes, Is.EqualTo(Encoding.UTF8.GetByteCount(source)));
    }

    [Test]
    public async Task GetAsync_StripsUtf8Bom()
    {
        var id = Guid.NewGuid();
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("body{}")).ToArray();

        SetupResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            webresourceid = id.ToString(),
            name = "sample_style.css",
            webresourcetype = 2,
            content = Convert.ToBase64String(bytes)
        }));

        var detail = await _svc.GetAsync(OrgUrl, id, CancellationToken.None);

        Assert.That(detail!.Content, Is.EqualTo("body{}"));
    }

    [Test]
    public async Task GetAsync_DoesNotDecodeBinaryContent_ButReportsSize()
    {
        var id = Guid.NewGuid();
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0x01 };

        SetupResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            webresourceid = id.ToString(),
            name = "sample_icon.png",
            webresourcetype = 5,
            content = Convert.ToBase64String(bytes)
        }));

        var detail = await _svc.GetAsync(OrgUrl, id, CancellationToken.None);

        Assert.That(detail!.ContentIsText, Is.False);
        Assert.That(detail.Content, Is.Null);
        Assert.That(detail.ContentBytes, Is.EqualTo(6));
        Assert.That(detail.WebResourceTypeName, Is.EqualTo("Png"));
    }

    [Test]
    public async Task UpsertAsync_CreatesWithInferredType_WhenNameIsUnknown()
    {
        var newId = Guid.NewGuid();
        var bodies = new List<string?>();
        var call = 0;

        _handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) =>
                bodies.Add(req.Content?.ReadAsStringAsync().GetAwaiter().GetResult()))
            .ReturnsAsync(() =>
            {
                call++;
                if (call == 1)
                {
                    // FindIdByNameAsync — nothing there yet
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            JsonSerializer.Serialize(new { value = Array.Empty<object>() }),
                            Encoding.UTF8,
                            "application/json")
                    };
                }

                var resp = new HttpResponseMessage(HttpStatusCode.NoContent);
                resp.Headers.TryAddWithoutValidation(
                    "OData-EntityId", $"{OrgUrl}/api/data/v9.2/webresourceset({newId})");
                return resp;
            });

        var (id, created, type) = await _svc.UpsertAsync(
            OrgUrl, "sample_new_script.js", Encoding.UTF8.GetBytes("alert(1);"), ct: CancellationToken.None);

        Assert.That(id, Is.EqualTo(newId));
        Assert.That(created, Is.True);
        Assert.That(type, Is.EqualTo((int)WebResourceType.JScript));

        using var body = JsonDocument.Parse(bodies[1]!);
        Assert.That(body.RootElement.GetProperty("name").GetString(), Is.EqualTo("sample_new_script.js"));
        Assert.That(body.RootElement.GetProperty("webresourcetype").GetInt32(), Is.EqualTo(3));
        Assert.That(
            body.RootElement.GetProperty("content").GetString(),
            Is.EqualTo(Convert.ToBase64String(Encoding.UTF8.GetBytes("alert(1);"))));
    }

    [Test]
    public async Task UpsertAsync_PatchesExistingRow_AndReportsCreatedFalse()
    {
        var existingId = Guid.NewGuid();
        var methods = new List<HttpMethod>();
        var call = 0;

        _handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => methods.Add(req.Method))
            .ReturnsAsync(() =>
            {
                call++;
                return call switch
                {
                    1 => JsonResponse(new { value = new[] { new { webresourceid = existingId.ToString() } } }),
                    2 => new HttpResponseMessage(HttpStatusCode.NoContent),
                    _ => JsonResponse(new { webresourcetype = 3 })
                };
            });

        var (id, created, type) = await _svc.UpsertAsync(
            OrgUrl, "sample_existing.js", Encoding.UTF8.GetBytes("x"), ct: CancellationToken.None);

        Assert.That(id, Is.EqualTo(existingId));
        Assert.That(created, Is.False);
        Assert.That(type, Is.EqualTo(3));
        Assert.That(methods[1], Is.EqualTo(HttpMethod.Patch));
    }

    [Test]
    public void UpsertAsync_Throws_WhenTypeCannotBeInferred()
    {
        SetupResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new { value = Array.Empty<object>() }));

        Assert.ThrowsAsync<InvalidOperationException>(async () => await _svc.UpsertAsync(
            OrgUrl, "sample_no_extension", Encoding.UTF8.GetBytes("x"), ct: CancellationToken.None));
    }

    [TestCase("a.js", 3)]
    [TestCase("a.HTML", 1)]
    [TestCase("a.css", 2)]
    [TestCase("a.xml", 4)]
    [TestCase("a.png", 5)]
    [TestCase("a.jpg", 6)]
    [TestCase("a.gif", 7)]
    [TestCase("a.xap", 8)]
    [TestCase("a.xsl", 9)]
    [TestCase("a.ico", 10)]
    [TestCase("a.svg", 11)]
    [TestCase("a.resx", 12)]
    public void InferTypeFromExtension_MapsKnownExtensions(string fileName, int expected)
    {
        Assert.That(WebResourceService.InferTypeFromExtension(fileName), Is.EqualTo(expected));
    }

    [Test]
    public void InferTypeFromExtension_ReturnsNull_ForUnknownExtension()
    {
        Assert.That(WebResourceService.InferTypeFromExtension("a.docx"), Is.Null);
    }

    [Test]
    public void IsTextType_CoversTheTextFormatsOnly()
    {
        foreach (var text in new[] { 1, 2, 3, 4, 9, 11, 12 })
        {
            Assert.That(WebResourceService.IsTextType(text), Is.True, $"type {text} should be text");
        }

        foreach (var binary in new[] { 5, 6, 7, 8, 10 })
        {
            Assert.That(WebResourceService.IsTextType(binary), Is.False, $"type {binary} should be binary");
        }
    }

    private static HttpResponseMessage JsonResponse(object payload) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

    private void SetupResponse(HttpStatusCode statusCode, string body) =>
        SetupCapturingResponse(statusCode, body, _ => { });

    private void SetupCapturingResponse(HttpStatusCode statusCode, string body, Action<HttpRequestMessage> capture)
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
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
