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

        Assert.That(detail!.Components[0].ComponentTypeName, Is.EqualTo("Workflow"));
        Assert.That(detail.Components[1].ComponentTypeName, Is.EqualTo("Role"));
        Assert.That(detail.Components[2].ComponentTypeName, Is.EqualTo("Type999"));
    }

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
