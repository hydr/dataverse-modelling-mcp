namespace Dataverse.Core.Models;

public sealed record EnvironmentVariableSummary(
    Guid DefinitionId,
    string SchemaName,
    string? DisplayName,
    string VariableType,
    string? DefaultValue,
    string? CurrentValue);

public sealed record EnvironmentVariableDetail(
    Guid DefinitionId,
    string SchemaName,
    string? DisplayName,
    string VariableType,
    string? DefaultValue,
    string? CurrentValue,
    string? Description,
    Guid? ValueId);
