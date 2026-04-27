namespace Dataverse.Core.Models;

public sealed record SolutionSummary(
    Guid SolutionId,
    string UniqueName,
    string FriendlyName,
    string Version,
    bool IsManaged,
    string? PublisherName);

public sealed record SolutionDetail(
    Guid SolutionId,
    string UniqueName,
    string FriendlyName,
    string Version,
    bool IsManaged,
    string? PublisherName,
    string? Description,
    DateTime? InstalledOn,
    IReadOnlyList<SolutionComponent> Components);

public sealed record SolutionComponent(
    Guid ComponentId,
    int ComponentType,
    string? ComponentTypeName,
    Guid? RootComponentId);

public sealed record SolutionExportResult(
    string UniqueName,
    bool IsManaged,
    string? FilePath,
    string? Base64Content);

public sealed record SolutionLayerInfo(
    Guid ComponentId,
    int ComponentType,
    IReadOnlyList<string> SolutionLayers);
