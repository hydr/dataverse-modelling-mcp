namespace Dataverse.Core.Models;

/// <summary>
/// Dataverse <c>webresource.webresourcetype</c> option set.
/// </summary>
public enum WebResourceType
{
    Html = 1,
    Css = 2,
    JScript = 3,
    Xml = 4,
    Png = 5,
    Jpg = 6,
    Gif = 7,
    Xap = 8,
    Xsl = 9,
    Ico = 10,
    Svg = 11,
    Resx = 12
}

public sealed record WebResourceSummary(
    Guid WebResourceId,
    string Name,
    string? DisplayName,
    int WebResourceType,
    string WebResourceTypeName,
    bool IsManaged);

public sealed record WebResourceDetail(
    Guid WebResourceId,
    string Name,
    string? DisplayName,
    string? Description,
    int WebResourceType,
    string WebResourceTypeName,
    bool IsManaged,
    bool IsCustomizable,
    int ContentBytes,
    bool ContentIsText,
    string? Content);

public sealed record WebResourceUpsertResult(
    Guid WebResourceId,
    string Name,
    int WebResourceType,
    string WebResourceTypeName,
    bool Created,
    int ContentBytes,
    string? SolutionUniqueName,
    bool SolutionComponentAdded,
    bool Published);
