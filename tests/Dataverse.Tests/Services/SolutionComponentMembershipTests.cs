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
/// Covers the three ways solution membership used to mislead: an add that reports success without
/// adding anything, a remove that cannot work at all, and a component list of bare GUIDs.
/// </summary>
[TestFixture]
public sealed class SolutionComponentMembershipTests
{
    private const string OrgUrl = "https://test.crm4.dynamics.com";
    private static readonly Guid SolutionId = Guid.Parse("22222222-2222-2222-2222-222222220001");
    private static readonly Guid ColumnId = Guid.Parse("456ea321-0000-0000-0000-000000000001");
    private static readonly Guid TableId = Guid.Parse("2c974c35-0000-0000-0000-000000000002");

    private Mock<HttpMessageHandler> _handlerMock = null!;
    private SolutionService _svc = null!;
    private List<(string Method, string Url, string? Body)> _requests = null!;

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
    }

    /// <summary>Route responses by URL, recording every request so the payloads can be asserted.</summary>
    private void Route(Func<string, string, HttpResponseMessage> responder)
    {
        _handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(async (HttpRequestMessage req, CancellationToken _) =>
            {
                var url = req.RequestUri!.ToString();
                var body = req.Content is null ? null : await req.Content.ReadAsStringAsync();
                _requests.Add((req.Method.Method, url, body));
                return responder(req.Method.Method, url);
            });
    }

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Value(params object[] rows) => JsonSerializer.Serialize(new { value = rows });

    private static string SolutionIdRow => Value(new { solutionid = SolutionId.ToString() });

    // ------------------------------------------------------------------ add

    [Test]
    public async Task AddComponentAsync_ReportsExplicitMembership_WhenARowWasCreated()
    {
        var rowId = Guid.NewGuid();
        Route((method, url) =>
        {
            if (method == "POST")
                return Ok("{}");
            if (url.Contains("/solutions?", StringComparison.Ordinal))
                return Ok(SolutionIdRow);
            return Ok(Value(new { solutioncomponentid = rowId.ToString() }));
        });

        var result = await _svc.AddComponentAsync(OrgUrl, "ContosoForms", ColumnId, 2);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.ExplicitMembership, Is.True);
            Assert.That(result.SolutionComponentId, Is.EqualTo(rowId));
            Assert.That(result.ComponentTypeName, Is.EqualTo("Attribute"));
            Assert.That(result.Note, Is.Null);
        });
    }

    /// <summary>
    /// The reported bug: adding a column to a solution that already holds the owning table with
    /// <c>rootcomponentbehavior 0</c> creates no row. A bare <c>success: true</c> there sends the
    /// caller looking for a membership that will never exist.
    /// </summary>
    [Test]
    public async Task AddComponentAsync_NamesTheCoveringTable_WhenNoRowWasCreated()
    {
        Route((method, url) =>
        {
            if (method == "POST")
                return Ok("{}");
            if (url.Contains("/solutions?", StringComparison.Ordinal))
                return Ok(SolutionIdRow);

            // The membership probe finds nothing; the behavior-0 probe finds the table.
            if (url.Contains("rootcomponentbehavior eq 0", StringComparison.Ordinal))
                return Ok(Value(new { objectid = TableId.ToString() }));
            if (url.Contains("/solutioncomponents?", StringComparison.Ordinal))
                return Ok(Value());

            if (url.Contains("EntityDefinitions?", StringComparison.Ordinal))
                return Ok(Value(new { MetadataId = TableId.ToString(), LogicalName = "sample_order" }));

            // The column belongs to that table — this is what pins the owner down exactly.
            if (url.Contains("/Attributes(", StringComparison.Ordinal))
                return Ok(JsonSerializer.Serialize(new { LogicalName = "sample_documentviewer" }));

            return Ok(Value());
        });

        var result = await _svc.AddComponentAsync(OrgUrl, "ContosoOrders", ColumnId, 2);

        Assert.Multiple(() =>
        {
            Assert.That(result.ExplicitMembership, Is.False,
                "No row exists, so the result must not claim an explicit membership.");
            Assert.That(result.Success, Is.True, "Covered by the parent is a fine outcome, not a failure.");
            Assert.That(result.Note, Does.Contain("sample_order"));
            Assert.That(result.Note, Does.Contain("rootcomponentbehavior 0"));
            Assert.That(result.CoveringRootComponents, Does.Contain("sample_order"));
        });
    }

    [Test]
    public async Task AddComponentAsync_ReportsFailure_WhenNothingCouldHaveCoveredTheComponent()
    {
        Route((method, url) =>
        {
            if (method == "POST")
                return Ok("{}");
            if (url.Contains("/solutions?", StringComparison.Ordinal))
                return Ok(SolutionIdRow);
            return Ok(Value());
        });

        var result = await _svc.AddComponentAsync(OrgUrl, "ContosoForms", ColumnId, 2);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ExplicitMembership, Is.False);
            Assert.That(result.Note, Does.Contain("verify componentId and componentType"));
            Assert.That(result.CoveringRootComponents, Is.Null);
        });
    }

    // ------------------------------------------------------------------ remove

    /// <summary>
    /// The Web API action takes an entity reference, not a <c>ComponentId</c>. Sending
    /// <c>ComponentId</c> fails with <c>0x80048d19 … not a valid parameter for the operation</c>,
    /// which is what made the tool unusable.
    /// </summary>
    [Test]
    public async Task RemoveComponentAsync_SendsASolutionComponentEntityReference()
    {
        Route((method, url) =>
        {
            if (method == "POST")
                return Ok("{}");
            if (url.Contains("/solutions?", StringComparison.Ordinal))
                return Ok(SolutionIdRow);

            // Present before the remove, gone after it.
            var alreadyRemoved = _requests.Any(r => r.Method == "POST");
            return Ok(alreadyRemoved
                ? Value()
                : Value(new { solutioncomponentid = Guid.NewGuid().ToString() }));
        });

        var result = await _svc.RemoveComponentAsync(OrgUrl, "ContosoForms", ColumnId, 2);

        Assert.That(result.Removed, Is.True);
        Assert.That(result.Success, Is.True);

        var action = _requests.Single(r => r.Method == "POST");
        Assert.That(action.Url, Does.Contain("RemoveSolutionComponent"));

        using var doc = JsonDocument.Parse(action.Body!);
        var root = doc.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(root.TryGetProperty("ComponentId", out _), Is.False,
                "ComponentId is not a parameter of the Web API action and makes it fail outright.");

            Assert.That(root.TryGetProperty("SolutionComponent", out var reference), Is.True);
            Assert.That(
                reference.GetProperty("@odata.type").GetString(),
                Is.EqualTo("Microsoft.Dynamics.CRM.solutioncomponent"));

            // The component's own objectid — not the membership row's id, which fails with 0x8004f021.
            Assert.That(
                reference.GetProperty("solutioncomponentid").GetString(),
                Is.EqualTo(ColumnId.ToString("D")));

            Assert.That(root.GetProperty("ComponentType").GetInt32(), Is.EqualTo(2));
            Assert.That(root.GetProperty("SolutionUniqueName").GetString(), Is.EqualTo("ContosoForms"));
        });
    }

    [Test]
    public async Task RemoveComponentAsync_DoesNotCallTheAction_WhenTheComponentIsNotAMember()
    {
        Route((method, url) =>
            url.Contains("/solutions?", StringComparison.Ordinal) ? Ok(SolutionIdRow) : Ok(Value()));

        var result = await _svc.RemoveComponentAsync(OrgUrl, "ContosoForms", ColumnId, 2);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.Removed, Is.False);
            Assert.That(result.Note, Does.Contain("nothing to remove"));

            // The id mix-up is the likeliest cause, so the note has to name it.
            Assert.That(result.Note, Does.Contain("objectid"));
            Assert.That(result.Note, Does.Contain("solutioncomponentid"));
        });

        Assert.That(_requests.Any(r => r.Method == "POST"), Is.False,
            "Calling the action for a non-member only produces a cryptic platform error.");
    }

    [Test]
    public async Task RemoveComponentAsync_ExplainsCoverage_WhenTheComponentBelongsToABehaviorZeroTable()
    {
        Route((method, url) =>
        {
            if (url.Contains("/solutions?", StringComparison.Ordinal))
                return Ok(SolutionIdRow);
            if (url.Contains("rootcomponentbehavior eq 0", StringComparison.Ordinal))
                return Ok(Value(new { objectid = TableId.ToString() }));
            if (url.Contains("/solutioncomponents?", StringComparison.Ordinal))
                return Ok(Value());
            if (url.Contains("EntityDefinitions?", StringComparison.Ordinal))
                return Ok(Value(new { MetadataId = TableId.ToString(), LogicalName = "sample_order" }));
            if (url.Contains("/Attributes(", StringComparison.Ordinal))
                return Ok(JsonSerializer.Serialize(new { LogicalName = "sample_documentviewer" }));
            return Ok(Value());
        });

        var result = await _svc.RemoveComponentAsync(OrgUrl, "ContosoOrders", ColumnId, 2);

        Assert.Multiple(() =>
        {
            Assert.That(result.Removed, Is.False);
            Assert.That(result.Note, Does.Contain("sample_order"));
            Assert.That(result.Note, Does.Contain("cannot be removed on its own"));
        });
    }

    [Test]
    public async Task RemoveComponentAsync_ReportsFailure_WhenTheRowSurvivesTheAction()
    {
        Route((method, url) =>
        {
            if (method == "POST")
                return Ok("{}");
            if (url.Contains("/solutions?", StringComparison.Ordinal))
                return Ok(SolutionIdRow);
            return Ok(Value(new { solutioncomponentid = Guid.NewGuid().ToString() }));
        });

        var result = await _svc.RemoveComponentAsync(OrgUrl, "ContosoForms", ColumnId, 2);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.Note, Does.Contain("still there"));
        });
    }

    // ------------------------------------------------------------------ read

    /// <summary>
    /// The component list is read as its own paged query, so a solution with more components than
    /// fit on one page comes back complete instead of quietly cut off.
    /// </summary>
    [Test]
    public async Task GetAsync_FollowsNextLink_AcrossComponentPages()
    {
        Route((method, url) =>
        {
            if (url.Contains("/solutions?", StringComparison.Ordinal))
                return Ok(JsonSerializer.Serialize(new
                {
                    value = new[]
                    {
                        new
                        {
                            solutionid = SolutionId.ToString(),
                            uniquename = "ContosoOrders",
                            friendlyname = "Purchase Orders",
                            version = "1.0",
                            ismanaged = false
                        }
                    }
                }));

            if (url.Contains("page2", StringComparison.Ordinal))
                return Ok(Value(
                    new { solutioncomponentid = Guid.NewGuid().ToString(), objectid = Guid.NewGuid().ToString(), componenttype = 61 }));

            if (url.Contains("/solutioncomponents?", StringComparison.Ordinal))
                return Ok(JsonSerializer.Serialize(new
                {
                    value = new[]
                    {
                        new { solutioncomponentid = Guid.NewGuid().ToString(), objectid = TableId.ToString(), componenttype = 1 }
                    },
                    @odata_nextLink = $"{OrgUrl}/api/data/v9.2/solutioncomponents?page2=1"
                }).Replace("odata_nextLink", "@odata.nextLink"));

            return Ok(Value());
        });

        var detail = await _svc.GetAsync(OrgUrl, "ContosoOrders", resolveComponentNames: false);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.ComponentCount, Is.EqualTo(2));
        Assert.That(detail.Components.Select(c => c.ComponentType), Is.EquivalentTo(new[] { 1, 61 }));
    }

    [Test]
    public async Task GetAsync_ReportsRootComponentBehaviour_AsCodeAndLabel()
    {
        Route((method, url) =>
        {
            if (url.Contains("/solutions?", StringComparison.Ordinal))
                return Ok(JsonSerializer.Serialize(new
                {
                    value = new[]
                    {
                        new { solutionid = SolutionId.ToString(), uniquename = "S", friendlyname = "S", version = "1.0", ismanaged = false }
                    }
                }));

            if (url.Contains("/solutioncomponents?", StringComparison.Ordinal))
                return Ok(Value(
                    new { solutioncomponentid = Guid.NewGuid().ToString(), objectid = Guid.NewGuid().ToString(), componenttype = 1, rootcomponentbehavior = 0 },
                    new { solutioncomponentid = Guid.NewGuid().ToString(), objectid = Guid.NewGuid().ToString(), componenttype = 1, rootcomponentbehavior = 1 },
                    new { solutioncomponentid = Guid.NewGuid().ToString(), objectid = Guid.NewGuid().ToString(), componenttype = 1, rootcomponentbehavior = 2 }));

            return Ok(Value());
        });

        var detail = await _svc.GetAsync(OrgUrl, "S", resolveComponentNames: false);

        Assert.That(
            detail!.Components.Select(c => c.RootComponentBehaviorName),
            Is.EqualTo(new[] { "IncludeSubcomponents", "DoNotIncludeSubcomponents", "IncludeAsShellOnly" }));
    }

    /// <summary>
    /// 0 is a meaningful behaviour ("include subcomponents"), so an absent value must stay null
    /// rather than being reported as 0. Column rows genuinely come back without the field.
    /// </summary>
    [Test]
    public async Task GetAsync_LeavesRootComponentBehaviour_NullWhenAbsent()
    {
        Route((method, url) =>
        {
            if (url.Contains("/solutions?", StringComparison.Ordinal))
                return Ok(JsonSerializer.Serialize(new
                {
                    value = new[]
                    {
                        new { solutionid = SolutionId.ToString(), uniquename = "S", friendlyname = "S", version = "1.0", ismanaged = false }
                    }
                }));
            if (url.Contains("/solutioncomponents?", StringComparison.Ordinal))
                return Ok(Value(new
                {
                    solutioncomponentid = Guid.NewGuid().ToString(),
                    objectid = ColumnId.ToString(),
                    componenttype = 2,
                    rootcomponentbehavior = (int?)null
                }));
            return Ok(Value());
        });

        var detail = await _svc.GetAsync(OrgUrl, "S", resolveComponentNames: false);

        Assert.Multiple(() =>
        {
            Assert.That(detail!.Components[0].RootComponentBehavior, Is.Null);
            Assert.That(detail.Components[0].RootComponentBehaviorName, Is.Null);
        });
    }

    /// <summary>
    /// A component list of bare GUIDs forces the caller to resolve every id by hand, which is what
    /// made a solution read practically unusable.
    /// </summary>
    [Test]
    public async Task GetAsync_ResolvesComponentNames_PerComponentType()
    {
        var webResourceId = Guid.NewGuid();

        Route((method, url) =>
        {
            if (url.Contains("/solutions?", StringComparison.Ordinal))
                return Ok(JsonSerializer.Serialize(new
                {
                    value = new[]
                    {
                        new { solutionid = SolutionId.ToString(), uniquename = "S", friendlyname = "S", version = "1.0", ismanaged = false }
                    }
                }));

            if (url.Contains("/solutioncomponents?", StringComparison.Ordinal))
                return Ok(Value(
                    new { solutioncomponentid = Guid.NewGuid().ToString(), objectid = TableId.ToString(), componenttype = 1 },
                    new { solutioncomponentid = Guid.NewGuid().ToString(), objectid = webResourceId.ToString(), componenttype = 61 }));

            if (url.Contains("EntityDefinitions?", StringComparison.Ordinal))
                return Ok(Value(new { MetadataId = TableId.ToString(), LogicalName = "sample_order" }));

            if (url.Contains("webresourceset", StringComparison.Ordinal))
                return Ok(Value(new { webresourceid = webResourceId.ToString(), name = "sample_helper_js" }));

            return Ok(Value());
        });

        var detail = await _svc.GetAsync(OrgUrl, "S");

        Assert.That(
            detail!.Components.Select(c => c.Name),
            Is.EquivalentTo(new[] { "sample_order", "sample_helper_js" }));
    }

    /// <summary>
    /// Name resolution is a convenience. A tenant that denies one of the lookups must still get its
    /// component list.
    /// </summary>
    [Test]
    public async Task GetAsync_StillReturnsComponents_WhenNameResolutionIsDenied()
    {
        Route((method, url) =>
        {
            if (url.Contains("/solutions?", StringComparison.Ordinal))
                return Ok(JsonSerializer.Serialize(new
                {
                    value = new[]
                    {
                        new { solutionid = SolutionId.ToString(), uniquename = "S", friendlyname = "S", version = "1.0", ismanaged = false }
                    }
                }));
            if (url.Contains("/solutioncomponents?", StringComparison.Ordinal))
                return Ok(Value(new { solutioncomponentid = Guid.NewGuid().ToString(), objectid = Guid.NewGuid().ToString(), componenttype = 61 }));

            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("{\"error\":{\"message\":\"no access\"}}", Encoding.UTF8, "application/json")
            };
        });

        var detail = await _svc.GetAsync(OrgUrl, "S");

        Assert.Multiple(() =>
        {
            Assert.That(detail!.Components, Has.Count.EqualTo(1));
            Assert.That(detail.Components[0].Name, Is.Null);
            Assert.That(detail.Components[0].ComponentTypeName, Is.EqualTo("WebResource"));
        });
    }
}
