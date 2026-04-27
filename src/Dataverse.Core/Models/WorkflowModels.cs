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
    DateTime? ModifiedOn);

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
    int WorkflowActivityGroupName);

public sealed record WorkflowActivityParameter(
    string Name,
    string ParameterType,
    string Direction,
    bool IsRequired,
    string? Description);

public sealed record WorkflowActivityDetail(
    Guid PluginTypeId,
    string Name,
    string AssemblyQualifiedName,
    string AssemblyName,
    string Version,
    string? Description,
    IReadOnlyList<WorkflowActivityParameter> Parameters);
