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
public sealed class CloudFlowServiceTests
{
    private Mock<HttpMessageHandler> _handlerMock = null!;
    private PowerAutomateHttpClient _client = null!;
    private CloudFlowService _svc = null!;

    private const string Region = "europe";
    private const string EnvironmentId = "699d56e2-7f65-ebb0-ae92-2ae5e3a45159";

    [SetUp]
    public void SetUp()
    {
        _handlerMock = new Mock<HttpMessageHandler>();

        var tokenProviderMock = new Mock<ITokenProvider>();
        tokenProviderMock
            .Setup(t => t.GetTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-token");

        _client = new PowerAutomateHttpClient(
            new HttpClient(_handlerMock.Object),
            tokenProviderMock.Object,
            NullLogger<PowerAutomateHttpClient>.Instance);

        _svc = new CloudFlowService(_client, NullLogger<CloudFlowService>.Instance);
    }

    [Test]
    public async Task ListAsync_ReturnsFlows_WhenApiRespondsWithResults()
    {
        var responseBody = JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new
                {
                    name = "b8dde7d1-2316-f400-904b-cae537785fb2",
                    properties = new
                    {
                        displayName = "Lead Distribution Assistent",
                        state = "Started",
                        createdTime = "2024-01-01T00:00:00Z",
                        lastModifiedTime = "2024-06-01T00:00:00Z",
                        definition = new
                        {
                            triggers = new { manual = new { type = "Request" } }
                        }
                    }
                }
            }
        });

        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var results = await _svc.ListAsync(Region, EnvironmentId, ct: CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].FlowId, Is.EqualTo("b8dde7d1-2316-f400-904b-cae537785fb2"));
        Assert.That(results[0].DisplayName, Is.EqualTo("Lead Distribution Assistent"));
        Assert.That(results[0].State, Is.EqualTo("Started"));
    }

    [Test]
    public async Task ListAsync_ReturnsEmptyList_WhenNoFlowsFound()
    {
        var responseBody = JsonSerializer.Serialize(new { value = Array.Empty<object>() });
        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var results = await _svc.ListAsync(Region, EnvironmentId, ct: CancellationToken.None);

        Assert.That(results, Is.Empty);
    }

    [Test]
    public async Task GetAsync_ReturnsFlowDetail_WhenFound()
    {
        var flowId = "b8dde7d1-2316-f400-904b-cae537785fb2";
        var responseBody = JsonSerializer.Serialize(new
        {
            name = flowId,
            properties = new
            {
                displayName = "Lead Distribution Assistent",
                state = "Started",
                createdTime = "2024-01-01T00:00:00Z",
                lastModifiedTime = "2024-06-01T00:00:00Z",
                definition = new
                {
                    triggers = new { When_a_HTTP_request_is_received = new { type = "Request" } },
                    actions = new { SendEmail = new { type = "ApiConnection" } }
                }
            }
        });

        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var detail = await _svc.GetAsync(Region, EnvironmentId, flowId, CancellationToken.None);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.FlowId, Is.EqualTo(flowId));
        Assert.That(detail.DisplayName, Is.EqualTo("Lead Distribution Assistent"));
        Assert.That(detail.Definition, Is.Not.Null);
    }

    [Test]
    public async Task GetRunsAsync_ReturnsRunList_WhenApiRespondsWithSucceededRun()
    {
        var flowId = "b8dde7d1-2316-f400-904b-cae537785fb2";
        var responseBody = JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new
                {
                    name = "run-001",
                    properties = new
                    {
                        status = "Succeeded",
                        startTime = "2024-06-01T08:00:00Z",
                        endTime = "2024-06-01T08:01:00Z",
                        triggerName = "manual"
                    }
                }
            }
        });

        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var runs = await _svc.GetRunsAsync(Region, EnvironmentId, flowId, ct: CancellationToken.None);

        Assert.That(runs, Has.Count.EqualTo(1));
        Assert.That(runs[0].Status, Is.EqualTo("Succeeded"));
        Assert.That(runs[0].RunId, Is.EqualTo("run-001"));
        Assert.That(runs[0].TriggerName, Is.EqualTo("manual"));
    }

    [Test]
    public async Task GetRunsAsync_ReturnsErrorDetails_WhenRunFailed()
    {
        var flowId = "b8dde7d1-2316-f400-904b-cae537785fb2";
        var responseBody = JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new
                {
                    name = "run-002",
                    properties = new
                    {
                        status = "Failed",
                        startTime = "2024-06-02T08:00:00Z",
                        endTime = "2024-06-02T08:00:30Z",
                        triggerName = "manual",
                        error = new { code = "ActionFailed", message = "An action failed." }
                    }
                }
            }
        });

        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var runs = await _svc.GetRunsAsync(Region, EnvironmentId, flowId, ct: CancellationToken.None);

        Assert.That(runs, Has.Count.EqualTo(1));
        Assert.That(runs[0].Status, Is.EqualTo("Failed"));
        Assert.That(runs[0].ErrorCode, Is.EqualTo("ActionFailed"));
        Assert.That(runs[0].ErrorMessage, Is.EqualTo("An action failed."));
    }

    [Test]
    public async Task DescribeAsync_ReturnsDescription_WithTriggerAndActions()
    {
        var flowId = "b8dde7d1-2316-f400-904b-cae537785fb2";
        var responseBody = JsonSerializer.Serialize(new
        {
            name = flowId,
            properties = new
            {
                displayName = "Lead Distribution Assistent",
                state = "Started",
                createdTime = (string?)null,
                lastModifiedTime = (string?)null,
                definition = new
                {
                    triggers = new
                    {
                        Recurrence = new { type = "Recurrence" }
                    },
                    actions = new
                    {
                        GetLeads = new { type = "ApiConnection" },
                        AssignLead = new { type = "Http" }
                    }
                }
            }
        });

        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var description = await _svc.DescribeAsync(Region, EnvironmentId, flowId, CancellationToken.None);

        Assert.That(description.DisplayName, Is.EqualTo("Lead Distribution Assistent"));
        Assert.That(description.TriggerSummary, Does.Contain("Recurrence"));
        Assert.That(description.ActionSummaries, Has.Count.EqualTo(2));
        Assert.That(description.FullDescription, Is.Not.Empty);
        Assert.That(description.FullDescription, Does.Contain("Lead Distribution Assistent"));
    }

    [Test]
    public async Task SetStateAsync_SendsPostRequest_WithCorrectAction_WhenEnabling()
    {
        Uri? capturedUri = null;

        _handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUri = req.RequestUri)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NoContent));

        await _svc.SetStateAsync(Region, EnvironmentId, "test-flow-id", enable: true, CancellationToken.None);

        Assert.That(capturedUri!.ToString(), Does.EndWith("/start?api-version=2016-11-01"));
    }

    [Test]
    public async Task SetStateAsync_SendsPostRequest_WithCorrectAction_WhenDisabling()
    {
        Uri? capturedUri = null;

        _handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUri = req.RequestUri)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NoContent));

        await _svc.SetStateAsync(Region, EnvironmentId, "test-flow-id", enable: false, CancellationToken.None);

        Assert.That(capturedUri!.ToString(), Does.EndWith("/stop?api-version=2016-11-01"));
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
