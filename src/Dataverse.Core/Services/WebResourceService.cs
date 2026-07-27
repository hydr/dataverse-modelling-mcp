namespace Dataverse.Core.Services;

using System.Text;
using System.Text.Json;
using Dataverse.Core.Clients;
using Dataverse.Core.Json;
using Dataverse.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// CRUD for web resources (<c>webresource</c>). Note the EntitySetName is
/// <c>webresourceset</c>, not <c>webresources</c> — the latter returns
/// <c>0x80060888 "Resource not found for the segment 'webresources'"</c>.
/// </summary>
public sealed class WebResourceService
{
    /// <summary>Solution component type of a web resource, for AddSolutionComponent.</summary>
    public const int WebResourceComponentType = 61;

    private const string EntitySet = "api/data/v9.2/webresourceset";

    private readonly DataverseHttpClient _client;
    private readonly ILogger<WebResourceService> _logger;

    public WebResourceService(DataverseHttpClient client, ILogger<WebResourceService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<IReadOnlyList<WebResourceSummary>> ListAsync(
        string orgUrl,
        string? namePrefix = null,
        CancellationToken ct = default)
    {
        var url = $"{EntitySet}?$select=webresourceid,name,displayname,webresourcetype,ismanaged&$orderby=name";
        if (!string.IsNullOrWhiteSpace(namePrefix))
        {
            var filter = $"startswith(name,'{namePrefix.Replace("'", "''")}')";
            url += $"&$filter={Uri.EscapeDataString(filter)}";
        }

        var raw = await _client.GetRawAsync(orgUrl, url, ct: ct);
        var doc = JsonDocument.Parse(raw);
        var results = new List<WebResourceSummary>();

        if (doc.RootElement.TryGetProperty("value", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                var type = item.GetInt32OrZero("webresourcetype");
                results.Add(new WebResourceSummary(
                    WebResourceId: item.TryGetGuid("webresourceid"),
                    Name: item.GetStringOrEmpty("name"),
                    DisplayName: item.GetStringOrNull("displayname"),
                    WebResourceType: type,
                    WebResourceTypeName: TypeName(type),
                    IsManaged: item.TryGetProperty("ismanaged", out var m) && m.ValueKind == JsonValueKind.True));
            }
        }

        return results;
    }

    /// <summary>Look up a web resource id by its unique <c>name</c>. Returns null when absent.</summary>
    public async Task<Guid?> FindIdByNameAsync(string orgUrl, string name, CancellationToken ct = default)
    {
        var filter = $"name eq '{name.Replace("'", "''")}'";
        var url = $"{EntitySet}?$select=webresourceid&$filter={Uri.EscapeDataString(filter)}&$top=1";
        var raw = await _client.GetRawAsync(orgUrl, url, ct: ct);
        var doc = JsonDocument.Parse(raw);

        if (doc.RootElement.TryGetProperty("value", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                var id = item.TryGetGuid("webresourceid");
                return id == Guid.Empty ? null : id;
            }
        }

        return null;
    }

    /// <summary>
    /// Fetch a web resource by id, decoding <c>content</c> for text formats
    /// (HTML, CSS, JScript, XML, XSL, SVG, RESX). Binary formats report their size only.
    /// <para>
    /// <c>content</c> read through a plain GET is the <b>published</b> payload: right after an
    /// unpublished PATCH this still returns the previous version. Publish the web resource before
    /// reading back.
    /// </para>
    /// </summary>
    public async Task<WebResourceDetail?> GetAsync(string orgUrl, Guid webResourceId, CancellationToken ct = default)
    {
        var url = $"{EntitySet}({webResourceId})" +
                  "?$select=webresourceid,name,displayname,description,webresourcetype,ismanaged,iscustomizable,content";

        var raw = await _client.GetRawAsync(orgUrl, url, ct: ct);
        var item = JsonDocument.Parse(raw).RootElement;

        var type = item.GetInt32OrZero("webresourcetype");
        var base64 = item.GetStringOrNull("content");
        var bytes = string.IsNullOrEmpty(base64) ? Array.Empty<byte>() : Convert.FromBase64String(base64);
        var isText = IsTextType(type);

        return new WebResourceDetail(
            WebResourceId: item.TryGetGuid("webresourceid"),
            Name: item.GetStringOrEmpty("name"),
            DisplayName: item.GetStringOrNull("displayname"),
            Description: item.GetStringOrNull("description"),
            WebResourceType: type,
            WebResourceTypeName: TypeName(type),
            IsManaged: item.TryGetProperty("ismanaged", out var m) && m.ValueKind == JsonValueKind.True,
            IsCustomizable: item.TryGetProperty("iscustomizable", out var c) &&
                            c.TryGetProperty("Value", out var cv) && cv.ValueKind == JsonValueKind.True,
            ContentBytes: bytes.Length,
            ContentIsText: isText,
            Content: isText ? DecodeText(bytes) : null);
    }

    /// <summary>
    /// Create a web resource when <paramref name="name"/> is unknown, otherwise patch the existing row.
    /// <paramref name="content"/> is the raw (already decoded) text or binary payload; it is Base64-encoded here.
    /// </summary>
    public async Task<(Guid WebResourceId, bool Created, int WebResourceType)> UpsertAsync(
        string orgUrl,
        string name,
        byte[] content,
        string? displayName = null,
        string? description = null,
        int? webResourceType = null,
        CancellationToken ct = default)
    {
        var existingId = await FindIdByNameAsync(orgUrl, name, ct);
        var base64 = Convert.ToBase64String(content);

        if (existingId is { } id)
        {
            var patch = new Dictionary<string, object?> { ["content"] = base64 };
            if (displayName is not null) { patch["displayname"] = displayName; }
            if (description is not null) { patch["description"] = description; }
            // webresourcetype is immutable once the row exists — Dataverse rejects a change.
            await _client.PatchAsync(orgUrl, $"{EntitySet}({id})", patch, ct);

            var current = await _client.GetRawAsync(orgUrl, $"{EntitySet}({id})?$select=webresourcetype", ct: ct);
            var type = JsonDocument.Parse(current).RootElement.GetInt32OrZero("webresourcetype");
            _logger.LogInformation("Updated web resource '{Name}' ({Id}).", name, id);
            return (id, false, type);
        }

        var resolvedType = webResourceType ?? InferTypeFromExtension(name)
            ?? throw new InvalidOperationException(
                $"Cannot infer webResourceType from the name '{name}' — pass webResourceType explicitly.");

        var body = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["displayname"] = displayName ?? name,
            ["description"] = description,
            ["webresourcetype"] = resolvedType,
            ["content"] = base64
        };

        var newId = await _client.PostForIdAsync(orgUrl, EntitySet, body, "webresourceid", ct);
        _logger.LogInformation("Created web resource '{Name}' ({Id}), type {Type}.", name, newId, resolvedType);
        return (newId, true, resolvedType);
    }

    public async Task DeleteAsync(string orgUrl, Guid webResourceId, CancellationToken ct = default)
    {
        await _client.DeleteAsync(orgUrl, $"{EntitySet}({webResourceId})", ct);
        _logger.LogInformation("Deleted web resource {Id}.", webResourceId);
    }

    /// <summary>Map a file extension to the <c>webresourcetype</c> option value; null when unknown.</summary>
    public static int? InferTypeFromExtension(string fileNameOrPath)
    {
        var ext = Path.GetExtension(fileNameOrPath).ToLowerInvariant();
        return ext switch
        {
            ".html" or ".htm" => (int)WebResourceType.Html,
            ".css" => (int)WebResourceType.Css,
            ".js" => (int)WebResourceType.JScript,
            ".xml" => (int)WebResourceType.Xml,
            ".png" => (int)WebResourceType.Png,
            ".jpg" or ".jpeg" => (int)WebResourceType.Jpg,
            ".gif" => (int)WebResourceType.Gif,
            ".xap" => (int)WebResourceType.Xap,
            ".xsl" or ".xslt" => (int)WebResourceType.Xsl,
            ".ico" => (int)WebResourceType.Ico,
            ".svg" => (int)WebResourceType.Svg,
            ".resx" => (int)WebResourceType.Resx,
            _ => null
        };
    }

    /// <summary>True for the formats whose content is meaningful as text.</summary>
    public static bool IsTextType(int webResourceType) => webResourceType is
        (int)WebResourceType.Html or
        (int)WebResourceType.Css or
        (int)WebResourceType.JScript or
        (int)WebResourceType.Xml or
        (int)WebResourceType.Xsl or
        (int)WebResourceType.Svg or
        (int)WebResourceType.Resx;

    public static string TypeName(int webResourceType) =>
        Enum.IsDefined(typeof(WebResourceType), webResourceType)
            ? ((WebResourceType)webResourceType).ToString()
            : $"Type{webResourceType}";

    /// <summary>Decode UTF-8, honouring a BOM when the uploaded file carried one.</summary>
    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
