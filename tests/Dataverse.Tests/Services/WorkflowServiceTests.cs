namespace Dataverse.Tests.Services;

using System.Net;
using System.Net.Http;
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
public sealed class WorkflowServiceTests
{
    private Mock<HttpMessageHandler> _handlerMock = null!;
    private DataverseHttpClient _client = null!;
    private WorkflowService _svc = null!;

    private const string OrgUrl = "https://test.crm4.dynamics.com";

    [SetUp]
    public void SetUp()
    {
        _handlerMock = new Mock<HttpMessageHandler>();

        var httpClient = new HttpClient(_handlerMock.Object);

        var tokenProviderMock = new Mock<ITokenProvider>();
        tokenProviderMock
            .Setup(t => t.GetTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-token");

        _client = new DataverseHttpClient(
            httpClient,
            tokenProviderMock.Object,
            new NullLogger<DataverseHttpClient>());

        _svc = new WorkflowService(_client, new NullLogger<WorkflowService>());
    }

    [Test]
    public async Task ListAsync_ReturnsWorkflows_WhenApiRespondsWithResults()
    {
        var responseBody = JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new
                {
                    workflowid = "3fa85f64-5717-4562-b3fc-2c963f66afa6",
                    name = "Test Workflow",
                    primaryentity = "account",
                    statecode = 1,
                    statuscode = 2,
                    _ownerid_value = (string?)null,
                    owneridname = (string?)null
                }
            }
        });

        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var results = await _svc.ListAsync(OrgUrl, ct: CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Name, Is.EqualTo("Test Workflow"));
        Assert.That(results[0].PrimaryEntity, Is.EqualTo("account"));
        Assert.That(results[0].StateCode, Is.EqualTo(1));
    }

    [Test]
    public async Task ListAsync_ReturnsEmptyList_WhenApiRespondsWithNoResults()
    {
        var responseBody = JsonSerializer.Serialize(new { value = Array.Empty<object>() });
        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var results = await _svc.ListAsync(OrgUrl, ct: CancellationToken.None);

        Assert.That(results, Is.Empty);
    }

    [Test]
    public async Task GetAsync_ReturnsWorkflowDetail_WhenFound()
    {
        var workflowId = Guid.NewGuid();
        var responseBody = JsonSerializer.Serialize(new
        {
            workflowid = workflowId.ToString(),
            name = "Detail Workflow",
            primaryentity = "contact",
            statecode = 0,
            statuscode = 1,
            _ownerid_value = (string?)null,
            owneridname = (string?)null,
            description = "A test workflow",
            xaml = "<xaml/>",
            createdon = "2024-01-01T00:00:00Z",
            modifiedon = "2024-06-01T00:00:00Z"
        });

        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var detail = await _svc.GetAsync(OrgUrl, workflowId, CancellationToken.None);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.Name, Is.EqualTo("Detail Workflow"));
        Assert.That(detail.PrimaryEntity, Is.EqualTo("contact"));
        Assert.That(detail.Xaml, Is.EqualTo("<xaml/>"));
    }

    [Test]
    public async Task ValidateAsync_ReturnsInvalidReport_WhenWorkflowLacksXaml()
    {
        var workflowId = Guid.NewGuid();
        var responseBody = JsonSerializer.Serialize(new
        {
            workflowid = workflowId.ToString(),
            name = "Incomplete Workflow",
            primaryentity = "account",
            statecode = 0,
            statuscode = 1,
            _ownerid_value = (string?)null,
            owneridname = (string?)null,
            description = (string?)null,
            xaml = (string?)null,
            createdon = (string?)null,
            modifiedon = (string?)null
        });

        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var report = await _svc.ValidateAsync(OrgUrl, workflowId, CancellationToken.None);

        Assert.That(report.IsValid, Is.False);
        Assert.That(report.Issues, Has.Some.Contains("XAML"));
    }

    [Test]
    public async Task ValidateAsync_ReturnsValidReport_WhenWorkflowIsActivatedWithXaml()
    {
        var workflowId = Guid.NewGuid();
        var responseBody = JsonSerializer.Serialize(new
        {
            workflowid = workflowId.ToString(),
            name = "Valid Workflow",
            primaryentity = "account",
            statecode = 1,
            statuscode = 2,
            _ownerid_value = (string?)null,
            owneridname = (string?)null,
            description = (string?)null,
            xaml = "<Activity/>",
            createdon = (string?)null,
            modifiedon = (string?)null
        });

        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var report = await _svc.ValidateAsync(OrgUrl, workflowId, CancellationToken.None);

        Assert.That(report.IsValid, Is.True);
        Assert.That(report.Issues, Is.Empty);
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
