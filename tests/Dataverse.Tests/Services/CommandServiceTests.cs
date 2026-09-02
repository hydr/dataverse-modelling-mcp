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
public sealed class CommandServiceTests
{
    private Mock<HttpMessageHandler> _handlerMock = null!;
    private CommandService _svc = null!;

    private const string OrgUrl = "https://test.crm4.dynamics.com";
    private static readonly Guid TableMetadataId = Guid.Parse("2c974c35-1fad-4409-9d1f-7465003ac518");
    private static readonly Guid WebResourceId = Guid.Parse("123355b4-8d89-f111-8077-002248997467");

    [SetUp]
    public void SetUp()
    {
        _handlerMock = new Mock<HttpMessageHandler>();

        var tokenProviderMock = new Mock<ITokenProvider>();
        tokenProviderMock
            .Setup(t => t.GetTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-token");

        var client = new DataverseHttpClient(
            new HttpClient(_handlerMock.Object),
            tokenProviderMock.Object,
            NullLogger<DataverseHttpClient>.Instance);

        _svc = new CommandService(client, NullLogger<CommandService>.Instance);
    }

    [Test]
    public async Task ListAsync_FiltersOnContextValue_AndResolvesEnumNames()
    {
        var id = Guid.NewGuid();
        Uri? capturedUri = null;

        SetupResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new
                {
                    appactionid = id.ToString(),
                    name = "sample.sample_purchaseorder.CorrectPrice.Grid",
                    uniquename = "sample_CorrectPriceGrid!sample_purchaseorder!1",
                    buttonlabeltext = "EA-ER-Differenz auflösen",
                    location = 1,
                    origin = 0,
                    visibilitytype = 0,
                    onclickeventjavascriptfunctionname = "Sample.PurchaseOrder.CorrectPrice.onGridButton",
                    hidden = false,
                    ismanaged = false
                }
            }
        }), req => capturedUri = req.RequestUri);

        var results = await _svc.ListAsync(OrgUrl, "sample_purchaseorder", CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].LocationName, Is.EqualTo("MainGrid"));
        Assert.That(results[0].OriginName, Is.EqualTo("Default"));
        Assert.That(results[0].VisibilityTypeName, Is.EqualTo("None"));
        Assert.That(
            Uri.UnescapeDataString(capturedUri!.ToString()),
            Does.Contain("contextvalue eq 'sample_purchaseorder'"));
    }

    [Test]
    public async Task GetAsync_DecodesParametersWithTypeNames()
    {
        var id = Guid.NewGuid();

        SetupResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            appactionid = id.ToString(),
            name = "sample.test.Grid",
            location = 1,
            context = 1,
            contextvalue = "sample_purchaseorder",
            _contextentity_value = TableMetadataId.ToString(),
            _onclickeventjavascriptwebresourceid_value = WebResourceId.ToString(),
            origin = 0,
            visibilitytype = 0,
            onclickeventtype = 2,
            type = 0,
            sequence = 1000100200.0,
            onclickeventjavascriptparameters =
                "[{\"type\":23,\"value\":null},{\"type\":21,\"value\":\"abc\"}]"
        }));

        var detail = await _svc.GetAsync(OrgUrl, id, CancellationToken.None);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.ContextName, Is.EqualTo("Entity"));
        Assert.That(detail.OnClickEventTypeName, Is.EqualTo("JavaScript"));
        Assert.That(detail.ContextEntityMetadataId, Is.EqualTo(TableMetadataId));
        Assert.That(detail.OnClickEventJavaScriptWebResourceId, Is.EqualTo(WebResourceId));
        Assert.That(detail.Parameters, Has.Count.EqualTo(2));
        Assert.That(detail.Parameters[0].TypeName, Is.EqualTo("SelectedControlSelectedItemIds"));
        Assert.That(detail.Parameters[1].TypeName, Is.EqualTo("StringParameter"));
        Assert.That(detail.Parameters[1].Value, Is.EqualTo("abc"));
        Assert.That(detail.Sequence, Is.EqualTo(1000100200m));
    }

    [Test]
    public async Task CreateAsync_BindsLookupsThroughNavigationPropertyNames()
    {
        var newId = Guid.NewGuid();
        var bodies = new List<string?>();
        var call = 0;

        _handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) =>
                bodies.Add(req.Content?.ReadAsStringAsync().GetAwaiter().GetResult()))
            .ReturnsAsync(() =>
            {
                call++;
                if (call == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            JsonSerializer.Serialize(new { MetadataId = TableMetadataId.ToString() }),
                            Encoding.UTF8,
                            "application/json")
                    };
                }

                var resp = new HttpResponseMessage(HttpStatusCode.NoContent);
                resp.Headers.TryAddWithoutValidation(
                    "OData-EntityId", $"{OrgUrl}/api/data/v9.2/appactions({newId})");
                return resp;
            });

        var created = await _svc.CreateAsync(
            OrgUrl,
            tableLogicalName: "sample_purchaseorder",
            name: "sample.sample_purchaseorder.CorrectPrice.Form",
            buttonLabelText: "EA-ER-Differenz auflösen",
            location: (int)CommandLocation.Form,
            javaScriptWebResourceId: WebResourceId,
            functionName: "Sample.PurchaseOrder.CorrectPrice.onFormButton",
            parameters: new[] { new CommandParameter((int)CommandParameterType.PrimaryControl, null) },
            ct: CancellationToken.None);

        Assert.That(created, Is.EqualTo(newId));

        using var body = JsonDocument.Parse(bodies[1]!);
        var root = body.RootElement;

        // Attribute names (contextentity / onclickeventjavascriptwebresourceid) fail with 0x80048d19.
        Assert.That(
            root.GetProperty("ContextEntity@odata.bind").GetString(),
            Is.EqualTo($"/entities({TableMetadataId})"));
        Assert.That(
            root.GetProperty("OnClickEventJavaScriptWebResourceId@odata.bind").GetString(),
            Is.EqualTo($"/webresourceset({WebResourceId})"));
        Assert.That(root.TryGetProperty("contextentity", out _), Is.False);
        Assert.That(root.TryGetProperty("onclickeventjavascriptwebresourceid", out _), Is.False);

        Assert.That(root.GetProperty("context").GetInt32(), Is.EqualTo((int)CommandContext.Entity));
        Assert.That(root.GetProperty("contextvalue").GetString(), Is.EqualTo("sample_purchaseorder"));
        Assert.That(root.GetProperty("onclickeventtype").GetInt32(), Is.EqualTo(2));
        Assert.That(
            root.GetProperty("onclickeventjavascriptparameters").GetString(),
            Is.EqualTo("[{\"type\":5,\"value\":null}]"));
    }

    [Test]
    public async Task CreateAsync_DefaultsToOriginDefaultAndVisibilityNone()
    {
        var bodies = new List<string?>();
        var call = 0;

        _handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) =>
                bodies.Add(req.Content?.ReadAsStringAsync().GetAwaiter().GetResult()))
            .ReturnsAsync(() =>
            {
                call++;
                return call == 1
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            JsonSerializer.Serialize(new { MetadataId = TableMetadataId.ToString() }),
                            Encoding.UTF8,
                            "application/json")
                    }
                    : new HttpResponseMessage(HttpStatusCode.NoContent);
            });

        await _svc.CreateAsync(
            OrgUrl, "sample_purchaseorder", "n", "l", 0, WebResourceId, "fn",
            Array.Empty<CommandParameter>(), ct: CancellationToken.None);

        using var body = JsonDocument.Parse(bodies[1]!);
        // origin=Migrated does not render and cannot be corrected by a later PATCH.
        Assert.That(body.RootElement.GetProperty("origin").GetInt32(), Is.EqualTo((int)CommandOrigin.Default));
        Assert.That(
            body.RootElement.GetProperty("visibilitytype").GetInt32(),
            Is.EqualTo((int)CommandVisibilityType.None));
        // The uniquename has to carry a customization prefix — otherwise Dataverse answers 0x800608ad.
        Assert.That(
            body.RootElement.GetProperty("uniquename").GetString(),
            Is.EqualTo("sample_n!sample_purchaseorder!0"));
    }

    [Test]
    public async Task DeleteAsync_SendsDeleteToTheAppActionRow()
    {
        var id = Guid.NewGuid();
        HttpMethod? method = null;
        Uri? uri = null;

        SetupResponse(HttpStatusCode.NoContent, string.Empty, req =>
        {
            method = req.Method;
            uri = req.RequestUri;
        });

        await _svc.DeleteAsync(OrgUrl, id, CancellationToken.None);

        Assert.That(method, Is.EqualTo(HttpMethod.Delete));
        Assert.That(uri!.ToString(), Does.EndWith($"appactions({id})"));
    }

    [Test]
    public void ParseParameterSpec_AcceptsCommaSeparatedNames()
    {
        var result = CommandService.ParseParameterSpec("SelectedControlSelectedItemIds, SelectedControl");

        Assert.That(result.Select(p => p.Type), Is.EqualTo(new[] { 23, 12 }));
        Assert.That(result.All(p => p.Value is null), Is.True);
    }

    [Test]
    public void ParseParameterSpec_AcceptsJsonArrayOfNamesAndNumbers()
    {
        var result = CommandService.ParseParameterSpec("[\"PrimaryControl\", 4]");

        Assert.That(result.Select(p => p.Type), Is.EqualTo(new[] { 5, 4 }));
    }

    [Test]
    public void ParseParameterSpec_AcceptsObjectsWithValues()
    {
        var result = CommandService.ParseParameterSpec(
            "[{\"type\":\"StringParameter\",\"value\":\"/de-de/manager/\"},{\"type\":12}]");

        Assert.That(result[0].Type, Is.EqualTo(21));
        Assert.That(result[0].Value, Is.EqualTo("/de-de/manager/"));
        Assert.That(result[1].Type, Is.EqualTo(12));
        Assert.That(result[1].Value, Is.Null);
    }

    [Test]
    public void ParseParameterSpec_ReturnsEmpty_ForNullOrBlank()
    {
        Assert.That(CommandService.ParseParameterSpec(null), Is.Empty);
        Assert.That(CommandService.ParseParameterSpec("   "), Is.Empty);
    }

    [Test]
    public void ParseParameterSpec_Throws_OnUnknownName()
    {
        Assert.Throws<ArgumentException>(() => CommandService.ParseParameterSpec("NotAParameter"));
    }

    [Test]
    public void SerializeParameters_ProducesTheStoredJsonShape()
    {
        var json = CommandService.SerializeParameters(new[]
        {
            new CommandParameter((int)CommandParameterType.SelectedControlSelectedItemIds, null),
            new CommandParameter((int)CommandParameterType.SelectedControl, null)
        });

        Assert.That(json, Is.EqualTo("[{\"type\":23,\"value\":null},{\"type\":12,\"value\":null}]"));
    }

    [Test]
    public void ParameterTypeEnum_MatchesTheValuesVerifiedAgainstTheRibbonXml()
    {
        // Derived by aligning migrated appaction rows with their ribbon CommandDefinition parameters.
        Assert.Multiple(() =>
        {
            Assert.That((int)CommandParameterType.PrimaryEntityTypeCode, Is.EqualTo(1));
            Assert.That((int)CommandParameterType.PrimaryEntityTypeName, Is.EqualTo(2));
            Assert.That((int)CommandParameterType.PrimaryItemIds, Is.EqualTo(3));
            Assert.That((int)CommandParameterType.FirstPrimaryItemId, Is.EqualTo(4));
            Assert.That((int)CommandParameterType.PrimaryControl, Is.EqualTo(5));
            Assert.That((int)CommandParameterType.SelectedEntityTypeCode, Is.EqualTo(7));
            Assert.That((int)CommandParameterType.SelectedEntityTypeName, Is.EqualTo(8));
            Assert.That((int)CommandParameterType.FirstSelectedItemId, Is.EqualTo(10));
            Assert.That((int)CommandParameterType.SelectedControl, Is.EqualTo(12));
            Assert.That((int)CommandParameterType.BoolParameter, Is.EqualTo(18));
            Assert.That((int)CommandParameterType.IntParameter, Is.EqualTo(20));
            Assert.That((int)CommandParameterType.StringParameter, Is.EqualTo(21));
            Assert.That((int)CommandParameterType.SelectedControlSelectedItemIds, Is.EqualTo(23));
            Assert.That((int)CommandParameterType.SelectedControlSelectedItemReferences, Is.EqualTo(24));
            Assert.That((int)CommandParameterType.SelectedControlAllItemCount, Is.EqualTo(25));
        });
    }

    [Test]
    public void LocationEnum_MatchesTheAppActionLocationChoice()
    {
        Assert.Multiple(() =>
        {
            Assert.That((int)CommandLocation.Form, Is.EqualTo(0));
            Assert.That((int)CommandLocation.MainGrid, Is.EqualTo(1));
            Assert.That((int)CommandLocation.SubGrid, Is.EqualTo(2));
            Assert.That((int)CommandLocation.AssociatedGrid, Is.EqualTo(3));
            Assert.That((int)CommandLocation.QuickForm, Is.EqualTo(4));
            Assert.That((int)CommandLocation.GlobalHeader, Is.EqualTo(5));
            Assert.That((int)CommandLocation.Dashboard, Is.EqualTo(6));
        });
    }

    [Test]
    public void BuildUniqueName_PrefixesAndSanitizes()
    {
        Assert.That(
            CommandService.BuildUniqueName("sample.purchaseorder.CorrectPrice", "sample_purchaseorder", 1, "sample"),
            Is.EqualTo("sample_samplepurchaseorderCorrectPrice!sample_purchaseorder!1"));
    }

    [Test]
    public void BuildUniqueName_Throws_WithoutAPrefix()
    {
        Assert.Throws<ArgumentException>(
            () => CommandService.BuildUniqueName("Name", "sample_purchaseorder", 0, string.Empty));
    }

    [Test]
    public void InferCustomizationPrefix_TakesThePrefixOfCustomTables()
    {
        Assert.That(CommandService.InferCustomizationPrefix("sample_purchaseorder"), Is.EqualTo("sample"));
        Assert.That(CommandService.InferCustomizationPrefix("account"), Is.Null);
    }

    // -------------------------------------------------------------------------------------------
    // Visibility
    // -------------------------------------------------------------------------------------------

    [Test]
    public void ValidateVisibility_RejectsAGridCommandWithoutAVisibilityRule()
    {
        // visibilitytype=None looks fine until rows are selected: the command bar switches into its
        // selection context and drops every command that has no rule.
        var ex = Assert.Throws<ArgumentException>(() => CommandService.ValidateVisibility(
            (int)CommandVisibilityType.None, (int)CommandLocation.MainGrid, null, null, null));

        Assert.That(ex!.Message, Does.Contain("selection"));
        Assert.That(ex.Message, Does.Contain("SelectionCountRule"));
    }

    [Test]
    public void ValidateVisibility_AllowsAGridCommandWithoutARuleWhenExplicitlyRequested()
    {
        Assert.DoesNotThrow(() => CommandService.ValidateVisibility(
            (int)CommandVisibilityType.None,
            (int)CommandLocation.SubGrid,
            null, null, null,
            allowGridWithoutVisibilityRule: true));
    }

    [Test]
    public void ValidateVisibility_AllowsAFormCommandWithoutARule()
    {
        Assert.DoesNotThrow(() => CommandService.ValidateVisibility(
            (int)CommandVisibilityType.None, (int)CommandLocation.Form, null, null, null));
    }

    [Test]
    public void ValidateVisibility_RequiresAllThreeFormulaFields()
    {
        var ex = Assert.Throws<ArgumentException>(() => CommandService.ValidateVisibility(
            (int)CommandVisibilityType.Formula,
            (int)CommandLocation.MainGrid,
            Guid.NewGuid(),
            "838813193347447db39c7ad8e32689c6",
            functionName: null));

        Assert.That(ex!.Message, Does.Contain("visibilityFormulaFunctionName"));
    }

    [Test]
    public void ValidateVisibility_AcceptsACompleteFormulaConfiguration()
    {
        Assert.DoesNotThrow(() => CommandService.ValidateVisibility(
            (int)CommandVisibilityType.Formula,
            (int)CommandLocation.MainGrid,
            Guid.Parse("31eaa81c-b8d7-4f9d-8e4d-900e1c81c301"),
            "838813193347447db39c7ad8e32689c6",
            "Visible"));
    }

    [Test]
    public void ValidateVisibility_RejectsFormulaFieldsWithoutFormulaVisibility()
    {
        Assert.Throws<ArgumentException>(() => CommandService.ValidateVisibility(
            (int)CommandVisibilityType.None,
            (int)CommandLocation.Form,
            Guid.NewGuid(),
            "component",
            "Visible"));
    }

    [Test]
    public void VisibilityTypeEnum_MatchesTheLiveOptionSet()
    {
        Assert.Multiple(() =>
        {
            Assert.That((int)CommandVisibilityType.None, Is.EqualTo(0));
            Assert.That((int)CommandVisibilityType.Formula, Is.EqualTo(1));
            Assert.That((int)CommandVisibilityType.ClassicRules, Is.EqualTo(2));
        });
    }

    // -------------------------------------------------------------------------------------------
    // Font icons
    // -------------------------------------------------------------------------------------------

    [Test]
    public async Task ValidateFontIconAsync_AcceptsAKnownIconWithoutCallingTheApi()
    {
        await _svc.ValidateFontIconAsync(OrgUrl, "$clientsvg:Add", CancellationToken.None);

        _handlerMock.Protected().Verify(
            "SendAsync", Times.Never(), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Test]
    public void ValidateFontIconAsync_RejectsAnIconTheEnvironmentDoesNotKnow()
    {
        // $clientsvg:Money reads as entirely plausible and silently prevents the command from rendering.
        SetupResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            value = new[] { new { fonticon = "$clientsvg:Add" } }
        }));

        var ex = Assert.ThrowsAsync<ArgumentException>(
            () => _svc.ValidateFontIconAsync(OrgUrl, "$clientsvg:Money", CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("silently"));
        Assert.That(ex.Message, Does.Contain("$clientsvg:Add"));
    }

    [Test]
    public async Task ValidateFontIconAsync_AcceptsAnIconTheEnvironmentActuallyUses()
    {
        SetupResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            value = new[] { new { fonticon = "$clientsvg:SomethingNewerThanThisBuild" } }
        }));

        await _svc.ValidateFontIconAsync(
            OrgUrl, "$clientsvg:SomethingNewerThanThisBuild", CancellationToken.None);
    }

    [Test]
    public async Task ValidateFontIconAsync_IgnoresANullIcon()
    {
        await _svc.ValidateFontIconAsync(OrgUrl, null, CancellationToken.None);
        await _svc.ValidateFontIconAsync(OrgUrl, "   ", CancellationToken.None);
    }

    [Test]
    public void FontIconCatalogue_CoversTheIconsSeenOnALiveOrg()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CommandFontIcons.IsKnown("$clientsvg:Add"), Is.True);
            Assert.That(CommandFontIcons.IsKnown("$clientsvg:PageCompleted"), Is.True);
            Assert.That(CommandFontIcons.IsKnown("$clientsvg:FollowUser"), Is.True);
            Assert.That(CommandFontIcons.IsKnown("Close"), Is.True);
            Assert.That(CommandFontIcons.IsKnown("$clientsvg:Money"), Is.False);
            Assert.That(CommandFontIcons.All, Is.Unique);
        });
    }

    // -------------------------------------------------------------------------------------------
    // Component libraries
    // -------------------------------------------------------------------------------------------

    [Test]
    public async Task ListComponentLibrariesAsync_FiltersOnCanvasAppTypeOne()
    {
        Uri? captured = null;
        var libraryId = Guid.NewGuid();

        SetupResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new
                {
                    canvasappid = libraryId.ToString(),
                    name = "sample_mainappdefaultcommandlibrary_01af8",
                    displayname = "mainapp_DefaultCommandLibrary",
                    ismanaged = false
                }
            }
        }), req => captured = req.RequestUri);

        var result = await _svc.ListComponentLibrariesAsync(OrgUrl, CancellationToken.None);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].CanvasAppId, Is.EqualTo(libraryId));
        Assert.That(Uri.UnescapeDataString(captured!.ToString()), Does.Contain("canvasapptype eq 1"));
    }

    [Test]
    public void ResolveComponentLibraryAsync_ExplainsThatNoneCanBeCreated()
    {
        SetupResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new { value = Array.Empty<object>() }));

        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.ResolveComponentLibraryAsync(OrgUrl, "whatever", CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("Command Designer"));
    }

    private void SetupResponse(HttpStatusCode statusCode, string body) =>
        SetupResponse(statusCode, body, _ => { });

    private void SetupResponse(HttpStatusCode statusCode, string body, Action<HttpRequestMessage> capture)
    {
        _handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capture(req))
            .ReturnsAsync(() => new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
