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
public sealed class SolutionServiceTests
{
    private Mock<HttpMessageHandler> _handlerMock = null!;
    private DataverseHttpClient _client = null!;
    private SolutionService _svc = null!;

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

        _svc = new SolutionService(_client, NullLogger<SolutionService>.Instance);
    }

    [Test]
    public async Task ListAsync_ReturnsSolutions_WhenApiRespondsWithResults()
    {
        var responseBody = JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new
                {
                    solutionid = "bf401562-6242-f111-bec6-7ced8d4a3a5d",
                    uniquename = "DV_MCP_Test",
                    friendlyname = "DV MCP Test",
                    version = "1.0.0.1",
                    ismanaged = false,
                    _publisherid_value = (string?)null,
                    publisheridname = "Default Publisher"
                }
            }
        });

        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var results = await _svc.ListAsync(OrgUrl, CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].UniqueName, Is.EqualTo("DV_MCP_Test"));
        Assert.That(results[0].FriendlyName, Is.EqualTo("DV MCP Test"));
        Assert.That(results[0].IsManaged, Is.False);
    }

    [Test]
    public async Task ListAsync_ReturnsEmptyList_WhenApiRespondsWithNoResults()
    {
        var responseBody = JsonSerializer.Serialize(new { value = Array.Empty<object>() });
        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var results = await _svc.ListAsync(OrgUrl, CancellationToken.None);

        Assert.That(results, Is.Empty);
    }

    [Test]
    public async Task GetAsync_ReturnsSolutionDetail_WhenFound()
    {
        var solutionId = Guid.NewGuid();
        var responseBody = JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new
                {
                    solutionid = solutionId.ToString(),
                    uniquename = "DV_MCP_Test",
                    friendlyname = "DV MCP Test",
                    version = "1.0.0.1",
                    ismanaged = false,
                    publisheridname = "Default Publisher",
                    description = "A test solution",
                    installedon = "2024-01-01T00:00:00Z",
                    solution_solutioncomponent = new[]
                    {
                        new { objectid = Guid.NewGuid().ToString(), componenttype = 1, rootcomponentbehavior = (string?)null }
                    }
                }
            }
        });

        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var detail = await _svc.GetAsync(OrgUrl, "DV_MCP_Test", CancellationToken.None);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.UniqueName, Is.EqualTo("DV_MCP_Test"));
        Assert.That(detail.FriendlyName, Is.EqualTo("DV MCP Test"));
        Assert.That(detail.Components, Has.Count.EqualTo(1));
        Assert.That(detail.Components[0].ComponentTypeName, Is.EqualTo("Entity"));
    }

    [Test]
    public async Task GetAsync_ReturnsNull_WhenSolutionNotFound()
    {
        var responseBody = JsonSerializer.Serialize(new { value = Array.Empty<object>() });
        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var detail = await _svc.GetAsync(OrgUrl, "NonExistentSolution", CancellationToken.None);

        Assert.That(detail, Is.Null);
    }

    [Test]
    public async Task CreateAsync_SendsPostRequest_WithPublisherResolved()
    {
        var publisherId = Guid.NewGuid();

        var callCount = 0;
        _handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // Publisher lookup
                    var pubBody = JsonSerializer.Serialize(new
                    {
                        value = new[] { new { publisherid = publisherId.ToString() } }
                    });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(pubBody, Encoding.UTF8, "application/json")
                    };
                }

                // Solution creation
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            });

        await _svc.CreateAsync(OrgUrl, "TestSolution", "Test Solution", "default", "1.0.0.0", CancellationToken.None);

        Assert.That(callCount, Is.EqualTo(2));
    }

    [Test]
    public async Task CreateAsync_BindsPublisherAsNavigationProperty_NotAsPrimitiveValue()
    {
        var publisherId = Guid.NewGuid();
        string? createBody = null;

        _handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                if (req.Method == HttpMethod.Get)
                {
                    var pubBody = JsonSerializer.Serialize(new
                    {
                        value = new[] { new { publisherid = publisherId.ToString() } }
                    });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(pubBody, Encoding.UTF8, "application/json")
                    };
                }

                createBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            });

        await _svc.CreateAsync(
            OrgUrl, "CRM531RibbonWork", "EXAMPLE-2 Ribbon Work", "crossvertise", "1.0.0.0", CancellationToken.None);

        Assert.That(createBody, Is.Not.Null);
        using var doc = JsonDocument.Parse(createBody!);
        var root = doc.RootElement;

        // The lookup MUST be sent as an @odata.bind navigation binding; a primitive "publisherid"
        // makes Dataverse fail with 0x80048d19.
        Assert.That(root.TryGetProperty("publisherid@odata.bind", out var bind), Is.True,
            "Expected publisherid to be sent as an @odata.bind navigation binding.");
        Assert.That(bind.GetString(), Is.EqualTo($"/publishers({publisherId})"));
        Assert.That(root.TryGetProperty("publisherid", out _), Is.False,
            "The primitive 'publisherid' property must not be sent.");
        Assert.That(root.GetProperty("uniquename").GetString(), Is.EqualTo("CRM531RibbonWork"));
        Assert.That(root.GetProperty("friendlyname").GetString(), Is.EqualTo("EXAMPLE-2 Ribbon Work"));
        Assert.That(root.GetProperty("version").GetString(), Is.EqualTo("1.0.0.0"));
    }

    [Test]
    public void CreateAsync_ThrowsDescriptiveError_WhenPublisherUnknown()
    {
        SetupHttpResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new { value = Array.Empty<object>() }));

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _svc.CreateAsync(OrgUrl, "TestSolution", "Test", "nosuchpublisher", "1.0.0.0", CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("nosuchpublisher"));
        Assert.That(ex.Message, Does.Contain("unique name"));
    }

    [Test]
    public async Task GetAsync_MapsComponentTypeNames_Correctly()
    {
        var responseBody = JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new
                {
                    solutionid = Guid.NewGuid().ToString(),
                    uniquename = "TestSolution",
                    friendlyname = "Test",
                    version = "1.0",
                    ismanaged = false,
                    publisheridname = (string?)null,
                    description = (string?)null,
                    installedon = (string?)null,
                    solution_solutioncomponent = new[]
                    {
                        new { objectid = Guid.NewGuid().ToString(), componenttype = 24, rootcomponentbehavior = (string?)null },
                        new { objectid = Guid.NewGuid().ToString(), componenttype = 92, rootcomponentbehavior = (string?)null },
                        new { objectid = Guid.NewGuid().ToString(), componenttype = 999, rootcomponentbehavior = (string?)null }
                    }
                }
            }
        });

        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var detail = await _svc.GetAsync(OrgUrl, "TestSolution", CancellationToken.None);

        // 24 = Form, 92 = SDK Message Processing Step (per the documented componenttype choice).
        Assert.That(detail!.Components[0].ComponentTypeName, Is.EqualTo("Form"));
        Assert.That(detail.Components[1].ComponentTypeName, Is.EqualTo("SdkMessageProcessingStep"));
        Assert.That(detail.Components[2].ComponentTypeName, Is.EqualTo("Type999"));
    }

    /// <summary>
    /// Regression guard for the mis-mapped component types: 61 used to be reported as "PluginType"
    /// and 62 as "PluginAssembly". The correct values come from the documented <c>componenttype</c>
    /// global choice of the <c>solutioncomponent</c> table.
    /// </summary>
    [TestCase(1, "Entity")]
    [TestCase(2, "Attribute")]
    [TestCase(3, "Relationship")]
    [TestCase(9, "OptionSet")]
    [TestCase(10, "EntityRelationship")]
    [TestCase(14, "EntityKey")]
    [TestCase(20, "Role")]
    [TestCase(24, "Form")]
    [TestCase(26, "SavedQuery")]
    [TestCase(29, "Workflow")]
    [TestCase(31, "Report")]
    [TestCase(44, "DuplicateRule")]
    [TestCase(59, "SavedQueryVisualization")]
    [TestCase(60, "SystemForm")]
    [TestCase(61, "WebResource")]
    [TestCase(62, "SiteMap")]
    [TestCase(63, "ConnectionRole")]
    [TestCase(90, "PluginType")]
    [TestCase(91, "PluginAssembly")]
    [TestCase(92, "SdkMessageProcessingStep")]
    [TestCase(93, "SdkMessageProcessingStepImage")]
    [TestCase(95, "ServiceEndpoint")]
    [TestCase(150, "RoutingRule")]
    [TestCase(380, "EnvironmentVariableDefinition")]
    [TestCase(381, "EnvironmentVariableValue")]
    [TestCase(4711, "Type4711")]
    public async Task GetAsync_MapsComponentType_ToDocumentedLabel(int componentType, string expectedName)
    {
        SetupHttpResponse(HttpStatusCode.OK, SolutionWithComponents(componentType));

        var detail = await _svc.GetAsync(OrgUrl, "TestSolution", CancellationToken.None);

        Assert.That(detail!.Components[0].ComponentType, Is.EqualTo(componentType));
        Assert.That(detail.Components[0].ComponentTypeName, Is.EqualTo(expectedName));
    }

    [Test]
    public async Task GetAsync_ResolvesFrameworkComponentTypes_FromSolutionComponentDefinitions()
    {
        var requestedUrls = new List<string>();

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

                var body = url.Contains("solutioncomponentdefinitions")
                    ? JsonSerializer.Serialize(new
                    {
                        value = new[]
                        {
                            new { name = "ManagedIdentity", objecttypecode = 10228 },
                            new { name = "CustomAPI", objecttypecode = 10160 }
                        }
                    })
                    : SolutionWithComponents(10228, 91);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            });

        var detail = await _svc.GetAsync(OrgUrl, "TestSolution", CancellationToken.None);

        Assert.That(detail!.Components[0].ComponentTypeName, Is.EqualTo("ManagedIdentity"));
        Assert.That(detail.Components[1].ComponentTypeName, Is.EqualTo("PluginAssembly"));
        Assert.That(requestedUrls.Any(u => u.Contains("solutioncomponentdefinitions")), Is.True);
    }

    [Test]
    public async Task GetAsync_KeepsTypeFallback_WhenFrameworkLookupFails()
    {
        _handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
                req.RequestUri!.ToString().Contains("solutioncomponentdefinitions")
                    ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        Content = new StringContent("{\"error\":{\"message\":\"no access\"}}", Encoding.UTF8, "application/json")
                    }
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(SolutionWithComponents(10228), Encoding.UTF8, "application/json")
                    });

        var detail = await _svc.GetAsync(OrgUrl, "TestSolution", CancellationToken.None);

        // Never invent a name — an unresolvable code stays Type<code>.
        Assert.That(detail!.Components[0].ComponentTypeName, Is.EqualTo("Type10228"));
    }

    [Test]
    public async Task GetAsync_DoesNotQueryComponentDefinitions_ForDocumentedTypes()
    {
        var requestedUrls = new List<string>();

        _handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                requestedUrls.Add(req.RequestUri!.ToString());
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SolutionWithComponents(61, 91), Encoding.UTF8, "application/json")
                };
            });

        await _svc.GetAsync(OrgUrl, "TestSolution", CancellationToken.None);

        Assert.That(requestedUrls.Any(u => u.Contains("solutioncomponentdefinitions")), Is.False,
            "Documented component types must be mapped without an extra metadata roundtrip.");
    }

    private static string SolutionWithComponents(params int[] componentTypes) => JsonSerializer.Serialize(new
    {
        value = new[]
        {
            new
            {
                solutionid = Guid.NewGuid().ToString(),
                uniquename = "TestSolution",
                friendlyname = "Test",
                version = "1.0",
                ismanaged = false,
                publisheridname = (string?)null,
                description = (string?)null,
                installedon = (string?)null,
                solution_solutioncomponent = componentTypes
                    .Select(t => new
                    {
                        objectid = Guid.NewGuid().ToString(),
                        componenttype = t,
                        rootcomponentbehavior = (string?)null
                    })
                    .ToArray()
            }
        }
    });

    [Test]
    public async Task ListAsync_ReturnsIsManaged_True_WhenApiReturnsTrue()
    {
        var responseBody = JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new
                {
                    solutionid = Guid.NewGuid().ToString(),
                    uniquename = "ManagedSolution",
                    friendlyname = "Managed Solution",
                    version = "1.0.0.0",
                    ismanaged = true,
                    _publisherid_value = (string?)null,
                    publisheridname = (string?)null
                }
            }
        });

        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var results = await _svc.ListAsync(OrgUrl, CancellationToken.None);

        Assert.That(results[0].IsManaged, Is.True);
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
