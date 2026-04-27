namespace Dataverse.Core.Models;

public sealed record ViewSummary(
    Guid ViewId,
    string Name,
    string? ViewType,
    bool IsDefault,
    bool IsCustomizable);

public sealed record ViewDetail(
    Guid ViewId,
    string Name,
    string? ViewType,
    bool IsDefault,
    bool IsCustomizable,
    string? FetchXml,
    string? LayoutXml,
    string? Description);
