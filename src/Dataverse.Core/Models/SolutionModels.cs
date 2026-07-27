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
    string? Base64Content,
    long? FileSizeBytes = null);

/// <summary>
/// Result of an asynchronous solution import (<c>ImportSolutionAsync</c> + ImportJob polling).
/// <paramref name="Success"/> is authoritative (driven by the async-operation status code);
/// <paramref name="ComponentErrors"/> carries the per-component failures parsed from the
/// ImportJob's <c>data</c> XML for diagnostics.
/// </summary>
public sealed record SolutionImportResult(
    bool Success,
    Guid AsyncOperationId,
    Guid ImportJobId,
    string? StatusReason,
    double? ProgressPercent,
    string? ErrorMessage,
    IReadOnlyList<string> ComponentErrors);

/// <summary>
/// The solution layers of a single component, as shown by the Maker's "Solution Layers" view.
/// Sourced from the <c>msdyn_componentlayer</c> virtual table.
/// <paramref name="Layers"/> is ordered bottom-up (<c>msdyn_order</c> ascending): the first entry is
/// the base layer, each following layer overrides the ones before it, and the last entry is the layer
/// currently in effect (also surfaced as <paramref name="TopLayerSolutionName"/>).
/// </summary>
public sealed record SolutionLayerInfo(
    Guid ComponentId,
    int ComponentType,
    string ComponentTypeName,
    string? ComponentName,
    int LayerCount,
    string? TopLayerSolutionName,
    IReadOnlyList<SolutionLayer> Layers);

/// <summary>
/// One layer of a component. <paramref name="Order"/> is the platform's <c>msdyn_order</c> — a higher
/// order sits higher in the stack and overrides everything below it.
/// </summary>
public sealed record SolutionLayer(
    int Order,
    string SolutionName,
    string? PublisherName,
    DateTime? OverwriteTime,
    bool IsTopLayer);

public sealed record PipelineSummary(
    Guid PipelineId,
    string Name,
    string? Description,
    string State);

public sealed record PipelineStageSummary(
    Guid StageId,
    string Name,
    Guid? PreviousStageId,
    string State,
    string? TargetEnvironmentName,
    string? TargetEnvironmentId,
    string? TargetDeploymentEnvironmentId);

public sealed record DeploymentEnvironmentSummary(
    Guid DeploymentEnvironmentId,
    string Name,
    string EnvironmentId);
