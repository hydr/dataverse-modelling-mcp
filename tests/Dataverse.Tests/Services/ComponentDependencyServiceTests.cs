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

/// <summary>
/// Covers the "what breaks if I delete this?" question.
/// </summary>
/// <remarks>
/// The raw function is easy to call wrongly — a function with parameters needs OData parameter
/// aliases, and getting that wrong returns an HTML error page rather than a JSON error — and its
/// answer is nothing but GUIDs and type codes, where an empty collection reads exactly like a call
/// that silently failed.
/// </remarks>
[TestFixture]
public sealed class ComponentDependencyServiceTests
{
    private const string OrgUrl = "https://test.crm4.dynamics.com";
    private static readonly Guid ColumnId = Guid.Parse("a1e66081-6242-f111-bec6-7c1e528730f7");
    private static readonly Guid TableId = Guid.Parse("a0e66081-6242-f111-bec6-7c1e528730f7");
    private static readonly Guid FormId = Guid.Parse("8016761a-2c24-484c-905e-efdca6af5347");

    private Mock<HttpMessageHandler> _handlerMock = null!;
    private ComponentDependencyService _svc = null!;
    private List<string> _urls = null!;

    [SetUp]
    public void SetUp()
    {
        _handlerMock = new Mock<HttpMessageHandler>();
        _urls = [];

        var tokenProviderMock = new Mock<ITokenProvider>();
        tokenProviderMock
            .Setup(t => t.GetTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-token");

        var client = new DataverseHttpClient(
            new HttpClient(_handlerMock.Object),
            tokenProviderMock.Object,
            NullLogger<DataverseHttpClient>.Instance);

        _svc = new ComponentDependencyService(client, NullLogger<ComponentDependencyService>.Instance);
    }

    private void Route(Func<string, string> bodyFactory)
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
                _urls.Add(url);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(bodyFactory(url), Encoding.UTF8, "application/json")
                };
            });
    }

    private static string Empty => "{\"value\":[]}";

    private static string OneDependency() => JsonSerializer.Serialize(new
    {
        value = new[]
        {
            new
            {
                dependentcomponentobjectid = FormId.ToString(),
                dependentcomponenttype = 60,
                dependentcomponentparentid = "00000000-0000-0000-0000-000000000000",
                requiredcomponentobjectid = ColumnId.ToString(),
                requiredcomponenttype = 2,
                requiredcomponentparentid = TableId.ToString(),
                dependencytype = 2
            }
        }
    });

    /// <summary>
    /// A function with parameters needs aliases. Inlining the values returns an HTML Runtime Error
    /// page, which then fails to parse as JSON somewhere far from the cause.
    /// </summary>
    [Test]
    public async Task CallsTheFunction_WithODataParameterAliases()
    {
        Route(_ => Empty);

        await _svc.GetDependenciesForDeleteAsync(OrgUrl, ColumnId, 2);

        var call = _urls[0];
        Assert.Multiple(() =>
        {
            Assert.That(call, Does.Contain("RetrieveDependenciesForDelete(ObjectId=@p1,ComponentType=@p2)"));
            Assert.That(call, Does.Contain($"@p1={ColumnId:D}"));
            Assert.That(call, Does.Contain("@p2=2"));
        });
    }

    /// <summary>
    /// "Nothing depends on this" is the answer people actually need before a delete, and an empty
    /// collection on its own does not say it.
    /// </summary>
    [Test]
    public async Task ReportsCanDelete_WhenNothingDependsOnTheComponent()
    {
        Route(_ => Empty);

        var report = await _svc.GetDependenciesForDeleteAsync(OrgUrl, ColumnId, 2);

        Assert.Multiple(() =>
        {
            Assert.That(report.CanDelete, Is.True);
            Assert.That(report.DependentCount, Is.Zero);
            Assert.That(report.Dependents, Is.Empty);
            Assert.That(report.Summary, Does.Contain("No dependencies"));
            Assert.That(report.ComponentTypeName, Is.EqualTo("Attribute"));
        });
    }

    [Test]
    public async Task ResolvesTheDependentsNameAndType()
    {
        Route(url =>
        {
            if (url.Contains("RetrieveDependenciesForDelete", StringComparison.Ordinal))
                return OneDependency();
            if (url.Contains("systemforms", StringComparison.Ordinal))
                return JsonSerializer.Serialize(new
                {
                    value = new[] { new { formid = FormId.ToString(), name = "Informationen" } }
                });
            return Empty;
        });

        var report = await _svc.GetDependenciesForDeleteAsync(OrgUrl, ColumnId, 2);

        Assert.Multiple(() =>
        {
            Assert.That(report.CanDelete, Is.False);
            Assert.That(report.DependentCount, Is.EqualTo(1));
            Assert.That(report.Dependents[0].ComponentTypeName, Is.EqualTo("SystemForm"));
            Assert.That(report.Dependents[0].Name, Is.EqualTo("Informationen"));
            Assert.That(report.Summary, Does.Contain("1× SystemForm"));
        });
    }

    /// <summary>
    /// A column can only be named through its owning table, and the dependency response carries that
    /// parent — so it should be used rather than scanning for a table that might not be in the list.
    /// </summary>
    [Test]
    public async Task ResolvesAColumnDependent_ThroughItsParentTable()
    {
        Route(url =>
        {
            if (url.Contains("RetrieveDependenciesForDelete", StringComparison.Ordinal))
                return JsonSerializer.Serialize(new
                {
                    value = new[]
                    {
                        new
                        {
                            dependentcomponentobjectid = ColumnId.ToString(),
                            dependentcomponenttype = 2,
                            dependentcomponentparentid = TableId.ToString(),
                            dependencytype = 2
                        }
                    }
                });

            if (url.Contains("EntityDefinitions?", StringComparison.Ordinal))
                return JsonSerializer.Serialize(new
                {
                    value = new[] { new { MetadataId = TableId.ToString(), LogicalName = "xv_mcptest" } }
                });

            if (url.Contains("/Attributes", StringComparison.Ordinal))
                return JsonSerializer.Serialize(new
                {
                    value = new[] { new { MetadataId = ColumnId.ToString(), LogicalName = "xv_name" } }
                });

            return Empty;
        });

        var report = await _svc.GetDependenciesForDeleteAsync(OrgUrl, TableId, 1);

        Assert.Multiple(() =>
        {
            Assert.That(report.Dependents[0].Name, Is.EqualTo("xv_mcptest.xv_name"));
            Assert.That(report.Dependents[0].ParentId, Is.EqualTo(TableId));
            Assert.That(report.Dependents[0].ParentName, Is.EqualTo("xv_mcptest"));
        });
    }

    /// <summary>An empty parent GUID means "no parent", not a component with a zero id.</summary>
    [Test]
    public async Task TreatsAnEmptyParentGuid_AsNoParent()
    {
        Route(url => url.Contains("RetrieveDependenciesForDelete", StringComparison.Ordinal)
            ? OneDependency()
            : Empty);

        var report = await _svc.GetDependenciesForDeleteAsync(OrgUrl, ColumnId, 2);

        Assert.That(report.Dependents[0].ParentId, Is.Null);
    }

    /// <summary>Name resolution is a convenience; a denied lookup must not lose the dependency.</summary>
    [Test]
    public async Task StillReportsDependents_WhenNameResolutionFails()
    {
        _handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
                req.RequestUri!.ToString().Contains("RetrieveDependenciesForDelete", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(OneDependency(), Encoding.UTF8, "application/json")
                    }
                    : new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        Content = new StringContent("{\"error\":{\"message\":\"no\"}}", Encoding.UTF8, "application/json")
                    });

        var report = await _svc.GetDependenciesForDeleteAsync(OrgUrl, ColumnId, 2);

        Assert.Multiple(() =>
        {
            Assert.That(report.CanDelete, Is.False);
            Assert.That(report.Dependents, Has.Count.EqualTo(1));
            Assert.That(report.Dependents[0].Name, Is.Null);
            Assert.That(report.Dependents[0].ComponentTypeName, Is.EqualTo("SystemForm"));
        });
    }
}
