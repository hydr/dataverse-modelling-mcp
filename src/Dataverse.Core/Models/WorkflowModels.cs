namespace Dataverse.Core.Models;

public sealed record WorkflowSummary(
    Guid WorkflowId,
    string Name,
    string? PrimaryEntity,
    int StateCode,
    int StatusCode,
    string? OwnerId,
    string? OwnerName);

public sealed record WorkflowDetail(
    Guid WorkflowId,
    string Name,
    string? PrimaryEntity,
    int StateCode,
    int StatusCode,
    string? OwnerId,
    string? OwnerName,
    string? Description,
    string? Xaml,
    DateTime? CreatedOn,
    DateTime? ModifiedOn,
    // Trigger
    bool OnDemand,
    bool IsOnCreate,
    bool IsOnUpdate,
    bool IsOnDelete,
    string? TriggerOnUpdateAttributes,
    string? CreateStage,
    string? UpdateStage,
    string? DeleteStage,
    // Execution
    string Scope,
    string Mode,
    string RunAs,
    bool IsTransacted,
    int Rank,
    // Logging
    bool SyncLogOnFailure,
    bool AsyncAutoDelete);

public sealed record WorkflowValidationReport(
    Guid WorkflowId,
    string Name,
    bool IsValid,
    IReadOnlyList<string> Issues);

public sealed record WorkflowActivitySummary(
    Guid PluginTypeId,
    string Name,
    string AssemblyQualifiedName,
    string AssemblyName,
    string Version,
    string? Description,
    // Dataverse stores this as a string like "Sample.CrmPlugins (1.0.0.0)", not a number.
    string? WorkflowActivityGroupName);

/// <summary>
/// One input or output parameter of a Custom Workflow Activity, read from
/// <c>plugintype.customworkflowactivityinfo</c>.
/// </summary>
/// <param name="Name">Display name shown in the designer (may contain spaces/dashes, e.g. "E-Mail").</param>
/// <param name="DependencyPropertyName">
/// The technical name. This — not <paramref name="Name"/> — is the <c>x:Key</c> of the
/// In/OutArgument in the XAML (e.g. "Email").
/// </param>
/// <param name="TypeName">Assembly-qualified .NET type of the parameter.</param>
/// <param name="XamlTypeArgument">
/// The matching XAML type argument for <c>x:TypeArguments</c> (e.g. "x:String"), derived from
/// <paramref name="TypeName"/>. Null when the type is not a known primitive.
/// </param>
/// <param name="DataType">
/// The <c>dataType</c> to use in the definition model for this parameter (e.g. "EntityReference"),
/// derived from <paramref name="TypeName"/>. Null when the type has no counterpart there.
/// </param>
/// <param name="EntityNames">
/// For lookup parameters: the target entities the parameter accepts. A reference to any other table
/// is rejected at runtime. Empty for all other parameter types.
/// </param>
public sealed record WorkflowActivityParameter(
    string Name,
    string DependencyPropertyName,
    string TypeName,
    string? XamlTypeArgument,
    string Direction,
    bool IsRequired,
    string? Description,
    string? DataType = null,
    IReadOnlyList<string>? EntityNames = null);

public sealed record WorkflowActivityDetail(
    Guid PluginTypeId,
    string Name,
    /// <summary>Ready-made value taken from customworkflowactivityinfo — use as-is in XAML.</summary>
    string AssemblyQualifiedName,
    string AssemblyName,
    string Version,
    string? PublicKeyToken,
    string? Culture,
    string? GroupName,
    string? Description,
    IReadOnlyList<WorkflowActivityParameter> Parameters);
