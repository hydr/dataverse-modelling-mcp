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
public sealed class TableServiceTests
{
    private Mock<HttpMessageHandler> _handlerMock = null!;
    private DataverseHttpClient _client = null!;
    private TableService _svc = null!;

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

        _svc = new TableService(_client, NullLogger<TableService>.Instance);
    }

    [Test]
    public async Task ListAsync_ReturnsTables_WhenApiRespondsWithResults()
    {
        var responseBody = JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new
                {
                    LogicalName = "account",
                    DisplayName = new { UserLocalizedLabel = new { Label = "Account" } },
                    EntitySetName = "accounts",
                    TableType = "Standard",
                    IsCustomEntity = false
                },
                new
                {
                    LogicalName = "sample_mcptest",
                    DisplayName = new { UserLocalizedLabel = new { Label = "MCP Test" } },
                    EntitySetName = "sample_mcptests",
                    TableType = "Standard",
                    IsCustomEntity = true
                }
            }
        });

        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var results = await _svc.ListAsync(OrgUrl, ct: CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results[0].LogicalName, Is.EqualTo("account"));
        Assert.That(results[0].IsCustomEntity, Is.False);
        Assert.That(results[1].LogicalName, Is.EqualTo("sample_mcptest"));
        Assert.That(results[1].IsCustomEntity, Is.True);
    }

    [Test]
    public async Task ListAsync_ReturnsEmptyList_WhenNoTablesReturned()
    {
        var responseBody = JsonSerializer.Serialize(new { value = Array.Empty<object>() });
        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var results = await _svc.ListAsync(OrgUrl, ct: CancellationToken.None);

        Assert.That(results, Is.Empty);
    }

    [Test]
    public async Task ListAsync_FiltersBySolution_WhenSolutionUniqueNameGiven()
    {
        var accountMetadataId = Guid.NewGuid();
        var customMetadataId = Guid.NewGuid();
        var solutionId = Guid.NewGuid();
        var requestedUrls = new List<string>();

        SetupHttpResponseByUrl(requestedUrls, url =>
        {
            if (url.Contains("/solutions?"))
            {
                return JsonSerializer.Serialize(new
                {
                    value = new[] { new { solutionid = solutionId.ToString() } }
                });
            }

            if (url.Contains("/solutioncomponents?"))
            {
                return JsonSerializer.Serialize(new
                {
                    value = new[] { new { objectid = customMetadataId.ToString() } }
                });
            }

            return JsonSerializer.Serialize(new
            {
                value = new[]
                {
                    new
                    {
                        MetadataId = accountMetadataId.ToString(),
                        LogicalName = "account",
                        DisplayName = new { UserLocalizedLabel = new { Label = "Account" } },
                        EntitySetName = "accounts",
                        TableType = "Standard",
                        IsCustomEntity = false
                    },
                    new
                    {
                        MetadataId = customMetadataId.ToString(),
                        LogicalName = "sample_mcptest",
                        DisplayName = new { UserLocalizedLabel = new { Label = "MCP Test" } },
                        EntitySetName = "sample_mcptests",
                        TableType = "Standard",
                        IsCustomEntity = true
                    }
                }
            });
        });

        var results = await _svc.ListAsync(OrgUrl, filter: null, solutionUniqueName: "XvTestSolution", ct: CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].LogicalName, Is.EqualTo("sample_mcptest"));

        Assert.That(requestedUrls.Any(u => u.Contains("uniquename%20eq%20'XvTestSolution'")
                                           || u.Contains("uniquename eq 'XvTestSolution'")), Is.True,
            "Expected the solution to be resolved by unique name.");
        Assert.That(requestedUrls.Any(u => u.Contains("solutioncomponents") && u.Contains("componenttype")), Is.True,
            "Expected solutioncomponents to be queried for componenttype 1 (Entity).");
    }

    [Test]
    public void ListAsync_Throws_WhenSolutionNotFound()
    {
        SetupHttpResponseByUrl(new List<string>(), _ =>
            JsonSerializer.Serialize(new { value = Array.Empty<object>() }));

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _svc.ListAsync(OrgUrl, filter: null, solutionUniqueName: "DoesNotExist", ct: CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("DoesNotExist"));
    }

    [Test]
    public async Task ListAsync_ReturnsAllTables_WhenNoSolutionGiven()
    {
        var requestedUrls = new List<string>();
        SetupHttpResponseByUrl(requestedUrls, _ => JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new { MetadataId = Guid.NewGuid().ToString(), LogicalName = "account" },
                new { MetadataId = Guid.NewGuid().ToString(), LogicalName = "contact" }
            }
        }));

        var results = await _svc.ListAsync(OrgUrl, ct: CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(requestedUrls.Any(u => u.Contains("solutioncomponents")), Is.False);
    }

    [Test]
    public async Task ListAsync_PassesFilterAsODataFilter_WhenFilterGiven()
    {
        var requestedUrls = new List<string>();
        SetupHttpResponseByUrl(requestedUrls, _ => JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new { MetadataId = Guid.NewGuid().ToString(), LogicalName = "sample_mcptest" }
            }
        }));

        await _svc.ListAsync(OrgUrl, filter: "IsCustomEntity eq true", ct: CancellationToken.None);

        var metadataUrl = requestedUrls.Single(u => u.Contains("EntityDefinitions"));
        Assert.That(Uri.UnescapeDataString(metadataUrl), Does.Contain("$filter=IsCustomEntity eq true"),
            "The filter parameter must be forwarded to EntityDefinitions as $filter.");
    }

    [Test]
    public async Task ListAsync_SendsNoFilter_WhenFilterIsNullOrWhitespace()
    {
        var requestedUrls = new List<string>();
        SetupHttpResponseByUrl(requestedUrls, _ => JsonSerializer.Serialize(new
        {
            value = new[] { new { MetadataId = Guid.NewGuid().ToString(), LogicalName = "account" } }
        }));

        await _svc.ListAsync(OrgUrl, filter: "   ", ct: CancellationToken.None);

        var metadataUrl = requestedUrls.Single(u => u.Contains("EntityDefinitions"));
        Assert.That(metadataUrl, Does.Not.Contain("$filter"));
    }

    [Test]
    public async Task ListAsync_CombinesFilterAndSolution_WhenBothGiven()
    {
        var accountMetadataId = Guid.NewGuid();
        var customMetadataId = Guid.NewGuid();
        var requestedUrls = new List<string>();

        SetupHttpResponseByUrl(requestedUrls, url =>
        {
            if (url.Contains("/solutions?"))
            {
                return JsonSerializer.Serialize(new
                {
                    value = new[] { new { solutionid = Guid.NewGuid().ToString() } }
                });
            }

            if (url.Contains("/solutioncomponents?"))
            {
                return JsonSerializer.Serialize(new
                {
                    value = new[] { new { objectid = customMetadataId.ToString() } }
                });
            }

            // What a server-side "$filter=IsCustomEntity eq true" would return.
            return JsonSerializer.Serialize(new
            {
                value = new[]
                {
                    new { MetadataId = accountMetadataId.ToString(), LogicalName = "sample_other" },
                    new { MetadataId = customMetadataId.ToString(), LogicalName = "sample_mcptest" }
                }
            });
        });

        var results = await _svc.ListAsync(
            OrgUrl, filter: "IsCustomEntity eq true", solutionUniqueName: "XvTestSolution", ct: CancellationToken.None);

        // Server-side filter goes to the metadata endpoint, the solution restriction is applied on top.
        var metadataUrl = requestedUrls.Single(u => u.Contains("EntityDefinitions"));
        Assert.That(Uri.UnescapeDataString(metadataUrl), Does.Contain("$filter=IsCustomEntity eq true"));
        Assert.That(requestedUrls.Any(u => u.Contains("solutioncomponents")), Is.True);
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].LogicalName, Is.EqualTo("sample_mcptest"));
    }

    [Test]
    public async Task GetAsync_ReturnsTableDetail_WithColumns_WhenFound()
    {
        var responseBody = JsonSerializer.Serialize(new
        {
            LogicalName = "sample_mcptest",
            DisplayName = new { UserLocalizedLabel = new { Label = "MCP Test" } },
            DisplayCollectionName = new { UserLocalizedLabel = new { Label = "MCP Tests" } },
            Description = new { UserLocalizedLabel = new { Label = "Test table" } },
            EntitySetName = "sample_mcptests",
            TableType = "Standard",
            IsCustomEntity = true,
            IsAuditEnabled = new { Value = false },
            Attributes = new[]
            {
                new
                {
                    LogicalName = "sample_name",
                    DisplayName = new { UserLocalizedLabel = new { Label = "Name" } },
                    AttributeType = "String",
                    RequiredLevel = new { Value = "ApplicationRequired" },
                    IsCustomAttribute = true
                },
                new
                {
                    LogicalName = "sample_mcptestid",
                    DisplayName = new { UserLocalizedLabel = new { Label = "MCP Test" } },
                    AttributeType = "Uniqueidentifier",
                    RequiredLevel = new { Value = "SystemRequired" },
                    IsCustomAttribute = true
                }
            }
        });

        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var detail = await _svc.GetAsync(OrgUrl, "sample_mcptest", CancellationToken.None);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.LogicalName, Is.EqualTo("sample_mcptest"));
        Assert.That(detail.IsCustomEntity, Is.True);
        Assert.That(detail.Attributes, Has.Count.EqualTo(2));
        Assert.That(detail.Attributes[0].LogicalName, Is.EqualTo("sample_name"));
        Assert.That(detail.Attributes[0].IsRequired, Is.True);
    }

    [Test]
    public async Task AddColumnAsync_SendsPostRequest_ToCorrectEndpoint()
    {
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

        var colDef = new Dictionary<string, object?>
        {
            ["SchemaName"] = "sample_score",
            ["@odata.type"] = "Microsoft.Dynamics.CRM.DecimalAttributeMetadata"
        };

        await _svc.AddColumnAsync(OrgUrl, "sample_mcptest", colDef, CancellationToken.None);

        Assert.That(capturedMethod, Is.EqualTo(HttpMethod.Post));
        Assert.That(capturedUri!.ToString(), Does.Contain("EntityDefinitions(LogicalName='sample_mcptest')/Attributes"));
    }

    [Test]
    public async Task UpdateAsync_SendsPatchRequest_ToCorrectEndpoint()
    {
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

        var props = new Dictionary<string, object?> { ["HasActivities"] = true };

        await _svc.UpdateAsync(OrgUrl, "sample_mcptest", props, CancellationToken.None);

        Assert.That(capturedMethod, Is.EqualTo(HttpMethod.Patch));
        Assert.That(capturedUri!.ToString(), Does.Contain("EntityDefinitions(LogicalName='sample_mcptest')"));
    }

    [Test]
    public async Task GetAsync_ReturnsNullDisplayName_WhenDisplayNameMissing()
    {
        var responseBody = JsonSerializer.Serialize(new
        {
            LogicalName = "custom_entity",
            EntitySetName = "custom_entities",
            TableType = "Standard",
            IsCustomEntity = true,
            Attributes = Array.Empty<object>()
        });

        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var detail = await _svc.GetAsync(OrgUrl, "custom_entity", CancellationToken.None);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.DisplayName, Is.Null);
        Assert.That(detail.Attributes, Is.Empty);
    }

    /// <summary>Respond per request URL — needed for calls that hit several endpoints in sequence.</summary>
    private void SetupHttpResponseByUrl(List<string> requestedUrls, Func<string, string> bodyFactory)
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
                requestedUrls.Add(url);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(bodyFactory(url), Encoding.UTF8, "application/json")
                };
            });
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
