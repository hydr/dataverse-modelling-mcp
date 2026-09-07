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
    int ComponentCount,
    IReadOnlyList<SolutionComponent> Components);

/// <summary>
/// One component of a solution, read from the <c>solutioncomponent</c> table.
/// </summary>
/// <param name="ComponentId">
/// The component's own id — <c>solutioncomponent.objectid</c>. For a table or column this is its
/// <c>MetadataId</c>. This is the id every other tool takes as <c>componentId</c>, NOT
/// <paramref name="SolutionComponentId"/>.
/// </param>
/// <param name="Name">
/// Resolved display name, or null when the type has no name source or the lookup failed. Purely
/// informational — nothing keys off it.
/// </param>
/// <param name="RootComponentBehavior">
/// For a root component (a table): whether its subcomponents travel with it.
/// 0 = include subcomponents, 1 = do not include subcomponents, 2 = include as shell only.
/// Null for components that are not roots.
/// </param>
/// <param name="SolutionComponentId">
/// Primary key of the membership row itself. Diagnostic only — passing it where a
/// <c>componentId</c> is expected fails with <c>0x8004f021 Cannot find solution component</c>.
/// </param>
public sealed record SolutionComponent(
    Guid ComponentId,
    int ComponentType,
    string? ComponentTypeName,
    string? Name = null,
    int? RootComponentBehavior = null,
    string? RootComponentBehaviorName = null,
    Guid? RootComponentId = null,
    Guid? SolutionComponentId = null);

/// <summary>
/// Result of <c>AddSolutionComponent</c>, with the membership actually verified afterwards.
/// </summary>
/// <remarks>
/// The action reports success even when it creates nothing: if the component's parent table is
/// already in the solution with <c>rootcomponentbehavior = 0</c> (include subcomponents), the
/// subcomponent is covered by the parent and gets no membership row of its own. A bare
/// "success" is misleading there, so the row is read back and <paramref name="ExplicitMembership"/>
/// says what really happened.
/// </remarks>
public sealed record AddComponentResult(
    bool Success,
    string SolutionUniqueName,
    Guid ComponentId,
    int ComponentType,
    string ComponentTypeName,
    bool ExplicitMembership,
    Guid? SolutionComponentId,
    string? Note = null,
    IReadOnlyList<string>? CoveringRootComponents = null);

/// <summary>
/// Result of <c>RemoveSolutionComponent</c>, with the membership checked before and after.
/// </summary>
public sealed record RemoveComponentResult(
    bool Success,
    string SolutionUniqueName,
    Guid ComponentId,
    int ComponentType,
    string ComponentTypeName,
    bool Removed,
    string? Note = null,
    IReadOnlyList<string>? CoveringRootComponents = null);

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
/// <para>
/// <paramref name="CustomControlWarnings"/> exists because a reported success is not proof that a
/// PCF control was updated: an import can leave an existing <c>customcontrol</c> row on its old
/// version and old manifest while the import job marks the component
/// <c>result="success"</c>. Every control in the zip is therefore compared against what the
/// environment stores afterwards.
/// </para>
/// </summary>
public sealed record SolutionImportResult(
    bool Success,
    Guid AsyncOperationId,
    Guid ImportJobId,
    string? StatusReason,
    double? ProgressPercent,
    string? ErrorMessage,
    IReadOnlyList<string> ComponentErrors,
    IReadOnlyList<string> CustomControlWarnings,
    IReadOnlyList<CustomControlVersionCheck> CustomControlVersions);

/// <summary>
/// Per-control outcome of the post-import version check: what the imported zip's
/// <c>ControlManifest.xml</c> declared versus what the environment stores now.
/// </summary>
/// <param name="StoredVersion">
/// Null when no matching <c>customcontrol</c> row was found — a first-time import, or a name that
/// did not match.
/// </param>
/// <param name="Matches">
/// Null when the comparison could not be made at all (no stored row, or no version in the
/// manifest). Only <c>false</c> is a confirmed mismatch, and only those raise a warning — an
/// unknown outcome must not be reported as a problem.
/// </param>
public sealed record CustomControlVersionCheck(
    string ManifestName,
    string? StoredName,
    string? ManifestVersion,
    string? StoredVersion,
    bool? Matches);

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
