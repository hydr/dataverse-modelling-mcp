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
public sealed class SecurityRoleServiceTests
{
    private Mock<HttpMessageHandler> _handlerMock = null!;
    private DataverseHttpClient _client = null!;
    private SecurityRoleService _svc = null!;

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

        _svc = new SecurityRoleService(_client, NullLogger<SecurityRoleService>.Instance);
    }

    [Test]
    public async Task ListAsync_ReturnsRoles_WhenApiRespondsWithResults()
    {
        var roleId = Guid.NewGuid();
        var buId = Guid.NewGuid();
        var responseBody = JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new
                {
                    roleid = roleId.ToString(),
                    name = "DV MCP Test Role",
                    description = "A test security role",
                    _businessunitid_value = buId.ToString(),
                    businessunitid = new { name = "Test BU" }
                }
            }
        });

        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var results = await _svc.ListAsync(OrgUrl, ct: CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Name, Is.EqualTo("DV MCP Test Role"));
        Assert.That(results[0].RoleId, Is.EqualTo(roleId));
        Assert.That(results[0].BusinessUnitName, Is.EqualTo("Test BU"));
    }

    [Test]
    public async Task ListAsync_ReturnsEmptyList_WhenNoRolesFound()
    {
        var responseBody = JsonSerializer.Serialize(new { value = Array.Empty<object>() });
        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var results = await _svc.ListAsync(OrgUrl, ct: CancellationToken.None);

        Assert.That(results, Is.Empty);
    }

    [Test]
    public async Task GetAsync_ReturnsRoleDetail_WithPrivileges_WhenFound()
    {
        var roleId = Guid.NewGuid();
        var privId = Guid.NewGuid();
        var responseBody = JsonSerializer.Serialize(new
        {
            roleid = roleId.ToString(),
            name = "DV MCP Test Role",
            description = "A test security role",
            _businessunitid_value = Guid.NewGuid().ToString(),
            businessunitid = new { name = "Test BU" },
            roleprivileges_association = new[]
            {
                new { privilegeid = privId.ToString(), name = "prvReadAccount", accessright = 1 },
                new { privilegeid = Guid.NewGuid().ToString(), name = "prvWriteAccount", accessright = 2 }
            }
        });

        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var detail = await _svc.GetAsync(OrgUrl, roleId, CancellationToken.None);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.Name, Is.EqualTo("DV MCP Test Role"));
        Assert.That(detail.Privileges, Has.Count.EqualTo(2));
        Assert.That(detail.Privileges[0].PrivilegeName, Is.EqualTo("prvReadAccount"));
        Assert.That(detail.Privileges[0].Depth, Is.EqualTo(1));
    }

    [Test]
    public async Task GetAsync_ReturnsRoleDetail_WithEmptyPrivileges_WhenNoneAssigned()
    {
        var roleId = Guid.NewGuid();
        var responseBody = JsonSerializer.Serialize(new
        {
            roleid = roleId.ToString(),
            name = "Empty Role",
            description = (string?)null,
            _businessunitid_value = (string?)null,
            roleprivileges_association = Array.Empty<object>()
        });

        SetupHttpResponse(HttpStatusCode.OK, responseBody);

        var detail = await _svc.GetAsync(OrgUrl, roleId, CancellationToken.None);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.Privileges, Is.Empty);
    }

    [Test]
    public async Task CreateAsync_SendsPostAndThenListRequest()
    {
        var buId = Guid.NewGuid();
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
                    // POST to create role
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }

                // GET list to retrieve created role ID
                var body = JsonSerializer.Serialize(new
                {
                    value = new[]
                    {
                        new { roleid = Guid.NewGuid().ToString(), name = "New Role", description = (string?)null, _businessunitid_value = (string?)null }
                    }
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            });

        var createdId = await _svc.CreateAsync(OrgUrl, "New Role", buId, "Description", CancellationToken.None);

        Assert.That(callCount, Is.EqualTo(2));
        Assert.That(createdId, Is.Not.EqualTo(Guid.Empty));
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
