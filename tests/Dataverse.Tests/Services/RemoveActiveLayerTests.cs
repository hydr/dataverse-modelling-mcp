namespace Dataverse.Tests.Services;

using System.Net;
using System.Text;
using Dataverse.Core.Auth;
using Dataverse.Core.Clients;
using Dataverse.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

/// <summary>
/// <c>solution_remove_active_layer</c> used to POST <c>RemoveActiveCustomizations</c> as an action with
/// an int <c>ComponentType</c>. Dataverse exposes it as a function taking the component type by name, so
/// every call answered 404 "No HTTP resource was found".
/// </summary>
[TestFixture]
public sealed class RemoveActiveLayerTests
{
    private const string OrgUrl = "https://contoso.crm4.dynamics.com";
    private static readonly Guid TableId = Guid.Parse("11111111-1111-1111-1111-111111110001");

    private sealed class FakeToken : ITokenProvider
    {
        public Task<string> GetTokenAsync(string scope, CancellationToken ct = default) => Task.FromResult("token");
    }

    private sealed class RecordingHandler(string body) : HttpMessageHandler
    {
        public List<string> Sent { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sent.Add($"{request.Method} {Uri.UnescapeDataString(request.RequestUri!.ToString())}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (SolutionService Service, RecordingHandler Handler) Create(string body)
    {
        var handler = new RecordingHandler(body);
        var client = new DataverseHttpClient(new HttpClient(handler), new FakeToken(), NullLogger<DataverseHttpClient>.Instance);
        return (new SolutionService(client, NullLogger<SolutionService>.Instance), handler);
    }

    [Test]
    public async Task RemoveActiveLayer_CallsTheFunctionWithTheComponentTypeName()
    {
        var (svc, handler) = Create("""{"Result":true}""");

        var removed = await svc.RemoveActiveLayerAsync(OrgUrl, TableId, componentType: 1);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.True);
            Assert.That(handler.Sent, Has.Count.EqualTo(1));
            Assert.That(handler.Sent[0], Is.EqualTo(
                "GET https://contoso.crm4.dynamics.com/api/data/v9.2/RemoveActiveCustomizations(" +
                "SolutionComponentName='Entity',ComponentId=11111111-1111-1111-1111-111111110001)"));
        });
    }

    [Test]
    public async Task RemoveActiveLayer_ReportsResultFalse()
    {
        var (svc, _) = Create("""{"Result":false}""");

        Assert.That(await svc.RemoveActiveLayerAsync(OrgUrl, TableId, componentType: 1), Is.False);
    }

    [TestCase(48)]
    [TestCase(50)]
    [TestCase(52)]
    [TestCase(55)]
    public void RemoveActiveLayer_RefusesRibbonComponents_BeforeAnyRequest(int componentType)
    {
        var (svc, handler) = Create("{}");

        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RemoveActiveLayerAsync(OrgUrl, TableId, componentType));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("0x8004f059"));
            Assert.That(ex.Message, Does.Contain("ribbon_sync_unmanaged_layer"));
            Assert.That(handler.Sent, Is.Empty);
        });
    }

    [Test]
    public async Task RemoveActiveLayer_InADryRun_IsRecordedNotSent()
    {
        var (svc, handler) = Create("""{"Result":true}""");

        IReadOnlyList<PlannedRequest> planned;
        using (var scope = DryRun.Begin())
        {
            Assert.That(await svc.RemoveActiveLayerAsync(OrgUrl, TableId, componentType: 1), Is.True);
            planned = scope.Requests;
        }

        Assert.Multiple(() =>
        {
            Assert.That(handler.Sent, Is.Empty, "a GET with a side effect must not reach the network in a dry run");
            Assert.That(planned, Has.Count.EqualTo(1));
            Assert.That(planned[0].Method, Is.EqualTo("GET"));
            Assert.That(Uri.UnescapeDataString(planned[0].Url), Does.Contain("RemoveActiveCustomizations(SolutionComponentName='Entity'"));
        });
    }
}
