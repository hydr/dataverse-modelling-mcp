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
public sealed class EnvironmentVariableServiceTests
{
    private Mock<HttpMessageHandler> _handlerMock = null!;
    private DataverseHttpClient _client = null!;
    private EnvironmentVariableService _svc = null!;

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

        _svc = new EnvironmentVariableService(_client, NullLogger<EnvironmentVariableService>.Instance);
    }

    [Test]
    public async Task ListAsync_ReturnsVariables_WhenApiRespondsWithResults()
    {
        var defId = Guid.NewGuid();
        var responseBody = JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new
                {
                    environmentvariabledefinitionid = defId.ToString(),
                    schemaname = "sample_mcptest_apiurl",
                    displayname = "MCP Test API URL",
                    type = "String",
                    defaultvalue = "https://test.example.com",
                    environmentvariabledefinition_environmentvariablevalue = new[]
                    {
                        new { value = "https://override.example.com" }
                    }
                }
            }
        });

        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var results = await _svc.ListAsync(OrgUrl, CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].SchemaName, Is.EqualTo("sample_mcptest_apiurl"));
        Assert.That(results[0].DefaultValue, Is.EqualTo("https://test.example.com"));
        Assert.That(results[0].CurrentValue, Is.EqualTo("https://override.example.com"));
    }

    [Test]
    public async Task ListAsync_ReturnsEmptyList_WhenNoVariablesFound()
    {
        var responseBody = JsonSerializer.Serialize(new { value = Array.Empty<object>() });
        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var results = await _svc.ListAsync(OrgUrl, CancellationToken.None);

        Assert.That(results, Is.Empty);
    }

    [Test]
    public async Task GetAsync_ReturnsDetail_WhenVariableFound()
    {
        var defId = Guid.NewGuid();
        var valueId = Guid.NewGuid();
        var responseBody = JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new
                {
                    environmentvariabledefinitionid = defId.ToString(),
                    schemaname = "sample_mcptest_apiurl",
                    displayname = "MCP Test API URL",
                    type = "String",
                    defaultvalue = "https://test.example.com",
                    description = "API URL for MCP test",
                    environmentvariabledefinition_environmentvariablevalue = new[]
                    {
                        new { environmentvariablevalueid = valueId.ToString(), value = "https://override.example.com" }
                    }
                }
            }
        });

        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var detail = await _svc.GetAsync(OrgUrl, "sample_mcptest_apiurl", CancellationToken.None);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.SchemaName, Is.EqualTo("sample_mcptest_apiurl"));
        Assert.That(detail.DefaultValue, Is.EqualTo("https://test.example.com"));
        Assert.That(detail.CurrentValue, Is.EqualTo("https://override.example.com"));
        Assert.That(detail.Description, Is.EqualTo("API URL for MCP test"));
        Assert.That(detail.ValueId, Is.EqualTo(valueId));
    }

    [Test]
    public async Task GetAsync_ReturnsNull_WhenVariableNotFound()
    {
        var responseBody = JsonSerializer.Serialize(new { value = Array.Empty<object>() });
        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var detail = await _svc.GetAsync(OrgUrl, "nonexistent_schema", CancellationToken.None);

        Assert.That(detail, Is.Null);
    }

    [Test]
    public async Task SetAsync_SendsPatchRequest_WhenValueAlreadyExists()
    {
        var defId = Guid.NewGuid();
        var valueId = Guid.NewGuid();
        var callCount = 0;
        var capturedRequests = new List<(HttpMethod Method, Uri Uri)>();

        _handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) =>
            {
                callCount++;
                capturedRequests.Add((req.Method, req.RequestUri!));
            })
            .ReturnsAsync(() =>
            {
                if (callCount == 1)
                {
                    // GetAsync call
                    var body = JsonSerializer.Serialize(new
                    {
                        value = new[]
                        {
                            new
                            {
                                environmentvariabledefinitionid = defId.ToString(),
                                schemaname = "sample_mcptest_apiurl",
                                displayname = (string?)null,
                                type = "String",
                                defaultvalue = "https://test.example.com",
                                description = (string?)null,
                                environmentvariabledefinition_environmentvariablevalue = new[]
                                {
                                    new { environmentvariablevalueid = valueId.ToString(), value = "old-value" }
                                }
                            }
                        }
                    });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json")
                    };
                }

                // PATCH call
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            });

        await _svc.SetAsync(OrgUrl, "sample_mcptest_apiurl", "new-value", CancellationToken.None);

        Assert.That(capturedRequests[1].Method, Is.EqualTo(HttpMethod.Patch));
        Assert.That(capturedRequests[1].Uri.ToString(), Does.Contain($"environmentvariablevalues({valueId})"));
    }

    [Test]
    public async Task ListAsync_ReturnsNullCurrentValue_WhenNoValueRecord()
    {
        var defId = Guid.NewGuid();
        var responseBody = JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new
                {
                    environmentvariabledefinitionid = defId.ToString(),
                    schemaname = "sample_novalue",
                    displayname = "No Value",
                    type = "String",
                    defaultvalue = "default",
                    environmentvariabledefinition_environmentvariablevalue = Array.Empty<object>()
                }
            }
        });

        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var results = await _svc.ListAsync(OrgUrl, CancellationToken.None);

        Assert.That(results[0].CurrentValue, Is.Null);
        Assert.That(results[0].DefaultValue, Is.EqualTo("default"));
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
