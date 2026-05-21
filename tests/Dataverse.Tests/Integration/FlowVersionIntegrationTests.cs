namespace Dataverse.Tests.Integration;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dataverse.Core.Auth;
using Dataverse.Tests.Infrastructure;
using NUnit.Framework;

[TestFixture]
[Category("Integration")]
public sealed class FlowVersionIntegrationTests : IntegrationTestBase
{
    private const string TestSolutionUniqueName = "DV_MCP_Test";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private HttpClient _http = null!;
    private ITokenProvider _tokenProvider = null!;

    [SetUp]
    public void SetUp()
    {
        _http = new HttpClient();
        _tokenProvider = new AzureCliTokenProvider();
    }

    [TearDown]
    public void TearDown() => _http.Dispose();

    [Test]
    public async Task CreateModifyRestore_ProducesVersionHistory()
    {
        var flowName = $"DvMcpTest_FlowVersion_{Guid.NewGuid():N}";
        Guid flowId = Guid.Empty;

        try
        {
            // 1. Create a minimal solution-aware modern flow (manual trigger + Compose action).
            flowId = await CreateModernFlowAsync(flowName, BuildClientData("Initial value v1"));
            Assert.That(flowId, Is.Not.EqualTo(Guid.Empty), "Flow creation failed.");

            // 2. Publish via the maker-UI-style action — creates a Publish (Operation=2) version.
            await FlowVersionService.PublishAsync(OrgUrl, flowId);

            var afterFirstPublish = await FlowVersionService.ListAsync(OrgUrl, flowId);
            Assert.That(afterFirstPublish.Any(v => v.Operation == 2), Is.True,
                "Expected a Publish-typed version after first publish.");

            var baselineVersion = afterFirstPublish
                .Where(v => v.Operation == 2)
                .OrderBy(v => v.CreatedOn)
                .First();

            // 3. Save draft (Update version) with a modified definition, then publish again.
            await FlowVersionService.SaveDraftAsync(
                OrgUrl, flowId, BuildClientData("Modified value v2"), name: flowName);

            var afterSaveDraft = await FlowVersionService.ListAsync(OrgUrl, flowId);
            Assert.That(afterSaveDraft.Any(v => v.Operation == 1), Is.True,
                "Expected an Update-typed version after save draft.");

            await FlowVersionService.PublishAsync(OrgUrl, flowId);

            var afterSecondPublish = await FlowVersionService.ListAsync(OrgUrl, flowId);
            Assert.That(afterSecondPublish.Count(v => v.Operation == 2), Is.GreaterThanOrEqualTo(2),
                "Expected at least two Publish-typed versions after second publish.");

            // 4. flow_get_version round-trips on the baseline version.
            var detail = await FlowVersionService.GetAsync(OrgUrl, flowId, baselineVersion.VersionId);
            Assert.That(detail, Is.Not.Null);
            Assert.That(detail!.VersionId, Is.EqualTo(baselineVersion.VersionId));

            // 5. Restore the baseline publish version via the bound RestoreComponentVersion action
            // (same call the Power Automate maker portal uses).
            var versionsBeforeRestore = await FlowVersionService.ListAsync(OrgUrl, flowId);

            var restoreResult = await FlowVersionService.RestoreAsync(
                OrgUrl, flowId, baselineVersion.VersionId,
                changeSummary: "Integration test restore");

            Assert.That(restoreResult.RestoredFromVersionId, Is.EqualTo(baselineVersion.VersionId));
            Assert.That(restoreResult.WorkflowId, Is.EqualTo(flowId));

            // RestoreComponentVersion completes asynchronously — give Dataverse a moment to
            // materialize the Restore-typed componentversion row.
            await Task.Delay(TimeSpan.FromSeconds(2));

            // 6. A new Restore-typed version (Operation == 3) should now point at the baseline.
            var versionsAfterRestore = await FlowVersionService.ListAsync(OrgUrl, flowId);
            Assert.That(versionsAfterRestore.Count, Is.GreaterThan(versionsBeforeRestore.Count),
                "Expected an additional componentversion row after restore.");
            Assert.That(versionsAfterRestore.Any(v =>
                    v.Operation == 3 && v.RestoredFromVersionId == baselineVersion.VersionId),
                Is.True,
                "Expected a Restore-typed version (Operation=3) pointing at the baseline version.");
        }
        finally
        {
            if (flowId != Guid.Empty)
                await TryDeleteWorkflowAsync(flowId);
        }
    }

    private static string BuildClientData(string composeValue)
    {
        // Hand-written so we can use Logic Apps keys ($schema, $connections, $authentication)
        // that collide with C# identifiers and would be mangled by string.Replace.
        var escapedValue = JsonEncodedText.Encode(composeValue).ToString();
        return $$"""
        {
          "properties": {
            "connectionReferences": {},
            "definition": {
              "$schema": "https://schema.management.azure.com/providers/Microsoft.Logic/schemas/2016-06-01/workflowdefinition.json#",
              "contentVersion": "1.0.0.0",
              "parameters": {
                "$connections": { "defaultValue": {}, "type": "Object" },
                "$authentication": { "defaultValue": {}, "type": "SecureObject" }
              },
              "triggers": {
                "manual": {
                  "type": "Request",
                  "kind": "Button",
                  "inputs": {
                    "schema": { "type": "object", "properties": {}, "required": [] }
                  }
                }
              },
              "actions": {
                "Compose": {
                  "type": "Compose",
                  "inputs": "{{escapedValue}}",
                  "runAfter": {}
                }
              }
            }
          },
          "schemaVersion": "1.0.0.0"
        }
        """;
    }

    private async Task<Guid> CreateModernFlowAsync(string name, string clientData)
    {
        var body = new
        {
            name,
            category = 5,
            type = 1,
            primaryentity = "none",
            statecode = 0,
            statuscode = 1,
            clientdata = clientData
        };

        using var req = await BuildRequestAsync(HttpMethod.Post, "api/data/v9.2/workflows", body);
        req.Headers.Add("MSCRM.SolutionUniqueName", TestSolutionUniqueName);
        req.Headers.Add("Prefer", "return=representation");

        using var resp = await _http.SendAsync(req);
        var content = await resp.Content.ReadAsStringAsync();
        Assert.That(resp.IsSuccessStatusCode, Is.True, $"Create flow failed: {(int)resp.StatusCode} {content}");

        using var doc = JsonDocument.Parse(content);
        return Guid.Parse(doc.RootElement.GetProperty("workflowid").GetString()!);
    }

    private async Task<string> GetWorkflowClientDataAsync(Guid flowId)
    {
        using var req = await BuildRequestAsync(HttpMethod.Get,
            $"api/data/v9.2/workflows({flowId})?$select=clientdata", body: null);
        using var resp = await _http.SendAsync(req);
        var content = await resp.Content.ReadAsStringAsync();
        Assert.That(resp.IsSuccessStatusCode, Is.True, $"Get workflow failed: {(int)resp.StatusCode} {content}");
        using var doc = JsonDocument.Parse(content);
        return doc.RootElement.GetProperty("clientdata").GetString() ?? string.Empty;
    }

    private async Task PatchWorkflowAsync(Guid flowId, object body)
    {
        using var req = await BuildRequestAsync(HttpMethod.Patch, $"api/data/v9.2/workflows({flowId})", body);
        using var resp = await _http.SendAsync(req);
        var content = await resp.Content.ReadAsStringAsync();
        Assert.That(resp.IsSuccessStatusCode, Is.True, $"Patch workflow failed: {(int)resp.StatusCode} {content}");
    }

    private async Task TryDeleteWorkflowAsync(Guid flowId)
    {
        try
        {
            // Deactivate first so delete is allowed.
            await PatchWorkflowAsync(flowId, new { statecode = 0, statuscode = 1 });
        }
        catch { /* ignore */ }

        try
        {
            using var req = await BuildRequestAsync(HttpMethod.Delete, $"api/data/v9.2/workflows({flowId})", body: null);
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                TestContext.Progress.WriteLine($"Cleanup delete failed for {flowId}: {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            TestContext.Progress.WriteLine($"Cleanup delete threw for {flowId}: {ex.Message}");
        }
    }

    private async Task<HttpRequestMessage> BuildRequestAsync(HttpMethod method, string relativeUrl, object? body)
    {
        var scope = $"{OrgUrl.TrimEnd('/')}/.default";
        var token = await _tokenProvider.GetTokenAsync(scope);

        var req = new HttpRequestMessage(method, new Uri($"{OrgUrl.TrimEnd('/')}/{relativeUrl}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("OData-MaxVersion", "4.0");
        req.Headers.Add("OData-Version", "4.0");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        return req;
    }
}
