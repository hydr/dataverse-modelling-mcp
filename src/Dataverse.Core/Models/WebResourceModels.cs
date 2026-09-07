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

/// <summary>
/// Where a web resource is referenced, assembled from several sources because no single one records
/// it all.
/// </summary>
/// <param name="IsUsed">
/// True when anything at all was found. False does not mean "safe to delete" on its own — read
/// <paramref name="NotSearched"/> first.
/// </param>
/// <param name="PlatformDependencies">
/// The platform's own dependency tracking. It is authoritative where it applies but does not cover
/// everything: a ribbon naming a library in a JavaScriptFunction creates no dependency row.
/// </param>
/// <param name="NotSearched">
/// What this did not look at, in plain words. A "nothing found" that quietly skipped half the
/// environment would be worse than no answer.
/// </param>
public sealed record WebResourceUsageReport(
    string Name,
    Guid WebResourceId,
    bool IsUsed,
    string Summary,
    IReadOnlyList<WebResourceUsage> Forms,
    IReadOnlyList<WebResourceUsage> RibbonDiffs,
    IReadOnlyList<WebResourceUsage> SiteMaps,
    IReadOnlyList<WebResourceUsage> ModernCommands,
    IReadOnlyList<WebResourceUsage> MergedRibbons,
    ComponentDependencyReport PlatformDependencies,
    IReadOnlyList<string> NotSearched);

/// <param name="Context">
/// Where the hit sits — for a form the table it belongs to, for a merged ribbon the table it was
/// compiled for.
/// </param>
public sealed record WebResourceUsage(string Kind, Guid Id, string? Name, string? Context);
