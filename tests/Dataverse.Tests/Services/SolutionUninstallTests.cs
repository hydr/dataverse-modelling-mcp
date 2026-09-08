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

/// <summary>
/// Uninstalling is deleting, and there is no undo — so the interesting assertions are about what the
/// dry run does <i>not</i> do.
/// </summary>
[TestFixture]
public sealed class SolutionUninstallTests
{
    private const string OrgUrl = "https://test.crm4.dynamics.com";
    private static readonly Guid SolutionId = Guid.Parse("bf401562-6242-f111-bec6-7ced8d4a3a5d");
    private static readonly Guid TableId = Guid.Parse("11111111-1111-1111-1111-111111110005");
    private static readonly Guid FormId = Guid.Parse("33333333-3333-3333-3333-333333330001");

    private Mock<HttpMessageHandler> _handlerMock = null!;
    private SolutionService _svc = null!;
    private ComponentDependencyService _dependencies = null!;
    private List<(HttpMethod Method, string Url)> _requests = null!;

    [SetUp]
    public void SetUp()
    {
        _handlerMock = new Mock<HttpMessageHandler>();
        _requests = [];

        var tokenProviderMock = new Mock<ITokenProvider>();
        tokenProviderMock
            .Setup(t => t.GetTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-token");

        var client = new DataverseHttpClient(
            new HttpClient(_handlerMock.Object),
            tokenProviderMock.Object,
            NullLogger<DataverseHttpClient>.Instance);

        _svc = new SolutionService(client, NullLogger<SolutionService>.Instance);
        _dependencies = new ComponentDependencyService(client, NullLogger<ComponentDependencyService>.Instance);
    }

    /// <param name="dependentCount">How many dependents the single root component reports.</param>
    private void Route(int dependentCount)
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
                _requests.Add((req.Method, url));

                string body;
                if (url.Contains("/solutions?", StringComparison.Ordinal))
                {
                    body = JsonSerializer.Serialize(new
                    {
                        value = new[] { new { solutionid = SolutionId.ToString() } }
                    });
                }
                else if (url.Contains($"solutions({SolutionId:D})", StringComparison.Ordinal))
                {
                    body = JsonSerializer.Serialize(new { ismanaged = true });
                }
                else if (url.Contains("RetrieveDependenciesForDelete", StringComparison.Ordinal))
                {
                    body = JsonSerializer.Serialize(new
                    {
                        value = Enumerable.Range(0, dependentCount).Select(_ => new
                        {
                            dependentcomponentobjectid = FormId.ToString(),
                            dependentcomponenttype = 60,
                            dependentcomponentparentid = "00000000-0000-0000-0000-000000000000",
                            dependencytype = 2
                        }).ToArray()
                    });
                }
                else if (url.Contains("/solutioncomponents?", StringComparison.Ordinal))
                {
                    body = JsonSerializer.Serialize(new
                    {
                        value = new[]
                        {
                            new
                            {
                                solutioncomponentid = Guid.NewGuid().ToString(),
                                objectid = TableId.ToString(),
                                componenttype = 1,
                                rootcomponentbehavior = (int?)0
                            },
                            // A subcomponent: no behaviour, so it is not checked separately.
                            new
                            {
                                solutioncomponentid = Guid.NewGuid().ToString(),
                                objectid = Guid.NewGuid().ToString(),
                                componenttype = 2,
                                rootcomponentbehavior = (int?)null
                            }
                        }
                    });
                }
                else
                {
                    body = "{\"value\":[]}";
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            });
    }

    [Test]
    public async Task DryRun_DeletesNothing()
    {
        Route(dependentCount: 0);

        var report = await _svc.UninstallAsync(OrgUrl, "DV_MCP_Test", _dependencies, dryRun: true);

        Assert.Multiple(() =>
        {
            Assert.That(report.DryRun, Is.True);
            Assert.That(report.Uninstalled, Is.False);
            Assert.That(_requests.Any(r => r.Method == HttpMethod.Delete), Is.False,
                "A dry run must not issue a delete.");
            Assert.That(report.Summary, Does.Contain("Pass dryRun=false"));
        });
    }

    /// <summary>Subcomponents ride along with their root, so checking them too would just repeat it.</summary>
    [Test]
    public async Task ChecksRootComponentsOnly()
    {
        Route(dependentCount: 0);

        var report = await _svc.UninstallAsync(OrgUrl, "DV_MCP_Test", _dependencies, dryRun: true);

        Assert.Multiple(() =>
        {
            Assert.That(report.ComponentCount, Is.EqualTo(2));
            Assert.That(report.RootComponentCount, Is.EqualTo(1));
            Assert.That(report.ComponentsChecked, Is.EqualTo(1));
            Assert.That(report.CheckTruncated, Is.False);
        });
    }

    [Test]
    public async Task DryRun_ReportsBlockers()
    {
        Route(dependentCount: 3);

        var report = await _svc.UninstallAsync(OrgUrl, "DV_MCP_Test", _dependencies, dryRun: true);

        Assert.Multiple(() =>
        {
            Assert.That(report.Blockers, Has.Count.EqualTo(1));
            Assert.That(report.Blockers[0].DependentCount, Is.EqualTo(3));
            Assert.That(report.Summary, Does.Contain("would block the uninstall"));
            Assert.That(report.Summary, Does.Contain("Nothing was changed"));
        });
    }

    [Test]
    public async Task DeletesTheSolution_WhenTheDryRunIsTurnedOff()
    {
        Route(dependentCount: 0);

        var report = await _svc.UninstallAsync(OrgUrl, "DV_MCP_Test", _dependencies, dryRun: false);

        Assert.Multiple(() =>
        {
            Assert.That(report.Uninstalled, Is.True);
            Assert.That(report.IsManaged, Is.True);
            Assert.That(
                _requests.Any(r => r.Method == HttpMethod.Delete
                                   && r.Url.Contains($"solutions({SolutionId:D})", StringComparison.Ordinal)),
                Is.True);
        });
    }

    /// <summary>
    /// A clean check over a capped set of components is not proof, and the summary must not read as
    /// though it were.
    /// </summary>
    [Test]
    public void Summary_AdmitsWhenTheCheckWasTruncated()
    {
        var summary = SolutionService.BuildUninstallSummary(
            "Big", dryRun: true, uninstalled: false, blockers: [], checkedCount: 100, truncated: true);

        Assert.Multiple(() =>
        {
            Assert.That(summary, Does.Contain("not a complete picture"));
            Assert.That(summary, Does.Contain("Only the first 100"));
        });
    }

    [Test]
    public void Summary_ListsBlockers_AndTrimsALongList()
    {
        var blockers = Enumerable.Range(0, 7)
            .Select(i => new ComponentDependencyReport(
                Guid.NewGuid(), 1, "Entity", $"table{i}", false, 2, "…", []))
            .ToList();

        var summary = SolutionService.BuildUninstallSummary(
            "Big", dryRun: true, uninstalled: false, blockers: blockers, checkedCount: 7, truncated: false);

        Assert.Multiple(() =>
        {
            Assert.That(summary, Does.Contain("table0"));
            Assert.That(summary, Does.Contain("table4"));
            Assert.That(summary, Does.Not.Contain("table5"));
            Assert.That(summary, Does.Contain(", …"));
        });
    }
}
