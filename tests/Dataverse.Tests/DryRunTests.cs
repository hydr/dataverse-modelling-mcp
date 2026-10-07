using System.Net;
using System.Text;
using System.Text.Json;
using Dataverse.Core.Auth;
using Dataverse.Core.Clients;
using Dataverse.Core.Services;
using Dataverse.Server;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Dataverse.Tests;

[TestFixture]
public class DryRunTests
{
    private const string OrgUrl = "https://contoso.crm4.dynamics.com";

    private sealed class FakeToken : ITokenProvider
    {
        public Task<string> GetTokenAsync(string scope, CancellationToken ct = default) => Task.FromResult("token");
    }

    // The recorded body is exactly what would go out — camelCase, as DataverseHttpClient serializes it.

    /// <summary>Answers GETs with a canned body and fails the test on anything else that reaches the wire.</summary>
    private sealed class GetOnlyHandler(string getBody) : HttpMessageHandler
    {
        public List<string> Sent { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sent.Add($"{request.Method} {request.RequestUri}");
            if (request.Method != HttpMethod.Get)
                Assert.Fail($"A {request.Method} reached the network during a dry run: {request.RequestUri}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(getBody, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static DataverseHttpClient Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler), new FakeToken(), NullLogger<DataverseHttpClient>.Instance);

    [Test]
    public async Task Writes_AreRecorded_NotSent()
    {
        var handler = new GetOnlyHandler("{}");
        var client = Client(handler);

        using var scope = DryRun.Begin();
        await client.PostAsync(OrgUrl, "api/data/v9.2/EntityDefinitions", new { SchemaName = "sample_widget" });
        await client.PutAsync(OrgUrl, "api/data/v9.2/EntityDefinitions(11111111-1111-1111-1111-111111110001)",
            new { a = 1 }, new Dictionary<string, string> { ["MSCRM.MergeLabels"] = "true" });
        await client.DeleteAsync(OrgUrl, "api/data/v9.2/savedqueries(11111111-1111-1111-1111-111111110002)");
        var id = await client.PostForIdAsync(OrgUrl, "api/data/v9.2/savedqueries", new { name = "x" }, "savedqueryid");

        Assert.Multiple(() =>
        {
            Assert.That(handler.Sent, Is.Empty);
            Assert.That(id, Is.EqualTo(Guid.Empty));
            Assert.That(scope.Requests.Select(r => r.Method), Is.EqualTo(new[] { "POST", "PUT", "DELETE", "POST" }));
            Assert.That(scope.Requests[0].Url, Is.EqualTo($"{OrgUrl}/api/data/v9.2/EntityDefinitions"));
            Assert.That(scope.Requests[0].Body!.Value.GetProperty("schemaName").GetString(), Is.EqualTo("sample_widget"));
            Assert.That(scope.Requests[1].Headers["MSCRM.MergeLabels"], Is.EqualTo("true"));
            Assert.That(scope.Requests[2].Body, Is.Null);
        });
    }

    [Test]
    public async Task Reads_StillGoOut()
    {
        var handler = new GetOnlyHandler("""{"value":1}""");
        var client = Client(handler);

        using var scope = DryRun.Begin();
        var result = await client.GetAsync<JsonElement>(OrgUrl, "api/data/v9.2/WhoAmI");

        Assert.Multiple(() =>
        {
            Assert.That(result.GetProperty("value").GetInt32(), Is.EqualTo(1));
            Assert.That(handler.Sent, Has.Count.EqualTo(1));
            Assert.That(scope.Requests, Is.Empty);
        });
    }

    [Test]
    public async Task OutsideAScope_WritesAreSent()
    {
        var handler = new GetOnlyHandler("{}");
        var client = Client(handler);

        Assert.That(DryRun.IsActive, Is.False);
        Assert.That(
            async () => await client.PostAsync(OrgUrl, "api/data/v9.2/roles", new { name = "x" }),
            Throws.InstanceOf<AssertionException>(), "the POST must reach the handler when no dry run is open");
    }

    [Test]
    public async Task ScopeEnds_WhenDisposed()
    {
        using (DryRun.Begin())
            Assert.That(DryRun.IsActive, Is.True);
        Assert.That(DryRun.IsActive, Is.False);
        await Task.CompletedTask;
    }

    [Test]
    public async Task TableCreate_ThroughTheService_RecordsTheMetadataPost()
    {
        var handler = new GetOnlyHandler("{}");
        var service = new TableService(Client(handler), NullLogger<TableService>.Instance);

        using var scope = DryRun.Begin();
        await service.CreateAsync(OrgUrl, "sample_widget", "Widget", "Widgets");

        Assert.That(scope.Requests, Has.Count.EqualTo(1));
        Assert.That(scope.Requests[0].Body!.Value.GetProperty("displayName")
            .GetProperty("localizedLabels")[0].GetProperty("label").GetString(), Is.EqualTo("Widget"));
    }

    // --- tool side ---

    [Test]
    public void SchemaTools_OfferDryRun()
    {
        var expected = new[]
        {
            "table_create", "table_update", "column_add", "column_update", "column_delete",
            "view_create", "view_update", "view_add_column", "view_set_sort", "role_create", "role_update",
        };
        Assert.That(ToolSafety.DryRunTools, Is.EquivalentTo(expected));
    }

    [Test]
    public void ToolsWithTheirOwnDryRun_AreNotWrappedByTheGenericOne() =>
        Assert.That(ToolSafety.DryRunTools, Has.None.AnyOf("workflow_set_definition", "bpf_set_definition", "solution_uninstall"));

    [Test]
    public void DryRunTools_AreAllWriteTools() =>
        Assert.That(ToolSafety.DryRunTools.Where(ToolSafety.IsReadOnlyTool), Is.Empty);

    [TestCase("table_create", """{"dryRun":true}""", true)]
    [TestCase("table_create", """{"dryRun":false}""", false)]
    [TestCase("table_create", """{}""", false)]
    [TestCase("table_create", """{"dryRun":"true"}""", false)]
    [TestCase("workflow_delete", """{"dryRun":true}""", false)] // no dry run offered — must not count
    [TestCase("workflow_set_definition", """{"dryRun":true}""", true)] // its own validate-and-report dry run
    [TestCase("solution_uninstall", """{}""", true)] // dry run is its default
    [TestCase("solution_uninstall", """{"dryRun":false}""", false)]
    public void IsDryRunRequest_OnlyForCapableToolsWithTrue(string tool, string args, bool expected)
    {
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args);
        Assert.That(ToolSafety.IsDryRunRequest(tool, arguments), Is.EqualTo(expected));
    }
}
