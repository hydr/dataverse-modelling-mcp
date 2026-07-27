namespace Dataverse.Core.Services;

using System.Text.Json;
using Dataverse.Core.Clients;
using Dataverse.Core.Json;
using Dataverse.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Modern commands (<c>appaction</c>) — the Unified Interface successor to classic RibbonDiffXml
/// buttons. Several tables no longer render RibbonDiffXml at all, so a command bar button has to be
/// an <c>appaction</c> row.
/// <para>
/// Two API shapes bite here:
/// </para>
/// <list type="bullet">
/// <item>The lookups must be bound through their <b>navigation property</b> names
/// <c>ContextEntity</c> (target collection <c>entities</c>, value = the table's
/// <b>MetadataId</b>) and <c>OnClickEventJavaScriptWebResourceId</c> (target collection
/// <c>webresourceset</c>). Using the attribute names <c>contextentity</c> /
/// <c>onclickeventjavascriptwebresourceid</c> in <c>@odata.bind</c> fails with
/// <c>0x80048d19 — undeclared property</c>.</item>
/// <item><c>origin</c> can only be set on create. A PATCH that changes it answers 200 and leaves the
/// stored value untouched.</item>
/// <item><c>uniquename</c> must begin with a publisher customization prefix followed by an
/// underscore, otherwise the create fails with
/// <c>0x800608ad — "Export key attribute uniquename for component appaction must start with a valid
/// customization prefix"</c>.</item>
/// </list>
/// </summary>
public sealed class CommandService
{
    /// <summary>Solution component type of a modern command, for AddSolutionComponent.</summary>
    public const int AppActionComponentType = 10343;

    private const string EntitySet = "api/data/v9.2/appactions";

    private readonly DataverseHttpClient _client;
    private readonly ILogger<CommandService> _logger;

    public CommandService(DataverseHttpClient client, ILogger<CommandService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CommandSummary>> ListAsync(
        string orgUrl,
        string tableLogicalName,
        CancellationToken ct = default)
    {
        var filter = $"contextvalue eq '{tableLogicalName.Replace("'", "''")}'";
        var url = $"{EntitySet}?$filter={Uri.EscapeDataString(filter)}" +
                  "&$select=appactionid,name,uniquename,buttonlabeltext,location,origin,visibilitytype," +
                  "onclickeventjavascriptfunctionname,hidden,ismanaged&$orderby=location,name";

        var raw = await _client.GetRawAsync(orgUrl, url, ct: ct);
        var doc = JsonDocument.Parse(raw);
        var results = new List<CommandSummary>();

        if (doc.RootElement.TryGetProperty("value", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                var location = item.GetInt32OrZero("location");
                var origin = item.GetInt32OrZero("origin");
                var visibility = item.GetInt32OrZero("visibilitytype");

                results.Add(new CommandSummary(
                    AppActionId: item.TryGetGuid("appactionid"),
                    Name: item.GetStringOrEmpty("name"),
                    UniqueName: item.GetStringOrNull("uniquename"),
                    ButtonLabelText: item.GetStringOrNull("buttonlabeltext"),
                    Location: location,
                    LocationName: EnumName<CommandLocation>(location),
                    Origin: origin,
                    OriginName: EnumName<CommandOrigin>(origin),
                    VisibilityType: visibility,
                    VisibilityTypeName: EnumName<CommandVisibilityType>(visibility),
                    OnClickEventJavaScriptFunctionName: item.GetStringOrNull("onclickeventjavascriptfunctionname"),
                    Hidden: item.TryGetProperty("hidden", out var h) && h.ValueKind == JsonValueKind.True,
                    IsManaged: item.TryGetProperty("ismanaged", out var m) && m.ValueKind == JsonValueKind.True));
            }
        }

        return results;
    }

    public async Task<CommandDetail?> GetAsync(string orgUrl, Guid appActionId, CancellationToken ct = default)
    {
        var raw = await _client.GetRawAsync(orgUrl, $"{EntitySet}({appActionId})", ct: ct);
        var item = JsonDocument.Parse(raw).RootElement;

        var context = item.GetInt32OrZero("context");
        var location = item.GetInt32OrZero("location");
        var buttonType = item.GetInt32OrZero("type");
        var origin = item.GetInt32OrZero("origin");
        var visibility = item.GetInt32OrZero("visibilitytype");
        var clickType = item.GetInt32OrZero("onclickeventtype");

        return new CommandDetail(
            AppActionId: item.TryGetGuid("appactionid"),
            Name: item.GetStringOrEmpty("name"),
            UniqueName: item.GetStringOrNull("uniquename"),
            ButtonLabelText: item.GetStringOrNull("buttonlabeltext"),
            ButtonTooltipTitle: item.GetStringOrNull("buttontooltiptitle"),
            ButtonTooltipDescription: item.GetStringOrNull("buttontooltipdescription"),
            Context: context,
            ContextName: EnumName<CommandContext>(context),
            ContextValue: item.GetStringOrNull("contextvalue"),
            ContextEntityMetadataId: NullableGuid(item, "_contextentity_value"),
            Location: location,
            LocationName: EnumName<CommandLocation>(location),
            ButtonType: buttonType,
            ButtonTypeName: EnumName<CommandButtonType>(buttonType),
            Origin: origin,
            OriginName: EnumName<CommandOrigin>(origin),
            VisibilityType: visibility,
            VisibilityTypeName: EnumName<CommandVisibilityType>(visibility),
            OnClickEventType: clickType,
            OnClickEventTypeName: EnumName<CommandOnClickEventType>(clickType),
            OnClickEventJavaScriptWebResourceId: NullableGuid(item, "_onclickeventjavascriptwebresourceid_value"),
            OnClickEventJavaScriptFunctionName: item.GetStringOrNull("onclickeventjavascriptfunctionname"),
            Parameters: ParseParameters(item.GetStringOrNull("onclickeventjavascriptparameters")),
            FontIcon: item.GetStringOrNull("fonticon"),
            IconWebResourceId: NullableGuid(item, "_iconwebresourceid_value"),
            Sequence: item.TryGetProperty("sequence", out var seq) && seq.ValueKind == JsonValueKind.Number
                ? seq.GetDecimal()
                : null,
            Hidden: item.TryGetProperty("hidden", out var hid) && hid.ValueKind == JsonValueKind.True,
            IsDisabled: item.TryGetProperty("isdisabled", out var dis) && dis.ValueKind == JsonValueKind.True,
            AppModuleId: NullableGuid(item, "_appmoduleid_value"),
            IsManaged: item.TryGetProperty("ismanaged", out var man) && man.ValueKind == JsonValueKind.True);
    }

    /// <summary>Resolve the <c>MetadataId</c> of a table — the value the <c>ContextEntity</c> lookup expects.</summary>
    public async Task<Guid> GetTableMetadataIdAsync(
        string orgUrl,
        string tableLogicalName,
        CancellationToken ct = default)
    {
        var url = $"api/data/v9.2/EntityDefinitions(LogicalName='{tableLogicalName}')?$select=MetadataId";
        var raw = await _client.GetRawAsync(orgUrl, url, ct: ct);
        var id = JsonDocument.Parse(raw).RootElement.TryGetGuid("MetadataId");
        if (id == Guid.Empty)
        {
            throw new InvalidOperationException($"Could not resolve the MetadataId of table '{tableLogicalName}'.");
        }

        return id;
    }

    /// <summary>
    /// Create a JavaScript-backed modern command bound to a table.
    /// <paramref name="origin"/> defaults to <see cref="CommandOrigin.Default"/> and should stay there —
    /// see the remarks on <see cref="CommandOrigin"/>.
    /// </summary>
    public async Task<Guid> CreateAsync(
        string orgUrl,
        string tableLogicalName,
        string name,
        string buttonLabelText,
        int location,
        Guid javaScriptWebResourceId,
        string functionName,
        IReadOnlyList<CommandParameter> parameters,
        string? uniqueName = null,
        string? tooltipTitle = null,
        string? tooltipDescription = null,
        string? fontIcon = null,
        decimal? sequence = null,
        int origin = (int)CommandOrigin.Default,
        int visibilityType = (int)CommandVisibilityType.None,
        int buttonType = (int)CommandButtonType.StandardButton,
        string? customizationPrefix = null,
        CancellationToken ct = default)
    {
        var metadataId = await GetTableMetadataIdAsync(orgUrl, tableLogicalName, ct);
        var resolvedUniqueName = uniqueName ?? BuildUniqueName(
            name,
            tableLogicalName,
            location,
            customizationPrefix ?? InferCustomizationPrefix(tableLogicalName));

        var body = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["uniquename"] = resolvedUniqueName,
            ["buttonlabeltext"] = buttonLabelText,
            ["buttontooltiptitle"] = tooltipTitle,
            ["buttontooltipdescription"] = tooltipDescription,
            ["context"] = (int)CommandContext.Entity,
            ["contextvalue"] = tableLogicalName,
            // Navigation property names, not attribute names — see the class remarks.
            ["ContextEntity@odata.bind"] = $"/entities({metadataId})",
            ["OnClickEventJavaScriptWebResourceId@odata.bind"] = $"/webresourceset({javaScriptWebResourceId})",
            ["location"] = location,
            ["type"] = buttonType,
            ["origin"] = origin,
            ["visibilitytype"] = visibilityType,
            ["onclickeventtype"] = (int)CommandOnClickEventType.JavaScript,
            ["onclickeventjavascriptfunctionname"] = functionName,
            ["onclickeventjavascriptparameters"] = SerializeParameters(parameters),
            ["fonticon"] = fontIcon,
            ["hidden"] = false,
            ["isdisabled"] = false
        };

        if (sequence.HasValue)
        {
            body["sequence"] = sequence.Value;
        }

        var id = await _client.PostForIdAsync(orgUrl, EntitySet, body, "appactionid", ct);
        _logger.LogInformation(
            "Created modern command '{Name}' ({Id}) on {Table}, location {Location}.",
            name, id, tableLogicalName, location);
        return id;
    }

    public async Task UpdateAsync(
        string orgUrl,
        Guid appActionId,
        Dictionary<string, object?> properties,
        CancellationToken ct = default)
    {
        await _client.PatchAsync(orgUrl, $"{EntitySet}({appActionId})", properties, ct);
    }

    public async Task DeleteAsync(string orgUrl, Guid appActionId, CancellationToken ct = default)
    {
        await _client.DeleteAsync(orgUrl, $"{EntitySet}({appActionId})", ct);
        _logger.LogInformation("Deleted modern command {Id}.", appActionId);
    }

    /// <summary>
    /// Mirrors the Command Designer's naming scheme:
    /// <c>&lt;prefix&gt;_&lt;name&gt;!&lt;table&gt;!&lt;location&gt;</c>. The leading customization prefix is
    /// mandatory — without it the create fails with <c>0x800608ad</c>. Characters that would produce a
    /// second prefix separator or break the <c>!</c>-delimited shape are stripped from the name.
    /// </summary>
    public static string BuildUniqueName(string name, string tableLogicalName, int location, string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            throw new ArgumentException(
                "A customization prefix is required for the appaction uniquename. Pass customizationPrefix " +
                "or a complete uniqueName.",
                nameof(prefix));
        }

        var sanitized = new string(name.Where(char.IsLetterOrDigit).ToArray());
        if (sanitized.Length == 0)
        {
            sanitized = "Command";
        }

        return $"{prefix.TrimEnd('_')}_{sanitized}!{tableLogicalName}!{location}";
    }

    /// <summary>
    /// Derive the customization prefix from a custom table's logical name (<c>sample_purchaseorder</c> →
    /// <c>xv</c>). Returns null for out-of-the-box tables, which have no prefix.
    /// </summary>
    public static string? InferCustomizationPrefix(string tableLogicalName)
    {
        var underscore = tableLogicalName.IndexOf('_');
        return underscore > 0 ? tableLogicalName[..underscore] : null;
    }

    public static string SerializeParameters(IReadOnlyList<CommandParameter> parameters) =>
        JsonSerializer.Serialize(
            parameters.Select(p => new { type = p.Type, value = p.Value }).ToArray());

    /// <summary>
    /// Parse the <c>parameters</c> tool argument. Accepts a JSON array whose entries are either
    /// enum names (<c>"PrimaryControl"</c>), raw numbers (<c>5</c>), or objects
    /// (<c>{"type":"StringParameter","value":"abc"}</c>), or a plain comma-separated list of names.
    /// </summary>
    public static IReadOnlyList<CommandParameter> ParseParameterSpec(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return Array.Empty<CommandParameter>();
        }

        var trimmed = spec.Trim();
        if (!trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            return trimmed
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(n => new CommandParameter(ResolveParameterType(n), null))
                .ToList();
        }

        using var doc = JsonDocument.Parse(trimmed);
        var result = new List<CommandParameter>();

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.String:
                    result.Add(new CommandParameter(ResolveParameterType(el.GetString()!), null));
                    break;

                case JsonValueKind.Number:
                    result.Add(new CommandParameter(el.GetInt32(), null));
                    break;

                case JsonValueKind.Object:
                    if (!el.TryGetProperty("type", out var typeEl))
                    {
                        throw new ArgumentException("Each parameter object needs a 'type' property.");
                    }

                    var type = typeEl.ValueKind == JsonValueKind.Number
                        ? typeEl.GetInt32()
                        : ResolveParameterType(typeEl.GetString() ?? string.Empty);

                    string? value = null;
                    if (el.TryGetProperty("value", out var valueEl) && valueEl.ValueKind != JsonValueKind.Null)
                    {
                        value = valueEl.ValueKind == JsonValueKind.String
                            ? valueEl.GetString()
                            : valueEl.GetRawText();
                    }

                    result.Add(new CommandParameter(type, value));
                    break;

                default:
                    throw new ArgumentException($"Unsupported parameter entry of kind {el.ValueKind}.");
            }
        }

        return result;
    }

    private static int ResolveParameterType(string nameOrNumber)
    {
        if (int.TryParse(nameOrNumber, out var number))
        {
            return number;
        }

        if (Enum.TryParse<CommandParameterType>(nameOrNumber, ignoreCase: true, out var parsed))
        {
            return (int)parsed;
        }

        throw new ArgumentException(
            $"Unknown command parameter type '{nameOrNumber}'. Known names: " +
            string.Join(", ", Enum.GetNames<CommandParameterType>()) +
            ". Raw integers are also accepted.");
    }

    internal static IReadOnlyList<CommandParameter> ParseParameters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<CommandParameter>();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var result = new List<CommandParameter>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var type = el.GetInt32OrZero("type");
                var value = el.GetStringOrNull("value");
                result.Add(new CommandParameter(type, value));
            }

            return result;
        }
        catch (JsonException)
        {
            return Array.Empty<CommandParameter>();
        }
    }

    private static Guid? NullableGuid(JsonElement el, string prop)
    {
        var id = el.TryGetGuid(prop);
        return id == Guid.Empty ? null : id;
    }

    private static string EnumName<T>(int value) where T : struct, Enum =>
        Enum.IsDefined(typeof(T), value) ? ((T)(object)value).ToString()! : $"Value{value}";
}
